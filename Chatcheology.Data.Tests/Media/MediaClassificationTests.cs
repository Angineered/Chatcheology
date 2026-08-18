using Chatcheology.Data.Media;

namespace Chatcheology.Data.Tests.Media
{
    /// <summary>
    /// Tests for the rules that read a media file's extension, size, path and name.
    /// </summary>
    /// <remarks>
    /// Tested directly rather than only through an inventory run, because these are pure functions
    /// and every interesting case is a string. A false-positive date or a mistaken direction is
    /// cheap to write here and expensive to notice in an archive of tens of thousands of files.
    /// </remarks>
    public class MediaClassificationTests
    {
        [Theory]
        [InlineData("photo.jpg", ".jpg")]
        [InlineData("photo.JPG", ".jpg")]
        [InlineData("photo.JpEg", ".jpeg")]
        [InlineData("clip.MP4", ".mp4")]
        [InlineData("archive.tar.gz", ".gz")]
        public void NormaliseExtension_LowerCasesAndKeepsTheDot(string fileName, string expected) =>
            Assert.Equal(expected, MediaClassification.NormaliseExtension(fileName));

        [Theory]
        [InlineData("README")]
        [InlineData("trailingdot.")]
        public void NormaliseExtension_FileWithNoExtension_IsNull(string fileName) =>
            Assert.Null(MediaClassification.NormaliseExtension(fileName));

        /// <remarks>
        /// Inherited from <see cref="Path.GetExtension(string)"/>: a dot-prefixed name with no other
        /// dot is treated as being entirely extension. Asserted rather than worked around, so the
        /// behaviour is a decision on record. Marker files of this shape are common in media trees,
        /// and either way the source file is never renamed.
        /// </remarks>
        [Fact]
        public void NormaliseExtension_DotPrefixedName_IsTreatedAsEntirelyExtension()
        {
            Assert.Equal(".nomedia", MediaClassification.NormaliseExtension(".nomedia"));

            Assert.Equal(
                MediaType.Unknown, MediaClassification.Classify(".nomedia", sizeBytes: 1));
        }

        [Theory]
        [InlineData(".jpg", MediaType.Image)]
        [InlineData(".jpeg", MediaType.Image)]
        [InlineData(".png", MediaType.Image)]
        [InlineData(".gif", MediaType.Image)]
        [InlineData(".webp", MediaType.Image)]
        [InlineData(".bmp", MediaType.Image)]
        [InlineData(".heic", MediaType.Image)]
        [InlineData(".heif", MediaType.Image)]
        [InlineData(".svg", MediaType.Image)]
        [InlineData(".eps", MediaType.Image)]
        [InlineData(".mp4", MediaType.Video)]
        [InlineData(".3gp", MediaType.Video)]
        [InlineData(".mkv", MediaType.Video)]
        [InlineData(".mov", MediaType.Video)]
        [InlineData(".webm", MediaType.Video)]
        [InlineData(".opus", MediaType.Audio)]
        [InlineData(".ogg", MediaType.Audio)]
        [InlineData(".mp3", MediaType.Audio)]
        [InlineData(".m4a", MediaType.Audio)]
        [InlineData(".aac", MediaType.Audio)]
        [InlineData(".wav", MediaType.Audio)]
        [InlineData(".amr", MediaType.Audio)]
        [InlineData(".flac", MediaType.Audio)]
        [InlineData(".pdf", MediaType.Document)]
        [InlineData(".doc", MediaType.Document)]
        [InlineData(".docx", MediaType.Document)]
        [InlineData(".xls", MediaType.Document)]
        [InlineData(".xlsx", MediaType.Document)]
        [InlineData(".ppt", MediaType.Document)]
        [InlineData(".pptx", MediaType.Document)]
        [InlineData(".txt", MediaType.Document)]
        [InlineData(".csv", MediaType.Document)]
        [InlineData(".rtf", MediaType.Document)]
        [InlineData(".zip", MediaType.Document)]
        [InlineData(".vcf", MediaType.Document)]
        [InlineData(".7z", MediaType.Document)]
        [InlineData(".rar", MediaType.Document)]
        [InlineData(".json", MediaType.Document)]
        [InlineData(".md", MediaType.Document)]
        [InlineData(".sql", MediaType.Document)]
        [InlineData(".exe", MediaType.Unknown)]
        [InlineData(".thumbdata", MediaType.Unknown)]
        [InlineData(null, MediaType.Unknown)]
        public void ClassifyByExtension_MapsTheWholeVocabulary(string? extension, MediaType expected) =>
            Assert.Equal(expected, MediaClassification.ClassifyByExtension(extension));

        /// <remarks>
        /// The extensions real archives were found to contain that are deliberately not classified.
        /// An installer, an application package and a marker file are not media, and calling them
        /// <see cref="MediaType.Document"/> to reduce an unknown count would be recording a
        /// classification the file does not support.
        /// </remarks>
        [Theory]
        [InlineData(".nomedia")]
        [InlineData(".was")]
        [InlineData(".tfr")]
        [InlineData(".exe")]
        [InlineData(".ipa")]
        public void ClassifyByExtension_DeliberatelyUnclassifiedExtensions_StayUnknown(string extension) =>
            Assert.Equal(MediaType.Unknown, MediaClassification.ClassifyByExtension(extension));

        /// <remarks>
        /// A sticker is image content and an animated GIF is image content. Neither gets a type of
        /// its own in this phase.
        /// </remarks>
        [Fact]
        public void ClassifyByExtension_StickerAndAnimatedImageFormats_AreImage()
        {
            Assert.Equal(MediaType.Image, MediaClassification.ClassifyByExtension(".webp"));
            Assert.Equal(MediaType.Image, MediaClassification.ClassifyByExtension(".gif"));
        }

        /// <remarks>
        /// The size rule takes precedence over the extension table, including over extensions only
        /// recently added to it. An empty file has no payload for any extension to describe.
        /// </remarks>
        [Theory]
        [InlineData(".jpg")]
        [InlineData(".mp4")]
        [InlineData(".pdf")]
        [InlineData(".opus")]
        [InlineData(".svg")]
        [InlineData(".eps")]
        [InlineData(".7z")]
        [InlineData(".rar")]
        [InlineData(".json")]
        [InlineData(".md")]
        [InlineData(".sql")]
        [InlineData(null)]
        public void Classify_EmptyFile_IsUnknownWhateverItsExtensionSays(string? extension) =>
            Assert.Equal(MediaType.Unknown, MediaClassification.Classify(extension, sizeBytes: 0));

        [Theory]
        [InlineData(".jpg", MediaType.Image)]
        [InlineData(".svg", MediaType.Image)]
        [InlineData(".7z", MediaType.Document)]
        [InlineData(".sql", MediaType.Document)]
        public void Classify_NonEmptyFile_UsesItsExtension(string extension, MediaType expected) =>
            Assert.Equal(expected, MediaClassification.Classify(extension, sizeBytes: 1));

        [Fact]
        public void Classify_NegativeSize_IsRejected() =>
            Assert.Throws<ArgumentOutOfRangeException>(
                () => MediaClassification.Classify(".jpg", sizeBytes: -1));

        [Theory]
        [InlineData("WhatsApp Images/Sent/IMG-20260105-WA0001.jpg")]
        [InlineData("WhatsApp Video/sent/VID-20260105-WA0002.mp4")]
        [InlineData("WhatsApp Documents/SENT/DOC-20260105-WA0003.pdf")]
        [InlineData("Sent/nested/deeper/file.jpg")]
        public void HasSentDirectorySegment_WholeSegment_IsTrue(string relativePath) =>
            Assert.True(MediaClassification.HasSentDirectorySegment(relativePath));

        /// <remarks>
        /// The substrings the rule must not match. A folder called <c>Sentimental</c> is not a Sent
        /// folder, and <c>Unsent</c> means very nearly the opposite of one.
        /// </remarks>
        [Theory]
        [InlineData("WhatsApp Images/Sentimental/IMG-20260105-WA0001.jpg")]
        [InlineData("WhatsApp Images/Unsent/IMG-20260105-WA0001.jpg")]
        [InlineData("WhatsApp Images/Presented/IMG-20260105-WA0001.jpg")]
        [InlineData("WhatsApp Images/Private/IMG-20260105-WA0001.jpg")]
        [InlineData("IMG-20260105-WA0001.jpg")]
        public void HasSentDirectorySegment_SubstringOrNoSentDirectory_IsFalse(string relativePath) =>
            Assert.False(MediaClassification.HasSentDirectorySegment(relativePath));

        /// <remarks>
        /// Direction is a fact about which folder something was filed in. A file that happens to be
        /// named <c>Sent</c> is not evidence that it was.
        /// </remarks>
        [Fact]
        public void HasSentDirectorySegment_FileNamedSent_IsNotDirectionEvidence() =>
            Assert.False(MediaClassification.HasSentDirectorySegment("WhatsApp Images/Sent"));

        /// <remarks>
        /// The positive case: a source that demonstrably files outgoing media under <c>Sent</c>.
        /// Only there does a file outside those folders mean "not sent".
        /// </remarks>
        [Theory]
        [InlineData("WhatsApp Images/Sent/IMG-20260105-WA0001.jpg", true)]
        [InlineData("Sent/nested/deeper/file.jpg", true)]
        [InlineData("WhatsApp Images/IMG-20260105-WA0001.jpg", false)]
        [InlineData("WhatsApp Images/Sentimental/IMG-20260105-WA0001.jpg", false)]
        [InlineData("WhatsApp Images/Unsent/IMG-20260105-WA0001.jpg", false)]
        public void DeriveIsSent_SourceWithSentDirectories_ReadsDirection(
            string relativePath, bool expected) =>
            Assert.Equal(
                expected,
                MediaClassification.DeriveIsSent(
                    MediaSourceTypes.WhatsAppMediaDirectory,
                    relativePath,
                    sourceHasSentDirectory: true));

        /// <remarks>
        /// The correction this rule exists for. A WhatsApp tree containing no <c>Sent</c> folder
        /// anywhere — a recovered or partially copied source — says nothing about direction, and
        /// every file in it is unknown rather than "not sent". Recording false here would turn one
        /// missing folder into a claim about every file beneath the root, and afterwards that claim
        /// would be indistinguishable from evidence the source really gave.
        /// </remarks>
        [Theory]
        [InlineData("WhatsApp Images/IMG-20260105-WA0001.jpg")]
        [InlineData("IMG-20260105-WA0001.jpg")]
        [InlineData("WhatsApp Images/Sentimental/IMG-20260105-WA0001.jpg")]
        public void DeriveIsSent_SourceWithNoSentDirectories_IsNull(string relativePath) =>
            Assert.Null(MediaClassification.DeriveIsSent(
                MediaSourceTypes.WhatsAppMediaDirectory,
                relativePath,
                sourceHasSentDirectory: false));

        /// <remarks>
        /// Null, not false. An unknown layout gives no direction evidence at all, whatever its
        /// folders happen to be called.
        /// </remarks>
        [Theory]
        [InlineData("Sent/IMG-20260105-WA0001.jpg")]
        [InlineData("Camera/IMG-20260105-WA0001.jpg")]
        public void DeriveIsSent_UnknownSourceType_IsNull(string relativePath) =>
            Assert.Null(MediaClassification.DeriveIsSent(
                "GenericMediaDirectory", relativePath, sourceHasSentDirectory: true));

        [Fact]
        public void ReadsDirectionFromPaths_OnlyForLayoutsWithKnownFolderConventions()
        {
            Assert.True(MediaClassification.ReadsDirectionFromPaths(
                MediaSourceTypes.WhatsAppMediaDirectory));

            Assert.False(MediaClassification.ReadsDirectionFromPaths("GenericMediaDirectory"));
        }

        [Theory]
        [InlineData("IMG-20260724-WA0004.jpg", 2026, 7, 24)]
        [InlineData("VID-20220128-WA0003.mp4", 2022, 1, 28)]
        [InlineData("DOC-20230820-WA0016.pdf", 2023, 8, 20)]
        [InlineData("PTT-20240229-WA0001.opus", 2024, 2, 29)]
        [InlineData("IMG-20260724-WA0004-2.jpg", 2026, 7, 24)]
        public void DeriveFileDate_WhatsAppNamingConvention_IsRead(
            string fileName, int year, int month, int day) =>
            Assert.Equal(
                new DateOnly(year, month, day),
                MediaClassification.DeriveFileDate(
                    MediaSourceTypes.WhatsAppMediaDirectory, fileName));

        /// <remarks>
        /// Structurally right, but no such date exists. February 29th in a common year is the case
        /// worth stating outright: it is a real-looking date that a lenient parser accepts.
        /// </remarks>
        [Theory]
        [InlineData("IMG-20261332-WA0004.jpg")]
        [InlineData("IMG-20260000-WA0004.jpg")]
        [InlineData("IMG-20230229-WA0004.jpg")]
        [InlineData("IMG-00000000-WA0004.jpg")]
        public void DeriveFileDate_InvalidCalendarDate_IsNull(string fileName) =>
            Assert.Null(MediaClassification.DeriveFileDate(
                MediaSourceTypes.WhatsAppMediaDirectory, fileName));

        /// <remarks>
        /// The false positives the surrounding <c>-</c> and <c>-WA</c> exist to exclude. Any eight
        /// consecutive digits in a file name would otherwise become a date that nothing afterwards
        /// could distinguish from one the export really recorded.
        /// </remarks>
        [Theory]
        [InlineData("20260724.jpg")]
        [InlineData("IMG_20260724_120000.jpg")]
        [InlineData("Scan-20260724-Invoice.pdf")]
        [InlineData("20260724-WA0004.jpg")]
        [InlineData("IMG-20260724-XY0004.jpg")]
        [InlineData("IMG-2026072-WA0004.jpg")]
        [InlineData("Receipt 0821234567 copy.pdf")]
        public void DeriveFileDate_NamesThatOnlyLookDated_AreNull(string fileName) =>
            Assert.Null(MediaClassification.DeriveFileDate(
                MediaSourceTypes.WhatsAppMediaDirectory, fileName));

        [Fact]
        public void DeriveFileDate_UnknownSourceType_IsNull() =>
            Assert.Null(MediaClassification.DeriveFileDate(
                "GenericMediaDirectory", "IMG-20260724-WA0004.jpg"));
    }
}
