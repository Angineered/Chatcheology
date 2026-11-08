using System.Text;
using Chatcheology.Data.Media;
using Chatcheology.Data.Workspace;
using Microsoft.Data.Sqlite;

namespace MsgstoreImport;

/// <summary>What <c>add-source</c> did, or would do. Counts only.</summary>
internal sealed class AddSourceResult
{
    internal required bool Applied { get; init; }
    internal required MediaDiscoverySummary Summary { get; init; }
    internal long? MediaSourceID { get; init; }
    internal MediaHashingResult? Hashing { get; init; }

    internal bool Complete =>
        !Applied || Hashing is { FailedFiles: 0, ClassificationConflicts: 0, RemainingUnhashed: 0, WasCancelled: false };
}

/// <summary>
/// <c>add-source</c> (CH-11): registers one more media folder in the workspace through the public
/// <see cref="MediaInventoryService"/>, then hashes its files through the public
/// <see cref="MediaHashingService"/>. Source type <see cref="MediaSourceTypes.WhatsAppMediaDirectory"/>,
/// so the <c>-YYYYMMDD-WA</c> name date is read.
/// </summary>
/// <remarks>
/// <para>Dry run by default: <see cref="MediaInventoryService.Discover"/> walks the folder and the
/// workspace is not opened. <c>--apply</c> inventories (one transaction; the service refuses a wrong
/// schema version, an overlapping root and an empty folder) and then hashes only the new source.</para>
/// <para>Output is counts only: no file name, path, display name or device text.</para>
/// </remarks>
internal static class SourceAdder
{
    /// <exception cref="RefusalException">Nothing was written.</exception>
    internal static AddSourceResult Run(Options options, DateTime? nowUtc = null)
    {
        Inputs.RequireWorkspace(options.WorkspacePath);

        var inventory = new MediaInventoryService();
        var root = options.SourceRoot!;

        if (!Directory.Exists(root))
        {
            throw new RefusalException("There is no folder at the --root path. Nothing was written.");
        }

        if (!options.ApplySource)
        {
            var summary = inventory.Discover(root, MediaSourceTypes.WhatsAppMediaDirectory);

            if (summary.FileCount == 0)
            {
                throw new RefusalException("The --root folder holds no files. Nothing was written.");
            }

            return new AddSourceResult { Applied = false, Summary = summary };
        }

        Inputs.RequireUnclaimed(options.WorkspacePath);

        using (var workspace = WorkspaceDatabase.OpenReadOnlyConnection(options.WorkspacePath))
        {
            Inputs.RequireSchemaVersion(workspace);
        }

        SqliteConnection.ClearAllPools();

        MediaInventoryResult added;
        MediaHashingResult hashing;

        // Held across both steps, so the viewer or another tool cannot write in between. The
        // claim was just shown to be free, so a failure here is a race, reported by type only.
        using (var claim = WorkspaceExclusiveLock.Acquire(options.WorkspacePath))
        {
            try
            {
                added = inventory.Inventory(options.WorkspacePath, new MediaSourceRequest
                {
                    DisplayName = options.SourceDisplayName!,
                    SourceType = MediaSourceTypes.WhatsAppMediaDirectory,
                    RootPath = root,
                    DeviceDescription = options.SourceDevice,
                    ImportedDateTimeUtc = nowUtc ?? DateTime.UtcNow,
                });
            }
            catch (InvalidOperationException exception)
            {
                // The service's own refusals: overlapping root (named by source id only), empty
                // folder, wrong schema version. Their messages carry no path.
                throw new RefusalException(exception.Message);
            }
            catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
            {
                throw new RefusalException(
                    "The workspace is busy: another program holds a lock on it (is the viewer open?). Nothing was written.");
            }
            finally
            {
                SqliteConnection.ClearAllPools();
            }

            try
            {
                hashing = new MediaHashingService().HashPendingFiles(options.WorkspacePath, added.MediaSourceID);
            }
            finally
            {
                SqliteConnection.ClearAllPools();
            }
        }

        return new AddSourceResult
        {
            Applied = true,
            Summary = added.Summary,
            MediaSourceID = added.MediaSourceID,
            Hashing = hashing,
        };
    }

    internal static string Report(AddSourceResult r)
    {
        var s = r.Summary;
        var text = new StringBuilder();

        text.AppendLine(r.Applied
            ? "# Add media source - APPLIED"
            : "# Add media source - dry run (nothing written; add --apply to write)");
        text.AppendLine();
        text.AppendLine("Source type WhatsAppMediaDirectory. Counts only. The folder itself is never written to.");
        text.AppendLine();
        text.AppendLine("```text");

        if (r.MediaSourceID is { } id)
        {
            text.AppendLine("new media source id".PadRight(58) + Text.Id(id));
        }

        text.AppendLine(Text.Line(r.Applied ? "files added" : "files found", s.FileCount));
        text.AppendLine("total bytes".PadRight(58) + s.TotalSizeBytes.ToString("N0", System.Globalization.CultureInfo.InvariantCulture));
        text.AppendLine(Text.Line("  with a naming-derived date (-YYYYMMDD-WA)", s.FileDateCount));
        text.AppendLine(Text.Line("  without one", s.FileCount - s.FileDateCount));
        text.AppendLine(Text.Line("direction: sent (under a Sent folder)", s.SentCount));
        text.AppendLine(Text.Line("direction: received", s.NonSentCount));
        text.AppendLine(Text.Line("direction: unknown (no Sent folder in the tree)", s.DirectionUnknownCount));
        text.AppendLine(Text.Line("zero-byte files", s.ZeroByteFileCount));

        if (r.Hashing is { } h)
        {
            text.AppendLine();
            text.AppendLine(Text.Line("files hashed", h.SuccessfullyHashed));
            text.AppendLine(Text.Line("  new assets", h.NewAssets));
            text.AppendLine(Text.Line("  linked to an existing asset", h.ExistingAssetLinks));
            text.AppendLine(Text.Line("failed to hash", h.FailedFiles));
            text.AppendLine(Text.Line("classification conflicts", h.ClassificationConflicts));
            text.AppendLine(Text.Line("still unhashed in this source", h.RemainingUnhashed));
            text.AppendLine(r.Complete
                ? "hashing                                                   COMPLETE"
                : "hashing                                                   INCOMPLETE");
        }
        else
        {
            text.AppendLine();
            text.AppendLine("Not checked until --apply: workspace schema version, overlap with an existing source.");
        }

        text.AppendLine("```");

        return text.ToString();
    }
}
