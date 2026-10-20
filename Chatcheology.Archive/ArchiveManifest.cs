using System.Text.Json;

namespace Chatcheology.Archive
{
    /// <summary>
    /// <c>manifest.json</c>: the format, its version, what produced the archive, when, and how many
    /// rows of each kind it holds. Counts only: no title, name or path.
    /// </summary>
    /// <param name="Format">Always <see cref="FormatName"/>.</param>
    /// <param name="FormatVersion">The format version, the same as the database's.</param>
    /// <param name="ProducerVersion">What wrote the archive.</param>
    /// <param name="CreatedUtc">When, in UTC, to the second.</param>
    /// <param name="Counts">How many rows of each kind <c>archive.db</c> holds.</param>
    public sealed record ArchiveManifest(
        string Format, int FormatVersion, string ProducerVersion, DateTime CreatedUtc, ArchiveCounts Counts)
    {
        /// <summary>The format's name, as the manifest records it.</summary>
        public const string FormatName = "chatcheology-portable-archive";

        private static readonly JsonSerializerOptions Json = new()
        {
            WriteIndented = true,
            NewLine = "\n",
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };

        /// <summary>The manifest's bytes: indented UTF-8 JSON without a BOM, ending in a newline.</summary>
        public byte[] ToBytes() => [.. JsonSerializer.SerializeToUtf8Bytes(this, Json), (byte)'\n'];

        /// <summary>Reads a manifest back.</summary>
        /// <exception cref="ArchiveFormatException">Not a manifest of this format.</exception>
        public static ArchiveManifest Parse(byte[] bytes)
        {
            ArchiveManifest? manifest;

            try
            {
                manifest = JsonSerializer.Deserialize<ArchiveManifest>(bytes, Json);
            }
            catch (JsonException)
            {
                throw new ArchiveFormatException("manifest.json is not valid.");
            }

            return manifest is { Format: FormatName, Counts: not null }
                ? manifest
                : throw new ArchiveFormatException("manifest.json is not this format's manifest.");
        }
    }
}
