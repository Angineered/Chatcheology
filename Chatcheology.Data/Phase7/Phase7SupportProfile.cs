namespace Chatcheology.Data.Phase7
{
    /// <summary>
    /// One candidate's reading on every dimension, indexed by dimension, in the generic form the
    /// comparator works over.
    /// </summary>
    /// <remarks>
    /// Internal, and exists for exactly one reason: it lets the Pareto algorithm be written once over
    /// a dimension it knows nothing about, and lets the branches the public read model cannot reach —
    /// two dimensions in conflict, and no dimension available to both candidates — be tested directly.
    /// <para>
    /// The public <see cref="Phase7CandidateSupport"/> maps onto this faithfully and exhaustively, and
    /// never produces a direction reading of <see cref="Phase7SupportLevel.LessSupportive"/>.
    /// </para>
    /// </remarks>
    internal readonly struct Phase7SupportProfile
    {
        private readonly Phase7SupportLevel _direction;
        private readonly Phase7SupportLevel _orderSupport;

        internal Phase7SupportProfile(
            Phase7SupportLevel direction, Phase7SupportLevel orderSupport)
        {
            _direction = direction;
            _orderSupport = orderSupport;
        }

        /// <summary>This profile's reading on <paramref name="dimension"/>.</summary>
        internal Phase7SupportLevel this[Phase7SupportDimension dimension] => dimension switch
        {
            Phase7SupportDimension.Direction => _direction,
            Phase7SupportDimension.OrderSupport => _orderSupport,
            _ => throw new ArgumentOutOfRangeException(
                nameof(dimension),
                dimension,
                "There is no such support dimension. The frozen rule is defined over exactly two."),
        };

        /// <summary>
        /// The generic profile of <paramref name="support"/>.
        /// </summary>
        /// <remarks>
        /// The mapping is total over the six reachable public combinations and produces
        /// <see cref="Phase7SupportLevel.LessSupportive"/> for the direction dimension in none of them,
        /// because <see cref="Phase7DirectionSupport"/> has no member that could.
        /// </remarks>
        internal static Phase7SupportProfile Of(Phase7CandidateSupport support)
        {
            ArgumentNullException.ThrowIfNull(support);

            return new Phase7SupportProfile(
                support.Direction switch
                {
                    Phase7DirectionSupport.Unavailable => Phase7SupportLevel.Unavailable,
                    Phase7DirectionSupport.Supportive => Phase7SupportLevel.Supportive,
                    var unexpected => throw new ArgumentOutOfRangeException(
                        nameof(support),
                        unexpected,
                        "There is no generic level for this direction support value."),
                },
                support.OrderSupport switch
                {
                    Phase7OrderSupport.Unavailable => Phase7SupportLevel.Unavailable,
                    Phase7OrderSupport.LessSupportive => Phase7SupportLevel.LessSupportive,
                    Phase7OrderSupport.Supportive => Phase7SupportLevel.Supportive,
                    var unexpected => throw new ArgumentOutOfRangeException(
                        nameof(support),
                        unexpected,
                        "There is no generic level for this order support value."),
                });
        }
    }
}
