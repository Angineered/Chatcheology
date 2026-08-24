using Chatcheology.Data.Media;
using Chatcheology.Data.Sequence;

namespace Chatcheology.Data.Tests.Sequence
{
    /// <summary>
    /// Tests the immutable per-pair snapshot the Stage B2C-0 gate offers, which is what the Stage B2C-1
    /// alignment stage reads instead of building the two direction streams a second time.
    /// </summary>
    /// <remarks>
    /// The properties that matter are not "the snapshot has fields". They are that the streams are the
    /// gate's own, in the gate's own order; that the pair state and both determinacy classifications
    /// travel with them, so an excluded pair cannot be readmitted by a consumer looking only at the
    /// streams; that a token-only date is offered at all, because an adjacent-date control needs it; and
    /// that the coverage counts are attributed to the date they belong to.
    /// </remarks>
    public class DirectionSequenceSnapshotTests
    {
        private static readonly DateOnly First = DirectionSequenceGateFixture.FirstDate;

        // ---------------------------------------------------------------------------------------
        // The streams are the gate's own.
        // ---------------------------------------------------------------------------------------

        /// <remarks>
        /// Message order is <c>SequenceNumber</c> then <c>Ordinal</c>, which for one message per
        /// attachment is send order. Three attachments in a known direction order must arrive in that
        /// order and no other.
        /// </remarks>
        [Fact]
        public void TheMessageStreamIsInTheGatesDeclaredOrder()
        {
            using var fixture = new DirectionSequenceGateFixture();

            fixture.AddOutgoing(First, "09:00:00");
            fixture.AddIncoming(First, "09:01:00");
            fixture.AddOutgoing(First, "09:02:00");
            fixture.AddToken(First, 1, isSent: true);

            Assert.Equal([true, false, true], At(fixture, First).Pattern);
        }

        /// <remarks>
        /// Token order is the supported four-digit value, which the grammar's fixed width makes
        /// identical to the digit string. Inserted out of order, they must still be read in order.
        /// </remarks>
        [Fact]
        public void TheTokenStreamIsOrderedByTheSupportedTokenValue()
        {
            using var fixture = new DirectionSequenceGateFixture();

            fixture.AddOutgoing(First);

            fixture.AddToken(First, 9, isSent: false);
            fixture.AddToken(First, 2, isSent: true);
            fixture.AddToken(First, 40, isSent: false);
            fixture.AddToken(First, 7, isSent: true);

            // 0002 O, 0007 O, 0009 I, 0040 I
            Assert.Equal([true, true, false, false], At(fixture, First).EmittedTokens);
        }

        /// <remarks>
        /// A position no copy of which records direction emits nothing and is counted, and a position
        /// whose copies disagree emits nothing and is counted separately. Neither is coerced into a
        /// symbol on the way to the snapshot.
        /// </remarks>
        [Fact]
        public void UnlabelledAndConflictingPositionsEmitNoSymbol()
        {
            using var fixture = new DirectionSequenceGateFixture();
            var second = fixture.AddSource();

            fixture.AddOutgoing(First);

            fixture.AddToken(First, 1, isSent: true);
            fixture.AddToken(First, 2, isSent: null);
            fixture.AddToken(First, 3, isSent: true);
            fixture.AddToken(First, 3, isSent: false, mediaSourceID: second);

            var snapshot = At(fixture, First);

            Assert.Equal([true], snapshot.EmittedTokens);
            Assert.Equal(1, snapshot.UnlabelledPositionCount);
            Assert.Equal(1, snapshot.ConflictingPositionCount);
        }

        /// <remarks>
        /// Equivalent same-direction observations of one logical position collapse to one symbol. Three
        /// copies of one position across two sources are one position, not three.
        /// </remarks>
        [Fact]
        public void EquivalentObservationsCollapseToOneSymbol()
        {
            using var fixture = new DirectionSequenceGateFixture();
            var second = fixture.AddSource();

            fixture.AddOutgoing(First);

            fixture.AddToken(First, 1, isSent: true);
            fixture.AddToken(First, 1, isSent: true);
            fixture.AddToken(First, 1, isSent: true, mediaSourceID: second);

            var snapshot = At(fixture, First);

            Assert.Equal([true], snapshot.EmittedTokens);
            Assert.Equal(3, snapshot.OutgoingCopiesBeforeCollapse);
            Assert.Equal(0, snapshot.IncomingCopiesBeforeCollapse);
        }

        // ---------------------------------------------------------------------------------------
        // Pair state travels with the streams.
        // ---------------------------------------------------------------------------------------

        /// <remarks>
        /// The property the extraction rests on. A conflicting pair still emits symbols for its
        /// non-conflicting positions, so a consumer judging eligibility from the streams would readmit
        /// it. The row says otherwise, and the row is what a population must be selected on.
        /// </remarks>
        [Fact]
        public void AnExcludedPairArrivesWithItsExclusionAndStillHasSymbols()
        {
            using var fixture = new DirectionSequenceGateFixture();
            var second = fixture.AddSource();

            fixture.AddOutgoing(First, "09:00:00");
            fixture.AddIncoming(First, "09:01:00");

            fixture.AddToken(First, 1, isSent: true);
            fixture.AddToken(First, 2, isSent: false);
            fixture.AddToken(First, 3, isSent: true);
            fixture.AddToken(First, 3, isSent: false, mediaSourceID: second);

            var snapshot = At(fixture, First);

            Assert.Equal(
                DirectionSequencePairState.ExcludedByDirectionConflict, snapshot.Row!.State);

            Assert.NotEmpty(snapshot.EmittedTokens);
        }

        /// <remarks>
        /// A classified pair carries both determinacy classifications and the exact reference counts the
        /// accumulator computed, so the alignment stage never recomputes an own-date reference.
        /// </remarks>
        [Fact]
        public void AClassifiedPairArrivesWithItsDeterminacyAndExactReferenceCounts()
        {
            using var fixture = Informative();

            var row = At(fixture, First).Row;

            Assert.NotNull(row);
            Assert.Equal(DirectionSequencePairState.Classified, row.State);
            Assert.True(row.ArrangementCount > 0);
            Assert.True(row.AdmittingArrangementCount > 0);
            Assert.True(row.EmbeddingPairCount > 0);
            Assert.True(row.SquaredEmbeddingCount > 0);
            Assert.Equal(DirectionSequenceDeterminacyClass.Informative, row.BinaryClass);
        }

        // ---------------------------------------------------------------------------------------
        // The universe offered, including neighbour dates.
        // ---------------------------------------------------------------------------------------

        /// <remarks>
        /// A date holding tokens but no attachment carries no pair row — the gate offers none — yet its
        /// token side is exactly what a <c>D-1</c> or <c>D+1</c> control needs. It must still be
        /// offered, with a null row and an empty message stream.
        /// </remarks>
        [Fact]
        public void ATokenOnlyDateIsOfferedWithNoRow()
        {
            using var fixture = new DirectionSequenceGateFixture();

            fixture.AddOutgoing(DirectionSequenceGateFixture.Day(1));

            fixture.AddToken(DirectionSequenceGateFixture.Day(1), 5, isSent: true);
            fixture.AddToken(First, 1, isSent: true);
            fixture.AddToken(First, 2, isSent: false);

            var neighbour = At(fixture, First);

            Assert.Null(neighbour.Row);
            Assert.Empty(neighbour.Pattern);
            Assert.Equal([true, false], neighbour.EmittedTokens);
        }

        /// <remarks>
        /// One snapshot per pair of the universe, per scope, and never two for one pair: the alignment
        /// stage indexes them by <c>(scope, scope key, date)</c> and a duplicate would make that index
        /// lossy.
        /// </remarks>
        [Fact]
        public void EachScopeKeyAndDateIsOfferedExactlyOncePerScope()
        {
            using var fixture = Informative();
            fixture.AddSource();

            var snapshots = fixture.Snapshots();

            Assert.NotEmpty(snapshots);

            Assert.Equal(
                snapshots.Count,
                snapshots
                    .Select(snapshot => (snapshot.Scope, snapshot.ScopeKey, snapshot.Date))
                    .Distinct()
                    .Count());

            Assert.Contains(snapshots, snapshot => snapshot.Scope == ScopeLevel.SourceDate);
            Assert.Contains(snapshots, snapshot => snapshot.Scope == ScopeLevel.DeviceGroupDate);
        }

        /// <remarks>
        /// The snapshot's streams are copies. Mutating what a consumer was handed must not reach the
        /// next census, because the gate caches its message patterns per date and shares its logical
        /// positions between the two scopes.
        /// </remarks>
        [Fact]
        public void TheSnapshotStreamsAreCopies()
        {
            using var fixture = Informative();

            var first = fixture.Snapshots().First(snapshot => snapshot.Pattern.Length > 0);

            first.Pattern[0] = !first.Pattern[0];
            Array.Fill(first.EmittedTokens, false);

            var again = fixture.Snapshots().First(
                snapshot => snapshot.Scope == first.Scope
                    && snapshot.ScopeKey == first.ScopeKey
                    && snapshot.Date == first.Date);

            Assert.NotEqual(first.Pattern, again.Pattern);
        }

        // ---------------------------------------------------------------------------------------
        // Grammar-coverage side-channel.
        // ---------------------------------------------------------------------------------------

        /// <remarks>
        /// A dated name whose marker agrees with the column but whose suffix is not four ASCII digits is
        /// the only grammar failure that can be attributed to a date, and it is counted there.
        /// </remarks>
        [Fact]
        public void ADatedUnsupportedNameIsCountedAgainstItsOwnDate()
        {
            using var fixture = new DirectionSequenceGateFixture();

            fixture.AddOutgoing(First);
            fixture.AddOutgoing(DirectionSequenceGateFixture.Day(1));

            fixture.AddToken(First, 1, isSent: true);
            fixture.AddNamed(First, "0010-1", isSent: true);
            fixture.AddNamed(First, "abcd", isSent: true);
            fixture.AddToken(DirectionSequenceGateFixture.Day(1), 2, isSent: true);

            Assert.Equal(2, At(fixture, First).UnsupportedTokenObservationCount);

            Assert.Equal(
                0, At(fixture, DirectionSequenceGateFixture.Day(1)).UnsupportedTokenObservationCount);
        }

        /// <remarks>
        /// An undated observation has no date to attribute a grammar failure to — the gate leaves the
        /// read before one exists — so it must raise no pair's count. Its loss is visible only in the
        /// archive-wide Stage A coverage figures.
        /// </remarks>
        [Fact]
        public void AnUndatedUnsupportedObservationRaisesNoPairsCount()
        {
            using var fixture = new DirectionSequenceGateFixture();

            fixture.AddOutgoing(First);
            fixture.AddToken(First, 1, isSent: true);
            fixture.AddUndated();
            fixture.AddUndated();

            Assert.All(
                fixture.Snapshots(),
                snapshot => Assert.Equal(0, snapshot.UnsupportedTokenObservationCount));

            var census = fixture.Analyse();
            var source = census.Sources.Single();

            Assert.Equal(3, source.PhysicalObservationCount);
            Assert.Equal(1, source.SupportedTokenObservationCount);
        }

        /// <remarks>
        /// Observations rather than positions, so a device group's count is the sum of its sources': two
        /// sources each losing a name on one date lost two observations, whatever those names were.
        /// </remarks>
        [Fact]
        public void ADeviceGroupsGrammarLossIsTheSumOfItsSources()
        {
            using var fixture = new DirectionSequenceGateFixture();
            var second = fixture.AddSource();

            fixture.AddOutgoing(First);

            fixture.AddToken(First, 1, isSent: true);
            fixture.AddNamed(First, "0010-1", isSent: true);
            fixture.AddNamed(First, "0010-1", isSent: true, mediaSourceID: second);

            Assert.Equal(2, At(fixture, First).UnsupportedTokenObservationCount);

            var perSource = fixture.Snapshots()
                .Where(snapshot => snapshot.Scope == ScopeLevel.SourceDate && snapshot.Date == First)
                .Select(snapshot => snapshot.UnsupportedTokenObservationCount)
                .Order()
                .ToList();

            Assert.Equal([1, 1], perSource);
        }

        /// <remarks>
        /// Source scope and device-group scope are projected by the same rule the position maps use, so
        /// a grouping that separates the sources separates their grammar loss too.
        /// </remarks>
        [Fact]
        public void GrammarLossFollowsTheDeclaredGrouping()
        {
            using var fixture = new DirectionSequenceGateFixture();
            var second = fixture.AddSource();

            fixture.AddOutgoing(First);

            fixture.AddToken(First, 1, isSent: true);
            fixture.AddNamed(First, "0010-1", isSent: true);
            fixture.AddNamed(First, "0011-1", isSent: true, mediaSourceID: second);

            var separated = fixture.Snapshots(fixture.OneGroupPerSource())
                .Where(
                    snapshot => snapshot.Scope == ScopeLevel.DeviceGroupDate
                        && snapshot.Date == First)
                .Select(snapshot => snapshot.UnsupportedTokenObservationCount)
                .Order()
                .ToList();

            Assert.Equal([1, 1], separated);
        }

        // ---------------------------------------------------------------------------------------
        // Helpers.
        // ---------------------------------------------------------------------------------------

        /// <summary>The device-group-scope snapshot for one date, of which there is exactly one.</summary>
        private static DirectionSequencePairSnapshot At(
            DirectionSequenceGateFixture fixture, DateOnly date) =>
            fixture.Snapshots()
                .Single(
                    snapshot => snapshot.Scope == ScopeLevel.DeviceGroupDate
                        && snapshot.Date == date);

        /// <summary>
        /// A workspace whose first date carries a binary-informative pair.
        /// </summary>
        /// <remarks>
        /// The message pattern is <c>OI</c> and the token side is <c>OOII</c>: two outgoing, two
        /// incoming, two runs. That class holds exactly two arrangements — <c>OOII</c> and <c>IIOO</c> —
        /// and only the first admits <c>OI</c>, so <c>q_r</c> is one half and the pair is informative
        /// rather than determinate. An alternating token side would not do: <c>OIOI</c> and <c>IOIO</c>
        /// both admit <c>OI</c>, which is <c>q_r = 1</c>.
        /// </remarks>
        private static DirectionSequenceGateFixture Informative()
        {
            var fixture = new DirectionSequenceGateFixture();

            fixture.AddOutgoing(First, "09:00:00");
            fixture.AddIncoming(First, "09:01:00");

            fixture.AddToken(First, 1, isSent: true);
            fixture.AddToken(First, 2, isSent: true);
            fixture.AddToken(First, 3, isSent: false);
            fixture.AddToken(First, 4, isSent: false);

            return fixture;
        }
    }
}
