using System.Collections;
using System.Globalization;
using System.Reflection;
using Chatcheology.Data.Sequence;

namespace Chatcheology.Data.Tests.Sequence
{
    /// <summary>
    /// Tests what the permanent alignment result is structurally unable to carry.
    /// </summary>
    /// <remarks>
    /// Written as a walk of the whole reachable object graph rather than as a list of fields nobody added,
    /// because the risk is a field somebody adds later. Individually harmless per-pair shape data
    /// reconstructs a private direction pattern when combined — a length of three with two outgoing and
    /// one incoming in three runs is uniquely one pattern, and the archive's median pattern is two symbols
    /// long — so the test is that the reconstructive material is not reachable at all.
    /// <para>
    /// The reconciliation subtree is the accepted gate census, whose token-side date coverage ranges are
    /// explicitly permitted preserved evidence: they carry no message-side shape beside them. The outcome
    /// sections are held to the stricter rule, which is where a date beside a pattern would actually be
    /// reconstructive.
    /// </para>
    /// </remarks>
    public class DirectionSequenceAlignmentPrivacyTests
    {
        private static readonly DateOnly First = DirectionSequenceGateFixture.FirstDate;

        // ---------------------------------------------------------------------------------------
        // What the outcome sections cannot carry.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void NoOutcomeSectionCarriesARealDate()
        {
            using var fixture = Populated();

            foreach (var (path, value) in Walk(OutcomeSections(fixture.AnalyseAlignment())))
            {
                Assert.False(
                    value is DateOnly or DateTime or DateTimeOffset,
                    $"{path} carries a date beside outcome shape data");
            }
        }

        /// <remarks>
        /// The raw streams stay in memory. A sequence of directions is the private pattern itself, so no
        /// result type may hold one — not as an array, not as a list, and not as a string of symbols.
        /// </remarks>
        [Fact]
        public void NoPartOfTheResultCarriesARawDirectionSequence()
        {
            using var fixture = Populated();

            foreach (var (path, value) in Walk(fixture.AnalyseAlignment()))
            {
                Assert.False(value is bool[], $"{path} carries a raw direction sequence");

                if (value is IEnumerable<bool>)
                {
                    Assert.Fail($"{path} carries a raw direction sequence");
                }

                if (value is string text)
                {
                    Assert.DoesNotContain("OIOI", text, StringComparison.Ordinal);
                    Assert.DoesNotContain("IOIO", text, StringComparison.Ordinal);
                }
            }
        }

        /// <remarks>
        /// Pair-level outcome rows are exactly what must not become permanent evidence: the anonymised
        /// pair identifier orders dates within a scope key, and the adjacent-date links make calendar
        /// neighbours explicit, so a table of them narrows real dates even without carrying one.
        /// </remarks>
        [Fact]
        public void NoPairLevelRowOrSnapshotIsReachableFromTheResult()
        {
            using var fixture = Populated();

            foreach (var (path, value) in Walk(OutcomeSections(fixture.AnalyseAlignment())))
            {
                Assert.False(
                    value is DirectionSequencePairSnapshot,
                    $"{path} carries a pair snapshot");

                Assert.False(
                    value is DirectionSequencePairRow,
                    $"{path} carries a pair-level outcome row");
            }
        }

        /// <remarks>
        /// Tested against the values this workspace actually holds rather than against words that might
        /// mean something private: the risk is a recovered name, a display name, a root path or a real
        /// date reaching the result, not the word "source" appearing in a sentence.
        /// </remarks>
        [Fact]
        public void NoPartOfTheResultCarriesAFileNameDisplayNamePathOrDate()
        {
            using var fixture = Populated();

            var own = DirectionSequenceGateFixture.Day(1);

            var forbidden = new[]
            {
                // Recovered names, exactly as the fixture wrote them.
                DirectionSequenceGateFixture.Name(own, DirectionSequenceGateFixture.Token(1)),
                DirectionSequenceGateFixture.Name(own, "0010-1"),

                // A display name and a root path the workspace holds.
                "Second synthetic source",
                fixture.Workspace.DatabasePath,

                // The real dates, in both the marker's format and the persisted one.
                own.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
                own.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                First.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
                First.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            };

            foreach (var (path, value) in Walk(fixture.AnalyseAlignment()))
            {
                if (value is not string text)
                {
                    continue;
                }

                foreach (var secret in forbidden)
                {
                    Assert.False(
                        text.Contains(secret, StringComparison.OrdinalIgnoreCase),
                        $"{path} carries \"{secret}\"");
                }
            }
        }

        /// <remarks>
        /// Every population is named, and the names are the frozen population labels rather than anything
        /// derived from the workspace.
        /// </remarks>
        [Fact]
        public void EveryOutcomeNamesItsPopulationAndCount()
        {
            using var fixture = Populated();

            var census = fixture.AnalyseAlignment();

            Assert.Equal("SupplyAssessed", census.SupplyLoss.PopulationName);
            Assert.Equal("ClassifiedPairs", census.OrderLoss.PopulationName);
            Assert.Equal("ClassifiedPairs", census.Multiplicity.PopulationName);
            Assert.Equal("ClassifiedPairs", census.Reversal.PopulationName);
            Assert.Equal("BinaryInformative", census.Binary.PopulationName);
            Assert.Equal("GradedInformative", census.Graded.PopulationName);

            Assert.True(census.SupplyLoss.PairCount > 0);
            Assert.True(census.OrderLoss.PairCount > 0);
            Assert.True(census.Binary.PairCount > 0);
            Assert.True(census.Graded.PairCount > 0);
        }

        // ---------------------------------------------------------------------------------------
        // Small-cell suppression.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void EveryPrintedDescriptiveCellIsAtOrAboveTheFloor()
        {
            using var fixture = Populated();

            var census = fixture.AnalyseAlignment();

            foreach (var split in census.DescriptiveSplits)
            {
                foreach (var cell in split.Cells)
                {
                    Assert.True(
                        cell.PairCount == 0 || cell.PairCount >= 10,
                        $"a printed {split.Dimension} cell holds {cell.PairCount} pair(s)");
                }
            }

            foreach (var control in census.BinaryAdjacentControls)
            {
                foreach (var cell in control.Composition.FullPopulation
                    .Concat(control.Composition.Intersection))
                {
                    Assert.True(cell.PairCount == 0 || cell.PairCount >= 10);
                }
            }
        }

        [Fact]
        public void ASuppressedCellIsCountedSoTheSplitStillReconciles()
        {
            using var fixture = Populated();

            foreach (var split in fixture.AnalyseAlignment().DescriptiveSplits)
            {
                Assert.Equal(split.PairCount, split.AccountedPairCount);

                Assert.Equal(
                    split.Cells.Count(cell => cell.Suppressed), split.SuppressedCellCount);
            }
        }

        // ---------------------------------------------------------------------------------------
        // The graph walk.
        // ---------------------------------------------------------------------------------------

        /// <summary>The outcome sections, excluding the accepted gate census.</summary>
        private static object[] OutcomeSections(DirectionSequenceAlignmentCensus census) =>
            [
                census.SupplyLoss,
                census.OrderLoss,
                census.Multiplicity,
                census.FailureAttribution,
                census.Graded,
                census.GradedDeterminateCheck,
                census.Binary,
                census.BinaryAdjacentControls,
                census.GradedAdjacentControls,
                census.Reversal,
                census.DescriptiveSplits,
                census.ScopeSensitivity,
            ];

        /// <summary>
        /// Every value reachable from a root by public property or by enumeration, with the path that
        /// reached it.
        /// </summary>
        private static IEnumerable<(string Path, object Value)> Walk(object root)
        {
            var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
            var pending = new Queue<(string Path, object Value)>();

            pending.Enqueue(("census", root));

            while (pending.Count > 0)
            {
                var (path, value) = pending.Dequeue();

                if (value is null || !seen.Add(value))
                {
                    continue;
                }

                yield return (path, value);

                if (value is string || value.GetType().IsPrimitive)
                {
                    continue;
                }

                if (value is IEnumerable sequence)
                {
                    var index = 0;

                    foreach (var item in sequence)
                    {
                        if (item is not null)
                        {
                            pending.Enqueue(($"{path}[{index}]", item));
                        }

                        index++;
                    }

                    continue;
                }

                foreach (var property in value.GetType()
                    .GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (property.GetIndexParameters().Length > 0)
                    {
                        continue;
                    }

                    var member = property.GetValue(value);

                    if (member is not null)
                    {
                        pending.Enqueue(($"{path}.{property.Name}", member));
                    }
                }
            }
        }

        /// <summary>
        /// A workspace with informative and determinate pairs, neighbours, coverage loss and two sources.
        /// </summary>
        private static DirectionSequenceGateFixture Populated()
        {
            var fixture = new DirectionSequenceGateFixture();
            var second = fixture.AddSource();

            var own = DirectionSequenceGateFixture.Day(1);

            fixture.AddOutgoing(own, "09:00:00");
            fixture.AddIncoming(own, "09:01:00");

            fixture.AddToken(own, 1, isSent: true);
            fixture.AddToken(own, 2, isSent: true);
            fixture.AddToken(own, 3, isSent: false);
            fixture.AddToken(own, 4, isSent: false);

            fixture.AddToken(First, 1, isSent: false);
            fixture.AddToken(First, 2, isSent: false);
            fixture.AddToken(First, 3, isSent: true);
            fixture.AddToken(First, 4, isSent: true);

            fixture.AddNamed(own, "0010-1", isSent: true);
            fixture.AddToken(own, 9, isSent: null, mediaSourceID: second);
            fixture.AddUndated(isSent: true, mediaSourceID: second);

            return fixture;
        }
    }
}
