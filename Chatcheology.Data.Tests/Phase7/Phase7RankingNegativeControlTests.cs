using System.Reflection;
using Chatcheology.Data.Phase7;

namespace Chatcheology.Data.Tests.Phase7
{
    /// <summary>
    /// The bonuses the frozen design forbids, each proved absent by a negative control.
    /// </summary>
    /// <remarks>
    /// <strong>Every control here asserts both halves:</strong> production's exact expected outcome,
    /// and that a locally-written mutant reduction or comparator produces a different one. A control
    /// asserting only that the mutant differs would still pass if production drifted to some third
    /// behaviour, which is the failure mode these tests exist to catch.
    /// </remarks>
    public class Phase7RankingNegativeControlTests
    {
        // ---------------------------------------------------------------------------------------
        // What the ranking input can hold at all.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void Phase7CandidateSupport_ExposesExactlyThreeProperties()
        {
            var properties = typeof(Phase7CandidateSupport)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(property => property.Name)
                .Order()
                .ToList();

            Assert.Equal(["Direction", "MediaAssetID", "OrderSupport"], properties);
        }

        [Fact]
        public void Phase7DirectionSupport_HasExactlyTwoMembers() =>
            Assert.Equal(["Unavailable", "Supportive"], Enum.GetNames<Phase7DirectionSupport>());

        [Fact]
        public void Phase7OrderSupport_HasExactlyThreeMembers() =>
            Assert.Equal(
                ["Unavailable", "LessSupportive", "Supportive"],
                Enum.GetNames<Phase7OrderSupport>());

        [Fact]
        public void OrderConsistencyAnnotation_HasExactlyTheFourFrozenValues() =>
            Assert.Equal(
                [
                    "NotAssessable",
                    "NoCompatibleArrangement",
                    "CompatibleWithAmbiguity",
                    "UniquelyConstrained",
                ],
                Enum.GetNames<OrderConsistencyAnnotation>());

        [Fact]
        public void Phase7SupportDimension_HasExactlyTwoMembers() =>
            Assert.Equal(
                ["Direction", "OrderSupport"], Enum.GetNames<Phase7SupportDimension>());

        // ---------------------------------------------------------------------------------------
        // No cherry-picking.
        // ---------------------------------------------------------------------------------------

        /// <remarks>
        /// The candidate whose two physical copies read differently. Production leaves the dimension
        /// unavailable; a best-occurrence reduction would call it supportive, which is a profile no
        /// single <c>MediaFile</c> witnesses.
        /// </remarks>
        [Fact]
        public void Ranking_CherryPickingBestOccurrence_WouldHaveChangedTheOutcome()
        {
            using var fixture = new Phase7TestFixture();
            var date = Phase7TestFixture.FirstDate;

            var (attachments, disagreeing) =
                Phase7SupportReductionTests.BuildDisagreementFixture(fixture, date);

            var result = fixture.Run();

            var annotations = AnnotationsOf(fixture, result.RunID, attachments[1], disagreeing);

            Assert.Equal(2, annotations.Count);

            var production = Phase7SupportReader.ReduceOrder(annotations);
            var cherryPicked = ReduceByBestOccurrence(annotations);

            // (i) production's exact expected outcome...
            Assert.Equal(Phase7OrderSupport.Unavailable, production);

            // ...and (ii) the mutant genuinely differs.
            Assert.Equal(Phase7OrderSupport.Supportive, cherryPicked);
            Assert.NotEqual(production, cherryPicked);
        }

        // ---------------------------------------------------------------------------------------
        // No uniqueness bonus.
        // ---------------------------------------------------------------------------------------

        /// <remarks>
        /// Driven end to end from a <c>T &gt; M</c> edge-position fixture, so the corrected
        /// classification is proved not to have acquired ranking weight along with its new visibility.
        /// </remarks>
        [Fact]
        public void Ranking_AUniquenessBonus_WouldHaveChangedTheOutcome()
        {
            using var fixture = new Phase7TestFixture();
            var date = Phase7TestFixture.FirstDate;

            var (attachments, assets) =
                fixture.AddOutgoingGroup(date, memberCount: 2, positionCount: 4);

            var result = fixture.Run();

            // Rank 0: position 1 is ambiguous, position 2 is uniquely constrained.
            var ambiguous = fixture.Annotation(result.RunID, attachments[0], assets[1]);
            var unique = fixture.Annotation(result.RunID, attachments[0], assets[2]);

            Assert.Equal(OrderConsistencyAnnotation.CompatibleWithAmbiguity, ambiguous);
            Assert.Equal(OrderConsistencyAnnotation.UniquelyConstrained, unique);

            var support = fixture.Support(result.RunID, attachments[0]);

            var ambiguousSupport = Assert.Single(support, entry => entry.MediaAssetID == assets[1]);
            var uniqueSupport = Assert.Single(support, entry => entry.MediaAssetID == assets[2]);

            // (i) production ties them...
            Assert.Equal(Phase7OrderSupport.Supportive, ambiguousSupport.OrderSupport);
            Assert.Equal(Phase7OrderSupport.Supportive, uniqueSupport.OrderSupport);

            Assert.Equal(
                Phase7SupportComparison.Incomparable,
                Phase7SupportRanking.Compare(uniqueSupport, ambiguousSupport));

            var tier = fixture.Rank(result.RunID, attachments[0]).Tiers[0]
                .Select(candidate => candidate.MediaAssetID)
                .ToList();

            Assert.Contains(assets[1], tier);
            Assert.Contains(assets[2], tier);

            // ...and (ii) a uniqueness-preferring comparator would have ordered them.
            Assert.Equal(
                Phase7SupportComparison.LeftPreferred, CompareWithUniquenessBonus(unique, ambiguous));
        }

        /// <remarks>
        /// The stored token-uniqueness fact is an availability gate, never positive support. A
        /// candidate whose key is unique gains nothing over one whose key collides: the collided
        /// candidate's order support is simply unavailable, so the two are incomparable rather than
        /// ordered.
        /// <para>
        /// <strong>Recorded deviation, with its reason.</strong> The frozen acceptance wording — "two
        /// otherwise-identical candidates differing only in <c>DateTokenKeyUniqueToPayload</c> produce
        /// the same ranking position" — is <em>unconstructible</em> under the Stage 7.0 CHECK
        /// constraints: grammar present forces the column non-null, and a value of 0 forces
        /// <c>NotAssessable</c>, so two rows cannot differ in that column alone. The intent is
        /// preserved by two strictly stronger structural proofs — the field is absent from the ranking
        /// input type and absent from the reader's SQL — plus this constructible availability test.
        /// </para>
        /// </remarks>
        [Fact]
        public void Ranking_TokenUniqueness_IsNeverPositiveSupport()
        {
            using var fixture = new Phase7TestFixture();
            var date = Phase7TestFixture.FirstDate;

            var attachment = fixture.AddOutgoing(date);

            // Two payloads colliding on one (date, token) key, and one payload with a key of its own.
            var collidingFirst = fixture.AddTokenAsset(date, isSent: true, 8);
            var collidingSecond = fixture.AddTokenAsset(date, isSent: true, 8);
            var unique = fixture.AddTokenAsset(date, isSent: true, 9);

            var result = fixture.Run();

            var support = fixture.Support(result.RunID, attachment);

            var uniqueSupport = Assert.Single(support, entry => entry.MediaAssetID == unique);
            var collided = Assert.Single(support, entry => entry.MediaAssetID == collidingFirst);

            Assert.Equal(Phase7OrderSupport.Unavailable, collided.OrderSupport);

            // Incomparable, not ordered: the unique key bought the other candidate nothing.
            Assert.Equal(
                Phase7SupportComparison.Incomparable,
                Phase7SupportRanking.Compare(uniqueSupport, collided));

            Assert.Equal(
                3, Assert.Single(fixture.Rank(result.RunID, attachment).Tiers).Count);

            Assert.Equal(
                Phase7OrderSupport.Unavailable,
                Assert.Single(support, entry => entry.MediaAssetID == collidingSecond).OrderSupport);
        }

        // ---------------------------------------------------------------------------------------
        // No narrowing-count bonus.
        // ---------------------------------------------------------------------------------------

        /// <remarks>
        /// Two attachments in groups of different window widths, whose candidates are otherwise
        /// identical, produce identical support. A smaller surviving count is not a claim that any
        /// individual candidate is more likely to be correct.
        /// </remarks>
        [Fact]
        public void Ranking_ANarrowingBonus_WouldHaveChangedTheOutcome()
        {
            using var fixture = new Phase7TestFixture();

            var narrow = Phase7TestFixture.Day(0);
            var wide = Phase7TestFixture.Day(5);

            var (narrowAttachments, narrowAssets) =
                fixture.AddOutgoingGroup(narrow, memberCount: 1, positionCount: 1);

            var (wideAttachments, wideAssets) =
                fixture.AddOutgoingGroup(wide, memberCount: 1, positionCount: 4);

            var result = fixture.Run();

            var narrowWidth = fixture.Snapshot(result.RunID, narrowAttachments[0]).Split(',')[^1];
            var wideWidth = fixture.Snapshot(result.RunID, wideAttachments[0]).Split(',')[^1];

            Assert.Equal("1", narrowWidth);
            Assert.Equal("4", wideWidth);
            Assert.NotEqual(narrowWidth, wideWidth);

            var narrowSupport = Assert.Single(
                fixture.Support(result.RunID, narrowAttachments[0]),
                entry => entry.MediaAssetID == narrowAssets[0]);

            var wideSupport = Assert.Single(
                fixture.Support(result.RunID, wideAttachments[0]),
                entry => entry.MediaAssetID == wideAssets[0]);

            // (i) production reads them identically, despite the different window widths...
            Assert.Equal(narrowSupport.Direction, wideSupport.Direction);
            Assert.Equal(narrowSupport.OrderSupport, wideSupport.OrderSupport);

            Assert.Equal(
                Phase7SupportComparison.Incomparable,
                Phase7SupportRanking.Compare(narrowSupport, wideSupport));

            // ...and (ii) a smaller-count-is-better comparator would have ordered them.
            Assert.Equal(
                Phase7SupportComparison.LeftPreferred,
                CompareByNarrowerWindow(
                    int.Parse(narrowWidth), int.Parse(wideWidth)));
        }

        [Fact]
        public void Ranking_NarrowingCount_IsNeverRead()
        {
            foreach (var statement in Phase7SupportReader.Statements)
            {
                Assert.DoesNotContain(
                    "RemainingCompatiblePositionCountUnderHypothesis",
                    statement,
                    StringComparison.Ordinal);
            }

            Assert.DoesNotContain(
                "Remaining",
                string.Join(
                    " ",
                    typeof(Phase7CandidateSupport)
                        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                        .Select(property => property.Name)),
                StringComparison.OrdinalIgnoreCase);
        }

        // ---------------------------------------------------------------------------------------
        // No forced-position bonus, and no group-flag bonus.
        // ---------------------------------------------------------------------------------------

        /// <remarks>
        /// A candidate from a <c>T = M</c> group against one from a <c>T &gt; M</c> group, both
        /// uniquely constrained. Production ties them; a forced-group-is-better comparator would not.
        /// The closure is blunt about why: "treating a forced position as a match would be reading
        /// arithmetic as evidence".
        /// </remarks>
        [Fact]
        public void Ranking_AForcedPositionBonus_WouldHaveChangedTheOutcome()
        {
            using var fixture = new Phase7TestFixture();

            var forcedDate = Phase7TestFixture.Day(0);
            var slackDate = Phase7TestFixture.Day(5);

            var (forcedAttachments, forcedAssets) =
                fixture.AddOutgoingGroup(forcedDate, memberCount: 1, positionCount: 1);

            var (slackAttachments, slackAssets) =
                fixture.AddOutgoingGroup(slackDate, memberCount: 2, positionCount: 4);

            var result = fixture.Run();

            var forcedFlag = fixture.Snapshot(result.RunID, forcedAttachments[0]).Split(',')[5];
            var slackFlag = fixture.Snapshot(result.RunID, slackAttachments[0]).Split(',')[5];

            Assert.Equal("1", forcedFlag);
            Assert.Equal("0", slackFlag);

            // Both uniquely constrained, from groups of different shape.
            Assert.Equal(
                OrderConsistencyAnnotation.UniquelyConstrained,
                fixture.Annotation(result.RunID, forcedAttachments[0], forcedAssets[0]));

            Assert.Equal(
                OrderConsistencyAnnotation.UniquelyConstrained,
                fixture.Annotation(result.RunID, slackAttachments[0], slackAssets[2]));

            var forcedSupport = Assert.Single(
                fixture.Support(result.RunID, forcedAttachments[0]),
                entry => entry.MediaAssetID == forcedAssets[0]);

            var slackSupport = Assert.Single(
                fixture.Support(result.RunID, slackAttachments[0]),
                entry => entry.MediaAssetID == slackAssets[2]);

            // (i) production ties them...
            Assert.Equal(
                Phase7SupportComparison.Incomparable,
                Phase7SupportRanking.Compare(forcedSupport, slackSupport));

            // ...and (ii) a forced-group-preferring comparator would not.
            Assert.Equal(
                Phase7SupportComparison.LeftPreferred,
                CompareByForcedGroupShape(forcedFlag == "1", slackFlag == "1"));
        }

        [Fact]
        public void Ranking_GroupShapeFlags_AreNeverRead()
        {
            foreach (var statement in Phase7SupportReader.Statements)
            {
                Assert.DoesNotContain(
                    "IsForcedPositionGroupShape", statement, StringComparison.Ordinal);

                Assert.DoesNotContain(
                    "IsWithinDirectionImpossibleGroup", statement, StringComparison.Ordinal);
            }
        }

        // ---------------------------------------------------------------------------------------
        // The mutants. Each is local to this file and exists only to be different from production.
        // ---------------------------------------------------------------------------------------

        /// <summary>The cherry-picking reduction the frozen design forbids.</summary>
        private static Phase7OrderSupport ReduceByBestOccurrence(
            IReadOnlyList<OrderConsistencyAnnotation> annotations) =>
            annotations.Any(
                annotation => annotation is OrderConsistencyAnnotation.CompatibleWithAmbiguity
                    or OrderConsistencyAnnotation.UniquelyConstrained)
                ? Phase7OrderSupport.Supportive
                : annotations.Any(
                    annotation =>
                        annotation == OrderConsistencyAnnotation.NoCompatibleArrangement)
                    ? Phase7OrderSupport.LessSupportive
                    : Phase7OrderSupport.Unavailable;

        /// <summary>The uniqueness bonus the frozen design forbids.</summary>
        private static Phase7SupportComparison CompareWithUniquenessBonus(
            OrderConsistencyAnnotation left, OrderConsistencyAnnotation right)
        {
            static int Strength(OrderConsistencyAnnotation annotation) => annotation switch
            {
                OrderConsistencyAnnotation.UniquelyConstrained => 3,
                OrderConsistencyAnnotation.CompatibleWithAmbiguity => 2,
                OrderConsistencyAnnotation.NoCompatibleArrangement => 1,
                _ => 0,
            };

            return Strength(left).CompareTo(Strength(right)) switch
            {
                > 0 => Phase7SupportComparison.LeftPreferred,
                < 0 => Phase7SupportComparison.RightPreferred,
                _ => Phase7SupportComparison.Incomparable,
            };
        }

        /// <summary>The narrowing bonus the frozen design forbids.</summary>
        private static Phase7SupportComparison CompareByNarrowerWindow(int left, int right) =>
            left.CompareTo(right) switch
            {
                < 0 => Phase7SupportComparison.LeftPreferred,
                > 0 => Phase7SupportComparison.RightPreferred,
                _ => Phase7SupportComparison.Incomparable,
            };

        /// <summary>The forced-position bonus the frozen design forbids.</summary>
        private static Phase7SupportComparison CompareByForcedGroupShape(bool left, bool right) =>
            (left, right) switch
            {
                (true, false) => Phase7SupportComparison.LeftPreferred,
                (false, true) => Phase7SupportComparison.RightPreferred,
                _ => Phase7SupportComparison.Incomparable,
            };

        private static List<OrderConsistencyAnnotation> AnnotationsOf(
            Phase7TestFixture fixture, long runID, long attachmentID, long mediaAssetID) =>
            [
                .. fixture.Rows(
                    $"""
                    SELECT o.OrderConsistencyAnnotation
                    FROM AttachmentCandidateOccurrenceEvidence AS o
                    INNER JOIN AttachmentCandidate AS c
                        ON c.AttachmentCandidateID = o.AttachmentCandidateID
                    WHERE c.RunID = {runID}
                      AND c.AttachmentID = {attachmentID}
                      AND c.MediaAssetID = {mediaAssetID}
                    ORDER BY o.MediaFileID;
                    """)
                    .Select(Enum.Parse<OrderConsistencyAnnotation>),
            ];
    }
}
