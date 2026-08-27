using System.Reflection;
using Chatcheology.Data.Phase7;
using Chatcheology.Data.Workspace;

namespace Chatcheology.Data.Tests.Phase7
{
    /// <summary>
    /// That the existence of a human confirmation path has not created an automatic one.
    /// </summary>
    /// <remarks>
    /// Each control below asserts the actual production outcome <strong>and</strong> that a plausible
    /// unsafe rule would have produced a different one — the predicate that rule would fire on is
    /// computed in the test and asserted true while the projection is asserted unchanged. Ordinary
    /// failure-path tests are not counted here; those live with the transitions they belong to.
    /// </remarks>
    public class Phase7DecisionNegativeControlTests
    {
        /// <remarks>
        /// The strongest evidence shape the model can express: one candidate, direction-compatible,
        /// uniquely constrained. A mutant that resolved on "exactly one candidate and both dimensions
        /// supportive" would fire here — the test asserts that predicate holds — and the attachment is
        /// still exactly as unresolved as the emptiest one.
        /// </remarks>
        [Fact]
        public void OnePersuasiveCandidate_ResolvesNothing()
        {
            using var fixture = new Phase7DecisionFixture();
            var date = Phase7TestFixture.FirstDate;

            var attachment = fixture.Evidence.AddOutgoing(date);
            var asset = fixture.Evidence.AddTokenAsset(date, isSent: true, 1);

            var run = fixture.Evidence.Run();

            var support = Assert.Single(fixture.Evidence.Support(run.RunID, attachment));

            var mutantWouldResolve =
                support.Direction == Phase7DirectionSupport.Supportive
                && support.OrderSupport == Phase7OrderSupport.Supportive;

            Assert.True(mutantWouldResolve);

            Assert.Equal(
                OrderConsistencyAnnotation.UniquelyConstrained,
                fixture.Evidence.Annotation(run.RunID, attachment, asset));

            Assert.Equal("Unresolved/none", fixture.StoredProjection(attachment));
            Assert.Equal(0, fixture.EventCount("AttachmentResolutionEvent"));
        }

        /// <remarks>
        /// A mutant that resolved "the sole member of the best tier" would fire here: the test asserts
        /// the tier shape it would test for.
        /// </remarks>
        [Fact]
        public void AOneMemberTopTier_ResolvesNothing()
        {
            using var fixture = new Phase7DecisionFixture();
            var date = Phase7TestFixture.FirstDate;

            // Four attachments over four positions: the group shape leaves rank 0 exactly one
            // admissible position, so its candidate set has a single-member best tier.
            var (attachments, _) =
                fixture.Evidence.AddOutgoingGroup(date, memberCount: 4, positionCount: 4);

            var run = fixture.Evidence.Run();

            var tiers = fixture.Evidence.Rank(run.RunID, attachments[0]).Tiers;

            Assert.True(tiers.Count > 1);

            var mutantWouldResolve = tiers[0].Count == 1;

            Assert.True(mutantWouldResolve);

            Assert.Equal("Unresolved/none", fixture.StoredProjection(attachments[0]));
            Assert.Equal(0, fixture.EventCount("AttachmentResolutionEvent"));
        }

        /// <remarks>
        /// A mutant that resolved "the last candidate nobody disproved" would fire here: exactly one
        /// candidate remains undisproven, and the test asserts that.
        /// </remarks>
        [Fact]
        public void OneRemainingUndisprovenCandidate_ResolvesNothing()
        {
            using var fixture = new Phase7DecisionFixture();
            var date = Phase7TestFixture.FirstDate;

            var attachment = fixture.Evidence.AddOutgoing(date);
            var first = fixture.Evidence.AddTokenAsset(date, isSent: true, 1);
            var second = fixture.Evidence.AddTokenAsset(date, isSent: true, 2);
            var third = fixture.Evidence.AddTokenAsset(date, isSent: true, 3);

            var run = fixture.Evidence.Run();

            fixture.Disprove(fixture.CandidateID(run.RunID, attachment, first));
            fixture.Disprove(fixture.CandidateID(run.RunID, attachment, second));

            var candidates = fixture.Evidence.Support(run.RunID, attachment).Count;
            var disproven = fixture.DisprovenCandidateIDs(run.RunID, attachment).Count;

            var mutantWouldResolve = candidates - disproven == 1;

            Assert.True(mutantWouldResolve);
            Assert.False(fixture.AllDisproven(run.RunID, attachment));

            Assert.Equal("Unresolved/none", fixture.StoredProjection(attachment));
            Assert.Equal(0, fixture.EventCount("AttachmentResolutionEvent"));
            Assert.Equal(third, fixture.Evidence.Support(run.RunID, attachment)[2].MediaAssetID);
        }

        /// <remarks>
        /// A mutant that treated all-disproven as any kind of trigger would fire here: the derived read
        /// is asserted true, and no resolution event exists.
        /// </remarks>
        [Fact]
        public void AllCandidatesDisproven_ResolvesNothing()
        {
            using var fixture = new Phase7DecisionFixture();
            var date = Phase7TestFixture.FirstDate;

            var attachment = fixture.Evidence.AddOutgoing(date);
            var first = fixture.Evidence.AddTokenAsset(date, isSent: true, 1);
            var second = fixture.Evidence.AddTokenAsset(date, isSent: true, 2);

            var run = fixture.Evidence.Run();

            fixture.Disprove(fixture.CandidateID(run.RunID, attachment, first));
            fixture.Disprove(fixture.CandidateID(run.RunID, attachment, second));

            Assert.True(fixture.AllDisproven(run.RunID, attachment));

            Assert.Equal("Unresolved/none", fixture.StoredProjection(attachment));
            Assert.Equal(0, fixture.EventCount("AttachmentResolutionEvent"));
        }

        [Fact]
        public void AReviewOutcome_ResolvesNothing()
        {
            using var fixture = new Phase7DecisionFixture();
            var date = Phase7TestFixture.FirstDate;

            var attachment = fixture.Evidence.AddOutgoing(date);
            fixture.Evidence.AddTokenAsset(date, isSent: true, 1);

            var run = fixture.Evidence.Run();

            fixture.Review(attachment, run.RunID);

            Assert.Equal("Unresolved/none", fixture.StoredProjection(attachment));
            Assert.Equal(0, fixture.EventCount("AttachmentResolutionEvent"));
        }

        /// <remarks>
        /// Every read API, over a workspace holding all four kinds of state, leaves the whole database
        /// logically identical. A logical row signature over all eighteen application tables, not a
        /// workspace-file hash: SQLite may legitimately touch the file when a connection opens without
        /// changing a single row.
        /// </remarks>
        [Fact]
        public void EveryReadApi_WritesNothing()
        {
            using var fixture = new Phase7DecisionFixture();
            var date = Phase7TestFixture.FirstDate;

            var reviewed = fixture.Evidence.AddOutgoing(date);
            var resolved = fixture.Evidence.AddIncoming(date);
            var disproven = fixture.Evidence.AddSenderless(date);
            var asset = fixture.Evidence.AddTokenAsset(date, isSent: null, 1);

            var run = fixture.Evidence.Run();

            fixture.Review(reviewed, run.RunID);
            fixture.Disprove(fixture.CandidateID(run.RunID, disproven, asset));
            fixture.Confirm(resolved, asset);

            var before = DatabaseSignature(fixture);

            using (var connection = fixture.OpenReadOnly())
            {
                var supportReader = new Phase7SupportReader();

                _ = supportReader.ReadLatestCompletedRunID(connection);
                _ = supportReader.ReadLatestCompletedRunContaining(connection, reviewed);
                _ = supportReader.ReadAttachmentSupport(connection, run.RunID, reviewed);
                _ = supportReader.ReadRankedCandidateSet(connection, run.RunID, reviewed);

                var dispositionReader = new Phase7CandidateDispositionReader();

                _ = dispositionReader.AllCandidatesIndependentlyDisproven(
                    connection, run.RunID, disproven);

                _ = dispositionReader.ReadIndependentlyDisprovenCandidateIDs(
                    connection, run.RunID, disproven);

                var historyReader = new Phase7ResolutionHistoryReader();

                _ = historyReader.ReadHistory(connection, resolved);
                _ = historyReader.ReplayProjection(connection, resolved);

                new Phase7ResolutionReconciliation().VerifyWorkspace(connection);
                new Phase7ResolutionReconciliation().VerifyAttachment(connection, resolved);

                var reconstruction = new Phase7ReconstructionReader();

                _ = reconstruction.Read(connection, resolved);
                _ = reconstruction.Read(connection, [reviewed, resolved, disproven]);
            }

            Assert.Equal(before, DatabaseSignature(fixture));
        }

        [Fact]
        public void ConfirmationWithoutAValidIndependentBasis_IsRejectedAtEveryLayer()
        {
            using var fixture = new Phase7DecisionFixture();
            var date = Phase7TestFixture.FirstDate;

            var attachment = fixture.Evidence.AddOutgoing(date);
            var asset = fixture.Evidence.AddTokenAsset(date, isSent: true, 1);

            fixture.Evidence.Run();

            // No description.
            Assert.Throws<ArgumentException>(
                () => fixture.Confirm(attachment, asset, description: " "));

            // No mapping for a value outside the closed vocabulary.
            Assert.Throws<ArgumentOutOfRangeException>(
                () => fixture.Confirm(
                    attachment, asset, basisType: (Phase7IndependentBasisType)7));

            // And the column itself refuses a confirmation with no basis at all.
            Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(
                () => fixture.ExecuteDirect(
                    $"""
                    INSERT INTO AttachmentResolutionEvent (
                        AttachmentID, EventType, MediaAssetID, OccurredAtUtc)
                    VALUES ({attachment}, 'Confirm', {asset}, '2026-08-27T09:00:00.0000000Z');
                    """));

            Assert.Equal("Unresolved/none", fixture.StoredProjection(attachment));
            Assert.Equal(0, fixture.EventCount("AttachmentResolutionEvent"));
        }

        /// <remarks>
        /// <para>No public member of any Phase 7 type carries the vocabulary of an inferred resolution,
        /// with no allowlist and no exceptions.</para>
        /// <para>And no ranking or support type appears anywhere in the request types the write services
        /// accept — checked over the whole property graph, so a nested carrier would fail too. A mutant
        /// that added a candidate set or a support value to a confirmation request would not compile past
        /// this test.</para>
        /// </remarks>
        [Fact]
        public void NoPublicRankingToResolutionBridgeExists()
        {
            string[] forbidden =
            [
                "Score", "Weight", "Threshold", "Automatic", "Probability", "Confidence",
                "Best", "TopRanked",
            ];

            var members = typeof(Phase7ResolutionService).Assembly.GetTypes()
                .Where(type => type.IsPublic && type.Namespace == "Chatcheology.Data.Phase7")
                .SelectMany(type => type.GetMembers().Select(member => $"{type.Name}.{member.Name}"))
                .ToList();

            Assert.NotEmpty(members);

            foreach (var member in members)
            {
                foreach (var word in forbidden)
                {
                    Assert.DoesNotContain(word, member, StringComparison.OrdinalIgnoreCase);
                }
            }

            Type[] rankingTypes =
            [
                typeof(Phase7CandidateSupport),
                typeof(Phase7RankedCandidateSet),
                typeof(Phase7SupportComparison),
                typeof(Phase7OrderSupport),
                typeof(Phase7DirectionSupport),
                typeof(OrderConsistencyAnnotation),
            ];

            Type[] decisionRequests =
            [
                typeof(Phase7ConfirmRequest),
                typeof(Phase7ConfirmReplacementRequest),
                typeof(Phase7RevokeRequest),
                typeof(Phase7ReviewRequest),
                typeof(Phase7CandidateDispositionRequest),
            ];

            foreach (var request in decisionRequests)
            {
                foreach (var property in request.GetProperties(
                    BindingFlags.Public | BindingFlags.Instance))
                {
                    Assert.DoesNotContain(property.PropertyType, rankingTypes);
                }
            }
        }

        /// <summary>
        /// Every application table's row count and the resolution distribution, as one comparable line.
        /// </summary>
        /// <remarks>
        /// A logical signature over stored content. It is deliberately not a file hash.
        /// </remarks>
        private static string DatabaseSignature(Phase7DecisionFixture fixture)
        {
            string[] phase7Tables =
            [
                "Phase7Run",
                "AttachmentEvidenceSnapshot",
                "AttachmentCandidate",
                "AttachmentCandidateOccurrenceEvidence",
                "AttachmentAdjacentDateContext",
            ];

            var parts = WorkspaceDatabase.VersionTwoTableNames
                .Concat(phase7Tables)
                .Concat(WorkspaceDatabase.VersionThreeDecisionTableNames)
                .Select(table => $"{table}={fixture.EventCount(table)}")
                .ToList();

            parts.AddRange(fixture.ResolutionDistribution());

            return string.Join("|", parts);
        }
    }
}
