using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Data.Memories
{
    /// <summary>Downloads one image. Behind an interface so tests never touch the network.</summary>
    public interface IMemoryImageSource
    {
        /// <summary>
        /// Returns the body of <paramref name="uri"/>, or null on any failure. Must never return
        /// more than <paramref name="maxBytes"/> + 1 bytes (one over the cap is how "too large" is seen).
        /// </summary>
        byte[]? Fetch(Uri uri, long maxBytes);
    }

    /// <summary>What one image fetch run did for one kind of image. Counts only.</summary>
    /// <param name="Fetched">Distinct source URLs downloaded and stored this run.</param>
    /// <param name="AlreadyPresent">Distinct source URLs whose image was already stored.</param>
    /// <param name="Failed">Distinct source URLs refused (host, scheme) or not downloaded (error, size, not an image).</param>
    public sealed record MemoryImageCounts(int Fetched, int AlreadyPresent, int Failed);

    /// <summary>What one image fetch run did. Counts only.</summary>
    /// <param name="GameImages">Entry images (game artwork).</param>
    /// <param name="TrophyIcons">Trophy definition icons, for definitions used by at least one entry item.</param>
    public sealed record MemoryImageFetchResult(MemoryImageCounts GameImages, MemoryImageCounts TrophyIcons)
    {
        /// <summary>Failures of both kinds.</summary>
        public int Failed => GameImages.Failed + TrophyIcons.Failed;
    }

    /// <summary>
    /// Fetches each distinct entry image source URL, and each distinct trophy icon URL of a
    /// definition used by at least one entry item, not yet stored, only over HTTPS from the
    /// allowlisted hosts, with a size cap, and stores it in the memories media folder by SHA-256.
    /// </summary>
    /// <remarks>
    /// A format 1 memories file has no definitions, so its trophy icon counts are all 0. The file is
    /// not upgraded here.
    /// </remarks>
    public static class MemoryImageFetcher
    {
        /// <summary>The default size cap per image.</summary>
        public const long DefaultMaxBytes = 5 * 1024 * 1024;

        /// <summary>The only hosts an image is fetched from (exact host match).</summary>
        public static IReadOnlyList<string> AllowedHosts { get; } =
        [
            "image.api.playstation.com",
            "psnobj.prod.dl.playstation.net",
        ];

        /// <summary>True when the URL is absolute, HTTPS, default port and on the allowlist.</summary>
        public static bool IsAllowed(string url) =>
            Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps
            && uri.IsDefaultPort
            && string.IsNullOrEmpty(uri.UserInfo)
            && AllowedHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase);

        // Entry images: every entry with a source URL.
        private const string GameImageQuery =
            """
            SELECT e.ImageSourceUrl, e.ImageSha256, m.Extension
            FROM Entry e LEFT JOIN MediaFile m ON m.Sha256 = e.ImageSha256
            WHERE e.ImageSourceUrl IS NOT NULL
            ORDER BY e.ImageSourceUrl;
            """;

        private const string GameImageUpdate = "UPDATE Entry SET ImageSha256 = $sha WHERE ImageSourceUrl = $url;";

        // Trophy icons: only definitions some entry item uses. An empty URL means no icon.
        private const string TrophyIconQuery =
            """
            SELECT d.IconSourceUrl, d.IconSha256, m.Extension
            FROM ItemDefinition d LEFT JOIN MediaFile m ON m.Sha256 = d.IconSha256
            WHERE d.IconSourceUrl IS NOT NULL AND length(trim(d.IconSourceUrl)) > 0
              AND EXISTS (SELECT 1 FROM EntryItem i WHERE i.SetId = d.SetId AND i.ItemId = d.ItemId)
            ORDER BY d.IconSourceUrl;
            """;

        private const string TrophyIconUpdate =
            """
            UPDATE ItemDefinition SET IconSha256 = $sha
            WHERE IconSourceUrl = $url
              AND EXISTS (SELECT 1 FROM EntryItem i WHERE i.SetId = ItemDefinition.SetId AND i.ItemId = ItemDefinition.ItemId);
            """;

        /// <summary>Runs the fetch over every entry and used trophy definition in the memories file.</summary>
        public static MemoryImageFetchResult FetchMissing(string memoriesPath, IMemoryImageSource source, long maxBytes = DefaultMaxBytes)
        {
            ArgumentNullException.ThrowIfNull(source);

            if (maxBytes < 1)
            {
                throw new ArgumentException("The size cap must be positive.", nameof(maxBytes));
            }

            using var connection = MemoriesDatabase.OpenConnection(memoriesPath);
            var mediaFolder = MemoriesDatabase.MediaFolderFor(memoriesPath);
            var version = MemoriesDatabase.ReadFormatVersion(connection);

            var games = FetchKind(connection, mediaFolder, source, maxBytes, GameImageQuery, GameImageUpdate);
            var icons = version == MemoriesDatabase.MarkerFormatVersion
                ? FetchKind(connection, mediaFolder, source, maxBytes, TrophyIconQuery, TrophyIconUpdate)
                : new MemoryImageCounts(0, 0, 0);

            return new MemoryImageFetchResult(games, icons);
        }

        private static MemoryImageCounts FetchKind(
            SqliteConnection connection, string mediaFolder, IMemoryImageSource source, long maxBytes, string query, string update)
        {
            var urls = ReadUrls(connection, mediaFolder, query);
            int fetched = 0, present = 0, failed = 0;

            foreach (var (url, missing) in urls)
            {
                if (!missing)
                {
                    present++;
                    continue;
                }

                if (!IsAllowed(url))
                {
                    failed++;
                    continue;
                }

                byte[]? body;

                try
                {
                    body = source.Fetch(new Uri(url), maxBytes);
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    body = null;
                }

                if (body is null || body.Length == 0 || body.Length > maxBytes || DetectExtension(body) is not { } extension)
                {
                    failed++;
                    continue;
                }

                var sha = Convert.ToHexStringLower(SHA256.HashData(body));
                Store(mediaFolder, sha, extension, body);

                using var transaction = connection.BeginTransaction();
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    "INSERT OR IGNORE INTO MediaFile (Sha256, Extension, SizeBytes) VALUES ($sha, $ext, $size);\n" + update;
                command.Parameters.AddWithValue("$sha", sha);
                command.Parameters.AddWithValue("$ext", extension);
                command.Parameters.AddWithValue("$size", body.LongLength);
                command.Parameters.AddWithValue("$url", url);
                command.ExecuteNonQuery();
                transaction.Commit();

                fetched++;
            }

            return new MemoryImageCounts(fetched, present, failed);
        }

        /// <summary>
        /// Distinct URLs, and whether any row using one still needs its image: no hash recorded,
        /// or a recorded hash whose file is not in the media folder.
        /// </summary>
        private static List<(string Url, bool Missing)> ReadUrls(SqliteConnection connection, string mediaFolder, string query)
        {
            using var command = connection.CreateCommand();
            command.CommandText = query;

            var missingByUrl = new SortedDictionary<string, bool>(StringComparer.Ordinal);
            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                var url = reader.GetString(0);
                var missing = reader.IsDBNull(1)
                    || reader.IsDBNull(2)
                    || !File.Exists(Path.Combine(mediaFolder, $"{reader.GetString(1)}.{reader.GetString(2)}"));

                missingByUrl[url] = missingByUrl.GetValueOrDefault(url) || missing;
            }

            return missingByUrl.Select(pair => (pair.Key, pair.Value)).ToList();
        }

        /// <summary>
        /// Writes the image as <c>&lt;sha&gt;.&lt;ext&gt;</c>. An existing file is kept only when its
        /// own hash matches; otherwise it is replaced.
        /// </summary>
        private static void Store(string mediaFolder, string sha, string extension, byte[] body)
        {
            Directory.CreateDirectory(mediaFolder);
            var target = Path.Combine(mediaFolder, $"{sha}.{extension}");

            if (File.Exists(target))
            {
                using var existing = File.OpenRead(target);

                if (string.Equals(Convert.ToHexStringLower(SHA256.HashData(existing)), sha, StringComparison.Ordinal))
                {
                    return;
                }
            }

            var temporary = target + ".partial";
            File.WriteAllBytes(temporary, body);
            File.Move(temporary, target, overwrite: true);
        }

        /// <summary>The image type from its first bytes, or null when it is not one this file stores.</summary>
        internal static string? DetectExtension(ReadOnlySpan<byte> body)
        {
            if (body.Length >= 8 && body[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
            {
                return "png";
            }

            if (body.Length >= 3 && body[0] == 0xFF && body[1] == 0xD8 && body[2] == 0xFF)
            {
                return "jpg";
            }

            if (body.Length >= 6 && (body[..6].SequenceEqual("GIF87a"u8) || body[..6].SequenceEqual("GIF89a"u8)))
            {
                return "gif";
            }

            if (body.Length >= 12 && body[..4].SequenceEqual("RIFF"u8) && body[8..12].SequenceEqual("WEBP"u8))
            {
                return "webp";
            }

            return null;
        }
    }

    /// <summary>
    /// The real image source: HTTPS GET, no redirects followed, a timeout, and a read that stops one
    /// byte past the cap. Re-checks the allowlist itself.
    /// </summary>
    public sealed class HttpsMemoryImageSource : IMemoryImageSource, IDisposable
    {
        /// <summary>The deadline for one image: send, headers and the whole body.</summary>
        internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

        private readonly HttpClient _client = new(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = RequestTimeout,
        };

        /// <inheritdoc/>
        public byte[]? Fetch(Uri uri, long maxBytes)
        {
            ArgumentNullException.ThrowIfNull(uri);

            if (!MemoryImageFetcher.IsAllowed(uri.AbsoluteUri))
            {
                return null;
            }

            // One deadline covers the send and every read of the body.
            using var deadline = new CancellationTokenSource(RequestTimeout);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = _client.Send(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);

            if (!response.IsSuccessStatusCode
                || response.Content.Headers.ContentLength > maxBytes)
            {
                return null;
            }

            using var stream = response.Content.ReadAsStream(deadline.Token);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;

            while ((read = stream.ReadAsync(chunk, deadline.Token).AsTask().GetAwaiter().GetResult()) > 0)
            {
                buffer.Write(chunk, 0, read);

                if (buffer.Length > maxBytes)
                {
                    break;
                }
            }

            return buffer.ToArray();
        }

        /// <inheritdoc/>
        public void Dispose() => _client.Dispose();
    }
}
