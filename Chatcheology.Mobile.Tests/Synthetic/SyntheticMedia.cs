using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Chatcheology.Mobile.Synthetic
{
    /// <summary>
    /// Invented media other than images: a short tone as a WAV file, a one-page PDF, and bytes that only
    /// claim to be a picture. Written by hand, no library.
    /// </summary>
    /// <remarks>Compiled into the tests and, as a linked file, into Debug builds of the Android app only.</remarks>
    internal static class SyntheticMedia
    {
        /// <summary>The length of every synthetic tone.</summary>
        internal const int ToneMilliseconds = 1500;

        /// <summary>A mono 16-bit PCM WAV of a sine tone, different per <paramref name="number"/>.</summary>
        internal static byte[] Wav(int number)
        {
            const int SampleRate = 8000;
            var samples = SampleRate * ToneMilliseconds / 1000;
            var frequency = 330.0 + 40 * (number % 10);
            var data = new byte[samples * 2];

            for (var i = 0; i < samples; i++)
            {
                var value = (short)(Math.Sin(2 * Math.PI * frequency * i / SampleRate) * 8000);
                BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(i * 2), value);
            }

            var wav = new byte[44 + data.Length];
            var span = wav.AsSpan();

            Encoding.ASCII.GetBytes("RIFF").CopyTo(span);
            BinaryPrimitives.WriteInt32LittleEndian(span[4..], 36 + data.Length);
            Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(span[8..]);
            BinaryPrimitives.WriteInt32LittleEndian(span[16..], 16);
            BinaryPrimitives.WriteInt16LittleEndian(span[20..], 1);
            BinaryPrimitives.WriteInt16LittleEndian(span[22..], 1);
            BinaryPrimitives.WriteInt32LittleEndian(span[24..], SampleRate);
            BinaryPrimitives.WriteInt32LittleEndian(span[28..], SampleRate * 2);
            BinaryPrimitives.WriteInt16LittleEndian(span[32..], 2);
            BinaryPrimitives.WriteInt16LittleEndian(span[34..], 16);
            Encoding.ASCII.GetBytes("data").CopyTo(span[36..]);
            BinaryPrimitives.WriteInt32LittleEndian(span[40..], data.Length);
            data.CopyTo(span[44..]);

            return wav;
        }

        /// <summary>A one-page PDF with a line of invented text, different per <paramref name="number"/>.</summary>
        internal static byte[] Pdf(int number)
        {
            var text = string.Create(CultureInfo.InvariantCulture, $"Synthetic document {number}");
            var stream = $"BT /F1 24 Tf 72 720 Td ({text}) Tj ET";
            var objects = new[]
            {
                "<< /Type /Catalog /Pages 2 0 R >>",
                "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
                "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
                string.Create(CultureInfo.InvariantCulture, $"<< /Length {stream.Length} >>\nstream\n{stream}\nendstream"),
                "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            };

            var pdf = new StringBuilder("%PDF-1.4\n");
            var offsets = new List<int>();

            for (var i = 0; i < objects.Length; i++)
            {
                offsets.Add(pdf.Length);
                pdf.Append(CultureInfo.InvariantCulture, $"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
            }

            var xref = pdf.Length;
            pdf.Append(CultureInfo.InvariantCulture, $"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");

            foreach (var offset in offsets)
            {
                pdf.Append(CultureInfo.InvariantCulture, $"{offset:D10} 00000 n \n");
            }

            pdf.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");

            return Encoding.ASCII.GetBytes(pdf.ToString());
        }

        /// <summary>Bytes stored as a picture that no decoder can read, different per <paramref name="number"/>.</summary>
        internal static byte[] BrokenImage(int number) =>
            Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture, $"not really a picture {number}"));
    }
}
