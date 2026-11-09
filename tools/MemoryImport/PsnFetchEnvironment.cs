namespace Chatcheology.Tools.MemoryImport
{
    /// <summary>What psn-fetch talks to: the HTTP handler, the secret prompt and the clock for waits.</summary>
    /// <param name="Handler">The HTTP handler (a fake one in tests: no network).</param>
    /// <param name="Prompt">Where the token and the other account's id come from.</param>
    /// <param name="Sleep">Waits between calls and before retries.</param>
    public sealed record PsnFetchEnvironment(HttpMessageHandler Handler, IPsnSecretPrompt Prompt, Action<TimeSpan> Sleep)
    {
        /// <summary>The real one: HTTPS, hidden console input, real waits.</summary>
        public static PsnFetchEnvironment Console() =>
            new(new SocketsHttpHandler { AllowAutoRedirect = false }, new ConsoleSecretPrompt(), Thread.Sleep);
    }
}
