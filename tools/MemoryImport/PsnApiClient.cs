using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Chatcheology.Tools.MemoryImport
{
    /// <summary>
    /// Asks the person running psn-fetch for the secrets it needs. Implementations never write the
    /// values anywhere (no file, no log, no screen echo).
    /// </summary>
    public interface IPsnSecretPrompt
    {
        /// <summary>Reads a PlayStation access token.</summary>
        /// <param name="renewal">True when PlayStation rejected the previous token.</param>
        string ReadToken(bool renewal);

        /// <summary>Reads the other account's numeric PlayStation account id.</summary>
        string ReadPartnerAccountId();
    }

    /// <summary>
    /// The PlayStation trophy API calls psn-fetch makes, with the old fetch script's behaviour: a
    /// bearer token, a pause after each call, retries with backoff on 408, 429 and 5xx (and on a
    /// network failure; a 429 or 503 Retry-After is honoured up to <see cref="MaxRetryAfter"/>), and
    /// a new token asked for on 401 or an expired- or invalid-token reply. A 403 stops the run: with a
    /// valid token it is usually the account's trophy privacy. At most <see cref="MaxAttempts"/>
    /// attempts per call.
    /// </summary>
    /// <remarks>
    /// No message from this class quotes a URL (it can hold the other account's id), the token or a
    /// response body.
    /// </remarks>
    public sealed class PsnApiClient : IDisposable
    {
        /// <summary>The API root every call is made under.</summary>
        public const string BaseUrl = "https://m.np.playstation.com/api/trophy/v1/";

        /// <summary>Attempts per call, as in the old script.</summary>
        public const int MaxAttempts = 6;

        /// <summary>The pause after each successful call.</summary>
        public static readonly TimeSpan Pause = TimeSpan.FromMilliseconds(450);

        /// <summary>The longest Retry-After wait honoured.</summary>
        public static readonly TimeSpan MaxRetryAfter = TimeSpan.FromMinutes(5);

        private static readonly HashSet<HttpStatusCode> Retryable =
        [
            HttpStatusCode.RequestTimeout,
            HttpStatusCode.TooManyRequests,
            HttpStatusCode.InternalServerError,
            HttpStatusCode.BadGateway,
            HttpStatusCode.ServiceUnavailable,
            HttpStatusCode.GatewayTimeout,
        ];

        private readonly HttpClient _client;
        private readonly IPsnSecretPrompt _prompt;
        private readonly Action<TimeSpan> _sleep;
        private readonly TextWriter _output;
        private readonly Func<DateTimeOffset> _utcNow;
        private string? _token;

        /// <summary>Makes a client.</summary>
        /// <param name="handler">The HTTP handler (a fake one in tests); disposed with the client.</param>
        /// <param name="prompt">Where tokens come from.</param>
        /// <param name="sleep">Waits (pauses and backoff); a no-op in tests.</param>
        /// <param name="output">Where retry and token notices go (counts only).</param>
        /// <param name="utcNow">The clock for a Retry-After given as a date; the system clock when null.</param>
        public PsnApiClient(
            HttpMessageHandler handler, IPsnSecretPrompt prompt, Action<TimeSpan> sleep, TextWriter output, Func<DateTimeOffset>? utcNow = null)
        {
            ArgumentNullException.ThrowIfNull(handler);
            ArgumentNullException.ThrowIfNull(prompt);
            ArgumentNullException.ThrowIfNull(sleep);
            ArgumentNullException.ThrowIfNull(output);
            _client = new HttpClient(handler, disposeHandler: true)
            {
                BaseAddress = new Uri(BaseUrl),
                Timeout = TimeSpan.FromSeconds(60),
            };
            _prompt = prompt;
            _sleep = sleep;
            _output = output;
            _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        }

        /// <summary>Calls made (each attempt counts).</summary>
        public int Attempts { get; private set; }

        /// <summary>Retries after a 408, 429, 5xx or network failure.</summary>
        public int Retries { get; private set; }

        /// <summary>New tokens asked for after a rejection.</summary>
        public int TokenRenewals { get; private set; }

        /// <summary>Asks for the token now if none has been given yet.</summary>
        public void EnsureToken()
        {
            _token ??= ReadToken(renewal: false);
        }

        /// <summary>GETs <paramref name="relativeUrl"/> (under <see cref="BaseUrl"/>) and parses the JSON reply.</summary>
        /// <param name="relativeUrl">The path and query.</param>
        /// <param name="what">A counts-only description for messages (never a title, id or URL).</param>
        /// <exception cref="InvalidOperationException">The call failed; the message names the HTTP status only.</exception>
        /// <exception cref="FormatException">The reply is not JSON.</exception>
        public JsonDocument Get(string relativeUrl, string what)
        {
            EnsureToken();

            for (var attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                Attempts++;
                HttpResponseMessage response;

                using (var request = new HttpRequestMessage(HttpMethod.Get, relativeUrl))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
                    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                    request.Headers.AcceptLanguage.Add(new StringWithQualityHeaderValue("en-US"));

                    try
                    {
                        response = _client.Send(request);
                    }
                    catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException)
                    {
                        if (attempt == MaxAttempts)
                        {
                            break;
                        }

                        Backoff(attempt, $"network failure ({exception.GetType().Name})");
                        continue;
                    }
                }

                using (response)
                {
                    var body = ReadBody(response);

                    if (response.IsSuccessStatusCode)
                    {
                        _sleep(Pause);

                        try
                        {
                            return JsonDocument.Parse(body);
                        }
                        catch (JsonException)
                        {
                            throw new FormatException($"PlayStation's reply for {what} is not JSON.");
                        }
                    }

                    var status = (int)response.StatusCode;

                    if (response.StatusCode == HttpStatusCode.Unauthorized
                        || body.Contains("Expired token", StringComparison.OrdinalIgnoreCase)
                        || body.Contains("Invalid access token", StringComparison.OrdinalIgnoreCase))
                    {
                        if (attempt == MaxAttempts)
                        {
                            throw new InvalidOperationException(
                                $"PlayStation rejected the token {MaxAttempts} times for {what} (HTTP {status}). " +
                                "Progress has been saved; run the same command again to continue.");
                        }

                        _output.WriteLine($"PlayStation rejected or expired the token (HTTP {status}).");
                        _output.WriteLine("In Postman run Request 1 then Request 2, then paste the new token.");
                        TokenRenewals++;
                        _token = ReadToken(renewal: true);
                        continue;
                    }

                    if (response.StatusCode == HttpStatusCode.Forbidden)
                    {
                        // A valid token refused access: usually the account's trophy privacy, not the token.
                        throw new InvalidOperationException(
                            $"PlayStation refused access to {what} (HTTP 403). This is usually the account's trophy " +
                            "privacy setting (trophies must be visible to the other account), not the token. " +
                            "Progress has been saved; fix it and run the same command again to continue.");
                    }

                    if (Retryable.Contains(response.StatusCode))
                    {
                        if (attempt == MaxAttempts)
                        {
                            break;
                        }

                        Backoff(attempt, $"HTTP {status}", RetryAfter(response));
                        continue;
                    }

                    throw new InvalidOperationException(
                        $"PlayStation request for {what} failed (HTTP {status}). " +
                        "Progress has been saved; run the same command again to continue.");
                }
            }

            throw new InvalidOperationException(
                $"PlayStation request for {what} failed after {MaxAttempts} attempts. " +
                "Progress has been saved; run the same command again to continue.");
        }

        /// <inheritdoc />
        public void Dispose() => _client.Dispose();

        private static string ReadBody(HttpResponseMessage response)
        {
            using var stream = response.Content.ReadAsStream();
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        /// <summary>
        /// The wait a 429 or 503 asks for in Retry-After (seconds or an HTTP date), capped at
        /// <see cref="MaxRetryAfter"/>; null when absent, unreadable or for another status.
        /// </summary>
        private TimeSpan? RetryAfter(HttpResponseMessage response)
        {
            if (response.StatusCode is not (HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
                || response.Headers.RetryAfter is not { } header)
            {
                return null;
            }

            TimeSpan? wait = header.Delta ?? (header.Date is { } date ? date - _utcNow() : null);

            if (wait is null)
            {
                return null;
            }

            if (wait < TimeSpan.Zero)
            {
                wait = TimeSpan.Zero;
            }

            return wait > MaxRetryAfter ? MaxRetryAfter : wait;
        }

        private void Backoff(int attempt, string reason, TimeSpan? retryAfter = null)
        {
            var seconds = retryAfter?.TotalSeconds ?? Math.Min(90, 5 * Math.Pow(2, attempt - 1));
            Retries++;
            _output.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"{reason}; retry after {seconds:0}s (attempt {attempt}/{MaxAttempts})."));
            _sleep(TimeSpan.FromSeconds(seconds));
        }

        private string ReadToken(bool renewal)
        {
            var token = _prompt.ReadToken(renewal)?.Trim();

            if (string.IsNullOrEmpty(token) || token.Any(char.IsWhiteSpace) || token.Any(char.IsControl))
            {
                throw new InvalidOperationException("No usable token was given. Nothing more has been fetched; run again with a new token.");
            }

            if (!token.All(char.IsAscii))
            {
                throw new InvalidOperationException(
                    "The token has a character that is not plain ASCII (a stray paste?). Nothing more has been fetched; run again with a new token.");
            }

            return token;
        }
    }
}
