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

        /// <summary>Cancelled while copying, or at the adoption question; the copy was deleted.</summary>
        Cancelled,

        /// <summary>
        /// CH-71 step 2: copied, checked and installed in place of an installed archive (a refresh with the same archive
        /// id, or an adopted earlier copy), with all its settings carried over.
        /// </summary>
        Refreshed,
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

        /// <summary>
        /// CH-71 step 2: an installed archive has the same archive id, but this one does not contain every one of its
        /// messages unchanged (a changed, removed or reordered message or attachment, or an older copy).
        /// </summary>
        RefreshMismatch,

        /// <summary>CH-71 step 2: more than one installed archive has this archive id, so which to refresh is not clear.</summary>
        RefreshAmbiguous,

        /// <summary>CH-71 step 2: the installed archive with this archive id cannot be read, so the refresh cannot be checked.</summary>
        RefreshInstalledUnreadable,
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

        /// <summary>
        /// The installed copy's folder name under <c>archives/</c>, for Installed, Refreshed and AlreadyInstalled. Not the
        /// format's archive id (<see cref="ArchiveInfo.ArchiveId"/>).
        /// </summary>
        public string? InstallId { get; internal init; }

        /// <summary>For Refreshed: the folder name of the installed copy it replaced.</summary>
        public string? ReplacedInstallId { get; internal init; }

        /// <summary>For Refreshed: whether the replaced copy was removed (false only when its delete failed; it stays listed).</summary>
        public bool ReplacedRemoved { get; internal init; }

        /// <summary>For Refreshed: how many messages the new archive adds after the replaced one's.</summary>
        public int AddedMessages { get; internal init; }

        /// <summary>For Refreshed: true when it replaced an archive installed before version 4, after the owner said so.</summary>
        public bool Adopted { get; internal init; }

        /// <summary>
        /// For Installed (a version 4 archive): how many archives installed before version 4 were compared with it and did
        /// not match, so their stars were not moved (CH-71 step 2). 0 when there were none.
        /// </summary>
        public int UnmatchedOlderArchives { get; internal init; }

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
            ImportOutcome.Installed when UnmatchedOlderArchives > 0 =>
                $"The archive was imported as a new archive. It did not match {(UnmatchedOlderArchives == 1 ? "the older archive" : Count(UnmatchedOlderArchives, "older archive"))} " +
                "on this phone, so no stars were moved. Remove this copy before starring if that is wrong.",
            ImportOutcome.Installed => "The archive was imported.",
            ImportOutcome.AlreadyInstalled => "This archive is already imported. Nothing was copied.",
            ImportOutcome.NotEnoughSpace =>
                $"Not enough free space. The archive needs {Bytes(RequiredBytes)}; {Bytes(AvailableBytes)} is free.",
            ImportOutcome.Cancelled => "The import was cancelled. Nothing was kept.",
            ImportOutcome.Refreshed =>
                $"The archive was updated with {Count(AddedMessages, "new message")}. Its stars and settings were kept." +
                (ReplacedRemoved ? string.Empty : " The earlier copy could not be removed; remove it from the start screen."),
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
                ImportRefusal.RefreshMismatch =>
                    "This archive has the same id as one already imported, but it does not contain all of that archive's messages " +
                    "unchanged, so it cannot replace it. It may be an older copy. Nothing was changed.",
                ImportRefusal.RefreshAmbiguous =>
                    "More than one imported archive has this archive's id. Remove the extra copy first. Nothing was changed.",
                ImportRefusal.RefreshInstalledUnreadable =>
                    "The imported archive this one would update cannot be read, so the update cannot be checked. Nothing was changed.",
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
