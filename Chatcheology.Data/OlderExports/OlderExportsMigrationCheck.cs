namespace Chatcheology.Data.OlderExports
{
    /// <summary>What a migration of the older-exports file would do (CH-71).</summary>
    public enum OlderExportsMigrationOutcome
    {
        /// <summary>The file is already at the current workspace schema version; nothing to do.</summary>
        AlreadyCurrent,

        /// <summary>The file is a version-5 older-exports file with the exact version-5 shape.</summary>
        WouldMigrate,

        /// <summary>The migration would refuse; <see cref="OlderExportsMigrationCheck.Refusal"/> says why.</summary>
        WouldRefuse,
    }

    /// <summary>The result of <see cref="OlderExportsDatabase.CheckMigration"/>. Counts and reasons only.</summary>
    /// <param name="Version">The file's workspace schema version.</param>
    /// <param name="Outcome">What the migration would do.</param>
    /// <param name="Refusal">Why it would refuse, or <see langword="null"/>. Names tables, never paths.</param>
    public sealed record OlderExportsMigrationCheck(
        int Version,
        OlderExportsMigrationOutcome Outcome,
        string? Refusal);
}
