using System.Buffers.Binary;

namespace Chatcheology.Mobile.Synthetic
{
    /// <summary>
    /// An invented JPEG photo with an invented camera location (CH-59), for checking that a shared memory photo
    /// loses its location. The picture is two-colour diagonal bands (160 x 80, made once on a development
    /// machine and kept here as bytes); the location is written by hand as an EXIF GPS block.
    /// </summary>
    /// <remarks>
    /// Compiled into the tests and, as a linked file, into Debug builds of the Android app only. The
    /// coordinates (12 deg 34' 56" N, 65 deg 43' 21" E, open sea) are invented.
    /// </remarks>
    internal static class SyntheticJpeg
    {
        /// <summary>The picture's width in pixels.</summary>
        internal const int Width = 160;

        /// <summary>The picture's height in pixels.</summary>
        internal const int Height = 80;

        /// <summary>The invented latitude, as EXIF writes it.</summary>
        internal static readonly (uint Degrees, uint Minutes, uint Seconds) Latitude = (12, 34, 56);

        /// <summary>The invented longitude, as EXIF writes it.</summary>
        internal static readonly (uint Degrees, uint Minutes, uint Seconds) Longitude = (65, 43, 21);

        private const string Picture =
            "/9j/4AAQSkZJRgABAQEAYABgAAD/2wBDAA0JCgsKCA0LCwsPDg0QFCEVFBISFCgdHhghMCoyMS8qLi00O0tANDhHOS0uQllCR05Q" +
            "VFVUMz9dY1xSYktTVFH/2wBDAQ4PDxQRFCcVFSdRNi42UVFRUVFRUVFRUVFRUVFRUVFRUVFRUVFRUVFRUVFRUVFRUVFRUVFRUVFR" +
            "UVFRUVFRUVH/wAARCABQAKADASIAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUF" +
            "BAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVW" +
            "V1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi" +
            "4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAEC" +
            "AxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVm" +
            "Z2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq" +
            "8vP09fb3+Pn6/9oADAMBAAIRAxEAPwCDRdY83ba3TfvOiOf4vY+/8/r1264Cuk0XWPN22t037zojn+L2Pv8Az+vXjzDL+W9WktOq" +
            "PZo1r+7Ir61o/lbrq1X931dB/D7j2/l9OmHXf1zmtaP5W66tV/d9XQfw+49v5fTppl+Yc1qVV69GKtRt70Sxousebttbpv3nRHP8" +
            "Xsff+f167dcBXSaLrHm7bW6b950Rz/F7H3/n9eueYZfy3q0lp1Q6Na/uyK+taP5W66tV/d9XQfw+49v5fTph139c5rWj+VuurVf3" +
            "fV0H8PuPb+X06aZfmHNalVevRirUbe9EsaLrHm7bW6b950Rz/F7H3/n9eu3XAV0mi6x5u21um/edEc/xex9/5/XrnmGX8t6tJadU" +
            "OjWv7sivrWj+VuurVf3fV0H8PuPb+X06Ydd/XOa1o/lbrq1X931dB/D7j2/l9OmmX5hzWpVXr0Yq1G3vRLGi6x5u21um/edEc/xe" +
            "x9/5/Xrt1wFdJousebttbpv3nRHP8Xsff+f1655hl/LerSWnVDo1r+7Ir61o/lbrq1X931dB/D7j2/l9OmHXf1zmtaP5W66tV/d9" +
            "XQfw+49v5fTppl+Yc1qVV69GKtRt70Sxousebttbpv3nRHP8Xsff+f167dcBXSaLrHm7bW6b950Rz/F7H3/n9eueYZfy3q0lp1Q6" +
            "Na/uyK+taP5W66tV/d9XQfw+49v5fTph139c5rWj+VuurVf3fV0H8PuPb+X06aZfmHNalVevRirUbe9ENa0fyt11ar+76ug/h9x7" +
            "fy+nTDrv65zWtH8rddWq/u+roP4fce38vp0MvzDmtSqvXowrUbe9EsaLrHm7bW6b950Rz/F7H3/n9eu3XAV0mi6x5u21um/edEc/" +
            "xex9/wCf1655hl/LerSWnVDo1r+7Ir61o/lbrq1X931dB/D7j2/l9OmHXf1zmtaP5W66tV/d9XQfw+49v5fTppl+Yc1qVV69GKtR" +
            "t70Sxousebttbpv3nRHP8Xsff+f167dcBXSaLrHm7bW6b950Rz/F7H3/AJ/XrnmGX8t6tJadUOjWv7sivrWj+VuurVf3fV0H8PuP" +
            "b+X06Ydd/XOa1o/lbrq1X931dB/D7j2/l9OmmX5hzWpVXr0Yq1G3vRLGi6x5u21um/edEc/xex9/5/Xrt1wFdJousebttbpv3nRH" +
            "P8Xsff8An9eueYZfy3q0lp1Q6Na/uyK+taP5W66tV/d9XQfw+49v5fTph139c5rWj+VuurVf3fV0H8PuPb+X06aZfmHNalVevRir" +
            "Ube9EsaLrHm7bW6b950Rz/F7H3/n9eu3XAV0mi6x5u21um/edEc/xex9/wCf1655hl/LerSWnVDo1r+7Ir61o/lbrq1X931dB/D7" +
            "j2/l9OmHXf1zmtaP5W66tV/d9XQfw+49v5fTppl+Yc1qVV69GKtRt70Sxousebttbpv3nRHP8Xsff+f167dcBXSaLrHm7bW6b950" +
            "Rz/F7H3/AJ/XrnmGX8t6tJadUOjWv7sg0XWPN22t037zojn+L2Pv/P69duuArpNF1jzdtrdN+86I5/i9j7/z+vUzDL+W9WktOqCj" +
            "Wv7sivrWj+VuurVf3fV0H8PuPb+X06Ydd/XOa1o/lbrq1X931dB/D7j2/l9OmmX5hzWpVXr0Yq1G3vRLGi6x5u21um/edEc/xex9" +
            "/wCf167dcBXSaLrHm7bW6b950Rz/ABex9/5/XrnmGX8t6tJadUOjWv7sivrWj+VuurVf3fV0H8PuPb+X06Ydd/XOa1o/lbrq1X93" +
            "1dB/D7j2/l9OmmX5hzWpVXr0Yq1G3vRLGi6x5u21um/edEc/xex9/wCf167dcBXSaLrHm7bW6b950Rz/ABex9/5/XrnmGX8t6tJa" +
            "dUOjWv7sivrWj+VuurVf3fV0H8PuPb+X06Ydd/XOa1o/lbrq1X931dB/D7j2/l9OmmX5hzWpVXr0Yq1G3vRLGi6x5u21um/edEc/" +
            "xex9/wCf167dcBXSaLrHm7bW6b950Rz/ABex9/5/XrnmGX8t6tJadUOjWv7sivrWj+VuurVf3fV0H8PuPb+X06Ydd/XOa1o/lbrq" +
            "1X931dB/D7j2/l9OmmX5hzWpVXr0Yq1G3vRLGi6x5u21um/edEc/xex9/wCf167dcBXSaLrHm7bW6b950Rz/ABex9/5/XrnmGX8t" +
            "6tJadUOjWv7sivrWj+VuurVf3fV0H8PuPb+X06Ydd/XOa1o/lbrq1X931dB/D7j2/l9OmmX5hzWpVXr0Yq1G3vRDWtH8rddWq/u+" +
            "roP4fce38vp0w67+uc1rR/K3XVqv7vq6D+H3Ht/L6dDL8w5rUqr16MK1G3vRLGi6x5u21um/edEc/wAXsff+f167dcBXSaLrHm7b" +
            "W6b950Rz/F7H3/n9eueYZfy3q0lp1Q6Na/uyK+taP5W66tV/d9XQfw+49v5fTph139c5rWj+VuurVf3fV0H8PuPb+X06aZfmHNal" +
            "VevRirUbe9EsaLrHm7bW6b950Rz/ABex9/5/Xrt1wFdJousebttbpv3nRHP8Xsff+f1655hl/LerSWnVDo1r+7Ir61o/lbrq1X93" +
            "1dB/D7j2/l9OmHXf1zmtaP5W66tV/d9XQfw+49v5fTppl+Yc1qVV69GKtRt70Sxousebttbpv3nRHP8AF7H3/n9eu3XAV0mi6x5u" +
            "21um/edEc/xex9/5/XrnmGX8t6tJadUOjWv7sivrWj+VuurVf3fV0H8PuPb+X06Ydd/XOa1o/lbrq1X931dB/D7j2/l9OmmX5hzW" +
            "pVXr0Yq1G3vRLGi6x5u21um/edEc/wAXsff+f167dcBXSaLrHm7bW6b950Rz/F7H3/n9eueYZfy3q0lp1Q6Na/uyK+taP5W66tV/" +
            "d9XQfw+49v5fTph139c5rWj+VuurVf3fV0H8PuPb+X06aZfmHNalVevRirUbe9EsaLrHm7bW6b950Rz/ABex9/5/Xrt1wFdJouse" +
            "bttbpv3nRHP8Xsff+f1655hl/LerSWnVDo1r+7INF1jzdtrdN+86I5/i9j7/AM/r1264Cuk0XWPN22t037zojn+L2Pv/AD+vUzDL" +
            "+W9WktOqCjWv7sivrWj+VuurVf3fV0H8PuPb+X06Ydd/XOa1o/lbrq1X931dB/D7j2/l9OmmX5hzWpVXr0Yq1G3vRLGi6x5u21um" +
            "/edEc/xex9/5/Xrt1wFdJousebttbpv3nRHP8Xsff+f1655hl/LerSWnVDo1r+7Ir61o/lbrq1X931dB/D7j2/l9OmHXf1zmtaP5" +
            "W66tV/d9XQfw+49v5fTppl+Yc1qVV69GKtRt70Sxousebttbpv3nRHP8Xsff+f167dcBXSaLrHm7bW6b950Rz/F7H3/n9eueYZfy" +
            "3q0lp1Q6Na/uyK+taP5W66tV/d9XQfw+49v5fTph139c5rWj+VuurVf3fV0H8PuPb+X06aZfmHNalVevRirUbe9EsaLrHm7bW6b9" +
            "50Rz/F7H3/n9eu3XAV0mi6x5u21um/edEc/xex9/5/XrnmGX8t6tJadUOjWv7sivrWj+VuurVf3fV0H8PuPb+X06Ydd/XOa1o/lb" +
            "rq1X931dB/D7j2/l9OmmX5hzWpVXr0Yq1G3vRLGi6x5u21um/edEc/xex9/5/Xrt1wFdJousebttbpv3nRHP8Xsff+f1655hl/Le" +
            "rSWnVDo1r+7Ir61o/lbrq1X931dB/D7j2/l9OmHXf1zmtaP5W66tV/d9XQfw+49v5fTppl+Yc1qVV69GKtRt70T/2Q==";

        /// <summary>
        /// The JPEG with an EXIF block (big-endian) that holds only a GPS directory: version, latitude, longitude and
        /// altitude. The JFIF header of the stored picture is replaced by it.
        /// </summary>
        internal static byte[] WithLocation()
        {
            var picture = Convert.FromBase64String(Picture);

            // SOI, then the stored picture's segments after its APP0 (JFIF) header.
            var app0Length = (picture[4] << 8) | picture[5];
            var rest = picture.AsSpan(4 + app0Length);

            var tiff = new byte[172];
            var t = tiff.AsSpan();
            "MM"u8.CopyTo(t);
            BinaryPrimitives.WriteUInt16BigEndian(t[2..], 42);
            BinaryPrimitives.WriteUInt32BigEndian(t[4..], 8);

            // IFD0: one entry, the GPS directory's offset.
            BinaryPrimitives.WriteUInt16BigEndian(t[8..], 1);
            Entry(t[10..], 0x8825, 4, 1, 26);
            BinaryPrimitives.WriteUInt32BigEndian(t[22..], 0);

            // The GPS directory at 26: seven entries; values that do not fit go after it, from 116.
            BinaryPrimitives.WriteUInt16BigEndian(t[26..], 7);
            Entry(t[28..], 0x0000, 1, 4, 0x02030000);                 // GPSVersionID 2.3.0.0
            Entry(t[40..], 0x0001, 2, 2, (uint)'N' << 24);           // GPSLatitudeRef "N"
            Entry(t[52..], 0x0002, 5, 3, 116);                       // GPSLatitude
            Entry(t[64..], 0x0003, 2, 2, (uint)'E' << 24);           // GPSLongitudeRef "E"
            Entry(t[76..], 0x0004, 5, 3, 140);                       // GPSLongitude
            Entry(t[88..], 0x0005, 1, 1, 0);                         // GPSAltitudeRef: above sea level
            Entry(t[100..], 0x0006, 5, 1, 164);                      // GPSAltitude
            BinaryPrimitives.WriteUInt32BigEndian(t[112..], 0);

            Rationals(t[116..], Latitude.Degrees, Latitude.Minutes, Latitude.Seconds);
            Rationals(t[140..], Longitude.Degrees, Longitude.Minutes, Longitude.Seconds);
            Rationals(t[164..], 7);

            using var output = new MemoryStream();
            output.Write([0xFF, 0xD8, 0xFF, 0xE1]);
            Span<byte> length = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(length, (ushort)(2 + 6 + tiff.Length));
            output.Write(length);
            output.Write("Exif\0\0"u8);
            output.Write(tiff);
            output.Write(rest);

            return output.ToArray();
        }

        private static void Entry(Span<byte> at, ushort tag, ushort type, uint count, uint value)
        {
            BinaryPrimitives.WriteUInt16BigEndian(at, tag);
            BinaryPrimitives.WriteUInt16BigEndian(at[2..], type);
            BinaryPrimitives.WriteUInt32BigEndian(at[4..], count);
            BinaryPrimitives.WriteUInt32BigEndian(at[8..], value);
        }

        private static void Rationals(Span<byte> at, params uint[] whole)
        {
            for (var i = 0; i < whole.Length; i++)
            {
                BinaryPrimitives.WriteUInt32BigEndian(at[(i * 8)..], whole[i]);
                BinaryPrimitives.WriteUInt32BigEndian(at[(i * 8 + 4)..], 1);
            }
        }
    }
}