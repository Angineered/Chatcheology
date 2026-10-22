using System.Globalization;
using Chatcheology.Archive;

namespace Chatcheology.Mobile.Import
{
    /// <summary>How an import ended.</summary>
    public enum ImportOutcome
    {
        /// <summary>Copied, checked and installed.</summary>
        Installed,

        /// <summary>An installed archive has the same identity; nothing was copied.</summary>
        AlreadyInstalled,

        /// <summary>Too little free space; nothing was copied.</summary>
        NotEnoughSpace,

        /// <summary>Refused; see <see cref="ImportResult.Refusal"/>. Nothing was kept.</summary>
        Refused,

        /// <summary>Cancelled while copying; the copy was deleted.</summary>
        Cancelled,
    }

    /// <summary>Why an import was refused.</summary>
    public enum ImportRefusal
    {
        /// <summary>Not refused.</summary>
        None,

        /// <summary>The folder has no <c>CONTENTS.sha256</c>, or it cannot be read.</summary>
        NoContentsList,

        /// <summary><c>CONTENTS.sha256</c> is not in its format, or names a path outside the folder.</summary>
        ContentsListUnusable,

        /// <summary>Files <c>CONTENTS.sha256</c> lists are not in the folder.</summary>
        ListedFilesMissing,

        /// <summary>The archive is of a newer format version than this app reads.</summary>
        NewerFormat,

        /// <summary>A listed file could not be read from the folder.</summary>
        SourceUnreadable,

        /// <summary>A file could not be written into the app's storage.</summary>
        CopyFailed,

        /// <summary>The copy did not pass the archive validator; see <see cref="ImportResult.FailedChecks"/>.</summary>
        FailedValidation,
    }

    /// <summary>One validator check that did not pass. A name and counts only.</summary>
    /// <param name="Check">The check.</param>
    /// <param name="Name">The check in plain words.</param>
    /// <param name="Ran">False when the archive could not be read far enough to run it.</param>
    /// <param name="Examined">How much it looked at.</param>
    /// <param name="Problems">How many problems it found.</param>
    public sealed record ImportCheck(ArchiveCheck Check, string Name, bool Ran, int Examined, int Problems)
    {
        /// <summary>The validator's check in plain words.</summary>
        public static string NameOf(ArchiveCheck check) => check switch
        {
            ArchiveCheck.Contents => "Files match the contents list",
            ArchiveCheck.Database => "Database",
            ArchiveCheck.Manifest => "Manifest",
            ArchiveCheck.Media => "Media files",
            ArchiveCheck.LinksAndBasis => "Links and confirmations",
            ArchiveCheck.SequenceAndSenders => "Message order and senders",
            _ => check.ToString(),
        };
    }

    /// <summary>
    /// The result of an import. Counts, sizes and fixed phrases only: never message text, a name, a file
    /// name or a path.
    /// </summary>
    public sealed class ImportResult
    {
        /// <summary>The message shown for an archive of a newer format version.</summary>
        public const string NewerFormatMessage = "This archive needs a newer version of the app";

        internal ImportResult(ImportOutcome outcome, ImportRefusal refusal = ImportRefusal.None)
        {
            Outcome = outcome;
            Refusal = refusal;
        }

        /// <summary>How the import ended.</summary>
        public ImportOutcome Outcome { get; }

        /// <summary>Why it was refused, when <see cref="Outcome"/> is <see cref="ImportOutcome.Refused"/>.</summary>
        public ImportRefusal Refusal { get; }

        /// <summary>The archive's identity, once its contents list was read.</summary>
        public string? ContentsSha256 { get; internal init; }

        /// <summary>The installed archive's id (its folder name), for Installed and AlreadyInstalled.</summary>
        public string? ArchiveId { get; internal init; }

        /// <summary>The bytes the import needs, the margin included; 0 before it was worked out.</summary>
        public long RequiredBytes { get; internal init; }

        /// <summary>The bytes free when the space was checked; 0 before.</summary>
        public long AvailableBytes { get; internal init; }

        /// <summary>How many listed files the folder lacks, for <see cref="ImportRefusal.ListedFilesMissing"/>.</summary>
        public int MissingFiles { get; internal init; }

        /// <summary>The checks that did not pass, for <see cref="ImportRefusal.FailedValidation"/>.</summary>
        public IReadOnlyList<ImportCheck> FailedChecks { get; internal init; } = [];

        /// <summary>How many checks the validator has in all.</summary>
        public int CheckCount { get; internal init; }

        /// <summary>What happened, in a sentence, for the import screen.</summary>
        public string Message => Outcome switch
        {
            ImportOutcome.Installed => "The archive was imported.",
            ImportOutcome.AlreadyInstalled => "This archive is already imported. Nothing was copied.",
            ImportOutcome.NotEnoughSpace =>
                $"Not enough free space. The archive needs {Bytes(RequiredBytes)}; {Bytes(AvailableBytes)} is free.",
            ImportOutcome.Cancelled => "The import was cancelled. Nothing was kept.",
            _ => Refusal switch
            {
                ImportRefusal.NoContentsList => "This folder has no readable CONTENTS.sha256, so it is not a complete archive.",
                ImportRefusal.ContentsListUnusable => "This folder's CONTENTS.sha256 is not a usable contents list.",
                ImportRefusal.ListedFilesMissing =>
                    $"{Count(MissingFiles, "file")} the archive lists {(MissingFiles == 1 ? "is" : "are")} not in the folder.",
                ImportRefusal.NewerFormat => NewerFormatMessage,
                ImportRefusal.SourceUnreadable => "A file in the folder could not be read. Nothing was kept.",
                ImportRefusal.CopyFailed => "A file could not be written to the app's storage. Nothing was kept.",
                ImportRefusal.FailedValidation =>
                    $"The copy did not pass its checks: {Count(FailedChecks.Count, "check")} of {CheckCount.ToString(CultureInfo.CurrentCulture)} failed. Nothing was kept.",
                _ => "The archive was not imported.",
            },
        };

        /// <summary>A size in bytes, as the import screen shows it.</summary>
        public static string Bytes(long bytes) => bytes switch
        {
            < 1024 * 1024 => $"{(bytes / 1024.0).ToString("0.#", CultureInfo.CurrentCulture)} KB",
            < 1024L * 1024 * 1024 => $"{(bytes / (1024.0 * 1024)).ToString("0.#", CultureInfo.CurrentCulture)} MB",
            _ => $"{(bytes / (1024.0 * 1024 * 1024)).ToString("0.##", CultureInfo.CurrentCulture)} GB",
        };

        private static string Count(int value, string noun) =>
            value == 1 ? $"1 {noun}" : $"{value.ToString("N0", CultureInfo.CurrentCulture)} {noun}s";
    }
}
