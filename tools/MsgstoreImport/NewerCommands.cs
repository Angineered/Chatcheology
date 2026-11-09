using System.Globalization;
using System.Text;
using Chatcheology.Data.NewerMessages;
using Microsoft.Data.Sqlite;

namespace MsgstoreImport;

/// <summary><c>newer plan</c> and <c>newer apply</c> (CH-71 step 3a). Counts only on screen.</summary>
internal static class NewerTool
{
    /// <returns>0 done; 1 failed; 2 refused.</returns>
    internal static int Run(string[] args, TextWriter stdout, TextWriter stderr)
    {
        if (args is [] or ["-h" or "--help" or "/?"])
        {
            stdout.WriteLine(NewerOptions.Usage);

            return args.Length == 0 ? 2 : 0;
        }

        NewerOptions options;

        try
        {
            options = NewerOptions.Parse(args);
        }
        catch (OptionsException exception)
        {
            stderr.WriteLine(exception.Message);
            stderr.WriteLine();
            stderr.WriteLine(NewerOptions.Usage);

            return 2;
        }

        try
        {
            if (options.Apply)
            {
                stdout.Write(ApplyReport(Apply(options)));
            }
            else
            {
                stdout.Write(PlanReport(Plan(options)));
            }

            return 0;
        }
        catch (RefusalException exception)
        {
            stderr.WriteLine("Refused: " + exception.Message);

            return 2;
        }
        catch (Exception exception)
        {
            // The exception's own message may carry a path, so only its type is printed.
            stderr.WriteLine($"The run could not complete ({exception.GetType().Name}). " +
                             (options.Apply
                                 ? "Apply is one transaction: newer-messages.db holds either the whole run or none of it. Run the same apply again."
                                 : "Nothing was written; plan is read-only."));

            return 1;
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    internal sealed record PlanResult(NewerComputation Computation, string Sha256);

    /// <exception cref="RefusalException">Nothing was written.</exception>
    internal static PlanResult Plan(NewerOptions options)
    {
        if (Planner.ManifestOutRefusal(options.ManifestOut!) is { } refusal)
        {
            throw new RefusalException(refusal);
        }

        var computation = NewerPlanner.Compute(
            options.MsgstorePath, options.WorkspacePath, options.MediaRoot,
            new NewerParameters(options.ConversationID, options.ChatID, options.LocalParticipantID));

        // CreateNew: never overwrite, even if something appeared since the path was checked.
        using (var stream = new FileStream(options.ManifestOut!, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            stream.Write(computation.ManifestBytes);
        }

        return new PlanResult(computation, Manifest.Sha256Hex(computation.ManifestBytes));
    }

    internal sealed record ApplyResult(bool AlreadyApplied, string Tag, int Messages, int Attachments, int Restored, int Links, bool Created);

    /// <exception cref="RefusalException">Nothing was written.</exception>
    internal static ApplyResult Apply(NewerOptions options)
    {
        if (!File.Exists(options.ManifestPath))
        {
            throw new RefusalException("There is no file at the --manifest path.");
        }

        var bytes = File.ReadAllBytes(options.ManifestPath!);
        var sha = Manifest.Sha256Hex(bytes);

        if (!string.Equals(sha, options.ManifestSha256, StringComparison.Ordinal))
        {
            throw new RefusalException(
                "The manifest's SHA-256 does not match --manifest-sha256. The manifest has changed since it was approved, " +
                "or the wrong digest was given. Nothing was opened or written.");
        }

        NewerParameters parameters;

        try
        {
            parameters = NewerPlanner.ParseParameters(bytes);
        }
        catch (FormatException exception)
        {
            throw new RefusalException("The manifest is malformed: " + exception.Message + " Nothing was written.");
        }

        Inputs.RequireWorkspace(options.WorkspacePath);
        var newerPath = NewerMessagesDatabase.PathBeside(options.WorkspacePath);

        // A rerun of an applied manifest writes nothing (its run is recorded by the manifest's SHA-256).
        if (File.Exists(newerPath))
        {
            NewerMessagesContents contents;

            try
            {
                contents = NewerMessagesReader.Read(newerPath);
            }
            catch (InvalidOperationException)
            {
                throw new RefusalException(
                    "The newer-messages.db beside the workspace is not a newer-messages file of this format. Nothing was written.");
            }

            if (contents.Runs.FirstOrDefault(r => r.ManifestSha256 == sha) is { } done)
            {
                return new ApplyResult(true, Manifest.Tag(sha), done.Messages, done.Attachments, done.Restored, 0, false);
            }
        }

        var now = NewerPlanner.Compute(options.MsgstorePath, options.WorkspacePath, options.MediaRoot, parameters);

        if (!now.ManifestBytes.AsSpan().SequenceEqual(bytes))
        {
            throw new RefusalException(
                "The msgstore, the workspace, the newer-messages file or the media folder no longer gives exactly the planned " +
                "rows (or another --media-root was given). Plan again. Nothing was written.");
        }

        var info = new NewerMessagesInfo(parameters.ConversationID, parameters.LocalParticipantID, now.OtherParticipantID,
            parameters.ChatID, DateTime.UtcNow);

        var run = new NewerRefreshRun
        {
            RunDateTimeUtc = DateTime.UtcNow,
            ManifestSha256 = sha,
            MsgstoreSizeBytes = now.MsgstoreSizeBytes,
            MsgstoreUserVersion = now.MsgstoreUserVersion,
            MsgstoreSchemaSha256 = now.MsgstoreSchemaSha256,
            HandOff = now.FirstRun ? NewerMessagesDatabase.HandOffWorkspace : NewerMessagesDatabase.HandOffPreviousRun,
            HandOffKeyID = now.HandOffKeyID,
            MediaRootPath = Path.GetFullPath(options.MediaRoot),
        };

        NewerMessagesWriteResult written;

        try
        {
            written = NewerMessagesWriter.Append(newerPath, info, run, now.Rows);
        }
        catch (Exception exception) when (Inputs.IsClaimFailure(exception))
        {
            throw new RefusalException("newer-messages.db is held by another program. Nothing was written.");
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            // The writer's messages name no path, row or text: only what was refused.
            throw new RefusalException("The newer-messages writer refused the run: " + exception.Message);
        }

        return new ApplyResult(written.AlreadyApplied, Manifest.Tag(sha), written.Messages, written.Attachments,
            now.Rows.Count(r => r.Attachment?.State == NewerMessagesDatabase.StateRestored), written.Links, written.Created);
    }

    internal static string PlanReport(PlanResult result)
    {
        var c = result.Computation;
        var text = new StringBuilder();
        var rows = c.Rows;
        var attachments = rows.Where(r => r.Attachment is not null).Select(r => r.Attachment!).ToList();

        text.AppendLine("# Msgstore import - newer plan (CH-71 step 3a)");
        text.AppendLine();
        text.AppendLine("Read-only on the msgstore, the workspace, newer-messages.db and the media folder. Counts only:");
        text.AppendLine("no message text, caption, name, file name, key_id or path. Identifiers and digests are in the");
        text.AppendLine("private manifest. Times: Africa/Johannesburg.");
        text.AppendLine();
        text.AppendLine("```text");

        if (c.FirstRun)
        {
            text.AppendLine($"{"hand-off",-58}first run, from the workspace");
            text.AppendLine(Text.Line("  earlier days compared, all with equal counts", c.EarlierDaysCompared));
            text.AppendLine(Text.Line("  workspace messages on its last day, all matched", c.LastDayWorkspaceMessages));
            text.AppendLine(Text.Line("    same normalised text", c.ComparedText));
            text.AppendLine(Text.Line("    media facing media", c.ComparedMedia));
            text.AppendLine(Text.Line("    compared by direction only", c.ComparedDirectionOnly));
        }
        else
        {
            text.AppendLine($"{"hand-off",-58}later run, after the last imported message");
            text.AppendLine(Text.Line("  previous runs", c.PreviousRuns));
            text.AppendLine($"{"  last imported message found in the msgstore",-58}yes");
        }

        text.AppendLine();
        text.AppendLine(Text.Line("new messages", rows.Count));
        text.AppendLine(Text.Line("  user text", rows.Count(r => r.Kind == NewerMessagesDatabase.KindUser && r.Attachment is null)));
        text.AppendLine(Text.Line("  user media", attachments.Count));
        text.AppendLine(Text.Line("  system (fixed wording)", rows.Count(r => r.Kind == NewerMessagesDatabase.KindSystem)));
        text.AppendLine(Text.Line("  placeholders (fixed wording)", rows.Count(r => r.Kind == NewerMessagesDatabase.KindPlaceholder)));

        foreach (var kind in NewerKinds.PlaceholderKindsInOrder)
        {
            text.AppendLine(Text.Line("    " + kind, rows.Count(r => r.PlaceholderKind == kind)));
        }

        text.AppendLine(Text.Line("      of which a media type without a media record", c.MediaTypeWithoutRecord));
        text.AppendLine();
        text.AppendLine("new messages by month:");

        foreach (var (month, count) in c.ByMonth)
        {
            text.AppendLine(Text.Line("  " + month, count));
        }

        text.AppendLine();
        text.AppendLine("new messages by msgstore message_type (kind given):");

        foreach (var (type, (kind, count)) in c.ByType)
        {
            text.AppendLine(Text.Line($"  type {Text.Id(type)} ({kind})", count));
        }

        text.AppendLine(Text.Line("  no message_type", c.NoType));
        text.AppendLine();
        text.AppendLine(Text.Line("attachments", attachments.Count));
        text.AppendLine(Text.Line("  Restored (SHA-256 equals msgstore file_hash)", attachments.Count(a => a.State == NewerMessagesDatabase.StateRestored)));
        text.AppendLine(Text.Line("  Unmatched", c.Unmatched.Total));

        foreach (var reason in NewerPlanner.UnmatchedReasons)
        {
            text.AppendLine(Text.Line("    " + reason, c.Unmatched[reason]));
        }

        text.AppendLine(Text.Line("  with a caption", rows.Count(r => r.Caption is not null)));
        text.AppendLine(Text.Line("low-quality copy links recorded (type 12)", c.Links));
        text.AppendLine(Text.Line("  links refused", c.LinksRefused.Total));

        foreach (var reason in NewerPlanner.LinkReasons)
        {
            text.AppendLine(Text.Line("    " + reason, c.LinksRefused[reason]));
        }

        text.AppendLine();

        if (c.ForwardingKnown)
        {
            text.AppendLine(Text.Line("forwarded", rows.Count(r => r.IsForwarded == true)));
            text.AppendLine(Text.Line($"  forwarded many times (score {FactsPlanner.ForwardedManyTimes} or more)",
                rows.Count(r => r.ForwardScore >= FactsPlanner.ForwardedManyTimes)));
        }
        else
        {
            text.AppendLine($"{"forwarded",-58}not known (the msgstore records no forwarding)");
        }

        text.AppendLine(c.AddOns is { } addOns
            ? Text.Line("reactions and other add-ons on new messages (counted only)", addOns)
            : $"{"reactions and other add-ons on new messages",-58}no message_add_on table");
        text.AppendLine(c.QuotedReplies is { } quoted
            ? Text.Line("new replies quoting a message (quote not shown)", quoted)
            : $"{"new replies quoting a message",-58}no message_quoted table");
        text.AppendLine();
        text.AppendLine(Text.Line("earlier imported messages changed on the phone (kept as archived)",
            c.EditedSinceImport + c.DeletedSinceImport + c.GoneFromMsgstore));
        text.AppendLine(Text.Line("  text or caption edited since", c.EditedSinceImport));
        text.AppendLine(Text.Line("  deleted for everyone since", c.DeletedSinceImport));
        text.AppendLine(Text.Line("  no longer in the msgstore", c.GoneFromMsgstore));
        text.AppendLine(Text.Line("texts the system could not normalise (compared as they are)", c.NfcFailures));
        text.AppendLine("```");
        text.AppendLine();
        text.AppendLine("manifest SHA-256: " + result.Sha256);
        text.AppendLine("batch tag:        " + Manifest.Tag(result.Sha256));

        return text.ToString();
    }

    internal static string ApplyReport(ApplyResult r)
    {
        var text = new StringBuilder();

        text.AppendLine("# Msgstore import - newer apply (CH-71 step 3a)");
        text.AppendLine();
        text.AppendLine("```text");
        text.AppendLine($"{"batch tag",-58}{r.Tag}");

        if (r.AlreadyApplied)
        {
            text.AppendLine($"{"this manifest was already applied",-58}nothing written");
            text.AppendLine(Text.Line("  messages of that run", r.Messages));
            text.AppendLine(Text.Line("  attachments of that run", r.Attachments));
        }
        else
        {
            text.AppendLine($"{"newer-messages.db",-58}{(r.Created ? "created" : "appended to")}");
            text.AppendLine(Text.Line("messages written", r.Messages));
            text.AppendLine(Text.Line("attachments written", r.Attachments));
            text.AppendLine(Text.Line("  Restored", r.Restored));
            text.AppendLine(Text.Line("  Unmatched", r.Attachments - r.Restored));
            text.AppendLine(Text.Line("low-quality copy links written", r.Links));
        }

        text.AppendLine("```");

        return text.ToString();
    }
}
