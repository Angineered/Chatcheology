namespace Chatcheology.Data.Phase7
{
    /// <summary>
    /// One of the three resolution transitions, and there is no fourth.
    /// </summary>
    /// <remarks>
    /// Each has exactly one job: <see cref="Confirm"/> sets the current asset,
    /// <see cref="ConfirmReplacement"/> changes it, and <see cref="Revoke"/> clears it. That is why a
    /// replacement whose target equals the attachment's current asset is refused rather than accepted
    /// as a re-statement of basis — it would give one operation two meanings — and why no "reaffirm"
    /// member exists to serve that case.
    /// </remarks>
    public enum Phase7ResolutionEventType
    {
        /// <summary>Sets the current asset, from an unresolved attachment.</summary>
        Confirm,

        /// <summary>Changes the current asset, to a different one, on a resolved attachment.</summary>
        ConfirmReplacement,

        /// <summary>Clears the current asset, leaving the attachment unresolved.</summary>
        Revoke,
    }
}
