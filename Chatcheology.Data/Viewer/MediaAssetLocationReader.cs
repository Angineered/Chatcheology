using System.Globalization;
using System.Text;
using Chatcheology.Data.Media;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Data.Viewer
{
    /// <summary>
    /// Reads the physical copies the workspace records for already-resolved media assets.
    /// </summary>
    /// <remarks>
    /// <para><strong>Presentation only.</strong> It answers one question — where are the recorded
    /// copies of this asset — so an image can be shown or a file handed to the operating system. It
    /// is not a matching API, it holds no evidence vocabulary, and it writes nothing.</para>
    /// <para><strong>It does not choose a copy.</strong> There is no preferred, best, selected or
    /// ranked copy here, and no member is named for one. The reader returns every recorded copy in
    /// a deterministic order and stops; the caller checks what is currently reachable and takes the
    /// first that is. Keeping the choosing outside this assembly is what makes "the chosen copy is
    /// not a decision" structural rather than a promise.</para>
    /// <para><strong>The order is insertion order, and carries no claim.</strong>
    /// <c>MediaAssetID, MediaSourceID, MediaFileID</c> — three surrogate keys SQLite assigned when
    /// the rows were written. It reads no dated fact, no direction fact and no naming fact, which
    /// is to say none of the columns Phase 6 evidence is built from. A copy discovered first is not
    /// a copy that is more correct.</para>
    /// <para><strong>Safe for any number of assets.</strong> Identifiers are bound as parameters in
    /// bounded chunks, so an ordinary caller may hand over a whole page's worth without knowing
    /// anything about SQLite's variable limit. The combined result is ordered as a whole, not
    /// merely within a chunk.</para>
    /// </remarks>
    public sealed class MediaAssetLocationReader
    {
        /// <summary>
        /// How many identifiers one statement binds.
        /// </summary>
        /// <remarks>
        /// An implementation bound on parameters per statement, chosen well inside every SQLite
        /// variable limit this package has shipped with. It is not derived from any archive and
        /// changes nothing a caller sees except how many statements are issued.
        /// </remarks>
        internal const int ChunkSize = 500;

        /// <summary>
        /// Every recorded physical copy of each requested asset, in deterministic order.
        /// </summary>
        /// <remarks>
        /// An empty request returns an empty list. Unlike a positional batch read there is no
        /// per-request answer to line up, so nothing is hidden by returning nothing.
        /// <para>
        /// An asset with no recorded copy contributes no rows, and an identifier naming no asset
        /// contributes none either. Both are ordinary states of a workspace whose media inventory
        /// is a separate concern from its resolutions, not caller mistakes.
        /// </para>
        /// </remarks>
        /// <exception cref="ArgumentException">The same asset was requested more than once.</exception>
        public IReadOnlyList<MediaAssetLocation> Read(
            SqliteConnection connection, IReadOnlyList<long> mediaAssetIDs)
        {
            ArgumentNullException.ThrowIfNull(connection);
            ArgumentNullException.ThrowIfNull(mediaAssetIDs);

            if (mediaAssetIDs.Distinct().Count() != mediaAssetIDs.Count)
            {
                throw new ArgumentException(
                    "The same media asset was requested more than once. Every recorded copy would " +
                    "then be returned twice, which reads as a workspace holding more copies than " +
                    "it does.",
                    nameof(mediaAssetIDs));
            }

            if (mediaAssetIDs.Count == 0)
            {
                return [];
            }

            var locations = new List<MediaAssetLocation>();

            for (var offset = 0; offset < mediaAssetIDs.Count; offset += ChunkSize)
            {
                var chunk = mediaAssetIDs
                    .Skip(offset)
                    .Take(ChunkSize)
                    .ToList();

                ReadChunk(connection, chunk, locations);
            }

            // Ordered as a whole rather than relying on the order the chunks happened to be
            // requested in, so the same set of assets always produces the same sequence however
            // the caller assembled its list.
            locations.Sort(CompareLocations);

            return locations;
        }

        private static void ReadChunk(
            SqliteConnection connection,
            IReadOnlyList<long> mediaAssetIDs,
            List<MediaAssetLocation> locations)
        {
            using var command = BuildCommand(connection, mediaAssetIDs);
            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                var rootPath = reader.GetString(4);
                var relativePath = reader.GetString(5);

                locations.Add(new MediaAssetLocation
                {
                    MediaAssetID = reader.GetInt64(0),
                    MediaFileID = reader.GetInt64(1),
                    MediaSourceID = reader.GetInt64(2),
                    MediaSourceDisplayName = reader.GetString(3),
                    RootPath = rootPath,
                    RelativePath = relativePath,
                    FileName = reader.GetString(6),
                    Extension = reader.IsDBNull(7) ? null : reader.GetString(7),
                    MediaType = MediaTypeText.Parse(reader.GetString(8)),
                    SizeBytes = reader.GetInt64(9),
                    FullPath = ComposeFullPath(rootPath, relativePath),
                });
            }
        }

        /// <summary>
        /// Turns the stored root and the stored relative path into a path this machine can open.
        /// </summary>
        /// <remarks>
        /// The stored relative form uses <c>/</c> as its separator whatever platform wrote it, so
        /// the separator is translated rather than assumed to match. Nothing else about either
        /// value is altered: the composed path is what the workspace recorded, spelled for this
        /// operating system.
        /// </remarks>
        private static string ComposeFullPath(string rootPath, string relativePath) =>
            Path.Combine(
                rootPath,
                relativePath.Replace(StoredPathSeparator, Path.DirectorySeparatorChar));

        private static int CompareLocations(MediaAssetLocation left, MediaAssetLocation right)
        {
            var byAsset = left.MediaAssetID.CompareTo(right.MediaAssetID);

            if (byAsset != 0)
            {
                return byAsset;
            }

            var bySource = left.MediaSourceID.CompareTo(right.MediaSourceID);

            return bySource != 0
                ? bySource
                : left.MediaFileID.CompareTo(right.MediaFileID);
        }

        private static SqliteCommand BuildCommand(
            SqliteConnection connection, IReadOnlyList<long> mediaAssetIDs)
        {
            var placeholders = new StringBuilder();

            for (var index = 0; index < mediaAssetIDs.Count; index++)
            {
                if (index > 0)
                {
                    placeholders.Append(", ");
                }

                placeholders.Append("$asset").Append(index.ToString(CultureInfo.InvariantCulture));
            }

            var command = connection.CreateCommand();

            command.CommandText =
                LocationsStatement.Replace(AssetIDPlaceholders, placeholders.ToString());

            for (var index = 0; index < mediaAssetIDs.Count; index++)
            {
                command.Parameters.AddWithValue(
                    $"$asset{index.ToString(CultureInfo.InvariantCulture)}",
                    mediaAssetIDs[index]);
            }

            return command;
        }

        /// <summary>The separator the workspace stores a relative media path with.</summary>
        private const char StoredPathSeparator = '/';

        /// <summary>The token the identifier list is substituted into.</summary>
        private const string AssetIDPlaceholders = "$mediaAssetIDs";

        /// <remarks>
        /// Driven from <c>MediaAssetFile</c> inward. A physical file that has been discovered but
        /// not yet hashed has no link row and therefore no known content identity, so starting from
        /// <c>MediaFile</c> would have to decide what such a file is a copy of — which is a
        /// question this reader has no business answering.
        /// <para>
        /// <c>MediaAsset.MediaType</c> rather than <c>MediaFile.MediaType</c>: the asset is the
        /// authority on what the payload is.
        /// </para>
        /// </remarks>
        private const string LocationsStatement =
            """
            SELECT
                l.MediaAssetID,
                f.MediaFileID,
                f.MediaSourceID,
                s.DisplayName,
                s.RootPath,
                f.RelativePath,
                f.FileName,
                f.Extension,
                a.MediaType,
                f.SizeBytes
            FROM MediaAssetFile AS l
            INNER JOIN MediaFile   AS f ON f.MediaFileID   = l.MediaFileID
            INNER JOIN MediaSource AS s ON s.MediaSourceID = f.MediaSourceID
            INNER JOIN MediaAsset  AS a ON a.MediaAssetID  = l.MediaAssetID
            WHERE l.MediaAssetID IN ($mediaAssetIDs)
            ORDER BY l.MediaAssetID, f.MediaSourceID, f.MediaFileID;
            """;
    }
}
