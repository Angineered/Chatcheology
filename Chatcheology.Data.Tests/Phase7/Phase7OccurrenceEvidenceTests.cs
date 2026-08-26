using Chatcheology.Data.Phase7;
using Chatcheology.Data.Tests.Matching;
using Chatcheology.Data.Workspace;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Data.Tests.Phase7
{
    /// <summary>
    /// Sequence evidence stays at the scope Phase 6 established it — one physical file — and the
    /// corrected order annotation is exercised end to end through the service.
    /// </summary>
    /// <remarks>
    /// Two supporting copies of one payload on one date legitimately carry different tokens, land on
    /// different positions and read differently. Collapsing them onto the asset, or choosing whichever
    /// copy reads better, would produce a verdict no file ever had.
    /// </remarks>
    public class Phase7OccurrenceEvidenceTests
    {
        // ---------------------------------------------------------------------------------------
        // Occurrence scoping.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void Run_TwoSupportingCopiesWithDifferentTokens_ProduceTwoOccurrenceRows()
        {
            using var fixture = new Phase7TestFixture();
            var date = Phase7TestFixture.FirstDate;

            var attachment = fixture.AddOutgoing(date);
            var asset = fixture.AddTokenAsset(date, isSent: true, 10, 20);

            var result = fixture.Run();

            Assert.Equal(1, result.CandidateRowCount);
            Assert.Equal(2, result.OccurrenceRowCount);

            var occurrences = fixture.Occurrences(result.RunID, attachment);

            Assert.Equal(2, occurrences.Count);

            // Two rows, two distinct tokens, never one collapsed value.
            Assert.Equal(
                ["10", "20"],
                occurrences.Select(row => row.Split(',')[3]).Order().ToList());

            // M = 1, T = 2: both admissible positions are uniquely constrained, which inflates
            // nothing because both reduce to the same strength.
            Assert.All(
                occurrences,
                row => Assert.EndsWith("UniquelyConstrained", row, StringComparison.Ordinal));

            Assert.Equal("1,0,1,0,0,0,2", fixture.Snapshot(result.RunID, attachment));
            Assert.Equal(asset, Assert.Single(fixture.Support(result.RunID, attachment)).MediaAssetID);
        }

        /// <remarks>
        /// One row per physical file even when both carry the same token: position collapse and
        /// occurrence separation are different things, and this stage keeps them apart.
        /// </remarks>
        [Fact]
        public void Run_TwoSupportingCopiesOfOnePayloadInTwoSources_ProduceTwoOccurrenceRows()
        {
            using var fixture = new Phase7TestFixture();
            var date = Phase7TestFixture.FirstDate;

            var attachment = fixture.AddOutgoing(date);

            var second = fixture.AddSource();
            var asset = fixture.AddTokenAsset(date, isSent: true, 10);

            fixture.AddCopy(
                asset,
                Phase7TestFixture.HashOf(fixture.AssetCount),
                date,
                isSent: true,
                Phase7TestFixture.Token(10),
                mediaSourceID: second);

            var result = fixture.Run();

            Assert.Equal(1, result.CandidateRowCount);
            Assert.Equal(2, result.OccurrenceRowCount);

            var occurrences = fixture.Occurrences(result.RunID, attachment);

            Assert.Equal(2, occurrences.Count);
            Assert.All(occurrences, row => Assert.Contains(",10,", row, StringComparison.Ordinal));

            // The two copies collapse to one token position, so T = 1 and the window is one wide.
            Assert.Equal("1,0,1,0,0,1,1", fixture.Snapshot(result.RunID, attachment));
        }

        /// <remarks>
        /// The known collision shape: two payloads sharing one <c>(date, token)</c> key. Uniqueness
        /// counts payloads, so this is the one case the archive really produces, and the frozen gate
        /// forces both occurrences to <c>NotAssessable</c> rather than letting the ambiguity be
        /// silently ignored.
        /// </remarks>
        [Fact]
        public void Run_CollidedDateTokenKey_ForcesNotAssessable()
        {
            using var fixture = new Phase7TestFixture();
            var date = Phase7TestFixture.FirstDate;

            var attachment = fixture.AddOutgoing(date);

            var first = fixture.AddTokenAsset(date, isSent: true, 8);
            var second = fixture.AddTokenAsset(date, isSent: true, 8);

            var result = fixture.Run();

            Assert.Equal(2, result.CandidateRowCount);
            Assert.Equal(2, result.OccurrenceRowCount);

            foreach (var asset in new[] { first, second })
            {
                Assert.Equal(
                    OrderConsistencyAnnotation.NotAssessable,
                    fixture.Annotation(result.RunID, attachment, asset));
            }

            Assert.All(
                fixture.Occurrences(result.RunID, attachment),
                row => Assert.Contains(",0,NotAssessable", row, StringComparison.Ordinal));

            // The colliding token still contributes its position: the gate acts on the occurrence,
            // never on group shape, because removing a position would change every other occurrence's
            // window on the strength of one occurrence's ambiguity.
            Assert.Equal("2,0,1,0,0,1,1", fixture.Snapshot(result.RunID, attachment));
        }

        /// <remarks>
        /// Proved at the database layer, not only in application logic: the frozen same-row
        /// <c>CHECK</c> is what makes a collided occurrence claiming an order reading impossible to
        /// store at all.
        /// </remarks>
        [Fact]
        public void Occurrence_CollidedTokenClaimingAnOrderReading_IsRejectedByTheDatabase()
        {
            using var fixture = new Phase7TestFixture();
            var date = Phase7TestFixture.FirstDate;

            var attachment = fixture.AddOutgoing(date);
            fixture.AddTokenAsset(date, isSent: true, 8);

            var result = fixture.Run();

            var candidateID = fixture.ScalarLong(
                $"SELECT AttachmentCandidateID FROM AttachmentCandidate " +
                $"WHERE RunID = {result.RunID} AND AttachmentID = {attachment};");

            var otherFileID = fixture.ScalarLong("SELECT MAX(MediaFileID) FROM MediaFile;");

            using var connection = WorkspaceDatabase.OpenConnection(fixture.DatabasePath);
            using var command = connection.CreateCommand();
            command.CommandText =
                $"""
                INSERT INTO AttachmentCandidateOccurrenceEvidence (
                    AttachmentCandidateID, MediaFileID, TokenGrammarSupported, SequenceToken,
                    DateTokenKeyUniqueToPayload, OrderConsistencyAnnotation)
                VALUES ({candidateID}, {otherFileID}, 1, 8, 0, 'UniquelyConstrained');
                """;

            Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());
        }

        /// <remarks>
        /// Unavailable is not negative. A name that says nothing is not a name that says something
        /// unfavourable, so the reduced order support is <c>Unavailable</c> and never
        /// <c>LessSupportive</c>.
        /// </remarks>
        [Fact]
        public void Run_GrammarUnsupportedSuffix_IsNotAssessableRatherThanNegative()
        {
            using var fixture = new Phase7TestFixture();
            var date = Phase7TestFixture.FirstDate;

            var attachment = fixture.AddOutgoing(date);
            var asset = fixture.AddNamedAsset(date, isSent: true, "0001-1");

            var result = fixture.Run();

            Assert.Equal(
                OrderConsistencyAnnotation.NotAssessable,
                fixture.Annotation(result.RunID, attachment, asset));

            Assert.Equal(
                $"{asset},{fixture.ScalarLong("SELECT MAX(MediaFileID) FROM MediaFile;")}," +
                $"0,<null>,<null>,NotAssessable",
                Assert.Single(fixture.Occurrences(result.RunID, attachment)));

            var support = Assert.Single(fixture.Support(result.RunID, attachment));

            Assert.Equal(Phase7DirectionSupport.Supportive, support.Direction);
            Assert.Equal(Phase7OrderSupport.Unavailable, support.OrderSupport);
            Assert.NotEqual(Phase7OrderSupport.LessSupportive, support.OrderSupport);
        }

        /// <remarks>
        /// A dated file whose name carries no locatable marker means this run and the committed Phase 5
        /// classifier disagree about which characters are a date. That is a fact about the workspace
        /// rather than about any attachment, so the run refuses rather than recording it as unsupported
        /// grammar.
        /// </remarks>
        [Fact]
        public void Run_DatedCopyWithNoLocatableMarker_IsARunFailure()
        {
            using var fixture = new Phase7TestFixture();
            var date = Phase7TestFixture.FirstDate;

            fixture.AddOutgoing(date);

            // The matching fixture's neutral default name carries no marker, which is the deliberate
            // failure case rather than a convenience.
            var sha = Phase7TestFixture.HashOf(90);
            var asset = fixture.Workspace.AddMediaAsset(sha);
            fixture.Workspace.AddMediaFile(fixture.SourceID, asset, sha, date, isSent: true);

            fixture.CloseBuilding();

            var error = Assert.Throws<InvalidOperationException>(() => fixture.Run());

            Assert.Contains("no locatable", error.Message, StringComparison.Ordinal);
            Assert.Equal(0, fixture.TotalEvidenceRows());
        }

        [Fact]
        public void Run_DatedCopyWhoseMarkerDisagreesWithItsFileDate_IsARunFailure()
        {
            using var fixture = new Phase7TestFixture();
            var date = Phase7TestFixture.FirstDate;

            fixture.AddOutgoing(date);
            fixture.AddAssetWithDisagreeingMarker(date, date.AddDays(-3));

            fixture.CloseBuilding();

            var error = Assert.Throws<InvalidOperationException>(() => fixture.Run());

            Assert.Contains(
                "not the date its own name encodes", error.Message, StringComparison.Ordinal);

            Assert.Equal(0, fixture.TotalEvidenceRows());
        }

        /// <remarks>
        /// The per-candidate agreement proof, observed from outside: the occurrence rows a run writes
        /// for a candidate are exactly as many as the frozen analysis counts supporting physical copies.
        /// </remarks>
        [Fact]
        public void Run_OccurrenceCount_MatchesTheFrozenSupportingCopyCount()
        {
            using var fixture = new Phase7TestFixture();
            var date = Phase7TestFixture.FirstDate;

            var attachment = fixture.AddOutgoing(date);

            var asset = fixture.AddTokenAsset(date, isSent: true, 10, 20, 30);

            // A copy on an unrelated date is not supporting evidence for this relation and must not
            // become an occurrence row.
            fixture.AddCopy(
                asset,
                Phase7TestFixture.HashOf(fixture.AssetCount),
                date.AddDays(40),
                isSent: true,
                Phase7TestFixture.Token(40));

            var result = fixture.Run();

            var frozen = MatchingTestData.AnalyseOne(
                fixture.Workspace, MatchingTestWorkspace.LocalParticipantID);

            var candidate = Assert.Single(frozen.ExactDateCandidates);

            Assert.Equal(3, candidate.SupportingPhysicalCopyCount);
            Assert.Equal(4, candidate.PhysicalCopyCount);

            Assert.Equal(3, result.OccurrenceRowCount);
            Assert.Equal(3, fixture.Occurrences(result.RunID, attachment).Count);
        }

        /// <remarks>
        /// The arrangement count is computed, used to choose among three frozen annotation values, and
        /// discarded. No Phase 7 column holds it, because a numeric quantity on an evidence row is
        /// something a future reader could be tempted to rank by.
        /// </remarks>
        [Fact]
        public void Run_ArrangementCount_IsNotPersistedAnywhere()
        {
            using var fixture = new Phase7TestFixture();
            var date = Phase7TestFixture.FirstDate;

            fixture.AddOutgoingGroup(date, memberCount: 2, positionCount: 4);

            var result = fixture.Run();

            Assert.True(result.OccurrenceRowCount > 0);

            var columns = fixture.Rows(
                "SELECT name FROM pragma_table_info('AttachmentCandidateOccurrenceEvidence');");

            Assert.Equal(
                [
                    "AttachmentCandidateOccurrenceEvidenceID",
                    "AttachmentCandidateID",
                    "MediaFileID",
                    "TokenGrammarSupported",
                    "SequenceToken",
                    "DateTokenKeyUniqueToPayload",
                    "OrderConsistencyAnnotation",
                ],
                columns);

            // No writer statement mentions an arrangement at all, and the occurrence insert — the one
            // statement that writes the rows the count chose an annotation for — carries no numeric
            // quantity beyond the token itself.
            var occurrenceStatement = Assert.Single(
                Phase7EvidenceWriter.Statements,
                statement => statement.Contains(
                    "AttachmentCandidateOccurrenceEvidence", StringComparison.Ordinal));

            foreach (var statement in Phase7EvidenceWriter.Statements)
            {
                Assert.DoesNotContain("Arrangement", statement, StringComparison.OrdinalIgnoreCase);
            }

            Assert.DoesNotContain("Count", occurrenceStatement, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Remaining", occurrenceStatement, StringComparison.OrdinalIgnoreCase);
        }

        // ---------------------------------------------------------------------------------------
        // The corrected order annotation, end to end.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void Run_ForcedPositionGroup_AnnotatesUniquelyConstrained()
        {
            using var fixture = new Phase7TestFixture();
            var date = Phase7TestFixture.FirstDate;

            var (attachments, assets) =
                fixture.AddOutgoingGroup(date, memberCount: 2, positionCount: 2);

            var result = fixture.Run();

            // T = M, so every rank's position is forced by shape alone.
            Assert.Equal(
                OrderConsistencyAnnotation.UniquelyConstrained,
                fixture.Annotation(result.RunID, attachments[0], assets[0]));

            Assert.Equal(
                OrderConsistencyAnnotation.UniquelyConstrained,
                fixture.Annotation(result.RunID, attachments[1], assets[1]));

            // ...and the group-shape flag records T = M, with a window one wide.
            Assert.Equal("2,0,1,0,0,1,1", fixture.Snapshot(result.RunID, attachments[0]));
            Assert.Equal("2,0,1,0,0,1,1", fixture.Snapshot(result.RunID, attachments[1]));
        }

        /// <remarks>
        /// The review counterexample, driven end to end: <c>T = 4 &gt; M = 2</c>, first rank at its
        /// highest admissible position. It is uniquely constrained <em>and</em>
        /// <c>IsForcedPositionGroupShape</c> is 0 — the two facts are no longer equated.
        /// </remarks>
        [Fact]
        public void Run_FirstRankAtItsHighestAdmissiblePosition_AnnotatesUniquelyConstrained()
        {
            using var fixture = new Phase7TestFixture();
            var date = Phase7TestFixture.FirstDate;

            var (attachments, assets) =
                fixture.AddOutgoingGroup(date, memberCount: 2, positionCount: 4);

            var result = fixture.Run();

            Assert.Equal(
                OrderConsistencyAnnotation.UniquelyConstrained,
                fixture.Annotation(result.RunID, attachments[0], assets[2]));

            Assert.Equal("4,0,1,0,0,0,3", fixture.Snapshot(result.RunID, attachments[0]));
        }

        [Fact]
        public void Run_LastRankAtItsLowestAdmissiblePosition_AnnotatesUniquelyConstrained()
        {
            using var fixture = new Phase7TestFixture();
            var date = Phase7TestFixture.FirstDate;

            var (attachments, assets) =
                fixture.AddOutgoingGroup(date, memberCount: 2, positionCount: 4);

            var result = fixture.Run();

            Assert.Equal(
                OrderConsistencyAnnotation.UniquelyConstrained,
                fixture.Annotation(result.RunID, attachments[1], assets[1]));

            Assert.Equal("4,0,1,0,0,0,3", fixture.Snapshot(result.RunID, attachments[1]));
        }

        [Fact]
        public void Run_PositionWithSeveralContainingArrangements_AnnotatesCompatibleWithAmbiguity()
        {
            using var fixture = new Phase7TestFixture();
            var date = Phase7TestFixture.FirstDate;

            var (attachments, assets) =
                fixture.AddOutgoingGroup(date, memberCount: 2, positionCount: 4);

            var result = fixture.Run();

            // Rank 0 at position 1: two arrangements contain it.
            Assert.Equal(
                OrderConsistencyAnnotation.CompatibleWithAmbiguity,
                fixture.Annotation(result.RunID, attachments[0], assets[1]));

            // Rank 1 at position 2: likewise.
            Assert.Equal(
                OrderConsistencyAnnotation.CompatibleWithAmbiguity,
                fixture.Annotation(result.RunID, attachments[1], assets[2]));
        }

        [Fact]
        public void Run_PositionOutsideTheWindow_AnnotatesNoCompatibleArrangement()
        {
            using var fixture = new Phase7TestFixture();
            var date = Phase7TestFixture.FirstDate;

            var (attachments, assets) =
                fixture.AddOutgoingGroup(date, memberCount: 2, positionCount: 4);

            var result = fixture.Run();

            // Rank 0 cannot reach the last position, and rank 1 cannot reach the first.
            Assert.Equal(
                OrderConsistencyAnnotation.NoCompatibleArrangement,
                fixture.Annotation(result.RunID, attachments[0], assets[3]));

            Assert.Equal(
                OrderConsistencyAnnotation.NoCompatibleArrangement,
                fixture.Annotation(result.RunID, attachments[1], assets[0]));
        }

        /// <remarks>
        /// Every admissible position of a single-member group is uniquely constrained, and the paired
        /// ranking assertion is what shows this inflates nothing.
        /// </remarks>
        [Fact]
        public void Run_SingleMessageGroupWithSeveralPositions_AnnotatesUniquelyConstrained()
        {
            using var fixture = new Phase7TestFixture();
            var date = Phase7TestFixture.FirstDate;

            var (attachments, assets) =
                fixture.AddOutgoingGroup(date, memberCount: 1, positionCount: 3);

            var result = fixture.Run();

            foreach (var asset in assets)
            {
                Assert.Equal(
                    OrderConsistencyAnnotation.UniquelyConstrained,
                    fixture.Annotation(result.RunID, attachments[0], asset));
            }

            // Three uniquely-constrained candidates, all of identical strength, so one tier.
            var ranked = fixture.Rank(result.RunID, attachments[0]);

            Assert.Equal(3, Assert.Single(ranked.Tiers).Count);
        }

        /// <remarks>
        /// An infeasible group reads <c>NotAssessable</c>, not <c>NoCompatibleArrangement</c>. In such a
        /// group no admissible arrangement exists for anyone, and reading that as a per-candidate
        /// negative would attribute to each candidate a weakness whose cause the evidence cannot
        /// separate. The group fact is recorded instead, unranked.
        /// </remarks>
        [Fact]
        public void Run_InfeasibleGroup_IsNotAssessableRatherThanNoCompatibleArrangement()
        {
            using var fixture = new Phase7TestFixture();
            var date = Phase7TestFixture.FirstDate;

            var (attachments, assets) =
                fixture.AddOutgoingGroup(date, memberCount: 3, positionCount: 2);

            var result = fixture.Run();

            foreach (var attachment in attachments)
            {
                foreach (var asset in assets)
                {
                    Assert.Equal(
                        OrderConsistencyAnnotation.NotAssessable,
                        fixture.Annotation(result.RunID, attachment, asset));
                }

                // T < M: the group fact is recorded here, and read by nothing that ranks.
                Assert.Equal("2,0,1,0,1,0,<null>", fixture.Snapshot(result.RunID, attachment));

                foreach (var support in fixture.Support(result.RunID, attachment))
                {
                    Assert.Equal(Phase7OrderSupport.Unavailable, support.OrderSupport);
                    Assert.NotEqual(Phase7OrderSupport.LessSupportive, support.OrderSupport);
                }
            }
        }

        [Fact]
        public void Run_AmbiguousAndUniquelyConstrained_ArePersistedAsDifferentValues()
        {
            using var fixture = new Phase7TestFixture();
            var date = Phase7TestFixture.FirstDate;

            var (attachments, assets) =
                fixture.AddOutgoingGroup(date, memberCount: 2, positionCount: 4);

            var result = fixture.Run();

            // The explanatory distinction survives in storage, for audit and display.
            Assert.Equal(
                OrderConsistencyAnnotation.CompatibleWithAmbiguity,
                fixture.Annotation(result.RunID, attachments[0], assets[1]));

            Assert.Equal(
                OrderConsistencyAnnotation.UniquelyConstrained,
                fixture.Annotation(result.RunID, attachments[0], assets[2]));
        }
    }
}
