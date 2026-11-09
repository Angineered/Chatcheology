using System.Text;

namespace Chatcheology.Tools.MemoryImport
{
    /// <summary>
    /// Reads the token and the other account's id from the console with hidden input: nothing typed
    /// or pasted is echoed. Refuses when there is no interactive console, so a secret can never come
    /// from a redirected file or pipe.
    /// </summary>
    public sealed class ConsoleSecretPrompt : IPsnSecretPrompt
    {
        /// <inheritdoc />
        public string ReadToken(bool renewal)
        {
            Console.WriteLine();
            Console.WriteLine(renewal
                ? "Paste the NEW psn-access-token from Postman (Request 1, then Request 2)."
                : "Paste the CURRENT psn-access-token from Postman (Request 1, then Request 2).");
            Console.WriteLine("Input is hidden and the token is never saved.");
            return ReadHidden("Access token: ");
        }

        /// <inheritdoc />
        public string ReadPartnerAccountId()
        {
            Console.WriteLine();
            Console.WriteLine("Paste the other account's numeric account id (partnerAccountId in the Postman collection).");
            Console.WriteLine("Input is hidden and the id is never saved.");
            return ReadHidden("Account id: ");
        }

        private static string ReadHidden(string label)
        {
            if (Console.IsInputRedirected)
            {
                throw new InvalidOperationException("psn-fetch needs an interactive console for the token; input is redirected.");
            }

            Console.Write(label);
            var value = new StringBuilder();

            while (true)
            {
                var key = Console.ReadKey(intercept: true);

                if (key.Key == ConsoleKey.Enter)
                {
                    break;
                }

                if (key.Key == ConsoleKey.Backspace)
                {
                    if (value.Length > 0)
                    {
                        value.Length--;
                    }

                    continue;
                }

                if (!char.IsControl(key.KeyChar))
                {
                    value.Append(key.KeyChar);
                }
            }

            Console.WriteLine();
            return value.ToString();
        }
    }
}
