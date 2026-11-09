using System.IO;
using System.Security.Cryptography;
using System.Text;
using Chatcheology.Archive;
using Chatcheology.Data.ArchiveExport;
using Chatcheology.Data.NewerMessages;
using Chatcheology.Desktop.Services;
using Chatcheology.Mobile.Import;
using Chatcheology.Mobile.Library;
using Chatcheology.Mobile.Settings;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Desktop.Tests
{
    /// <summary>
    /// CH-71 step 3b end to end, with the real exporter, validator, comparer and phone importer on a synthetic workspace:
    /// export without newer messages, install on the "phone", star and read; add newer messages beside the workspace;
    /// export again; the phone's refresh digest over the earlier part is unchanged, and the import is a refresh that
    /// keeps the stars and the position. Invented text, ids and bytes only.
    /// </summary>
    public sealed class ArchiveNewerMessagesRefreshTests : IDisposable
    {
        private static readonly DateOnly LastWorkspaceDay = new(2026, 4, 6);
        private static readonly DateTime RunTime = new(2026, 11, 9, 8, 0, 0, DateTimeKind.Utc);
        private static readonly byte[] NewerPhoto = Encoding.UTF8.GetBytes("invented newer photo payload");

        private readonly ArchiveTestWorkspace _workspace = new();
        private readonly string _phoneRoot;
        private readonly ArchiveLibrary _library;
        private readonly ArchiveSettings _settings = new(new MemoryStore());

        public ArchiveNewerMessagesRefreshTests()
        {
            _phoneRoot = Path.Combine(_workspace.Root, "phone-copy", "WhatsApp");
            Directory.CreateDirectory(Path.Combine(_phoneRoot, "Media"));
            _library = new ArchiveLibrary(Path.Combine(_workspace.Root, "phone-files"));
        }

        public void Dispose() => _workspace.Dispose();

        private string Export(string name)
        {
            var output = Path.Combine(_workspace.ExportsFolder, name);

            new ArchiveExporter().Export(new ArchiveExportRequest
            {
                WorkspacePath = _workspace.DatabasePath,
                ConversationID = 1,
                LocalParticipantID = 1,
                OutputPath = output,
                ExpectResolved = 1,
                ExpectMissingReviewed = 1,
            });

            return output;
        }

        private ImportResult Import(string folder) =>
            new ArchiveImporter(_library, new PlentyOfSpace(), _settings).Import(new Folder(folder), null, default);

        private void AddNewerMessages()
        {
            File.WriteAllBytes(Path.Combine(_phoneRoot, "Media", "IMG-NEWER-0001.jpg"), NewerPhoto);

            NewerMessagesWriter.Append(
                NewerMessagesDatabase.PathBeside(_workspace.DatabasePath),
                new NewerMessagesInfo(1, 1, 2, 7, LastWorkspaceDay, RunTime),
                new NewerRefreshRun
                {
                    RunDateTimeUtc = RunTime,
                    ManifestSha256 = new string('1', 64),
                    MsgstoreSizeBytes = 4096,
                    MsgstoreUserVersion = 1,
                    MsgstoreSchemaSha256 = new string('c', 64),
                    HandOff = NewerMessagesDatabase.HandOffWorkspace,
                    HandOffKeyID = "K0",
                    HandOffFromMe = false,
                    MediaRootPath = _phoneRoot,
                },
                [
                    Row("K1", 1, fromMe: false, text: ArchiveTestWorkspace.TextMarker + " newer"),
                    Row("K2", 2, fromMe: true, caption: "Invented newer caption", attachment: new NewerAttachmentRow
                    {
                        FileName = "IMG-NEWER-0001.jpg",
                        RelativePath = "Media/IMG-NEWER-0001.jpg",
                        State = NewerMessagesDatabase.StateRestored,
                        Sha256 = Convert.ToHexString(SHA256.HashData(NewerPhoto)).ToLowerInvariant(),
                    }),
                    Row("K3", 3, fromMe: false, kind: NewerMessagesDatabase.KindPlaceholder, text: "Location (not shown in this archive)"),
                ]);

            SqliteConnection.ClearAllPools();
        }

        private static NewerMessageRow Row(
            string key, int minute, bool fromMe, string kind = NewerMessagesDatabase.KindUser, string? text = null,
            string? caption = null, NewerAttachmentRow? attachment = null)
        {
            var utc = new DateTimeOffset(LastWorkspaceDay.AddDays(3).ToDateTime(new TimeOnly(18, minute)), TimeSpan.FromHours(2))
                .ToUnixTimeMilliseconds();

            return new NewerMessageRow
            {
                KeyID = key,
                MsgstoreRowID = minute,
                SortID = minute,
                FromMe = fromMe,
                SenderParticipantID = fromMe ? 1 : 2,
                TimestampUtcMs = utc,
                DateTimeLocal = NewerMessagesTimeZone.Format(NewerMessagesTimeZone.ToLocal(utc)!.Value),
                MessageType = attachment is null ? 0 : 1,
                Kind = kind,
                PlaceholderKind = kind == NewerMessagesDatabase.KindPlaceholder ? "Location" : null,
                Text = text,
                Caption = caption,
                Attachment = attachment,
            };
        }

        [Fact]
        public void A_refresh_that_only_adds_newer_messages_is_accepted_and_keeps_stars_and_position()
        {
            // 1. Export without newer messages and install it.
            var first = Export("first");
            var installed = Import(first);
            Assert.Equal(ImportOutcome.Installed, installed.Outcome);

            var hash = installed.ContentsSha256!;
            _settings.SetStarredMessages(hash, [1, 5]);
            _settings.SetStarredAttachments(hash, [new StarredAttachment(2, _workspace.RestoredAttachmentID)]);
            _settings.SetPosition(hash, 4);

            // 2. Newer messages arrive beside the workspace; export again.
            AddNewerMessages();
            var second = Export("second");

            Assert.True(ArchiveValidator.Validate(second).Passed);

            var check = new ArchiveOperations().Check(second, new ArchiveComparisonTarget(_workspace.DatabasePath, 1, "Ada and Bo"));
            Assert.True(check.Comparison?.Passed, string.Join(", ", check.Comparison?.FirstMismatches ?? []));
            Assert.Equal(3, check.Counts?.MsgstoreMessages);
            Assert.Equal(3, check.Comparison?.MsgstoreMessages);

            // 3. The phone's own digest over the earlier part is unchanged, and the newer messages follow it.
            MessageDigest before;
            (MessageDigest Whole, MessageDigest? Through) after;

            using (var one = ArchiveReader.Open(first))
            using (var two = ArchiveReader.Open(second))
            {
                before = MessageDigest.Of(one);
                after = MessageDigest.Compute(two, before.LastSequenceNumber);

                Assert.Equal(one.Info.ArchiveId, two.Info.ArchiveId);

                var newer = two.ReadAllPages().SelectMany(p => p.Messages).Skip(5).ToList();
                Assert.Equal([6, 7, 8], newer.Select(m => m.SequenceNumber));
                Assert.All(newer, m => Assert.Equal(ArchiveMessageSource.Msgstore, m.Source));
            }

            Assert.True(MessageDigest.Continues(before, after.Through));
            Assert.Equal(8, after.Whole.Count);

            // 4. Imported on the phone: a refresh, stars and position kept under the new archive.
            var refreshed = Import(second);

            Assert.Equal(ImportOutcome.Refreshed, refreshed.Outcome);
            Assert.Equal(3, refreshed.AddedMessages);
            Assert.Equal(installed.InstallId, refreshed.ReplacedInstallId);

            var newHash = refreshed.ContentsSha256!;
            Assert.NotEqual(hash, newHash);
            Assert.Equal([1, 5], _settings.GetStarredMessages(newHash));
            Assert.Equal([new StarredAttachment(2, _workspace.RestoredAttachmentID)], _settings.GetStarredAttachments(newHash));
            Assert.Equal(4, _settings.GetPosition(newHash));
        }

        /// <summary>A settings store in memory.</summary>
        private sealed class MemoryStore : ISettingsStore
        {
            private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

            public string? Get(string key) => _values.TryGetValue(key, out var value) ? value : null;

            public void Set(string key, string? value)
            {
                if (value is null)
                {
                    _values.Remove(key);
                }
                else
                {
                    _values[key] = value;
                }
            }

            public bool Flush() => true;
        }

        private sealed class PlentyOfSpace : IStorageSpace
        {
            public long AvailableBytes() => long.MaxValue;
        }

        /// <summary>An import source over a local folder, read only.</summary>
        private sealed class Folder(string folder) : IImportSource
        {
            public IReadOnlyList<ImportSourceFile> ListFiles(CancellationToken cancellationToken) =>
                Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                    .Select(path => new ImportSourceFile(
                        Path.GetRelativePath(folder, path).Replace(Path.DirectorySeparatorChar, '/'),
                        new FileInfo(path).Length))
                    .ToList();

            public Stream OpenRead(string relativePath) =>
                new FileStream(
                    Path.Combine(folder, relativePath.Replace('/', Path.DirectorySeparatorChar)),
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read);
        }
    }
}
