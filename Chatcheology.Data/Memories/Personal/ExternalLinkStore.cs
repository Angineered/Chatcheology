using Chatcheology.Archive;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Data.Memories.Personal
{
    /// <summary>One stored external link (CH-48).</summary>
    /// <param name="ExternalLinkID">Its identifier.</param>
    /// <param name="Ordinal">Its place among its owner's links, from 1.</param>
    /// <param name="Url">The address, as stored.</param>
    /// <param name="Label">The label, or <see langword="null"/>.</param>
    public sealed record ExternalLink(long ExternalLinkID, int Ordinal, string Url, string? Label)
    {
        /// <summary>The host to show (<see cref="ExternalLinkRules.DisplayHost"/>): punycode, lower case.</summary>
        public string? Host => ExternalLinkRules.DisplayHost(Url);
    }

    /// <summary>One link of the list a save hands to the store, in the order it is shown.</summary>
    /// <param name="Url">The address; trimmed before it is checked and stored.</param>
    /// <param name="Label">Optional; trimmed, empty means none.</param>
    public sealed record ExternalLinkDraft(string Url, string? Label = null);

    /// <summary>
    /// External links (CH-48) in the memories file beside the open workspace: web addresses for photos kept
    /// elsewhere, as an ordered list of up to 10 on a personal entry or a noted date.
    /// </summary>
    /// <remarks>
    /// <para><strong>Nothing is fetched.</strong> A link is only text here: never opened, resolved or checked
    /// online.</para>
    /// <para><strong>One set of rules.</strong> Every link is checked with <see cref="ExternalLinkRules"/>, the
    /// rules the archive writer and validator use, before anything is opened for writing. A refusal is a
    /// <see cref="PersonalMemoryValidationException"/> keyed <see cref="PersonalFields.Links"/>, with the bad
    /// link's index in <see cref="PersonalMemoryValidationException.LinkIndex"/>.</para>
    /// <para><strong>Replace all, in one transaction.</strong> A save replaces the owner's whole list (the new
    /// order is 1..n); an empty list removes every link. An older memories file (format 1 to 4) is upgraded to
    /// format 5 inside that transaction, so a failed save leaves it as it was. Read-only opens of an older file
    /// list no links.</para>
    /// <para><strong>Personal only.</strong> An imported entry never gets a link; a gone entry or noted date is
    /// refused. Deleting an entry, a noted date or a collection removes their links (in
    /// <see cref="PersonalMemoryStore"/> and <see cref="NotedDateStore"/>).</para>
    /// <para>A save never creates the memories file: the owner must already be in it.</para>
    /// </remarks>
    public sealed class ExternalLinkStore
    {
        /// <summary>Creates a store over the memories file at <paramref name="memoriesPath"/>.</summary>
        /// <param name="memoriesPath">A path named <see cref="MemoriesDatabase.FileName"/>.</param>
        public ExternalLinkStore(string memoriesPath)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(memoriesPath);

            if (!string.Equals(Path.GetFileName(memoriesPath), MemoriesDatabase.FileName, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"The memories file must be named '{MemoriesDatabase.FileName}'.", nameof(memoriesPath));
            }

            MemoriesPath = Path.GetFullPath(memoriesPath);
        }

        /// <summary>The store for the memories file beside a main workspace.</summary>
        public static ExternalLinkStore Beside(string workspacePath) => new(MemoriesDatabase.PathBeside(workspacePath));

        /// <summary>The memories file.</summary>
        public string MemoriesPath { get; }

        /// <summary>Whether the memories file exists.</summary>
        public bool FileExists => File.Exists(MemoriesPath);

        /// <summary>For tests: runs inside each save's transaction, just before it commits.</summary>
        internal Action? BeforeCommit { get; set; }

        /// <summary>
        /// The first rule the list breaks, as it would be stored, or <see langword="null"/> when it may be saved.
        /// For a form that checks before saving; a save checks the same.
        /// </summary>
        public static ExternalLinkError? Validate(IReadOnlyList<ExternalLinkDraft> links)
        {
            ArgumentNullException.ThrowIfNull(links);

            if (links.Any(link => link is null))
            {
                throw new ArgumentException("A link in the list is missing.", nameof(links));
            }

            return ExternalLinkRules.Validate(links.Select(link => ((string?)link.Url, link.Label)).ToList());
        }

        /// <summary>A personal entry's links, by ordinal. None without a file, before format 5, or for an imported entry.</summary>
        public IReadOnlyList<ExternalLink> ReadForEntry(long entryID) => Read(ExternalLinkOwnerKind.Entry, entryID);

        /// <summary>A noted date's links, by ordinal. None without a file or before format 5.</summary>
        public IReadOnlyList<ExternalLink> ReadForNotedDate(long notedDateID) => Read(ExternalLinkOwnerKind.NotedDate, notedDateID);

        /// <summary>Replaces a personal entry's links with <paramref name="links"/>, in that order.</summary>
        /// <exception cref="PersonalMemoryValidationException">
        /// A link breaks a rule (<see cref="PersonalFields.Links"/>), or the entry is imported or gone
        /// (<see cref="PersonalFields.Entry"/>). Nothing was written.
        /// </exception>
        /// <exception cref="FileNotFoundException">There is no memories file.</exception>
        public void ReplaceForEntry(long entryID, IReadOnlyList<ExternalLinkDraft> links) =>
            Replace(ExternalLinkOwnerKind.Entry, entryID, links);

        /// <summary>Replaces a noted date's links with <paramref name="links"/>, in that order.</summary>
        /// <exception cref="PersonalMemoryValidationException">
        /// A link breaks a rule (<see cref="PersonalFields.Links"/>), or the date is gone
        /// (<see cref="PersonalFields.NotedDate"/>). Nothing was written.
        /// </exception>
        /// <exception cref="FileNotFoundException">There is no memories file.</exception>
        public void ReplaceForNotedDate(long notedDateID, IReadOnlyList<ExternalLinkDraft> links) =>
            Replace(ExternalLinkOwnerKind.NotedDate, notedDateID, links);

        // -----------------------------------------------------------------------------------
        // Plumbing.
        // -----------------------------------------------------------------------------------

        private IReadOnlyList<ExternalLink> Read(ExternalLinkOwnerKind kind, long ownerID)
        {
            if (!FileExists)
            {
                return [];
            }

            try
            {
                using var connection = MemoriesDatabase.OpenReadOnlyConnection(MemoriesPath);

                if (MemoriesDatabase.ReadFormatVersion(connection) < MemoriesDatabase.FirstFormatWithExternalLinks)
                {
                    return [];
                }

                using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    SELECT ExternalLinkID, Ordinal, Url, Label FROM ExternalLink
                    WHERE OwnerKind = $kind AND OwnerID = $owner ORDER BY Ordinal;
                    """;
                command.Parameters.AddWithValue("$kind", ExternalLinkRules.OwnerKindText(kind));
                command.Parameters.AddWithValue("$owner", ownerID);

                var links = new List<ExternalLink>();

                using var reader = command.ExecuteReader();

                while (reader.Read())
                {
                    links.Add(new ExternalLink(
                        reader.GetInt64(0),
                        reader.GetInt32(1),
                        reader.GetString(2),
                        reader.IsDBNull(3) ? null : reader.GetString(3)));
                }

                return links;
            }
            finally
            {
                SqliteConnection.ClearAllPools();
            }
        }

        private void Replace(ExternalLinkOwnerKind kind, long ownerID, IReadOnlyList<ExternalLinkDraft> links)
        {
            if (Validate(links) is { } error)
            {
                throw new PersonalMemoryValidationException(PersonalFields.Links, Describe(error)) { LinkIndex = error.Index };
            }

            try
            {
                using var connection = MemoriesDatabase.OpenConnection(MemoriesPath);
                using var transaction = connection.BeginTransaction();

                // An older file is upgraded on the first write (CH-48: format 5 adds ExternalLink), inside the
                // same transaction, so a failed save leaves the file as it was.
                MemoriesDatabase.UpgradeToCurrent(connection, transaction);

                RequireOwner(transaction, kind, ownerID);
                ReplaceRows(transaction, kind, ownerID, links);

                BeforeCommit?.Invoke();
                transaction.Commit();
            }
            finally
            {
                SqliteConnection.ClearAllPools();
            }
        }

        /// <summary>Deletes the owner's links and inserts <paramref name="links"/>, numbered from 1 in order.</summary>
        private static void ReplaceRows(
            SqliteTransaction transaction, ExternalLinkOwnerKind kind, long ownerID, IReadOnlyList<ExternalLinkDraft> links)
        {
            using var command = Command(transaction, "DELETE FROM ExternalLink WHERE OwnerKind = $kind AND OwnerID = $owner;");
            command.Parameters.AddWithValue("$kind", ExternalLinkRules.OwnerKindText(kind));
            command.Parameters.AddWithValue("$owner", ownerID);
            command.ExecuteNonQuery();

            command.CommandText =
                """
                INSERT INTO ExternalLink (OwnerKind, OwnerID, Ordinal, Url, Label)
                VALUES ($kind, $owner, $ordinal, $url, $label);
                """;

            for (var index = 0; index < links.Count; index++)
            {
                command.Parameters.Clear();
                command.Parameters.AddWithValue("$kind", ExternalLinkRules.OwnerKindText(kind));
                command.Parameters.AddWithValue("$owner", ownerID);
                command.Parameters.AddWithValue("$ordinal", index + 1);
                command.Parameters.AddWithValue("$url", ExternalLinkRules.NormaliseUrl(links[index].Url)!);
                command.Parameters.AddWithValue("$label", (object?)ExternalLinkRules.NormaliseLabel(links[index].Label) ?? DBNull.Value);
                command.ExecuteNonQuery();
            }
        }

        /// <summary>Deletes every link of one owner. For the stores that delete entries, collections and noted dates.</summary>
        /// <returns>How many links were removed.</returns>
        internal static int DeleteFor(SqliteTransaction transaction, ExternalLinkOwnerKind kind, long ownerID)
        {
            using var command = Command(transaction, "DELETE FROM ExternalLink WHERE OwnerKind = $kind AND OwnerID = $owner;");
            command.Parameters.AddWithValue("$kind", ExternalLinkRules.OwnerKindText(kind));
            command.Parameters.AddWithValue("$owner", ownerID);

            return command.ExecuteNonQuery();
        }

        private static void RequireOwner(SqliteTransaction transaction, ExternalLinkOwnerKind kind, long ownerID)
        {
            if (kind == ExternalLinkOwnerKind.NotedDate)
            {
                using var exists = Command(transaction, "SELECT COUNT(*) FROM NotedDate WHERE NotedDateID = $id;");
                exists.Parameters.AddWithValue("$id", ownerID);

                if (Convert.ToInt64(exists.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) == 0)
                {
                    throw new PersonalMemoryValidationException(PersonalFields.NotedDate, "That date is no longer there.");
                }

                return;
            }

            using var command = Command(transaction,
                "SELECT c.Kind FROM Entry e JOIN Collection c ON c.CollectionID = e.CollectionID WHERE e.EntryID = $id;");
            command.Parameters.AddWithValue("$id", ownerID);

            switch (command.ExecuteScalar() as string)
            {
                case null:
                    throw new PersonalMemoryValidationException(PersonalFields.Entry, "That memory is no longer there.");
                case MemoriesDatabase.KindPersonal:
                    return;
                default:
                    throw new PersonalMemoryValidationException(PersonalFields.Entry, "Imported memories are read-only.");
            }
        }

        /// <summary>The refusal's message: which link (counted from 1) and what is wrong. Never the link itself.</summary>
        private static string Describe(ExternalLinkError error) =>
            error.Index is { } index
                ? "Link " + (index + 1).ToString(System.Globalization.CultureInfo.CurrentCulture) + ": " + error.Message
                : error.Message;

        private static SqliteCommand Command(SqliteTransaction transaction, string sql)
        {
            var command = transaction.Connection!.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;

            return command;
        }
    }
}
