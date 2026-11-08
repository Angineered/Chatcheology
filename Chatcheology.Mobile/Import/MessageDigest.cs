using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Chatcheology.Archive;

namespace Chatcheology.Mobile.Import
{
    /// <summary>
    /// A digest of an archive's messages (CH-71 step 2): how many, the last sequence number, and a SHA-256 over every
    /// message in sequence order. Two archives whose messages up to sequence number N give the same digest hold the
    /// same earlier messages, so the phone's stars, position and "me" stay valid when one replaces the other.
    /// </summary>
    /// <param name="Count">How many messages the digest covers.</param>
    /// <param name="LastSequenceNumber">The last covered message's sequence number; 0 when none.</param>
    /// <param name="Sha256">The SHA-256, lower-case hex.</param>
    /// <remarks>
    /// <para><strong>What is covered, per message, in sequence order:</strong> sequence number, local time (to the
    /// second), kind, sender participant id, source (workspace, older export, message store), the raw text, and per
    /// attachment in ordinal order: attachment id, ordinal, state, media SHA-256, and the full copy it is the
    /// low-quality copy of.</para>
    /// <para><strong>Why these:</strong> stars are kept by sequence number and by attachment id (with its message's
    /// sequence number), the reading position by sequence number, "me" by participant id; the rest is what a reader
    /// sees as "the same message". The attachment's state and media hash make a changed photo a different message;
    /// the low-quality link decides which copy is shown, so a starred copy cannot silently become hidden.</para>
    /// <para><strong>Left out on purpose:</strong> forwarded facts and picture captions (the desktop may add these
    /// to earlier messages on a later export: new msgstore pairings, CH-71 D9 step 4; they never move or re-identify
    /// a message), the message and attachment row ids the phone never stores, the original file name and the
    /// confirmation basis (how a restore is known, not what it is). Memories are not messages: their collections and
    /// entries may change freely.</para>
    /// <para>Encoding: a fixed header, then each field in the order above; integers little-endian, strings as their
    /// UTF-8 byte count then the bytes, null as a count of -1. Version <see cref="Version"/>; a change to the
    /// definition gets a new version, so a stored digest of another version is never compared.</para>
    /// </remarks>
    public sealed record MessageDigest(int Count, int LastSequenceNumber, string Sha256)
    {
        /// <summary>The definition's version, part of the header and of the stored form.</summary>
        public const int Version = 1;

        private const string Header = "chatcheology-message-digest/1";

        /// <summary>The stored form: <c>1:count:last:sha256</c>.</summary>
        public string Format() => string.Create(CultureInfo.InvariantCulture, $"{Version}:{Count}:{LastSequenceNumber}:{Sha256}");

        /// <summary>A stored digest, or <see langword="null"/> when it is of another version or damaged.</summary>
        public static MessageDigest? Parse(string? text)
        {
            if (text?.Split(':') is not [var version, var count, var last, var sha]
                || version != Version.ToString(CultureInfo.InvariantCulture)
                || !int.TryParse(count, NumberStyles.None, CultureInfo.InvariantCulture, out var c)
                || !int.TryParse(last, NumberStyles.None, CultureInfo.InvariantCulture, out var l)
                || sha.Length != 64
                || !sha.All(ch => ch is >= '0' and <= '9' or >= 'a' and <= 'f'))
            {
                return null;
            }

            return new MessageDigest(c, l, sha);
        }

        /// <summary>The digest of every message of the archive <paramref name="reader"/> reads.</summary>
        public static MessageDigest Of(ArchiveReader reader, CancellationToken cancellationToken = default) =>
            Compute(reader, null, cancellationToken).Whole;

        /// <summary>
        /// The digest of every message, and of the messages with a sequence number up to
        /// <paramref name="throughSequenceNumber"/> (the earlier part an installed archive covers), in one pass.
        /// </summary>
        /// <exception cref="ArchiveFormatException">The messages cannot all be read in order.</exception>
        public static (MessageDigest Whole, MessageDigest? Through) Compute(
            ArchiveReader reader, int? throughSequenceNumber, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(reader);

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var writer = new FieldWriter(hash);
            writer.Text(Header);

            var count = 0;
            var last = 0;
            MessageDigest? through = null;

            foreach (var page in reader.ReadAllPages())
            {
                foreach (var message in page.Messages)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (through is null && throughSequenceNumber is { } limit && message.SequenceNumber > limit)
                    {
                        through = Snapshot(hash, count, last);
                    }

                    writer.Message(message);
                    count++;
                    last = message.SequenceNumber;
                }
            }

            var whole = Snapshot(hash, count, last);

            return (whole, throughSequenceNumber is null ? null : through ?? whole);
        }

        /// <summary>
        /// Whether <paramref name="newer"/>'s earlier part (its digest through <paramref name="installed"/>'s last
        /// sequence number) is exactly <paramref name="installed"/>: same count, same last sequence number, same hash.
        /// </summary>
        public static bool Continues(MessageDigest installed, MessageDigest? newer)
        {
            ArgumentNullException.ThrowIfNull(installed);

            return newer is not null
                   && newer.Count == installed.Count
                   && newer.LastSequenceNumber == installed.LastSequenceNumber
                   && string.Equals(newer.Sha256, installed.Sha256, StringComparison.Ordinal);
        }

        private static MessageDigest Snapshot(IncrementalHash hash, int count, int last) =>
            new(count, last, ArchiveHashing.Hex(hash.GetCurrentHash()));

        /// <summary>Writes fields into the hash in the fixed encoding.</summary>
        private sealed class FieldWriter(IncrementalHash hash)
        {
            private readonly byte[] _number = new byte[8];

            internal void Message(ArchiveMessage message)
            {
                Byte('M');
                Int32(message.SequenceNumber);
                Text(message.DateTimeLocal.ToString("yyyy'-'MM'-'dd'T'HH':'mm':'ss", CultureInfo.InvariantCulture));
                Byte(message.Kind switch
                {
                    ArchiveMessageKind.User => 1,
                    ArchiveMessageKind.System => 2,
                    _ => throw new ArchiveFormatException("A message has an unknown kind."),
                });
                Int64(message.SenderParticipantID);
                Byte(message.Source switch
                {
                    ArchiveMessageSource.Workspace => 1,
                    ArchiveMessageSource.Export => 2,
                    ArchiveMessageSource.Msgstore => 3,
                    _ => throw new ArchiveFormatException("A message has an unknown source."),
                });
                Text(message.Text);
                Int32(message.Attachments.Count);

                foreach (var attachment in message.Attachments.OrderBy(a => a.Ordinal).ThenBy(a => a.AttachmentID))
                {
                    Byte('A');
                    Int64(attachment.AttachmentID);
                    Int32(attachment.Ordinal);
                    Byte(attachment.State switch
                    {
                        ArchiveAttachmentState.Restored => 1,
                        ArchiveAttachmentState.Missing => 2,
                        ArchiveAttachmentState.Unmatched => 3,
                        _ => throw new ArchiveFormatException("An attachment has an unknown state."),
                    });
                    Text(attachment.MediaSha256);
                    Int64(attachment.LowQualityCopyOfAttachmentID);
                }
            }

            internal void Text(string? value)
            {
                if (value is null)
                {
                    Int32(-1);
                    return;
                }

                var bytes = Encoding.UTF8.GetBytes(value);
                Int32(bytes.Length);
                hash.AppendData(bytes);
            }

            private void Byte(int value)
            {
                _number[0] = (byte)value;
                hash.AppendData(_number, 0, 1);
            }

            private void Int32(int value)
            {
                BinaryPrimitives.WriteInt32LittleEndian(_number, value);
                hash.AppendData(_number, 0, 4);
            }

            private void Int64(long? value)
            {
                Byte(value is null ? 0 : 1);

                if (value is { } number)
                {
                    BinaryPrimitives.WriteInt64LittleEndian(_number, number);
                    hash.AppendData(_number, 0, 8);
                }
            }
        }
    }
}
