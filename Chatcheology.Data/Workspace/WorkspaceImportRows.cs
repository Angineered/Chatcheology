namespace Chatcheology.Data.Workspace
{
    /// <summary>
    /// What <see cref="WorkspaceImporter.ImportWithin"/> wrote, with the generated identifiers a
    /// caller adding rows to the same import needs.
    /// </summary>
    /// <remarks>
    /// Internal: the public import result stays exactly what it was. Sender keys are compared
    /// ordinally, as the importer distinguishes them.
    /// </remarks>
    internal sealed class WorkspaceImportRows
    {
        /// <summary>The same result <see cref="WorkspaceImporter.Import"/> returns.</summary>
        internal required WorkspaceImportResult Result { get; init; }

        /// <summary>The participant created for each distinct user-message sender.</summary>
        internal required IReadOnlyDictionary<string, long> ParticipantIDsBySender { get; init; }

        /// <summary>The message created for each parsed message, by its sequence number.</summary>
        internal required IReadOnlyDictionary<int, long> MessageIDsBySequenceNumber { get; init; }
    }
}
