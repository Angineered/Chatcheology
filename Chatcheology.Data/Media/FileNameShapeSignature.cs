using System.Text;

namespace Chatcheology.Data.Media
{
    /// <summary>
    /// Reduces a fragment of a file name to a shape that carries no characters from the name it
    /// came from.
    /// </summary>
    /// <remarks>
    /// The census has to say what unrecognised names look like without ever saying what they are.
    /// Letters and digits leave as a class and a run length, so <c>copy</c> and <c>edit</c> both
    /// become <c>A4</c> and nothing distinguishes them afterwards. Only punctuation and spacing
    /// survive literally, because that is what makes one shape distinguishable from another.
    /// <para>
    /// The retained set is deliberately a short allowlist rather than "anything that is not a letter
    /// or digit". An unusual character in a recovered name would otherwise pass straight through
    /// into a report, which is precisely the leak this exists to prevent.
    /// </para>
    /// <para>
    /// Run lengths are exact rather than bucketed, so a four-digit group and a two-digit group are
    /// different shapes. That distinguishes more shapes at the cost of more of them, which is the
    /// right trade when the question being asked is what widths and layouts actually occur.
    /// </para>
    /// </remarks>
    internal static class FileNameShapeSignature
    {
        /// <summary>
        /// The characters that survive into a signature literally, because they carry structure
        /// rather than content.
        /// </summary>
        private const string RetainedCharacters = "-_.()[] ";

        /// <summary>
        /// The shape of <paramref name="value"/>, or an empty string when it is empty.
        /// </summary>
        /// <remarks>
        /// Ordinal and culture-free throughout. Nothing is lower-cased, because case is content:
        /// folding it would be a second decision hiding inside a normalisation.
        /// </remarks>
        internal static string Normalise(string value)
        {
            ArgumentNullException.ThrowIfNull(value);

            var signature = new StringBuilder(value.Length);
            var index = 0;

            while (index < value.Length)
            {
                var character = value[index];

                if (RetainedCharacters.Contains(character, StringComparison.Ordinal))
                {
                    signature.Append(character);
                    index++;

                    continue;
                }

                var runStart = index;
                var runClass = ClassOf(character);

                while (index < value.Length
                       && !RetainedCharacters.Contains(value[index], StringComparison.Ordinal)
                       && ClassOf(value[index]) == runClass)
                {
                    index++;
                }

                signature.Append(runClass).Append(index - runStart);
            }

            return signature.ToString();
        }

        /// <summary>
        /// The token letter for <paramref name="character"/>: letters, digits, or anything else.
        /// </summary>
        private static char ClassOf(char character) => character switch
        {
            _ when char.IsAsciiLetter(character) => 'A',
            _ when char.IsAsciiDigit(character) => 'D',
            _ => 'X',
        };
    }
}
