namespace Chatcheology.Data.Sequence
{
    /// <summary>
    /// Where a relation's sequence tokens came from, in terms of the direction evidence the copies
    /// carrying them hold.
    /// </summary>
    /// <remarks>
    /// A relation is direction-compatible at asset and date level: some exact-date copy agrees with
    /// the message's direction and none contradicts it. That says nothing about which copies carry a
    /// supported token, so a compatible relation's tokens can come entirely from copies whose own
    /// direction is unrecorded. Filtering those out would silently redefine the relation, so they are
    /// classified and reported instead.
    /// <para>
    /// Reported twice, and the two must not be merged: once per cohort relation, which is operational
    /// coverage of the attachments, and once per distinct
    /// <c>(date, direction, candidate asset)</c> group, which is coverage of the evidence itself.
    /// Relation-weighted figures are not independent observations.
    /// </para>
    /// </remarks>
    public sealed class TokenDirectionProvenanceCounts
    {
        /// <summary>Every token-bearing copy agrees with the message's direction.</summary>
        public required int AgreeingOnly { get; init; }

        /// <summary>Every token-bearing copy records no direction at all.</summary>
        public required int UnknownOnly { get; init; }

        /// <summary>Both kinds of token-bearing copy are present.</summary>
        public required int AgreeingAndUnknown { get; init; }

        /// <summary>No qualifying copy carries a supported token.</summary>
        public required int NoSupportedToken { get; init; }

        /// <summary>The population these classes divide.</summary>
        public int Total => AgreeingOnly + UnknownOnly + AgreeingAndUnknown + NoSupportedToken;
    }
}
