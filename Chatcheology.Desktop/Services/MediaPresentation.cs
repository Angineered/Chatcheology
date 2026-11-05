using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Chatcheology.Data.Media;
using Chatcheology.Mobile.Presentation;

namespace Chatcheology.Desktop.Services
{
    /// <summary>
    /// Showing a recorded media copy without taking hold of it.
    /// </summary>
    /// <remarks>
    /// <para><strong>The raw media is read-only, and stays that way.</strong> Nothing here writes,
    /// copies, renames or moves a file. The two things it does are decode a small preview and ask
    /// the operating system to open a file the operator asked to see.</para>
    /// <para><strong>Nothing here is a decision.</strong> Which copy of an asset was convenient to
    /// show is forgotten when the page changes, and is never recorded anywhere.</para>
    /// </remarks>
    internal static class MediaPresentation
    {
        /// <summary>How wide a preview is decoded.</summary>
        /// <remarks>
        /// A thumbnail, not a viewer. Decoding at the source resolution would hold a full frame in
        /// memory for every image on a page in exchange for detail nothing displays.
        /// </remarks>
        internal const int ThumbnailWidth = 320;

        /// <summary>
        /// A frozen preview of the image at <paramref name="fullPath"/>, or <see langword="null"/>
        /// when it cannot be decoded.
        /// </summary>
        /// <remarks>
        /// <para>The file is opened for reading, shared for reading, writing and deletion, so the
        /// viewer never blocks anything else that wants the recovery media — not even for the
        /// moment it is reading.</para>
        /// <para><c>BitmapCacheOption.OnLoad</c> completes the decode inside <c>EndInit</c>, so the
        /// stream can be disposed immediately and no handle survives the call, and <c>Freeze</c>
        /// makes the result usable from the thread that will display it.</para>
        /// <para><c>BitmapCreateOptions</c> is left alone deliberately.
        /// <c>IgnoreImageCache</c> belongs to a <c>UriSource</c> load, whose decode WPF caches by
        /// URI; asking for it beside a <c>StreamSource</c> throws, because there is no key to
        /// ignore. A stream decode is never served from that cache in the first place, so there is
        /// nothing here to opt out of.</para>
        /// <para>A file that cannot be decoded — truncated, corrupt, or not the format its name
        /// suggests, all of which recovered media legitimately is — returns null. That is a
        /// statement about this file on this machine, never about whether the attachment is
        /// resolved.</para>
        /// </remarks>
        internal static BitmapSource? TryLoadThumbnail(string fullPath) => TryLoadImage(fullPath, ThumbnailWidth);

        /// <summary>The width a memory photo is decoded at for the large photo view (CH-46).</summary>
        internal const int LargeWidth = 1600;

        /// <summary>
        /// As <see cref="TryLoadThumbnail"/>, decoded at <paramref name="decodeWidth"/> pixels wide
        /// and shown the right way up.
        /// </summary>
        /// <remarks>
        /// The EXIF orientation is read from the frame's metadata first, without decoding pixels
        /// (CH-57). When it swaps the sides, the stored height is the upright width, so the decode is
        /// sized by <c>DecodePixelHeight</c> instead. The decoded picture is then mirrored and
        /// rotated as <see cref="ImageOrientation"/> defines. A tag that cannot be read leaves the
        /// picture as stored, exactly as before.
        /// </remarks>
        internal static BitmapSource? TryLoadImage(string fullPath, int decodeWidth)
        {
            try
            {
                using var stream = new FileStream(
                    fullPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    bufferSize: 4096,
                    FileOptions.SequentialScan);

                var orientation = ReadOrientation(stream);

                stream.Position = 0;

                var image = new BitmapImage();

                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;

                if (orientation.SwapsSides)
                {
                    image.DecodePixelHeight = decodeWidth;
                }
                else
                {
                    image.DecodePixelWidth = decodeWidth;
                }

                image.StreamSource = stream;
                image.EndInit();
                image.Freeze();

                return orientation.IsAsStored ? image : Orient(image, orientation);
            }
            catch (Exception exception) when (
                exception is IOException
                    or UnauthorizedAccessException
                    or NotSupportedException
                    or ArgumentException
                    or InvalidOperationException
                    or FileFormatException
                    or OverflowException)
            {
                return null;
            }
        }

        /// <summary>The JPEG metadata path of the EXIF orientation tag.</summary>
        private const string JpegOrientationQuery = "/app1/ifd/{ushort=274}";

        /// <summary>
        /// The EXIF orientation of the picture in <paramref name="stream"/>, read from the first
        /// frame's metadata without decoding its pixels; as stored when the format carries none or
        /// it cannot be read.
        /// </summary>
        /// <remarks>
        /// Every failure here falls back to "as stored" rather than propagating: a picture that
        /// showed before CH-57 must never become "Can't preview" because of its tag. The decode that
        /// follows reports genuinely unreadable files as it always has.
        /// </remarks>
        private static ImageOrientation ReadOrientation(Stream stream)
        {
            try
            {
                var decoder = BitmapDecoder.Create(
                    stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);

                if (decoder.Frames.Count == 0 || decoder.Frames[0].Metadata is not BitmapMetadata metadata)
                {
                    return ImageOrientation.AsStored;
                }

                var value = metadata.GetQuery("System.Photo.Orientation");

                if (value is null && metadata.Format == "jpg")
                {
                    value = metadata.GetQuery(JpegOrientationQuery);
                }

                return ImageOrientation.FromExif(value switch
                {
                    ushort number => number,
                    short number => number,
                    uint number when number <= int.MaxValue => (int)number,
                    int number => number,
                    byte number => number,
                    _ => null,
                });
            }
#pragma warning disable CA1031 // Any failure to read the tag means "as stored", by design (CH-57).
            catch (Exception)
#pragma warning restore CA1031
            {
                return ImageOrientation.AsStored;
            }
        }

        /// <summary>
        /// <paramref name="image"/> mirrored left-to-right when the orientation says so, then
        /// rotated clockwise, frozen. As stored if the transform cannot be applied.
        /// </summary>
        private static BitmapSource Orient(BitmapSource image, ImageOrientation orientation)
        {
            try
            {
                var transform = new TransformGroup();

                if (orientation.FlipHorizontal)
                {
                    transform.Children.Add(new ScaleTransform(-1, 1));
                }

                transform.Children.Add(new RotateTransform(orientation.RotateDegrees));

                var turned = new TransformedBitmap(image, transform);
                turned.Freeze();

                return turned;
            }
            catch (Exception exception) when (
                exception is ArgumentException
                    or InvalidOperationException
                    or NotSupportedException
                    or System.Runtime.InteropServices.ExternalException)
            {
                return image;
            }
        }

        /// <summary>
        /// Whether a copy of this kind may be handed to the operating system.
        /// </summary>
        /// <remarks>
        /// <see cref="MediaType.Unknown"/> deliberately may not. It records that the workspace
        /// could not classify the file, and shell-executing something nobody has classified is not
        /// a thing a read-only viewer should do on the operator's behalf. It is shown as an unknown
        /// media card instead.
        /// </remarks>
        internal static bool CanOpenExternally(MediaType mediaType) =>
            mediaType is not MediaType.Unknown;

        /// <summary>
        /// Opens <paramref name="fullPath"/> with whatever this machine associates with it.
        /// </summary>
        /// <returns>
        /// <see langword="null"/> when the request was handed over, or a message describing why it
        /// could not be.
        /// </returns>
        /// <remarks>
        /// The file is checked first, so an unplugged drive is reported as an absent file rather
        /// than as a shell error. Every failure is reported rather than thrown: a media file that
        /// will not open is a fact about this machine, not a fault in the browse.
        /// </remarks>
        internal static string? TryOpenExternally(string fullPath)
        {
            try
            {
                if (!File.Exists(fullPath))
                {
                    return "The recorded media file is not available on this machine.";
                }

                using var process = Process.Start(
                    new ProcessStartInfo(fullPath) { UseShellExecute = true });

                return null;
            }
            catch (Win32Exception)
            {
                return "This machine has no application associated with that media file.";
            }
            catch (Exception exception) when (
                exception is InvalidOperationException
                    or IOException
                    or UnauthorizedAccessException)
            {
                return "The media file could not be opened.";
            }
        }
    }
}
