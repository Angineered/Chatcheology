using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Archive
{
    /// <summary>
    /// Writes one new <c>archive.db</c>, once.
    /// </summary>
    /// <remarks>
    /// <para><strong>All or nothing.</strong> Everything is written in one transaction, and
    /// <c>PRAGMA user_version</c> is set only in <see cref="Complete"/>, just before the commit. A
    /// database the writer did not complete therefore reads as version 0, which no reader accepts as
    /// an archive.</para>
    /// <para><strong>The format's rules are checked as rows arrive</strong>, with a fixed message
    /// that names the rule and never a value, and the schema's own constraints check them again.
    /// Cross-row rules (exactly one local participant, no media nobody names, every link pointing at
    /// a row that exists) are checked in <see cref="Complete"/>.</para>
    /// <para>After the commit the file is put in rollback-journal mode and vacuumed, so the archive
    /// is one compact file with no companion files.</para>
    /// </remarks>
    public sealed class ArchiveWriter : IDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly ArchiveInfo _info;
        private SqliteTransaction? _transaction;

        private int _participants;
        private int _messages;
        private int _attachments;
        private int _restored;
        private int _missing;
        private int _unmatched;
        private int _media;
        private bool _completed;

        private ArchiveWriter(SqliteConnection connection, SqliteTransaction transaction, ArchiveInfo info)
        {
            _connection = connection;
            _transaction = transaction;
            _info = info;
        }

        /// <summary>Creates a new archive database at <paramref name="databasePath"/>.</summary>
        /// <exception cref="InvalidOperationException">
        /// A file already exists there, or the info breaks a format rule. Nothing was written.
        /// </exception>
        public static ArchiveWriter Create(string databasePath, ArchiveInfo info)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
            ArgumentNullException.ThrowIfNull(info);

            ValidateInfo(info);

            if (File.Exists(databasePath) || Directory.Exists(databasePath))
            {
                throw new InvalidOperationException(
                    "Something already exists where the archive database would go. An archive is " +
                    "only ever written as a new file. Nothing was written.");
            }

            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                ForeignKeys = true,
                Pooling = false,
            }.ToString());

            try
            {
                connection.Open();

                Execute(connection, null, "PRAGMA journal_mode = DELETE;");

                var transaction = connection.BeginTransaction();

                foreach (var statement in ArchiveSchema.StatementsOf(info.FormatVersion))
                {
                    Execute(connection, transaction, statement);
                }

                var writer = new ArchiveWriter(connection, transaction, info);
                writer.WriteInfo();

                return writer;
            }
            catch
            {
                connection.Dispose();
                throw;
            }
        }

        /// <summary>Writes one participant.</summary>
        /// <exception cref="InvalidOperationException">The row breaks a format rule.</exception>
        public void WriteParticipant(ArchiveParticipant participant)
        {
            ArgumentNullException.ThrowIfNull(participant);
            var transaction = RequireOpen();

            if (participant.IsLocal != (participant.ParticipantID == _info.LocalParticipantID))
            {
                throw new InvalidOperationException(
                    "A participant's IsLocal must be true exactly for the archive's LocalParticipantID.");
            }

            using var command = Command(
                transaction,
                "INSERT INTO Participant (ParticipantID, DisplayName, IsLocal) VALUES ($id, $name, $local);");
            command.Parameters.AddWithValue("$id", participant.ParticipantID);
            command.Parameters.AddWithValue("$name", participant.DisplayName);
            command.Parameters.AddWithValue("$local", participant.IsLocal ? 1 : 0);
            command.ExecuteNonQuery();

            _participants++;
        }

        /// <summary>Writes one distinct media payload. Each restored attachment names one.</summary>
        /// <exception cref="InvalidOperationException">The row breaks a format rule.</exception>
        public void WriteMedia(ArchiveMedia media)
        {
            ArgumentNullException.ThrowIfNull(media);
            var transaction = RequireOpen();

            if (!ArchiveFormat.IsSha256(media.Sha256))
            {
                throw new InvalidOperationException("A media hash must be 64 lower-case hexadecimal digits.");
            }

            using var command = Command(
                transaction,
                """
                INSERT INTO Media (Sha256, MediaType, SizeBytes, DurationMS, Width, Height)
                VALUES ($sha, $type, $size, $duration, $width, $height);
                """);
            command.Parameters.AddWithValue("$sha", media.Sha256);
            command.Parameters.AddWithValue("$type", media.MediaType);
            command.Parameters.AddWithValue("$size", media.SizeBytes);
            command.Parameters.AddWithValue("$duration", (object?)media.DurationMS ?? DBNull.Value);
            command.Parameters.AddWithValue("$width", (object?)media.Width ?? DBNull.Value);
            command.Parameters.AddWithValue("$height", (object?)media.Height ?? DBNull.Value);
            command.ExecuteNonQuery();

            _media++;
        }

        /// <summary>Writes one message, its attachments and their basis lines.</summary>
        /// <exception cref="InvalidOperationException">A row breaks a format rule.</exception>
        public void WriteMessage(ArchiveMessage message)
        {
            ArgumentNullException.ThrowIfNull(message);
            var transaction = RequireOpen();

            if ((message.Kind == ArchiveMessageKind.User) != message.SenderParticipantID.HasValue)
            {
                throw new InvalidOperationException(
                    "A user message must have a sender and a system message must have none.");
            }

            if (message.DateTimeLocal.Kind == DateTimeKind.Utc)
            {
                throw new InvalidOperationException(
                    "A message time is a local wall-clock reading; a UTC time would be stored as the " +
                    "wrong reading.");
            }

            if (!Enum.IsDefined(message.Source))
            {
                throw new InvalidOperationException("A message's source must be Workspace or Export.");
            }

            if (_info.FormatVersion < 2 && message.Source != ArchiveMessageSource.Workspace)
            {
                throw new InvalidOperationException("A version 1 archive holds workspace messages only.");
            }

            if (message.Source == ArchiveMessageSource.Export
                && (message.IsForwarded is not null || message.ForwardScore is not null))
            {
                throw new InvalidOperationException(
                    "A message from an older export has no forwarded fact: its forwarded status and score " +
                    "must be null.");
            }

            using (var command = Command(
                       transaction,
                       _info.FormatVersion < 2
                           ? """
                             INSERT INTO Message (
                                 MessageID, SequenceNumber, DateTimeLocal, Kind, SenderParticipantID, Text,
                                 IsForwarded, ForwardScore)
                             VALUES ($id, $sequence, $time, $kind, $sender, $text, $forwarded, $score);
                             """
                           : """
                             INSERT INTO Message (
                                 MessageID, SequenceNumber, DateTimeLocal, Kind, SenderParticipantID, Text,
                                 IsForwarded, ForwardScore, Source)
                             VALUES ($id, $sequence, $time, $kind, $sender, $text, $forwarded, $score, $source);
                             """))
            {
                if (_info.FormatVersion >= 2)
                {
                    command.Parameters.AddWithValue("$source", FormatSource(message.Source));
                }

                command.Parameters.AddWithValue("$id", message.MessageID);
                command.Parameters.AddWithValue("$sequence", message.SequenceNumber);
                command.Parameters.AddWithValue("$time", ArchiveFormat.FormatLocal(message.DateTimeLocal));
                command.Parameters.AddWithValue("$kind", FormatKind(message.Kind));
                command.Parameters.AddWithValue("$sender", (object?)message.SenderParticipantID ?? DBNull.Value);
                command.Parameters.AddWithValue("$text", message.Text);
                command.Parameters.AddWithValue("$forwarded", Flag(message.IsForwarded));
                command.Parameters.AddWithValue("$score", (object?)message.ForwardScore ?? DBNull.Value);
                command.ExecuteNonQuery();
            }

            foreach (var attachment in message.Attachments)
            {
                WriteAttachment(transaction, message.MessageID, attachment);
            }

            _messages++;
        }

        /// <summary>Writes one collection (format version 2).</summary>
        /// <exception cref="InvalidOperationException">The row breaks a format rule, or the archive is version 1.</exception>
        public void WriteCollection(ArchiveCollection collection)
        {
            ArgumentNullException.ThrowIfNull(collection);
            var transaction = RequireMemories();

            if (collection.Kind is not (ArchiveMemoryVocabulary.KindImported or ArchiveMemoryVocabulary.KindPersonal)
                || (collection.Kind == ArchiveMemoryVocabulary.KindImported) != collection.PeriodGapMinutes is > 0
                || (collection.Kind == ArchiveMemoryVocabulary.KindPersonal && collection.PeriodGapMinutes is not null))
            {
                throw new InvalidOperationException(
                    "A collection must be Imported with a positive period gap, or Personal with none.");
            }

            using var command = Command(
                transaction,
                """
                INSERT INTO Collection (CollectionID, Kind, Name, SourceDescription, ShownByDefault, PeriodGapMinutes)
                VALUES ($id, $kind, $name, $source, $shown, $gap);
                """);
            command.Parameters.AddWithValue("$id", collection.CollectionID);
            command.Parameters.AddWithValue("$kind", collection.Kind);
            command.Parameters.AddWithValue("$name", collection.Name);
            command.Parameters.AddWithValue("$source", (object?)collection.SourceDescription ?? DBNull.Value);
            command.Parameters.AddWithValue("$shown", collection.ShownByDefault ? 1 : 0);
            command.Parameters.AddWithValue("$gap", (object?)collection.PeriodGapMinutes ?? DBNull.Value);
            command.ExecuteNonQuery();
        }

        /// <summary>Writes one entry with its periods and their items (format version 2).</summary>
        /// <exception cref="InvalidOperationException">A row breaks a format rule, or the archive is version 1.</exception>
        public void WriteEntry(ArchiveEntry entry)
        {
            ArgumentNullException.ThrowIfNull(entry);
            var transaction = RequireMemories();

            if ((entry.ImageSha256 is null) != (entry.ImageExtension is null)
                || (entry.ImageSha256 is { } sha && !ArchiveFormat.IsSha256(sha))
                || (entry.ImageExtension is { } extension
                    && (extension.Length < 2 || ArchiveFormat.SafeExtension(extension) != extension)))
            {
                throw new InvalidOperationException(
                    "An entry image must have both a media hash of 64 lower-case hexadecimal digits and a plain " +
                    "lower-case extension, or neither.");
            }

            foreach (var time in new DateTime?[] { entry.StartLocal, entry.EndLocal })
            {
                if (time is { Kind: DateTimeKind.Utc })
                {
                    throw new InvalidOperationException("An entry's local times must not be marked as UTC.");
                }
            }

            var hasEveryYear = _info.FormatVersion >= ArchiveFormat.FirstVersionWithEveryYear;

            if (entry.EveryYear)
            {
                if (!hasEveryYear)
                {
                    throw new InvalidOperationException("A yearly entry needs format version 3 or later.");
                }

                // That its collection is personal is checked in Complete: the collection is another table.
                if (entry.DatePrecision != "Day" || entry.EndLocal is not null)
                {
                    throw new InvalidOperationException(
                        "Only a personal entry dated to the day without an end date can be yearly.");
                }
            }

            using (var command = Command(
                       transaction,
                       hasEveryYear
                           ? """
                             INSERT INTO Entry (
                                 EntryID, CollectionID, SourceKey, Title, StartLocal, EndLocal, StartUtc, EndUtc,
                                 DatePrecision, Summary, ImageSha256, ImageExtension, Source, DayLevel, EveryYear)
                             VALUES ($id, $collection, $key, $title, $startLocal, $endLocal, $startUtc, $endUtc,
                                     $precision, $summary, $sha, $extension, $source, $level, $everyYear);
                             """
                           : """
                             INSERT INTO Entry (
                                 EntryID, CollectionID, SourceKey, Title, StartLocal, EndLocal, StartUtc, EndUtc,
                                 DatePrecision, Summary, ImageSha256, ImageExtension, Source, DayLevel)
                             VALUES ($id, $collection, $key, $title, $startLocal, $endLocal, $startUtc, $endUtc,
                                     $precision, $summary, $sha, $extension, $source, $level);
                             """))
            {
                if (hasEveryYear)
                {
                    command.Parameters.AddWithValue("$everyYear", entry.EveryYear ? 1 : 0);
                }

                command.Parameters.AddWithValue("$id", entry.EntryID);
                command.Parameters.AddWithValue("$collection", entry.CollectionID);
                command.Parameters.AddWithValue("$key", (object?)entry.SourceKey ?? DBNull.Value);
                command.Parameters.AddWithValue("$title", entry.Title);
                command.Parameters.AddWithValue("$startLocal", ArchiveFormat.FormatLocal(entry.StartLocal));
                command.Parameters.AddWithValue("$endLocal", Local(entry.EndLocal));
                command.Parameters.AddWithValue("$startUtc", Utc(entry.StartUtc));
                command.Parameters.AddWithValue("$endUtc", Utc(entry.EndUtc));
                command.Parameters.AddWithValue("$precision", entry.DatePrecision);
                command.Parameters.AddWithValue("$summary", (object?)entry.Summary ?? DBNull.Value);
                command.Parameters.AddWithValue("$sha", (object?)entry.ImageSha256 ?? DBNull.Value);
                command.Parameters.AddWithValue("$extension", (object?)entry.ImageExtension ?? DBNull.Value);
                command.Parameters.AddWithValue("$source", (object?)entry.Source ?? DBNull.Value);
                command.Parameters.AddWithValue("$level", (object?)entry.DayLevel ?? DBNull.Value);
                command.ExecuteNonQuery();
            }

            foreach (var image in entry.Images)
            {
                if (image.EntryID != entry.EntryID)
                {
                    throw new InvalidOperationException("An extra photo must be written with the entry it belongs to.");
                }

                if (!ArchiveFormat.IsSha256(image.Sha256)
                    || image.Extension is null
                    || image.Extension.Length < 2
                    || ArchiveFormat.SafeExtension(image.Extension) != image.Extension)
                {
                    throw new InvalidOperationException(
                        "An extra photo must have a media hash of 64 lower-case hexadecimal digits and a plain " +
                        "lower-case extension.");
                }

                using var command = Command(
                    transaction,
                    """
                    INSERT INTO EntryImage (EntryID, Ordinal, Sha256, Extension, Caption)
                    VALUES ($entry, $ordinal, $sha, $extension, $caption);
                    """);
                command.Parameters.AddWithValue("$entry", image.EntryID);
                command.Parameters.AddWithValue("$ordinal", image.Ordinal);
                command.Parameters.AddWithValue("$sha", image.Sha256);
                command.Parameters.AddWithValue("$extension", image.Extension);
                command.Parameters.AddWithValue("$caption", (object?)image.Caption ?? DBNull.Value);
                command.ExecuteNonQuery();
            }

            foreach (var period in entry.Periods)
            {
                if (period.EntryID != entry.EntryID)
                {
                    throw new InvalidOperationException("A period must be written with the entry it belongs to.");
                }

                using (var command = Command(
                           transaction,
                           """
                           INSERT INTO EntryPeriod (
                               EntryID, PeriodIndex, StartUtc, EndUtc, StartLocal, EndLocal, Label,
                               LocalItemCount, OtherItemCount)
                           VALUES ($entry, $index, $startUtc, $endUtc, $startLocal, $endLocal, $label, $local, $other);
                           """))
                {
                    command.Parameters.AddWithValue("$entry", period.EntryID);
                    command.Parameters.AddWithValue("$index", period.PeriodIndex);
                    command.Parameters.AddWithValue("$startUtc", ArchiveFormat.FormatUtc(period.StartUtc));
                    command.Parameters.AddWithValue("$endUtc", ArchiveFormat.FormatUtc(period.EndUtc));
                    command.Parameters.AddWithValue("$startLocal", ArchiveFormat.FormatLocal(period.StartLocal));
                    command.Parameters.AddWithValue("$endLocal", ArchiveFormat.FormatLocal(period.EndLocal));
                    command.Parameters.AddWithValue("$label", period.Label);
                    command.Parameters.AddWithValue("$local", period.LocalItemCount);
                    command.Parameters.AddWithValue("$other", period.OtherItemCount);
                    command.ExecuteNonQuery();
                }

                foreach (var item in period.Items)
                {
                    if (item.EntryID != entry.EntryID || item.PeriodIndex != period.PeriodIndex)
                    {
                        throw new InvalidOperationException("An item must be written with the period it belongs to.");
                    }

                    using var command = Command(
                        transaction,
                        """
                        INSERT INTO EntryItem (
                            EntryItemID, EntryID, PeriodIndex, ParticipantID, SetId, ItemId, ItemType, TimeUtc, TimeLocal)
                        VALUES ($id, $entry, $index, $participant, $set, $item, $type, $utc, $local);
                        """);
                    command.Parameters.AddWithValue("$id", item.EntryItemID);
                    command.Parameters.AddWithValue("$entry", item.EntryID);
                    command.Parameters.AddWithValue("$index", item.PeriodIndex);
                    command.Parameters.AddWithValue("$participant", item.ParticipantID);
                    command.Parameters.AddWithValue("$set", item.SetId);
                    command.Parameters.AddWithValue("$item", item.ItemId);
                    command.Parameters.AddWithValue("$type", item.ItemType);
                    command.Parameters.AddWithValue("$utc", ArchiveFormat.FormatUtc(item.TimeUtc));
                    command.Parameters.AddWithValue("$local", ArchiveFormat.FormatLocal(item.TimeLocal));
                    command.ExecuteNonQuery();
                }
            }
        }

        /// <summary>Writes one link from a personal entry (format version 2).</summary>
        /// <exception cref="InvalidOperationException">The row breaks a format rule, or the archive is version 1.</exception>
        public void WriteEntryLink(ArchiveEntryLink link)
        {
            ArgumentNullException.ThrowIfNull(link);
            var transaction = RequireMemories();

            using var command = Command(
                transaction,
                """
                INSERT INTO EntryLink (EntryLinkID, FromEntryID, ToEntryID, ToMessageSequenceNumber)
                VALUES ($id, $from, $toEntry, $toMessage);
                """);
            command.Parameters.AddWithValue("$id", link.EntryLinkID);
            command.Parameters.AddWithValue("$from", link.FromEntryID);
            command.Parameters.AddWithValue("$toEntry", (object?)link.ToEntryID ?? DBNull.Value);
            command.Parameters.AddWithValue("$toMessage", (object?)link.ToMessageSequenceNumber ?? DBNull.Value);
            command.ExecuteNonQuery();
        }

        /// <summary>Writes one item definition (format version 2). Only definitions an item uses may be written.</summary>
        /// <exception cref="InvalidOperationException">The row breaks a format rule, or the archive is version 1.</exception>
        public void WriteItemDefinition(ArchiveItemDefinition definition)
        {
            ArgumentNullException.ThrowIfNull(definition);
            var transaction = RequireMemories();

            if ((definition.IconSha256 is null) != (definition.IconExtension is null)
                || (definition.IconSha256 is { } sha && !ArchiveFormat.IsSha256(sha))
                || (definition.IconExtension is { } extension
                    && ArchiveFormat.SafeExtension(extension) != extension))
            {
                throw new InvalidOperationException(
                    "An item icon must have both a media hash of 64 lower-case hexadecimal digits and a plain " +
                    "lower-case extension, or neither.");
            }

            using var command = Command(
                transaction,
                """
                INSERT INTO ItemDefinition (SetId, ItemId, ItemType, Name, Detail, Hidden, GroupId, IconSha256, IconExtension)
                VALUES ($set, $item, $type, $name, $detail, $hidden, $group, $sha, $extension);
                """);
            command.Parameters.AddWithValue("$set", definition.SetId);
            command.Parameters.AddWithValue("$item", definition.ItemId);
            command.Parameters.AddWithValue("$type", definition.ItemType);
            command.Parameters.AddWithValue("$name", (object?)definition.Name ?? DBNull.Value);
            command.Parameters.AddWithValue("$detail", (object?)definition.Detail ?? DBNull.Value);
            command.Parameters.AddWithValue("$hidden", Flag(definition.Hidden));
            command.Parameters.AddWithValue("$group", (object?)definition.GroupId ?? DBNull.Value);
            command.Parameters.AddWithValue("$sha", (object?)definition.IconSha256 ?? DBNull.Value);
            command.Parameters.AddWithValue("$extension", (object?)definition.IconExtension ?? DBNull.Value);
            command.ExecuteNonQuery();
        }

        /// <summary>
        /// Writes one noted date (format version 2 only, CH-47) as the memories file holds it. Its fields must
        /// keep <see cref="NotedDateRules"/>, the latest year being the archive's creation year. Version 3 has no
        /// noted dates (CH-53).
        /// </summary>
        /// <exception cref="InvalidOperationException">The row breaks a format rule, or the archive is not version 2.</exception>
        public void WriteNotedDate(ArchiveNotedDate notedDate)
        {
            ArgumentNullException.ThrowIfNull(notedDate);
            var transaction = RequireMemories();

            if (_info.FormatVersion != ArchiveFormat.NotedDatesFormatVersion)
            {
                throw new InvalidOperationException(
                    "Noted dates are written into a version 2 archive only; from version 3 they are yearly memories.");
            }

            var errors = NotedDateRules.Validate(
                notedDate.Title, notedDate.Month, notedDate.Day, notedDate.Year, notedDate.Note,
                NotedDateRules.LatestYearAt(_info.CreatedUtc));

            if (errors.Count > 0)
            {
                // Names the rule, never the value.
                throw new InvalidOperationException(
                    "A noted date breaks a field rule (" + string.Join(", ", errors.Keys) + ").");
            }

            using var command = Command(
                transaction,
                """
                INSERT INTO NotedDate (NotedDateID, Title, Month, Day, Year, Note)
                VALUES ($id, $title, $month, $day, $year, $note);
                """);
            command.Parameters.AddWithValue("$id", notedDate.NotedDateID);
            command.Parameters.AddWithValue("$title", notedDate.Title);
            command.Parameters.AddWithValue("$month", notedDate.Month);
            command.Parameters.AddWithValue("$day", notedDate.Day);
            command.Parameters.AddWithValue("$year", (object?)notedDate.Year ?? DBNull.Value);
            command.Parameters.AddWithValue("$note", (object?)notedDate.Note ?? DBNull.Value);
            command.ExecuteNonQuery();
        }

        /// <summary>
        /// Writes one external link (format version 2, CH-48) as the memories file holds it. Its address and
        /// label must keep <see cref="ExternalLinkRules"/>; its owner (a personal entry or a noted date of the
        /// archive), the numbering 1..n of its owner's links and their count are checked in <see cref="Complete"/>.
        /// </summary>
        /// <exception cref="InvalidOperationException">The row breaks a format rule, or the archive is version 1.</exception>
        public void WriteExternalLink(ArchiveExternalLink link)
        {
            ArgumentNullException.ThrowIfNull(link);
            var transaction = RequireMemories();

            // Each message names the rule, never the value.
            if (!Enum.IsDefined(link.OwnerKind))
            {
                throw new InvalidOperationException("An external link must belong to an entry or a noted date.");
            }

            if (link.Ordinal < 1)
            {
                throw new InvalidOperationException("An external link's ordinal must be 1 or more.");
            }

            if (ExternalLinkRules.UrlError(link.Url) is not null)
            {
                throw new InvalidOperationException(
                    "An external link's address breaks a rule (an absolute https link with a host, no user-info part, " +
                    "no space, at most 2,000 characters).");
            }

            if (ExternalLinkRules.LabelError(link.Label) is not null)
            {
                throw new InvalidOperationException(
                    "An external link's label must be null, or not blank and at most 60 characters.");
            }

            using var command = Command(
                transaction,
                """
                INSERT INTO ExternalLink (ExternalLinkID, OwnerKind, OwnerID, Ordinal, Url, Label)
                VALUES ($id, $kind, $owner, $ordinal, $url, $label);
                """);
            command.Parameters.AddWithValue("$id", link.ExternalLinkID);
            command.Parameters.AddWithValue("$kind", ExternalLinkRules.OwnerKindText(link.OwnerKind));
            command.Parameters.AddWithValue("$owner", link.OwnerID);
            command.Parameters.AddWithValue("$ordinal", link.Ordinal);
            command.Parameters.AddWithValue("$url", link.Url);
            command.Parameters.AddWithValue("$label", (object?)link.Label ?? DBNull.Value);
            command.ExecuteNonQuery();
        }

        /// <summary>
        /// Checks the cross-row rules, stamps the format version, commits, and compacts the file.
        /// </summary>
        /// <returns>What was written.</returns>
        /// <exception cref="InvalidOperationException">
        /// A cross-row rule is broken. Nothing was committed.
        /// </exception>
        public ArchiveCounts Complete()
        {
            var transaction = RequireOpen();

            RequireCount(
                transaction,
                "SELECT COUNT(*) FROM Participant WHERE IsLocal = 1;",
                1,
                "The archive must have exactly one local participant, and it must be a participant of " +
                "the conversation.");

            var v2 = _info.FormatVersion >= 2;

            RequireCount(
                transaction,
                v2
                    ? ArchiveMemoryRules.MediaUnusedWithEntryImages
                    : "SELECT COUNT(*) FROM Media WHERE Sha256 NOT IN (SELECT MediaSha256 FROM Attachment " +
                      "WHERE MediaSha256 IS NOT NULL);",
                0,
                v2
                    ? "Every media payload must be named by at least one restored attachment, entry image, extra photo or item icon."
                    : "Every media payload must be named by at least one restored attachment.");

            if (v2)
            {
                RequireCount(transaction, ArchiveMemoryRules.LinksNotFromPersonal, 0,
                    "A link must start at an entry of a personal collection.");

                RequireCount(transaction, ArchiveMemoryRules.EntryImagesNotPersonal, 0,
                    "Extra photos belong to entries of a personal collection only.");

                RequireCount(transaction, ArchiveMemoryRules.EntryImagesRepeatCover, 0,
                    "An extra photo must not repeat its entry's cover.");

                RequireCount(transaction, ArchiveMemoryRules.DefinitionsUnused, 0,
                    "Only item definitions that an item of the archive uses may be written.");

                var notedDates = _info.FormatVersion == ArchiveFormat.NotedDatesFormatVersion;

                RequireCount(transaction, ArchiveMemoryRules.ExternalLinksOwnerInvalid(hasNotedDates: notedDates), 0,
                    notedDates
                        ? "An external link must belong to a personal entry or a noted date of the archive."
                        : "An external link must belong to a personal entry of the archive.");

                if (_info.FormatVersion >= ArchiveFormat.FirstVersionWithEveryYear)
                {
                    RequireCount(transaction, ArchiveMemoryRules.EntriesEveryYearInvalid, 0,
                        "Only a personal entry dated to the day without an end date can be yearly.");
                }

                RequireCount(transaction, ArchiveMemoryRules.ExternalLinkOwnersOrdinalsInvalid, 0,
                    "The external links of an entry or a noted date must be numbered 1, 2, ... without a gap.");

                RequireCount(transaction, ArchiveMemoryRules.ExternalLinkOwnersTooMany, 0,
                    "An entry or a noted date has at most 10 external links.");

                using (var command = Command(transaction, ArchiveMemoryRules.PeriodCountsDiffer))
                {
                    command.Parameters.AddWithValue("$local", _info.LocalParticipantID);

                    if (Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 0)
                    {
                        throw new InvalidOperationException(
                            "A period's local and other item counts must equal its items of the local and the " +
                            "other participant. Nothing was committed.");
                    }
                }
            }

            RequireCount(
                transaction,
                "SELECT COUNT(*) FROM Attachment AS a LEFT JOIN AttachmentBasis AS b " +
                "ON b.AttachmentID = a.AttachmentID WHERE (a.State = 'Restored') <> (b.AttachmentID IS NOT NULL);",
                0,
                "Every restored attachment, and only a restored attachment, must have one basis line.");

            RequireCount(transaction, "SELECT COUNT(*) FROM pragma_foreign_key_check;", 0,
                v2
                    ? "A message, attachment, basis line, entry, period, item, link, extra photo or item definition refers to a row the archive " +
                      "does not hold."
                    : "A message, attachment or basis line refers to a row the archive does not hold.");

            var counts = new ArchiveCounts(
                _participants, _messages, _attachments, _restored, _missing, _unmatched, _media);

            if (v2)
            {
                counts = counts with
                {
                    WorkspaceMessages = (int)Count(transaction, "SELECT COUNT(*) FROM Message WHERE Source = 'Workspace';"),
                    ExportMessages = (int)Count(transaction, "SELECT COUNT(*) FROM Message WHERE Source = 'Export';"),
                    ExportPeriods = _info.ExportPeriods.Count,
                    EntryLinks = (int)Count(transaction, "SELECT COUNT(*) FROM EntryLink;"),
                    ItemDefinitions = (int)Count(transaction, "SELECT COUNT(*) FROM ItemDefinition;"),
                    EntryImages = (int)Count(transaction, "SELECT COUNT(*) FROM EntryImage;"),
                    NotedDates = _info.FormatVersion == ArchiveFormat.NotedDatesFormatVersion
                        ? (int)Count(transaction, "SELECT COUNT(*) FROM NotedDate;")
                        : 0,
                    ExternalLinks = (int)Count(transaction, "SELECT COUNT(*) FROM ExternalLink;"),
                    Collections = ReadCollectionCounts(transaction),
                };
            }

            Execute(
                _connection,
                transaction,
                $"PRAGMA user_version = {_info.FormatVersion.ToString(CultureInfo.InvariantCulture)};");

            transaction.Commit();
            transaction.Dispose();
            _transaction = null;
            _completed = true;

            foreach (var statement in CompactionStatements)
            {
                Execute(_connection, null, statement);
            }

            _connection.Close();

            return counts;
        }

        /// <summary>
        /// Closes the database. An archive not completed is rolled back and left at version 0; the
        /// caller removes the file.
        /// </summary>
        public void Dispose()
        {
            if (_transaction is not null)
            {
                try
                {
                    _transaction.Rollback();
                }
                catch (SqliteException)
                {
                    // Already rolled back by SQLite itself; the file is version 0 either way.
                }

                _transaction.Dispose();
                _transaction = null;
            }

            _connection.Dispose();
        }

        // -------------------------------------------------------------------------------------
        // Rows.
        // -------------------------------------------------------------------------------------

        private void WriteInfo()
        {
            var values = new Dictionary<string, string>
            {
                [ArchiveInfo.Keys.FormatVersion] = _info.FormatVersion.ToString(CultureInfo.InvariantCulture),
                [ArchiveInfo.Keys.CreatedUtc] = ArchiveFormat.FormatUtc(_info.CreatedUtc),
                [ArchiveInfo.Keys.ProducerVersion] = _info.ProducerVersion,
                [ArchiveInfo.Keys.ConversationTitle] = _info.ConversationTitle,
                [ArchiveInfo.Keys.SourceExportSha256] = _info.SourceExportSha256,
                [ArchiveInfo.Keys.TimeZoneID] = _info.TimeZoneID,
                [ArchiveInfo.Keys.LocalParticipantID] = _info.LocalParticipantID.ToString(CultureInfo.InvariantCulture),
            };

            var keys = ArchiveInfo.Keys.All.ToList();

            for (var index = 0; index < _info.ExportPeriods.Count; index++)
            {
                var key = ArchiveInfo.ExportPeriodKey(index + 1);
                values[key] = _info.ExportPeriods[index].Format();
                keys.Add(key);
            }

            foreach (var key in keys)
            {
                using var command = Command(
                    _transaction!, "INSERT INTO ArchiveInfo (Key, Value) VALUES ($key, $value);");
                command.Parameters.AddWithValue("$key", key);
                command.Parameters.AddWithValue("$value", values[key]);
                command.ExecuteNonQuery();
            }
        }

        private List<ArchiveCollectionCounts> ReadCollectionCounts(SqliteTransaction transaction)
        {
            using var command = Command(transaction, ArchiveMemoryRules.CollectionCounts);

            var counts = new List<ArchiveCollectionCounts>();

            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                counts.Add(new ArchiveCollectionCounts(
                    reader.GetInt64(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3)));
            }

            return counts;
        }

        private void WriteAttachment(SqliteTransaction transaction, long messageID, ArchiveAttachment attachment)
        {
            if (attachment.MessageID != messageID)
            {
                throw new InvalidOperationException("An attachment must be written with the message that carries it.");
            }

            var restored = attachment.State == ArchiveAttachmentState.Restored;

            if (restored != (attachment.MediaSha256 is not null)
                || restored != (attachment.OriginalFileName is not null)
                || restored != (attachment.Basis is not null))
            {
                throw new InvalidOperationException(
                    "A restored attachment must have a media hash, an original file name and a basis, " +
                    "and any other attachment must have none of them.");
            }

            if (restored)
            {
                if (!ArchiveFormat.IsSha256(attachment.MediaSha256))
                {
                    throw new InvalidOperationException("A media hash must be 64 lower-case hexadecimal digits.");
                }

                if (attachment.OriginalFileName!.Length == 0
                    || attachment.OriginalFileName.IndexOfAny(['/', '\\', ':']) >= 0
                    || attachment.OriginalFileName.Any(char.IsControl))
                {
                    throw new InvalidOperationException(
                        "An original file name must be a bare, non-empty file name with no separator, colon " +
                        "or control character.");
                }
            }

            using (var command = Command(
                       transaction,
                       """
                       INSERT INTO Attachment (
                           AttachmentID, MessageID, Ordinal, State, MediaSha256, OriginalFileName,
                           IsForwarded, ForwardScore, LowQualityCopyOfAttachmentID)
                       VALUES ($id, $message, $ordinal, $state, $sha, $name, $forwarded, $score, $copyOf);
                       """))
            {
                command.Parameters.AddWithValue("$id", attachment.AttachmentID);
                command.Parameters.AddWithValue("$message", messageID);
                command.Parameters.AddWithValue("$ordinal", attachment.Ordinal);
                command.Parameters.AddWithValue("$state", FormatState(attachment.State));
                command.Parameters.AddWithValue("$sha", (object?)attachment.MediaSha256 ?? DBNull.Value);
                command.Parameters.AddWithValue("$name", (object?)attachment.OriginalFileName ?? DBNull.Value);
                command.Parameters.AddWithValue("$forwarded", Flag(attachment.IsForwarded));
                command.Parameters.AddWithValue("$score", (object?)attachment.ForwardScore ?? DBNull.Value);
                command.Parameters.AddWithValue(
                    "$copyOf", (object?)attachment.LowQualityCopyOfAttachmentID ?? DBNull.Value);
                command.ExecuteNonQuery();
            }

            if (attachment.Basis is { } basis)
            {
                using var command = Command(
                    transaction,
                    """
                    INSERT INTO AttachmentBasis (AttachmentID, BasisType, BasisText, Actor, ConfirmedUtc)
                    VALUES ($id, $type, $text, $actor, $confirmed);
                    """);
                command.Parameters.AddWithValue("$id", attachment.AttachmentID);
                command.Parameters.AddWithValue("$type", basis.BasisType);
                command.Parameters.AddWithValue("$text", basis.BasisText);
                command.Parameters.AddWithValue("$actor", (object?)basis.Actor ?? DBNull.Value);
                command.Parameters.AddWithValue("$confirmed", ArchiveFormat.FormatUtc(basis.ConfirmedUtc));
                command.ExecuteNonQuery();
            }

            _attachments++;

            switch (attachment.State)
            {
                case ArchiveAttachmentState.Restored: _restored++; break;
                case ArchiveAttachmentState.Missing: _missing++; break;
                default: _unmatched++; break;
            }
        }

        // -------------------------------------------------------------------------------------
        // Vocabulary and plumbing.
        // -------------------------------------------------------------------------------------

        /// <summary>
        /// Run after the commit, in this order. VACUUM builds a temporary copy of the whole database;
        /// with <c>temp_store = MEMORY</c> that copy stays in memory instead of going to the system
        /// temporary folder, so no copy of the archive is ever written outside the output folder.
        /// </summary>
        internal static IReadOnlyList<string> CompactionStatements { get; } =
        [
            "PRAGMA temp_store = MEMORY;",
            "VACUUM;",
        ];

        internal static string FormatKind(ArchiveMessageKind kind) => kind switch
        {
            ArchiveMessageKind.User => "User",
            ArchiveMessageKind.System => "System",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a message kind of the format."),
        };

        internal static string FormatState(ArchiveAttachmentState state) => state switch
        {
            ArchiveAttachmentState.Restored => "Restored",
            ArchiveAttachmentState.Missing => "Missing",
            ArchiveAttachmentState.Unmatched => "Unmatched",
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Not an attachment state of the format."),
        };

        internal static string FormatSource(ArchiveMessageSource source) => source switch
        {
            ArchiveMessageSource.Workspace => "Workspace",
            ArchiveMessageSource.Export => "Export",
            _ => throw new ArgumentOutOfRangeException(nameof(source), source, "Not a message source of the format."),
        };

        private static object Local(DateTime? value) =>
            value is { } time ? ArchiveFormat.FormatLocal(time) : DBNull.Value;

        private static object Utc(DateTime? value) =>
            value is { } time ? ArchiveFormat.FormatUtc(time) : DBNull.Value;

        private SqliteTransaction RequireMemories()
        {
            var transaction = RequireOpen();

            return _info.FormatVersion >= 2
                ? transaction
                : throw new InvalidOperationException("A version 1 archive has no memory tables.");
        }

        private long Count(SqliteTransaction transaction, string sql)
        {
            using var command = Command(transaction, sql);

            return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        private static void ValidateInfo(ArchiveInfo info)
        {
            // The current version is what an export writes; versions 1 and 2 are kept writable so a build can
            // prove it still reads what earlier builds wrote.
            if (!ArchiveFormat.IsKnownVersion(info.FormatVersion))
            {
                throw new InvalidOperationException(
                    "This build writes format versions " +
                    ArchiveFormat.MinimumFormatVersion.ToString(CultureInfo.InvariantCulture) + " to " +
                    ArchiveFormat.FormatVersion.ToString(CultureInfo.InvariantCulture) + " only.");
            }

            if (info.FormatVersion < 2 && info.ExportPeriods.Count > 0)
            {
                throw new InvalidOperationException("A version 1 archive records no export periods.");
            }

            foreach (var period in info.ExportPeriods)
            {
                if (period is null || period.LastLocal < period.FirstLocal
                    || period.FirstLocal.Kind == DateTimeKind.Utc || period.LastLocal.Kind == DateTimeKind.Utc)
                {
                    throw new InvalidOperationException(
                        "An export period must be two local wall-clock times, the first not after the last.");
                }
            }

            if (info.SourceExportSha256.Length != 0 && !ArchiveFormat.IsSha256(info.SourceExportSha256))
            {
                throw new InvalidOperationException(
                    "SourceExportSha256 must be empty or 64 lower-case hexadecimal digits.");
            }

            // Checked here so a bad value is refused before anything is created.
            ArchiveFormat.FormatUtc(info.CreatedUtc);
        }

        private static object Flag(bool? value) => value is { } flag ? (flag ? 1 : 0) : DBNull.Value;

        private SqliteTransaction RequireOpen() =>
            _transaction
            ?? throw new InvalidOperationException(
                _completed
                    ? "The archive is complete; nothing more can be written to it."
                    : "The archive writer is closed.");

        private SqliteCommand Command(SqliteTransaction transaction, string sql)
        {
            var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;

            return command;
        }

        private void RequireCount(SqliteTransaction transaction, string sql, long expected, string rule)
        {
            using var command = Command(transaction, sql);

            if (Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) != expected)
            {
                throw new InvalidOperationException(rule + " Nothing was committed.");
            }
        }

        private static void Execute(SqliteConnection connection, SqliteTransaction? transaction, string sql)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }
    }

    /// <summary>How many rows of each kind an archive holds.</summary>
    /// <param name="Participants">Participant rows.</param>
    /// <param name="Messages">Message rows.</param>
    /// <param name="Attachments">Attachment rows, of any state.</param>
    /// <param name="Restored">Attachments in the Restored state.</param>
    /// <param name="Missing">Attachments in the Missing state.</param>
    /// <param name="Unmatched">Attachments in the Unmatched state.</param>
    /// <param name="MediaFiles">Distinct media payloads (Media rows).</param>
    /// <remarks>
    /// The members after the positional ones are format version 2's counts. A version 1 manifest has
    /// none of them, and they read as zero and empty.
    /// </remarks>
    public sealed record ArchiveCounts(
        int Participants, int Messages, int Attachments, int Restored, int Missing, int Unmatched, int MediaFiles)
    {
        /// <summary>Version 2: messages from the main workspace.</summary>
        public int WorkspaceMessages { get; init; }

        /// <summary>Version 2: messages from older chat exports.</summary>
        public int ExportMessages { get; init; }

        /// <summary>Version 2: shown older exports, one info period each.</summary>
        public int ExportPeriods { get; init; }

        /// <summary>Version 2: links from personal entries.</summary>
        public int EntryLinks { get; init; }

        /// <summary>Version 2: item definitions (those some item uses).</summary>
        public int ItemDefinitions { get; init; }

        /// <summary>
        /// Version 2 (CH-46): extra photos of personal entries. A manifest written before CH-46 has none
        /// and reads as 0, matching an archive without the <c>EntryImage</c> table.
        /// </summary>
        public int EntryImages { get; init; }

        /// <summary>
        /// Version 2 (CH-47): noted dates. A manifest written before CH-47 has none and reads as 0, matching
        /// an archive without the <c>NotedDate</c> table; a version 3 archive has none (CH-53).
        /// </summary>
        public int NotedDates { get; init; }

        /// <summary>
        /// Version 2 (CH-48): external links on personal entries and noted dates. A manifest written before CH-48
        /// has none and reads as 0, matching an archive without the <c>ExternalLink</c> table.
        /// </summary>
        public int ExternalLinks { get; init; }

        /// <summary>Version 2: entries, periods and items of each collection, ordered by collection.</summary>
        public IReadOnlyList<ArchiveCollectionCounts> Collections { get; init; } = [];

        /// <summary>Equal when every count is equal, the collections' counts included.</summary>
        public bool Equals(ArchiveCounts? other) =>
            other is not null
            && Participants == other.Participants
            && Messages == other.Messages
            && Attachments == other.Attachments
            && Restored == other.Restored
            && Missing == other.Missing
            && Unmatched == other.Unmatched
            && MediaFiles == other.MediaFiles
            && WorkspaceMessages == other.WorkspaceMessages
            && ExportMessages == other.ExportMessages
            && ExportPeriods == other.ExportPeriods
            && EntryLinks == other.EntryLinks
            && ItemDefinitions == other.ItemDefinitions
            && EntryImages == other.EntryImages
            && NotedDates == other.NotedDates
            && ExternalLinks == other.ExternalLinks
            && (Collections ?? []).SequenceEqual(other.Collections ?? []);

        /// <inheritdoc />
        public override int GetHashCode() =>
            HashCode.Combine(
                HashCode.Combine(Participants, Messages, Attachments, Restored, Missing, Unmatched, MediaFiles),
                WorkspaceMessages,
                ExportMessages,
                ExportPeriods,
                EntryLinks,
                HashCode.Combine(ItemDefinitions, EntryImages, NotedDates, ExternalLinks, (Collections ?? []).Count));
    }

    /// <summary>Version 2: how many rows one collection holds.</summary>
    /// <param name="CollectionID">The collection.</param>
    /// <param name="Entries">Its entries.</param>
    /// <param name="Periods">Its entries' periods.</param>
    /// <param name="Items">Its entries' items.</param>
    public sealed record ArchiveCollectionCounts(long CollectionID, int Entries, int Periods, int Items);
}
