namespace Chatcheology.Data.Sequence
{
    /// <summary>Slack bands for one direction or pooled, by group and by message.</summary>
    public sealed class SequenceSlackDistribution
    {
        public required SlackBandCounts Groups { get; init; }

        public required SlackBandCounts Messages { get; init; }
    }
}
