using Microsoft.Data.Sqlite;

namespace MsgstoreImport;

/// <summary>
/// Turns the msgstore census matches into Phase 7 confirmations (CH-2), one per attachment, through
/// the public <c>Phase7ResolutionService</c> only.
/// </summary>
/// <remarks>
/// <para><c>plan</c> is read-only on both databases and writes the private manifest.
/// <c>apply</c> re-checks every row against the msgstore and workspace and confirms it.
/// <c>undo-batch</c> revokes this batch's confirmations that are still current.</para>
/// <para>Aggregates only on stdout and stderr: no attachment, asset or message identifier, key_id,
/// file name or path. Identifiers go only to the private manifest.</para>
/// </remarks>
internal static class Program
{
    private static int Main(string[] args) => ImportTool.Run(args, Console.Out, Console.Error);
}

internal static class ImportTool
{
    /// <returns>0 done and reconciled; 1 stopped, failed or not reconciled; 2 refused.</returns>
    internal static int Run(string[] args, TextWriter stdout, TextWriter stderr)
    {
        if (args is ["-h" or "--help" or "/?"])
        {
            stdout.WriteLine(Options.Usage);

            return 0;
        }

        Options options;

        try
        {
            options = Options.Parse(args);
        }
        catch (OptionsException exception)
        {
            stderr.WriteLine(exception.Message);
            stderr.WriteLine();
            stderr.WriteLine(Options.Usage);

            return 2;
        }

        try
        {
            switch (options.Mode)
            {
                case Mode.ReviewList:
                {
                    var result = ReviewLister.Run(options);
                    stdout.Write(ReviewLister.Report(result));

                    return result.Rows + result.Excluded.Total == result.Attachments ? 0 : 1;
                }

                case Mode.Plan when options.Rule == PairingRule.ReviewMissing:
                {
                    var result = MissingReview.Plan(options);
                    stdout.Write(MissingReview.Report(result));

                    return 0;
                }

                case Mode.Apply when MissingManifest.IsOne(options.ManifestPath):
                {
                    var result = MissingReview.Apply(options);
                    stdout.Write(MissingReview.WriteReport(result));

                    return result.Reconciled && !result.Stopped ? 0 : 1;
                }

                case Mode.UndoBatch when MissingManifest.IsOne(options.ManifestPath):
                    throw new RefusalException(MissingReview.UndoRefusal);

                case Mode.Migrate:
                {
                    var result = WorkspaceMigrator.Run(options);
                    stdout.Write(WorkspaceMigrator.Report(result));

                    return WorkspaceMigrator.Succeeded(result) ? 0 : 1;
                }

                case Mode.Plan when options.Rule == PairingRule.Facts:
                {
                    var result = FactsPlanner.Plan(options);
                    stdout.Write(FactsPlanner.Report(result));

                    return FactsPlanner.Reconciled(result.Facts) ? 0 : 1;
                }

                case Mode.Apply when FactsManifest.IsOne(options.ManifestPath):
                {
                    var result = FactsBatch.Apply(options);
                    stdout.Write(FactsBatch.Report(result));

                    return result.Reconciled ? 0 : 1;
                }

                case Mode.UndoBatch when FactsManifest.IsOne(options.ManifestPath):
                {
                    var result = FactsBatch.Undo(options);
                    stdout.Write(FactsBatch.Report(result));

                    return result.Reconciled ? 0 : 1;
                }

                case Mode.Plan when options.Rule == PairingRule.TextFacts:
                {
                    var result = TextFactsPlanner.Plan(options);
                    stdout.Write(TextFactsPlanner.Report(result));

                    return TextFactsPlanner.Reconciled(result.Facts) ? 0 : 1;
                }

                case Mode.Apply when TextFactsManifest.IsOne(options.ManifestPath):
                {
                    var result = TextFactsBatch.Apply(options);
                    stdout.Write(FactsBatch.Report(result));

                    return result.Reconciled ? 0 : 1;
                }

                case Mode.UndoBatch when TextFactsManifest.IsOne(options.ManifestPath):
                {
                    var result = TextFactsBatch.Undo(options);
                    stdout.Write(FactsBatch.Report(result));

                    return result.Reconciled ? 0 : 1;
                }

                case Mode.Plan when options.Rule == PairingRule.CaptionFacts:
                {
                    var result = CaptionFactsPlanner.Plan(options);
                    stdout.Write(CaptionFactsPlanner.Report(result));

                    return CaptionFactsPlanner.Reconciled(result.Captions) ? 0 : 1;
                }

                case Mode.Apply when CaptionFactsManifest.IsOne(options.ManifestPath):
                {
                    var result = CaptionFactsBatch.Apply(options);
                    stdout.Write(FactsBatch.Report(result));

                    return result.Reconciled ? 0 : 1;
                }

                case Mode.UndoBatch when CaptionFactsManifest.IsOne(options.ManifestPath):
                {
                    var result = CaptionFactsBatch.Undo(options);
                    stdout.Write(FactsBatch.Report(result));

                    return result.Reconciled ? 0 : 1;
                }

                case Mode.Plan when options.Rule == PairingRule.NameSizeReviewed:
                {
                    var result = NameSizePlanner.Plan(options);
                    stdout.Write(NameSizePlanner.Report(result));

                    return result.Rows + result.Excluded.Total == result.ListRows ? 0 : 1;
                }

                case Mode.Plan when options.Rule == PairingRule.Documents:
                {
                    var result = DocumentsPlanner.Plan(options);
                    stdout.Write(DocumentsPlanner.Report(result));

                    return result.Rows + result.Excluded.Total == result.Attachments ? 0 : 1;
                }

                case Mode.Plan when options.Rule == PairingRule.OutsideCandidateSet:
                {
                    var result = OutsidePlanner.Plan(options);
                    stdout.Write(OutsidePlanner.Report(result));

                    return result.Rows + result.Excluded.Total == result.Attachments ? 0 : 1;
                }

                case Mode.Plan when options.Rule == PairingRule.DaySequence:
                {
                    var result = DayPlanner.Plan(options);
                    stdout.Write(DayPlanner.Report(result));

                    return result.Rows + result.Excluded.Total == result.Attachments ? 0 : 1;
                }

                case Mode.Plan:
                {
                    var result = Planner.Plan(options);
                    stdout.Write(Planner.Report(result));

                    return result.Rows + result.Excluded.Total == result.Attachments ? 0 : 1;
                }

                case Mode.Apply:
                {
                    var result = Applier.Apply(options);
                    stdout.Write(WriteReport.Of(result));

                    return result.Reconciled && !result.Stopped ? 0 : 1;
                }

                case Mode.AddSource:
                {
                    var result = SourceAdder.Run(options);
                    stdout.Write(SourceAdder.Report(result));

                    return result.Complete ? 0 : 1;
                }

                case Mode.ListUnresolved:
                {
                    stdout.Write(Unresolved.Run(options));

                    return 0;
                }

                case Mode.ExplainNoDate:
                {
                    stdout.Write(ExplainNoDate.Run(options));

                    return 0;
                }

                case Mode.ProbeLinks:
                {
                    stdout.Write(LinkProber.Run(options));

                    return 0;
                }

                case Mode.ProbeText:
                {
                    stdout.Write(TextProber.Run(options));

                    return 0;
                }

                case Mode.Probe:
                {
                    stdout.Write(Prober.Report(Prober.Probe(options)));

                    return 0;
                }

                default:
                {
                    var result = Undoer.Undo(options);
                    stdout.Write(WriteReport.Of(result));

                    return result.Reconciled && !result.Stopped ? 0 : 1;
                }
            }
        }
        catch (RefusalException exception)
        {
            stderr.WriteLine("Refused: " + exception.Message);

            return 2;
        }
        catch (Exception exception)
        {
            // The exception's own message may carry a path, so only its type is printed.
            if (options.Mode is Mode.ProbeLinks or Mode.ProbeText)
            {
                stderr.WriteLine($"The run could not complete ({exception.GetType().Name}). Nothing was written; the probe is read-only.");

                return 1;
            }

            if (options.Mode == Mode.Migrate)
            {
                stderr.WriteLine($"The run could not complete ({exception.GetType().Name}). The migration is one " +
                                 "transaction: the workspace is either still version 5 or fully version 6. Run " +
                                 "migrate again without --apply to see which.");

                return 1;
            }

            stderr.WriteLine($"The run could not complete ({exception.GetType().Name}). No counts were " +
                             "printed. Rows already written stay written; apply and undo-batch are both " +
                             "safe to rerun with the same manifest.");

            return 1;
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }
}
