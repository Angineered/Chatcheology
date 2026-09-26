using System.IO;
using Chatcheology.Data.Workspace;
using Chatcheology.Desktop.Services;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Desktop.Tests
{
    /// <summary>
    /// How a decision is coordinated with the read-only viewer session.
    /// </summary>
    /// <remarks>
    /// The properties pinned here are the ones the safety of the write sequence rests on: the read
    /// session is closed before the decision runs, the decision runs exactly once, the session is
    /// reopened whether it succeeded or was refused, and a refusal is never tried again.
    /// </remarks>
    public class DecisionCoordinatorTests
    {
        [Fact]
        public async Task ADecision_RunsWithNoReadSessionOpen()
        {
            using var workspace = new TemporaryWorkspace();

            var session = WorkspaceSession.Open(workspace.DatabasePath);
            var disposedBeforeWrite = false;

            var outcome = await new DecisionCoordinator().ExecuteAsync(
                session,
                () =>
                {
                    // The session's connection is gone by the time the decision runs. A read
                    // arriving here would contend with the writer's exclusive upgrade, which is
                    // the whole reason the session is closed first.
                    disposedBeforeWrite = IsDisposed(session);
                },
                CancellationToken.None);

            using var reopened = outcome.Session;

            Assert.True(disposedBeforeWrite);
            Assert.True(outcome.Succeeded);
        }

        [Fact]
        public async Task ASuccessfulDecision_RunsOnceAndReopensAWorkingSession()
        {
            using var workspace = new TemporaryWorkspace();

            var session = WorkspaceSession.Open(workspace.DatabasePath);
            var invocations = 0;

            var outcome = await new DecisionCoordinator().ExecuteAsync(
                session, () => invocations++, CancellationToken.None);

            using var reopened = outcome.Session;

            Assert.Equal(1, invocations);
            Assert.True(outcome.Succeeded);
            Assert.Null(outcome.Failure);

            Assert.NotSame(session, reopened);
            Assert.Equal(workspace.DatabasePath, reopened.DatabasePath);

            // And the reopened session reads.
            Assert.NotNull(await reopened.ReadConversationsAsync(CancellationToken.None));
        }

        /// <remarks>
        /// A refusal must not cost the operator their workspace. The session comes back either way,
        /// and the failure comes back with it rather than as an exception the caller has to guess
        /// the meaning of.
        /// </remarks>
        [Fact]
        public async Task ARefusedDecision_StillReopensAndReportsTheRefusal()
        {
            using var workspace = new TemporaryWorkspace();

            var session = WorkspaceSession.Open(workspace.DatabasePath);
            var invocations = 0;

            var refusal = new InvalidOperationException(
                "The run is no longer this attachment's current evidence run. Nothing has been " +
                "committed and the workspace is unchanged.");

            var outcome = await new DecisionCoordinator().ExecuteAsync(
                session,
                () =>
                {
                    invocations++;

                    throw refusal;
                },
                CancellationToken.None);

            using var reopened = outcome.Session;

            Assert.Equal(1, invocations);
            Assert.False(outcome.Succeeded);
            Assert.Same(refusal, outcome.Failure);

            Assert.NotNull(await reopened.ReadConversationsAsync(CancellationToken.None));
        }

        /// <remarks>
        /// <strong>Single-shot.</strong> A refused decision usually means the workspace moved on,
        /// and the answer to that is to look again rather than to ask again. Nothing in the
        /// coordinator loops.
        /// </remarks>
        [Fact]
        public async Task ARefusedDecision_IsNotRetried()
        {
            using var workspace = new TemporaryWorkspace();

            var session = WorkspaceSession.Open(workspace.DatabasePath);
            var invocations = 0;

            var outcome = await new DecisionCoordinator().ExecuteAsync(
                session,
                () =>
                {
                    invocations++;

                    throw new InvalidOperationException("Refused.");
                },
                CancellationToken.None);

            outcome.Session.Dispose();

            Assert.Equal(1, invocations);
        }

        /// <remarks>
        /// The workspace file is genuinely released between the close and the reopen — which is what
        /// clearing the connection pool is for — so a Phase 7 service is free to take its own write
        /// connection and its own lock in the gap.
        /// </remarks>
        [Fact]
        public async Task BetweenCloseAndReopen_TheWorkspaceFileIsWritable()
        {
            using var workspace = new TemporaryWorkspace();

            var session = WorkspaceSession.Open(workspace.DatabasePath);
            var wroteDuringTheGap = false;

            var outcome = await new DecisionCoordinator().ExecuteAsync(
                session,
                () =>
                {
                    // Exactly what a Phase 7 decision service does: its own write connection, its
                    // own transaction, on a file the viewer is no longer holding.
                    using var connection = WorkspaceDatabase.OpenConnection(workspace.DatabasePath);
                    using var transaction = connection.BeginTransaction(deferred: false);
                    using var command = connection.CreateCommand();

                    command.Transaction = transaction;
                    command.CommandText =
                        "INSERT INTO Participant (DisplayName) VALUES ('Written in the gap');";

                    command.ExecuteNonQuery();
                    transaction.Commit();

                    wroteDuringTheGap = true;
                },
                CancellationToken.None);

            using var reopened = outcome.Session;

            Assert.True(wroteDuringTheGap);
            Assert.True(outcome.Succeeded);
        }

        [Fact]
        public async Task ANullSessionOrDecision_IsRefused()
        {
            using var workspace = new TemporaryWorkspace();

            using var session = WorkspaceSession.Open(workspace.DatabasePath);

            await Assert.ThrowsAsync<ArgumentNullException>(
                () => new DecisionCoordinator().ExecuteAsync(session, null!, CancellationToken.None));

            await Assert.ThrowsAsync<ArgumentNullException>(
                () => new DecisionCoordinator().ExecuteAsync(null!, () => { }, CancellationToken.None));
        }

        /// <summary>Whether a session's connection has been disposed.</summary>
        /// <remarks>
        /// Asked by using it: a disposed session cannot read, and that is exactly the property the
        /// first test is checking.
        /// </remarks>
        private static bool IsDisposed(WorkspaceSession session)
        {
            try
            {
                session.ReadConversationsAsync(CancellationToken.None).GetAwaiter().GetResult();

                return false;
            }
            catch (ObjectDisposedException)
            {
                return true;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        }

        /// <summary>A synthetic schema-v3 workspace in its own temporary directory.</summary>
        /// <remarks>
        /// Everything in it is invented. No real participant, path, hash or archive figure appears
        /// anywhere in this project.
        /// </remarks>
        private sealed class TemporaryWorkspace : IDisposable
        {
            private readonly string _directoryPath;

            internal TemporaryWorkspace()
            {
                _directoryPath = Path.Combine(
                    Path.GetTempPath(),
                    "Chatcheology.Desktop.Tests",
                    Guid.NewGuid().ToString("N"));

                Directory.CreateDirectory(_directoryPath);

                DatabasePath = Path.Combine(_directoryPath, "workspace.db");

                WorkspaceDatabase.Initialise(DatabasePath);

                using var connection = WorkspaceDatabase.OpenConnection(DatabasePath);
                using var command = connection.CreateCommand();

                command.CommandText =
                    """
                    INSERT INTO Conversation (Title, CreatedDateTimeUtc)
                    VALUES ('Synthetic conversation', '2026-04-01T00:00:00.0000000Z');

                    INSERT INTO Participant (DisplayName) VALUES ('Ada');

                    INSERT INTO ConversationParticipant (ConversationID, ParticipantID)
                    VALUES (1, 1);
                    """;

                command.ExecuteNonQuery();
            }

            internal string DatabasePath { get; }

            public void Dispose()
            {
                SqliteConnection.ClearAllPools();

                try
                {
                    Directory.Delete(_directoryPath, recursive: true);
                }
                catch (IOException)
                {
                    // A handle outliving a test would show up as the coordinator's own failure
                    // first; this cleanup is not where that is diagnosed.
                }
            }
        }
    }
}
