using Chatcheology.Data.Matching;
using Chatcheology.Data.Phase7;
using Chatcheology.Data.Tests.Matching;
using Chatcheology.Data.Workspace;

namespace Chatcheology.Data.Tests.Phase7
{
    /// <summary>
    /// The publication checks, observed from outside the service, and the failure paths that prove they
    /// gate publication rather than merely reporting on it.
    /// </summary>
    /// <remarks>
    /// <strong>None of these checks identifies anything.</strong> Counts agreeing is a statement about
    /// bookkeeping, never about whether a candidate is the attachment's media. What the tests establish
    /// is that a bookkeeping failure publishes nothing at all, which is the opposite of a resolution
    /// rule.
    /// </remarks>
    public class Phase7ReconciliationTests
    {
        [Fact]
        public void Publish_SnapshotCoverage_HasTwoIndependentDerivations()
        {
            using var fixture = new Phase7TestFixture();
            var date = Phase7TestFixture.FirstDate;

            fixture.AddOutgoingGroup(date, memberCount: 3, positionCount: 4);
            fixture.AddIncoming(date);
            fixture.AddSenderless(date);
            fixture.AddOutgoingInOtherConversation(date);

            var result = fixture.Run();

            // Derivation one: the run's own snapshot rows.
            var persisted = fixture.ScalarLong(
                $"SELECT COUNT(*) FROM AttachmentEvidenceSnapshot WHERE RunID = {result.RunID};");

            // Derivation two: the conversation's unresolved attachments, counted independently.
            var unresolved = fixture.ScalarLong(
                $"""
                SELECT COUNT(*)
                FROM Attachment AS a
                INNER JOIN Message AS m ON m.MessageID = a.MessageID
                WHERE m.ConversationID = {MatchingTestWorkspace.ConversationID}
                  AND a.ResolutionStatus = 'Unresolved';
                """);

            Assert.Equal(5, persisted);
            Assert.Equal(persisted, unresolved);
            Assert.Equal(persisted, result.SnapshotRowCount);
        }

        [Fact]
        public void Publish_EveryOccurrenceRow_BelongsToACandidateOfTheRun()
        {
            using var fixture = new Phase7TestFixture();
            var date = Phase7TestFixture.FirstDate;

            fixture.AddOutgoingGroup(date, memberCount: 2, positionCount: 3);

            var first = fixture.Run();
            var second = fixture.Run();

            foreach (var runID in new[] { first.RunID, second.RunID })
            {
                var forRun = fixture.ScalarLong(
                    $"""
                    SELECT COUNT(*)
                    FROM AttachmentCandidateOccurrenceEvidence AS o
                    INNER JOIN AttachmentCandidate AS c
                        ON c.AttachmentCandidateID = o.AttachmentCandidateID
                    WHERE c.RunID = {runID};
                    """);

                Assert.Equal(first.OccurrenceRowCount, forRun);
            }

            // No orphans anywhere, in either run.
            Assert.Equal(
                0,
                fixture.ScalarLong(
                    """
                    SELECT COUNT(*)
                    FROM AttachmentCandidateOccurrenceEvidence AS o
                    LEFT JOIN AttachmentCandidate AS c
                        ON c.AttachmentCandidateID = o.AttachmentCandidateID
                    WHERE c.AttachmentCandidateID IS NULL;
                    """));
        }

        /// <remarks>
        /// The occurrence rows of one run's candidate belong to that run's candidate only, never to the
        /// other run's — which is what makes the two runs' evidence independently readable.
        /// </remarks>
        [Fact]
        public void Publish_TwoRuns_KeepTheirOccurrenceRowsApart()
        {
            using var fixture = new Phase7TestFixture();
            var date = Phase7TestFixture.FirstDate;

            var attachment = fixture.AddOutgoing(date);
            fixture.AddTokenAsset(date, isSent: true, 10, 20);

            var first = fixture.Run();
            var second = fixture.Run();

            Assert.Equal(2, fixture.Occurrences(first.RunID, attachment).Count);
            Assert.Equal(2, fixture.Occurrences(second.RunID, attachment).Count);

            Assert.Equal(
                4,
                fixture.ScalarLong("SELECT COUNT(*) FROM AttachmentCandidateOccurrenceEvidence;"));
        }

        [Fact]
        public void Publish_EveryCandidate_HasAPhysicalCopyDatedToItsMessageDate()
        {
            using var fixture = new Phase7TestFixture();
            var date = Phase7TestFixture.FirstDate;

            fixture.AddOutgoing(date);
            fixture.AddTokenAsset(date, isSent: true, 1);
            fixture.AddTokenAsset(date.AddDays(1), isSent: true, 2);

            var result = fixture.Run();

            Assert.Equal(1, result.CandidateRowCount);
            Assert.Equal(1, result.AdjacentDateContextRowCount);

            Assert.Equal(
                0,
                fixture.ScalarLong(
                    $"""
                    SELECT COUNT(*)
                    FROM AttachmentCandidate AS c
                    INNER JOIN Attachment AS a ON a.AttachmentID = c.AttachmentID
                    INNER JOIN Message AS m ON m.MessageID = a.MessageID
                    WHERE c.RunID = {result.RunID}
                      AND NOT EXISTS (
                          SELECT 1
                          FROM MediaAssetFile AS l
                          INNER JOIN MediaFile AS f ON f.MediaFileID = l.MediaFileID
                          WHERE l.MediaAssetID = c.MediaAssetID
                            AND f.FileDate = substr(m.MessageDateTimeLocal, 1, 10));
                    """));
        }

        [Fact]
        public void Publish_GroupShapeFacts_AgreeAcrossEveryMemberOfAGroup()
        {
            using var fixture = new Phase7TestFixture();
            var date = Phase7TestFixture.FirstDate;

            var (attachments, _) = fixture.AddOutgoingGroup(date, memberCount: 3, positionCount: 5);

            var result = fixture.Run();

            var shapes = attachments
                .Select(attachment => fixture.Snapshot(result.RunID, attachment))
                .Select(row => string.Join(",", row.Split(',')[4..]))
                .Distinct()
                .ToList();

            // One group, one set of group-shape facts: T = 5, M = 3, feasible, not forced, window 3.
            Assert.Equal(["0,0,3"], shapes);
        }

        [Fact]
        public void Publish_TheThreeStageSevenTwoEventTables_StayEmpty()
        {
            using var fixture = new Phase7TestFixture();
            var date = Phase7TestFixture.FirstDate;

            fixture.AddOutgoingGroup(date, memberCount: 2, positionCount: 3);

            fixture.Run();

            Assert.Equal(
                0,
                fixture.ScalarLong(
                    """
                    SELECT (SELECT COUNT(*) FROM AttachmentReviewEvent)
                         + (SELECT COUNT(*) FROM AttachmentCandidateDispositionEvent)
                         + (SELECT COUNT(*) FROM AttachmentResolutionEvent);
                    """));
        }

        /// <remarks>
        /// The mid-run tripwire, which the run reads once before it starts and again inside the
        /// publication transaction. It covers the ten pre-existing tables' row counts and the
        /// <c>Attachment</c> resolution distribution.
        /// </remarks>
        [Fact]
        public void Baseline_ReadsThePreExistingRowCountsAndTheAttachmentDistribution()
        {
            using var fixture = new Phase7TestFixture();
            var date = Phase7TestFixture.FirstDate;

            fixture.AddOutgoing(date);
            fixture.AddTokenAsset(date, isSent: true, 1);
            fixture.CloseBuilding();

            using var connection = WorkspaceDatabase.OpenReadOnlyConnection(
                fixture.DatabasePath);

            var baseline = Phase7RunReconciliation.ReadBaseline(connection);

            foreach (var tableName in WorkspaceDatabase.VersionTwoTableNames)
            {
                Assert.Contains(tableName + "=", baseline.Signature, StringComparison.Ordinal);
            }

            Assert.Contains(
                "Attachment[Unresolved,unlinked=1]=1", baseline.Signature, StringComparison.Ordinal);
        }

        /// <remarks>
        /// A payload the checks must reject, driven through the writer seam: one snapshot row is missing,
        /// so the run's coverage disagrees with the conversation's unresolved attachments. Publication is
        /// abandoned, nothing survives, and a previously completed run is untouched.
        /// </remarks>
        [Fact]
        public void Publish_WhenReconciliationFails_LeavesNoEvidenceRowAndMarksTheRunFailed()
        {
            using var fixture = new Phase7TestFixture();
            var date = Phase7TestFixture.FirstDate;

            var attachments = new[]
            {
                fixture.AddOutgoing(date, time: "09:00:00"),
                fixture.AddOutgoing(date, time: "09:01:00"),
            };

            fixture.AddTokenAsset(date, isSent: true, 1);
            fixture.AddTokenAsset(date, isSent: true, 2);

            var completed = fixture.Run();
            var completedEvidence = fixture.EvidenceSignature(completed.RunID);

            long runID;

            using (var session = Phase7EvidenceSnapshotService.OpenSession(fixture.Request()))
            {
                runID = session.BeginRun();

                session.Writer.BeginPublicationGuard();

                var payload = new Phase7EvidencePayload
                {
                    // One snapshot row, for a workspace whose conversation holds two attachments.
                    SnapshotRows =
                    [
                        new Phase7SnapshotRow(
                            attachments[0], 0, 0, false, false, false, false, null),
                    ],
                    CandidateRows = [],
                    OccurrenceRows = [],
                    AdjacentContextRows = [],
                    InScopeAttachmentCount = 1,
                    TwoPassAgreementVerified = true,
                };

                var error = Assert.Throws<InvalidOperationException>(
                    () => session.Writer.Publish(
                        runID, session.Request, payload, session.Baseline, DateTime.UtcNow));

                Assert.Contains("reconciliation check", error.Message, StringComparison.Ordinal);
                Assert.Contains("snapshot coverage", error.Message, StringComparison.Ordinal);

                session.Writer.RollbackGuard();
                session.Writer.MarkFailed(runID);
            }

            Assert.Equal("Failed", fixture.RunStatus(runID));

            Assert.Equal(
                "<null>",
                fixture.ScalarText($"SELECT CompletedAtUtc FROM Phase7Run WHERE RunID = {runID};"));

            Assert.Equal(0, fixture.EvidenceRowsFor(runID));

            // The previously completed run is still current and still intact.
            Assert.Equal("Completed", fixture.RunStatus(completed.RunID));
            Assert.Equal(completedEvidence, fixture.EvidenceSignature(completed.RunID));
        }

        /// <remarks>
        /// The same outcome from a foreign-key failure rather than a check failure: a payload naming a
        /// candidate asset that does not exist. Publication is atomic, so a partial run cannot survive
        /// either way.
        /// </remarks>
        [Fact]
        public void Publish_WhenAnInsertFails_LeavesNoPartialRun()
        {
            using var fixture = new Phase7TestFixture();
            var date = Phase7TestFixture.FirstDate;

            var attachment = fixture.AddOutgoing(date);
            fixture.AddTokenAsset(date, isSent: true, 1);

            fixture.CloseBuilding();

            var absentAsset = fixture.ScalarLong("SELECT MAX(MediaAssetID) FROM MediaAsset;") + 1000;

            long runID;

            using (var session = Phase7EvidenceSnapshotService.OpenSession(fixture.Request()))
            {
                runID = session.BeginRun();

                session.Writer.BeginPublicationGuard();

                var payload = new Phase7EvidencePayload
                {
                    SnapshotRows =
                    [
                        new Phase7SnapshotRow(attachment, 1, 0, true, false, false, false, null),
                    ],
                    CandidateRows =
                    [
                        new Phase7CandidateRow(
                            attachment, absentAsset, DirectionCompatibility.Compatible, 1),
                    ],
                    OccurrenceRows = [],
                    AdjacentContextRows = [],
                    InScopeAttachmentCount = 1,
                    TwoPassAgreementVerified = true,
                };

                Assert.ThrowsAny<Exception>(
                    () => session.Writer.Publish(
                        runID, session.Request, payload, session.Baseline, DateTime.UtcNow));

                session.Writer.RollbackGuard();
                session.Writer.MarkFailed(runID);
            }

            Assert.Equal("Failed", fixture.RunStatus(runID));
            Assert.Equal(0, fixture.EvidenceRowsFor(runID));
        }

        /// <remarks>
        /// The two-pass agreement flag is a gate, not a label: a payload that does not record the check
        /// having been made is refused.
        /// </remarks>
        [Fact]
        public void Publish_WhenThePayloadDoesNotRecordTheTwoPassCheck_IsRefused()
        {
            using var fixture = new Phase7TestFixture();

            var attachment = fixture.AddOutgoing(Phase7TestFixture.FirstDate);

            fixture.CloseBuilding();

            long runID;

            using (var session = Phase7EvidenceSnapshotService.OpenSession(fixture.Request()))
            {
                runID = session.BeginRun();

                session.Writer.BeginPublicationGuard();

                var payload = new Phase7EvidencePayload
                {
                    SnapshotRows =
                    [
                        new Phase7SnapshotRow(attachment, 0, 0, false, false, false, false, null),
                    ],
                    CandidateRows = [],
                    OccurrenceRows = [],
                    AdjacentContextRows = [],
                    InScopeAttachmentCount = 1,
                    TwoPassAgreementVerified = false,
                };

                var error = Assert.Throws<InvalidOperationException>(
                    () => session.Writer.Publish(
                        runID, session.Request, payload, session.Baseline, DateTime.UtcNow));

                Assert.Contains("two-pass determinism", error.Message, StringComparison.Ordinal);

                session.Writer.RollbackGuard();
                session.Writer.MarkFailed(runID);
            }

            Assert.Equal("Failed", fixture.RunStatus(runID));
            Assert.Equal(0, fixture.EvidenceRowsFor(runID));
        }

        /// <remarks>
        /// Check 8, made to fail. The old quadratic form of this statement was semantically correct, so a
        /// purely functional test could not have distinguished it from the restructured one — but a
        /// restructuring that quietly stopped detecting anything would look identical to a fast, passing
        /// check. This is the test that makes the check's detection capability explicit.
        /// <para>
        /// The payload names as a candidate an asset whose only dated copy is ten days away, so it is
        /// neither an exact-date nor an adjacent-date candidate. Checks 1 through 7 are satisfied
        /// deliberately — one snapshot row, matching declared counts, one occurrence row per candidate —
        /// so the failure is specifically check 8 and not an earlier invariant, which the asserted message
        /// confirms.
        /// </para>
        /// </remarks>
        [Fact]
        public void Publish_WhenACandidateHasNoCopyOnTheMessageDate_IsRefusedByCheckEight()
        {
            using var fixture = new Phase7TestFixture();
            var date = Phase7TestFixture.FirstDate;

            var attachment = fixture.AddOutgoing(date);

            fixture.AddTokenAsset(date, isSent: true, 1);

            // Ten days away: not exact-date, and not adjacent either.
            var distant = fixture.AddTokenAsset(date.AddDays(10), isSent: true, 2);

            fixture.CloseBuilding();

            var distantFileID = fixture.ScalarLong(
                $"""
                SELECT MAX(f.MediaFileID)
                FROM MediaFile AS f
                INNER JOIN MediaAssetFile AS l ON l.MediaFileID = f.MediaFileID
                WHERE l.MediaAssetID = {distant};
                """);

            long runID;

            using (var session = Phase7EvidenceSnapshotService.OpenSession(fixture.Request()))
            {
                runID = session.BeginRun();

                session.Writer.BeginPublicationGuard();

                var payload = new Phase7EvidencePayload
                {
                    SnapshotRows =
                    [
                        new Phase7SnapshotRow(attachment, 1, 0, true, false, false, false, null),
                    ],
                    CandidateRows =
                    [
                        new Phase7CandidateRow(
                            attachment, distant, DirectionCompatibility.Compatible, 1),
                    ],
                    OccurrenceRows =
                    [
                        new Phase7OccurrenceRow(
                            0, distantFileID, null, null, OrderConsistencyAnnotation.NotAssessable),
                    ],
                    AdjacentContextRows = [],
                    InScopeAttachmentCount = 1,
                    TwoPassAgreementVerified = true,
                };

                var error = Assert.Throws<InvalidOperationException>(
                    () => session.Writer.Publish(
                        runID, session.Request, payload, session.Baseline, DateTime.UtcNow));

                // Specifically check 8, not an earlier invariant.
                Assert.Contains(
                    "every candidate has a physical copy dated to its attachment's message date",
                    error.Message,
                    StringComparison.Ordinal);

                session.Writer.RollbackGuard();
                session.Writer.MarkFailed(runID);
            }

            Assert.Equal("Failed", fixture.RunStatus(runID));
            Assert.Equal(0, fixture.EvidenceRowsFor(runID));
        }

        /// <remarks>
        /// Check 12, made to fail, for the same reason: the old form was correct but quadratic, and a
        /// restructuring must be shown still to detect what it existed to detect.
        /// <para>
        /// The frozen analysis cannot construct this state — a candidate with a contradicting supporting
        /// copy would never have been annotated <c>Compatible</c> by <c>ResolveCompatibility</c> — so it
        /// is built through the narrow internal publication seam with a payload that claims the
        /// annotation the analysis would have refused. No production semantics are weakened: the
        /// annotation is a lie the check is meant to catch, and it catches it.
        /// </para>
        /// </remarks>
        [Fact]
        public void Publish_WhenACompatibleCandidateHasAContradictingCopy_IsRefusedByCheckTwelve()
        {
            using var fixture = new Phase7TestFixture();
            var date = Phase7TestFixture.FirstDate;

            // Outgoing message, and the asset's only dated copy is not beneath a Sent directory, which
            // contradicts it. The frozen analysis would annotate this ContradictoryOnly.
            var attachment = fixture.AddOutgoing(date);
            var asset = fixture.AddTokenAsset(date, isSent: false, 1);

            fixture.CloseBuilding();

            var fileID = fixture.ScalarLong("SELECT MAX(MediaFileID) FROM MediaFile;");

            long runID;

            using (var session = Phase7EvidenceSnapshotService.OpenSession(fixture.Request()))
            {
                runID = session.BeginRun();

                session.Writer.BeginPublicationGuard();

                var payload = new Phase7EvidencePayload
                {
                    SnapshotRows =
                    [
                        new Phase7SnapshotRow(attachment, 1, 0, true, false, false, false, null),
                    ],
                    CandidateRows =
                    [
                        new Phase7CandidateRow(
                            attachment, asset, DirectionCompatibility.Compatible, 1),
                    ],
                    OccurrenceRows =
                    [
                        new Phase7OccurrenceRow(
                            0, fileID, null, null, OrderConsistencyAnnotation.NotAssessable),
                    ],
                    AdjacentContextRows = [],
                    InScopeAttachmentCount = 1,
                    TwoPassAgreementVerified = true,
                };

                var error = Assert.Throws<InvalidOperationException>(
                    () => session.Writer.Publish(
                        runID, session.Request, payload, session.Baseline, DateTime.UtcNow));

                Assert.Contains(
                    "no candidate row annotated Compatible is supported on its message date by a copy " +
                    "recording the contradicting direction",
                    error.Message,
                    StringComparison.Ordinal);

                session.Writer.RollbackGuard();
                session.Writer.MarkFailed(runID);
            }

            Assert.Equal("Failed", fixture.RunStatus(runID));
            Assert.Equal(0, fixture.EvidenceRowsFor(runID));
        }

        /// <remarks>
        /// The contrast case for check 12's direction-blind semantics, which the restructuring had to
        /// preserve: a supporting copy recording <em>no</em> direction at all contradicts nothing, so the
        /// same payload publishes cleanly. Without this, a restructuring that dropped the
        /// <c>IsSent IS NOT NULL</c> filter would still pass the failing test above.
        /// </remarks>
        [Fact]
        public void Publish_WhenACompatibleCandidateHasADirectionBlindCopy_IsNotRefused()
        {
            using var fixture = new Phase7TestFixture();
            var date = Phase7TestFixture.FirstDate;

            var attachment = fixture.AddOutgoing(date);

            // isSent: null — a recovered store with no Sent structure at all.
            var asset = fixture.AddTokenAsset(date, isSent: null, 1);

            fixture.CloseBuilding();

            var fileID = fixture.ScalarLong("SELECT MAX(MediaFileID) FROM MediaFile;");

            long runID;

            using (var session = Phase7EvidenceSnapshotService.OpenSession(fixture.Request()))
            {
                runID = session.BeginRun();

                session.Writer.BeginPublicationGuard();

                var payload = new Phase7EvidencePayload
                {
                    SnapshotRows =
                    [
                        new Phase7SnapshotRow(attachment, 1, 0, true, false, false, false, null),
                    ],
                    CandidateRows =
                    [
                        new Phase7CandidateRow(
                            attachment, asset, DirectionCompatibility.Compatible, 1),
                    ],
                    OccurrenceRows =
                    [
                        new Phase7OccurrenceRow(
                            0, fileID, null, null, OrderConsistencyAnnotation.NotAssessable),
                    ],
                    AdjacentContextRows = [],
                    InScopeAttachmentCount = 1,
                    TwoPassAgreementVerified = true,
                };

                session.Writer.Publish(
                    runID, session.Request, payload, session.Baseline, DateTime.UtcNow);
            }

            Assert.Equal("Completed", fixture.RunStatus(runID));
            Assert.Equal(3, fixture.EvidenceRowsFor(runID));
        }

        /// <remarks>
        /// The tripwire firing: pre-existing data changed between the baseline read and the publication
        /// transaction. Reported with both possibilities named and neither asserted.
        /// </remarks>
        [Fact]
        public void Publish_WhenPreExistingDataChangedDuringTheRun_IsRefused()
        {
            using var fixture = new Phase7TestFixture();
            var date = Phase7TestFixture.FirstDate;

            var attachment = fixture.AddOutgoing(date);

            fixture.CloseBuilding();

            long runID;

            using (var session = Phase7EvidenceSnapshotService.OpenSession(fixture.Request()))
            {
                runID = session.BeginRun();

                var payload = new Phase7EvidencePayload
                {
                    SnapshotRows =
                    [
                        new Phase7SnapshotRow(attachment, 0, 0, false, false, false, false, null),
                    ],
                    CandidateRows = [],
                    OccurrenceRows = [],
                    AdjacentContextRows = [],
                    InScopeAttachmentCount = 1,
                    TwoPassAgreementVerified = true,
                };

                // A baseline taken from a state this workspace was never in, which is what the
                // tripwire compares against.
                var wrongBaseline = new Phase7PreExistingBaseline("Attachment=99");

                session.Writer.BeginPublicationGuard();

                var error = Assert.Throws<InvalidOperationException>(
                    () => session.Writer.Publish(
                        runID, session.Request, payload, wrongBaseline, DateTime.UtcNow));

                Assert.Contains(
                    "pre-existing data changed during this run",
                    error.Message,
                    StringComparison.Ordinal);

                Assert.Contains(
                    "This run cannot distinguish those", error.Message, StringComparison.Ordinal);

                session.Writer.RollbackGuard();
                session.Writer.MarkFailed(runID);
            }

            Assert.Equal("Failed", fixture.RunStatus(runID));
            Assert.Equal(0, fixture.EvidenceRowsFor(runID));
        }
    }
}
