using System.Globalization;
using System.Text;
using System.Text.Json;
using Chatcheology.Archive;

namespace Chatcheology.Data.ArchiveExport
{
    /// <summary>
    /// The fixed archive id of one workspace (CH-71, archive format version 4): the small file
    /// <see cref="FileName"/> beside the workspace, the way <c>older-exports.db</c> and <c>memories.db</c> sit there.
    /// </summary>
    /// <remarks>
    /// <para><strong>Made once, then kept.</strong> The first version 4 export that passes every guard creates the
    /// file with a new random id; every later export of the same workspace reads it and writes the same id into the
    /// archive, so a phone can tell a refreshed archive from a different one (CH-71 D7). Nothing ever rewrites or
    /// deletes it. It is never in the workspace database: no schema change, and the workspace stays read-only for
    /// an export.</para>
    /// <para><strong>The file.</strong> UTF-8 JSON, exactly three members: <c>format</c> =
    /// <see cref="FormatName"/>, <c>conversationID</c> = the exported conversation, <c>archiveId</c> = a GUID in
    /// lower case with hyphens (<see cref="ArchiveFormat.IsArchiveId"/>). The conversation is recorded so the id of
    /// one conversation is never given to an archive of another.</para>
    /// <para><strong>Refused loudly, never repaired.</strong> A file that is not exactly that (unreadable, not
    /// JSON, a member missing, extra or of the wrong kind, an id not in the format's spelling) stops the export
    /// before anything is written: a new id would make the phone treat the refresh as a different archive and lose
    /// its stars and position. Restore the file from a backup instead. The messages are fixed phrases, never a path
    /// or the id.</para>
    /// <para>Creation writes a temporary file beside it, flushes it to disk and moves it into place without
    /// overwriting; if another run created the file meanwhile, that file is read instead.</para>
    /// </remarks>
    public static class ArchiveIdFile
    {
        /// <summary>The fixed file name, in the same folder as the main workspace.</summary>
        public const string FileName = "archive-id.json";

        /// <summary>The value of the file's <c>format</c> member.</summary>
        public const string FormatName = "chatcheology-archive-id";

        /// <summary>The largest file read; a real one is about a hundred bytes.</summary>
        private const int MaximumBytes = 4096;

        /// <summary>The file's path beside <paramref name="workspacePath"/>.</summary>
        public static string PathBeside(string workspacePath)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);

            var folder = Path.GetDirectoryName(Path.GetFullPath(workspacePath))
                ?? throw new ArgumentException("The workspace path has no containing folder.", nameof(workspacePath));

            return Path.Combine(folder, FileName);
        }

        /// <summary>
        /// The id recorded beside the workspace for <paramref name="conversationID"/>, or <see langword="null"/>
        /// when there is no file yet. Read-only.
        /// </summary>
        /// <exception cref="ArchiveExportRefusedException">The file is damaged or belongs to another conversation.</exception>
        public static string? Read(string workspacePath, long conversationID)
        {
            var path = PathBeside(workspacePath);

            return File.Exists(path) ? Parse(ReadBytes(path), conversationID) : null;
        }

        /// <summary>
        /// The id a comparison expects for <paramref name="conversationID"/>: the recorded one, or
        /// <see langword="null"/> when there is no file or the file is another conversation's (that conversation has
        /// no id recorded, so any archive id differs). Read-only.
        /// </summary>
        /// <exception cref="ArchiveExportRefusedException">The file is damaged.</exception>
        internal static string? ReadForComparison(string workspacePath, long conversationID)
        {
            var path = PathBeside(workspacePath);

            if (!File.Exists(path))
            {
                return null;
            }

            var (recorded, id) = ParseAny(ReadBytes(path));

            return recorded == conversationID ? id : null;
        }

        /// <summary>
        /// The id recorded beside the workspace, created (with a new random id) when there is no file yet.
        /// </summary>
        /// <exception cref="ArchiveExportRefusedException">
        /// The file is damaged, belongs to another conversation, or could not be created. Nothing else was written.
        /// </exception>
        public static string ReadOrCreate(string workspacePath, long conversationID)
        {
            if (Read(workspacePath, conversationID) is { } existing)
            {
                return existing;
            }

            var path = PathBeside(workspacePath);
            var id = ArchiveFormat.NewArchiveId();
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";

            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(Bytes(conversationID, id));
                    stream.Flush(flushToDisk: true);
                }

                File.Move(temporary, path, overwrite: false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                TryDelete(temporary);

                // Another run created the file in the meantime: its id is the one.
                if (File.Exists(path))
                {
                    return Parse(ReadBytes(path), conversationID);
                }

                throw new ArchiveExportRefusedException(
                    $"The archive id file ({FileName}) could not be created beside the workspace. Nothing was written.",
                    exception);
            }

            return id;
        }

        /// <summary>The file's bytes for <paramref name="conversationID"/> and <paramref name="archiveId"/>.</summary>
        internal static byte[] Bytes(long conversationID, string archiveId)
        {
            var text =
                "{\n" +
                $"  \"format\": \"{FormatName}\",\n" +
                $"  \"conversationID\": {conversationID.ToString(CultureInfo.InvariantCulture)},\n" +
                $"  \"archiveId\": \"{archiveId}\"\n" +
                "}\n";

            return new UTF8Encoding(false).GetBytes(text);
        }

        /// <summary>Parses the file strictly.</summary>
        /// <exception cref="ArchiveExportRefusedException">Not exactly this file's format, or another conversation's.</exception>
        internal static string Parse(byte[] bytes, long conversationID)
        {
            var (conversation, id) = ParseAny(bytes);

            if (conversation != conversationID)
            {
                throw new ArchiveExportRefusedException(
                    $"The archive id file ({FileName}) beside the workspace belongs to another conversation. An archive " +
                    "id is kept for one conversation only. Nothing was written.");
            }

            return id;
        }

        /// <summary>Parses the file strictly, whichever conversation it is for.</summary>
        /// <exception cref="ArchiveExportRefusedException">Not exactly this file's format.</exception>
        private static (long ConversationID, string ArchiveId) ParseAny(byte[] bytes)
        {
            string? format = null, id = null;
            long? conversation = null;

            try
            {
                using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { AllowTrailingCommas = false });

                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    throw Damaged();
                }

                var names = new HashSet<string>(StringComparer.Ordinal);

                foreach (var member in document.RootElement.EnumerateObject())
                {
                    if (!names.Add(member.Name))
                    {
                        throw Damaged();
                    }

                    switch (member.Name)
                    {
                        case "format" when member.Value.ValueKind == JsonValueKind.String:
                            format = member.Value.GetString();
                            break;

                        case "archiveId" when member.Value.ValueKind == JsonValueKind.String:
                            id = member.Value.GetString();
                            break;

                        case "conversationID" when member.Value.ValueKind == JsonValueKind.Number
                                                   && member.Value.TryGetInt64(out var value):
                            conversation = value;
                            break;

                        default:
                            throw Damaged();
                    }
                }
            }
            catch (Exception exception) when (exception is JsonException or ArgumentException)
            {
                // Not JSON, or not valid UTF-8.
                throw Damaged();
            }

            if (format != FormatName || conversation is not { } recorded || !ArchiveFormat.IsArchiveId(id))
            {
                throw Damaged();
            }

            return (recorded, id!);
        }

        private static byte[] ReadBytes(string path)
        {
            try
            {
                var info = new FileInfo(path);

                if (info.Length > MaximumBytes)
                {
                    throw Damaged();
                }

                return File.ReadAllBytes(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new ArchiveExportRefusedException(
                    $"The archive id file ({FileName}) beside the workspace could not be read. Nothing was written.",
                    exception);
            }
        }

        private static ArchiveExportRefusedException Damaged() =>
            new(
                $"The archive id file ({FileName}) beside the workspace is damaged. It is never replaced, because a new " +
                "id would make the phone treat this archive as a different one; restore it from a backup. Nothing was " +
                "written.");

        private static void TryDelete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A leftover temporary file names no data; it is harmless.
            }
        }
    }
}
