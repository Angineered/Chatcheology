using System.Text;

namespace Chatcheology.Archive
{
    /// <summary>
    /// <c>CONTENTS.sha256</c>: one line per file in <c>sha256sum</c> format (<c>&lt;hex&gt;  &lt;relative
    /// path&gt;</c>, forward slashes), every file in the folder except itself, in ordinal path order.
    /// UTF-8 without a BOM, LF line endings, so <c>sha256sum -c CONTENTS.sha256</c> works on any
    /// machine.
    /// </summary>
    /// <remarks>
    /// The same file format as the CH-14 input backup, so one habit checks both.
    /// </remarks>
    public static class ArchiveContents
    {
        /// <summary>The file's name in the archive folder.</summary>
        public const string FileName = ArchiveFormat.ContentsFileName;

        /// <summary>Every file beneath <paramref name="root"/> as a forward-slash relative path, ordinal order.</summary>
        public static IReadOnlyList<string> RelativeFiles(string root) =>
            Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/'))
                .Order(StringComparer.Ordinal)
                .ToList();

        /// <summary>A listed relative path, spelled for this machine beneath <paramref name="root"/>.</summary>
        public static string FullPath(string root, string relative) =>
            Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));

        /// <summary>Hashes every file but this one, as it now sits on disk, and returns the file's bytes.</summary>
        public static byte[] Build(string root)
        {
            var text = new StringBuilder();

            foreach (var relative in RelativeFiles(root).Where(r => r != FileName))
            {
                text.Append(ArchiveHashing.Sha256OfFile(FullPath(root, relative))).Append("  ").Append(relative).Append('\n');
            }

            return new UTF8Encoding(false).GetBytes(text.ToString());
        }

        /// <summary>Parses the file strictly.</summary>
        /// <exception cref="FormatException">
        /// Not a file in this format, or a line names a path that could reach outside the folder.
        /// </exception>
        public static IReadOnlyList<(string Sha256, string Path)> Parse(byte[] bytes)
        {
            ArgumentNullException.ThrowIfNull(bytes);

            string content;

            try
            {
                content = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                throw new FormatException("CONTENTS.sha256 is not valid UTF-8.");
            }

            if (content.Length == 0 || content[^1] != '\n' || content.Contains('\r'))
            {
                throw new FormatException("CONTENTS.sha256 is truncated or has CR characters.");
            }

            var rows = new List<(string, string)>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var line in content[..^1].Split('\n'))
            {
                if (line.Length < 67 || line[64] != ' ' || line[65] != ' ' || !line[..64].All(Uri.IsHexDigit))
                {
                    throw new FormatException("A CONTENTS.sha256 line is malformed.");
                }

                var path = line[66..];

                if (path.StartsWith('/') || path.Contains('\\') || path.Contains(':')
                    || path.Split('/').Any(part => part is "" or "." or ".."))
                {
                    throw new FormatException("A CONTENTS.sha256 line names an unusable path.");
                }

                if (!seen.Add(path))
                {
                    throw new FormatException("CONTENTS.sha256 names a file twice.");
                }

                rows.Add((line[..64].ToLowerInvariant(), path));
            }

            return rows;
        }

        /// <summary>
        /// Re-hashes every listed file and looks for files the list leaves out. Read-only.
        /// </summary>
        /// <exception cref="ArgumentException">There is no folder at the path.</exception>
        public static ArchiveContentsCheck Check(string archiveFolder)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(archiveFolder);

            var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(archiveFolder));

            if (!Directory.Exists(folder))
            {
                throw new ArgumentException("There is no folder at the archive folder path.", nameof(archiveFolder));
            }

            var result = new ArchiveContentsCheck();
            var contentsPath = Path.Combine(folder, FileName);

            if (!File.Exists(contentsPath))
            {
                result.Problems.Add("CONTENTS.sha256 is missing: this is not a complete archive.");

                return result;
            }

            IReadOnlyList<(string Sha256, string Path)> listed;

            try
            {
                listed = Parse(File.ReadAllBytes(contentsPath));
            }
            catch (FormatException exception)
            {
                result.Problems.Add(exception.Message);

                return result;
            }

            result.Listed = listed.Count;

            foreach (var (expected, relative) in listed)
            {
                var path = FullPath(folder, relative);

                // Belt and braces after the parser's own path checks: never read outside the folder.
                if (!ArchivePaths.IsWithin(path, folder) || ArchivePaths.IsWithin(folder, path))
                {
                    result.Problems.Add("CONTENTS.sha256 names a path outside the folder.");

                    return result;
                }

                if (!File.Exists(path))
                {
                    result.Missing++;
                    continue;
                }

                if (ArchiveHashing.Sha256OfFile(path) == expected)
                {
                    result.Matching++;
                }
                else
                {
                    result.Changed++;
                }
            }

            var listedPaths = listed.Select(l => l.Path).ToHashSet(StringComparer.Ordinal);

            result.NotListed = RelativeFiles(folder).Count(r => r != FileName && !listedPaths.Contains(r));

            return result;
        }
    }

    /// <summary>The outcome of <see cref="ArchiveContents.Check"/>: counts, and fixed phrases only.</summary>
    public sealed class ArchiveContentsCheck
    {
        /// <summary>Files <c>CONTENTS.sha256</c> lists.</summary>
        public int Listed { get; internal set; }

        /// <summary>Listed files whose bytes still hash as listed.</summary>
        public int Matching { get; internal set; }

        /// <summary>Listed files whose bytes differ.</summary>
        public int Changed { get; internal set; }

        /// <summary>Listed files that are not there.</summary>
        public int Missing { get; internal set; }

        /// <summary>Files in the folder the list leaves out.</summary>
        public int NotListed { get; internal set; }

        /// <summary>Other problems. Fixed phrases only: never a path or a name.</summary>
        public List<string> Problems { get; } = [];

        /// <summary>Whether every file is listed and every listed file matches.</summary>
        public bool Ok => Problems.Count == 0 && Changed == 0 && Missing == 0 && NotListed == 0 && Listed > 0;
    }
}
