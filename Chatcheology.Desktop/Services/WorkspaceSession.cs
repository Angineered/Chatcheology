using System.IO;
using Chatcheology.Data.MsgstoreFacts;
using Chatcheology.Data.Phase7;
using Chatcheology.Data.Viewer;
using Chatcheology.Data.Workspace;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Desktop.Services
{
    /// <summary>
    /// Which page of a conversation to read.
    /// </summary>
    public enum PageDirection
    {
        /// <summary>The first page of the conversation.</summary>
        First,

        /// <summary>The last page of the conversation.</summary>
        Last,

        /// <summary>The page beginning at a sequence number.</summary>
        From,

        /// <summary>The page immediately before a sequence number.</summary>
        Before,
    }

    /// <summary>
    /// One page of a conversation with everything the viewer needs to draw it.
    /// </summary>
    /// <remarks>
    /// Assembled from independent readers and one round of file-system probing, all of it away
    /// from the dispatcher, so what reaches the UI thread is finished. <see cref="Facts"/> is the
    /// page's attachment msgstore facts (CH-8, CH-10) and <see cref="MessageFacts"/> its text
    /// messages' msgstore facts (CH-22): presentation only, never a Phase 7 state.
    /// </remarks>
    public sealed record LoadedPage(
        ConversationPage Page,
        IReadOnlyDictionary<long, Phase7ReconstructionState> States,
        IReadOnlyDictionary<long, MediaAvailability> Media,
        MsgstoreFactSet Facts,
        IReadOnlyDictionary<long, MessageMsgstoreFact> MessageFacts);

    /// <summary>
    /// What the viewer found when it looked for an asset's recorded copies.
    /// </summary>
    /// <param name="RecordedCopyCount">How many copies the workspace records.</param>
    /// <param name="Available">
    /// The first recorded copy that is currently reachable, in the location reader's own
    /// deterministic order, or <see langword="null"/> when none is.
    /// </param>
    /// <remarks>
    /// <strong>Taking the first reachable copy is not a decision.</strong> It selects nothing,
    /// ranks nothing and is recorded nowhere; a different copy on a later page load would display
    /// the same asset just as correctly.
    /// </remarks>
    public sealed record MediaAvailability(int RecordedCopyCount, MediaAssetLocation? Available);

    /// <summary>
    /// One attachment's review state, with what of its candidates' media is reachable.
    /// </summary>
    /// <remarks>
    /// Candidate availability is probed by the same root-first path the conversation's own media
    /// uses, so a detached recovery drive costs one probe per source rather than one per candidate.
    /// </remarks>
    public sealed record LoadedReview(
        AttachmentReviewState State,
        IReadOnlyDictionary<long, MediaAvailability> Media);

    /// <summary>
    /// One open workspace, read-only, and everything read from it.
    /// </summary>
    /// <remarks>
    /// <para><strong>It cannot write.</strong> The connection is opened through
    /// <see cref="WorkspaceDatabase.OpenReadOnlyConnection"/>, whose guarantee is SQLite's own
    /// <c>Mode=ReadOnly</c> rather than a promise this class makes. It never calls
    /// <see cref="WorkspaceDatabase.Initialise"/>: a workspace that is not the current schema
    /// version is refused, because creating or migrating one is not something a viewer should do as
    /// a side effect of being pointed at a file.</para>
    /// <para><strong>One connection, one reader of each kind, one at a time.</strong> A
    /// <see cref="SqliteConnection"/> is not safe for concurrent use and
    /// <see cref="ConversationReconstructionReader"/> holds a reader with mutable internal state,
    /// so every read passes through one gate. Reads run on the thread pool, never on the
    /// dispatcher.</para>
    /// <para><strong>It lets go of the file.</strong> Microsoft.Data.Sqlite pools connections by
    /// connection string, so disposing the connection is not enough on its own; the pool is cleared
    /// too, and the workspace is not left held open by this process.</para>
    /// </remarks>
    internal sealed class WorkspaceSession : IDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly ConversationReader _conversations = new();
        private readonly ConversationReconstructionReader _reconstruction = new();
        private readonly MediaAssetLocationReader _locations = new();
        private readonly AttachmentReviewReader _review = new();
        private readonly MsgstoreFactReader _facts = new();
        private readonly MessageMsgstoreFactReader _messageFacts = new();

        private WorkspaceSession(SqliteConnection connection, string databasePath)
        {
            _connection = connection;
            DatabasePath = databasePath;
        }

        /// <summary>The workspace file this session is reading.</summary>
        internal string DatabasePath { get; }

        /// <summary>
        /// Opens an existing current workspace read-only.
        /// </summary>
        /// <remarks>
        /// Every refusal leaves the file exactly as it was found. Nothing is created, nothing is
        /// migrated, and the diagnostics distinguish the cases an operator can act on — a path that
        /// holds nothing, a file that is not a workspace, and a workspace this build is not the
        /// right version for.
        /// </remarks>
        /// <exception cref="InvalidOperationException">
        /// The file is not a workspace at the current schema version.
        /// </exception>
        internal static WorkspaceSession Open(string databasePath)
        {
            var connection = WorkspaceDatabase.OpenReadOnlyConnection(databasePath);

            try
            {
                var version = WorkspaceDatabase.ReadSchemaVersion(connection);

                if (version != WorkspaceDatabase.SchemaVersion)
                {
                    throw new InvalidOperationException(Refusal(version));
                }

                return new WorkspaceSession(connection, databasePath);
            }
            catch
            {
                connection.Dispose();
                SqliteConnection.ClearAllPools();

                throw;
            }
        }

        private static string Refusal(int version) => version switch
        {
            0 =>
                "That file is not a Chatcheology workspace. Nothing has been created or changed.",

            < WorkspaceDatabase.SchemaVersion =>
                $"That workspace is schema version {version} and this viewer reads version " +
                $"{WorkspaceDatabase.SchemaVersion}. The viewer does not migrate a workspace, so " +
                $"it has been left exactly as it was.",

            _ =>
                $"That workspace is schema version {version}, which is newer than the version " +
                $"{WorkspaceDatabase.SchemaVersion} this build reads. It has been left unchanged.",
        };

        /// <summary>Every conversation in the workspace.</summary>
        internal Task<IReadOnlyList<ConversationSummary>> ReadConversationsAsync(
            CancellationToken cancellationToken) =>
            ReadAsync(connection => _conversations.ReadConversations(connection), cancellationToken);

        /// <summary>
        /// Where in the conversation the first message on or after a date sits, or
        /// <see langword="null"/> when none does.
        /// </summary>
        internal Task<int?> FindFirstSequenceNumberOnOrAfterAsync(
            long conversationID, DateOnly localDate, CancellationToken cancellationToken) =>
            ReadAsync(
                connection => _conversations.FindFirstSequenceNumberOnOrAfter(
                    connection, conversationID, localDate),
                cancellationToken);

        /// <summary>
        /// One page, its attachments' reconstruction states, and what of their media is reachable.
        /// </summary>
        internal Task<LoadedPage> LoadPageAsync(
            long conversationID,
            PageDirection direction,
            int sequenceNumber,
            int pageSize,
            CancellationToken cancellationToken) =>
            ReadAsync(
                connection =>
                {
                    var page = ReadPage(
                        connection, conversationID, direction, sequenceNumber, pageSize);

                    var attachmentIDs = page.Messages
                        .SelectMany(message => message.Attachments)
                        .Select(attachment => attachment.AttachmentID)
                        .ToList();

                    cancellationToken.ThrowIfCancellationRequested();

                    // Deliberately not called for a page that carries no attachment: the batch read
                    // refuses an empty list, because "nothing was asked about" and "none of them
                    // has anything" are different statements.
                    var states = attachmentIDs.Count == 0
                        ? new Dictionary<long, Phase7ReconstructionState>()
                        : _reconstruction.Read(connection, attachmentIDs);

                    cancellationToken.ThrowIfCancellationRequested();

                    var assetIDs = states.Values
                        .Where(state => state.ResolvedAsset is not null)
                        .Select(state => state.ResolvedAsset!.MediaAssetID)
                        .Distinct()
                        .ToList();

                    var media = ReadMedia(connection, assetIDs);

                    cancellationToken.ThrowIfCancellationRequested();

                    var facts = attachmentIDs.Count == 0
                        ? MsgstoreFactSet.Empty
                        : _facts.Read(connection, attachmentIDs);

                    cancellationToken.ThrowIfCancellationRequested();

                    var messageFacts = _messageFacts.ReadForPage(connection, page);

                    return new LoadedPage(page, states, media, facts, messageFacts);
                },
                cancellationToken);

        /// <summary>
        /// Whether the workspace records any low-quality copy, which is what decides whether the
        /// viewer offers to show them at all.
        /// </summary>
        internal Task<bool> HasAnyLowQualityCopyAsync(CancellationToken cancellationToken) =>
            ReadAsync(connection => _facts.HasAnyLowQualityCopy(connection), cancellationToken);

        /// <summary>
        /// One attachment's review state, and what of its candidates' media is reachable.
        /// </summary>
        /// <remarks>
        /// The same read path and the same gate as every other read. It writes nothing: the review
        /// panel reads what Phase 7 already recorded, and every decision leaves through a Phase 7
        /// service on a connection this session does not own.
        /// </remarks>
        internal Task<LoadedReview> LoadReviewAsync(
            long attachmentID, CancellationToken cancellationToken) =>
            ReadAsync(
                connection =>
                {
                    var state = _review.Read(connection, attachmentID);

                    cancellationToken.ThrowIfCancellationRequested();

                    var assetIDs = state.Candidates
                        .Select(candidate => candidate.MediaAssetID)
                        .Concat(
                            state.Reconstruction.ResolvedAsset is { } resolved
                                ? [resolved.MediaAssetID]
                                : Array.Empty<long>())
                        .Distinct()
                        .ToList();

                    return new LoadedReview(state, ReadMedia(connection, assetIDs));
                },
                cancellationToken);

        private ConversationPage ReadPage(
            SqliteConnection connection,
            long conversationID,
            PageDirection direction,
            int sequenceNumber,
            int pageSize) => direction switch
            {
                PageDirection.First =>
                    _conversations.ReadFirstPage(connection, conversationID, pageSize),

                PageDirection.Last =>
                    _conversations.ReadLastPage(connection, conversationID, pageSize),

                PageDirection.From =>
                    _conversations.ReadPageFrom(
                        connection, conversationID, sequenceNumber, pageSize),

                PageDirection.Before =>
                    _conversations.ReadPageBefore(
                        connection, conversationID, sequenceNumber, pageSize),

                _ => throw new ArgumentOutOfRangeException(nameof(direction), direction, null),
            };

        /// <summary>
        /// Which of the supplied assets have a copy this machine can currently reach.
        /// </summary>
        /// <remarks>
        /// One implementation for both the conversation's resolved media and the review panel's
        /// candidate media. A second copy of this probing would be a second place for the
        /// root-first short-circuit to be forgotten.
        /// </remarks>
        /// <remarks>
        /// <para>Each distinct media source root is probed once, before any individual file. A
        /// recovery root commonly lives on an external volume, and when that volume is not
        /// attached, a per-file check would block once for every attachment on the page in order to
        /// learn the same thing the root already said.</para>
        /// <para>Every probe failure counts as "not reachable now". An unreachable copy is a fact
        /// about this machine at this moment; it says nothing about whether the attachment is
        /// resolved, and it must never be allowed to look as though it did.</para>
        /// </remarks>
        private IReadOnlyDictionary<long, MediaAvailability> ReadMedia(
            SqliteConnection connection, IReadOnlyList<long> assetIDs)
        {
            if (assetIDs.Count == 0)
            {
                return new Dictionary<long, MediaAvailability>();
            }

            var locations = _locations.Read(connection, assetIDs);
            var reachableRoots = new Dictionary<long, bool>();
            var media = new Dictionary<long, MediaAvailability>();

            foreach (var location in locations)
            {
                if (!reachableRoots.TryGetValue(location.MediaSourceID, out var rootReachable))
                {
                    rootReachable = Exists(location.RootPath, directory: true);
                    reachableRoots[location.MediaSourceID] = rootReachable;
                }

                var existing = media.TryGetValue(location.MediaAssetID, out var found)
                    ? found
                    : new MediaAvailability(0, null);

                var available = existing.Available;

                // The locations arrive in the reader's deterministic order, so the first reachable
                // one is taken and later ones are not probed at all.
                if (available is null && rootReachable && Exists(location.FullPath, directory: false))
                {
                    available = location;
                }

                media[location.MediaAssetID] =
                    new MediaAvailability(existing.RecordedCopyCount + 1, available);
            }

            foreach (var assetID in assetIDs)
            {
                if (!media.ContainsKey(assetID))
                {
                    media[assetID] = new MediaAvailability(0, null);
                }
            }

            return media;
        }

        private static bool Exists(string path, bool directory)
        {
            try
            {
                return directory ? Directory.Exists(path) : File.Exists(path);
            }
            catch (Exception exception) when (
                exception is IOException
                    or UnauthorizedAccessException
                    or ArgumentException
                    or NotSupportedException)
            {
                return false;
            }
        }

        /// <summary>
        /// Runs one read against the session's connection, off the dispatcher and alone.
        /// </summary>
        private async Task<T> ReadAsync<T>(
            Func<SqliteConnection, T> read, CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                return await Task.Run(
                    () =>
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        return read(_connection);
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <remarks>
        /// The pool is cleared as well as the connection disposed, so the next thing to want this
        /// workspace file — another Chatcheology tool, or this viewer reopening it — finds it free.
        /// One session is open at a time, which is what makes the process-wide clear the right
        /// scope here.
        /// </remarks>
        public void Dispose()
        {
            _connection.Dispose();
            _gate.Dispose();

            SqliteConnection.ClearAllPools();
        }
    }
}
