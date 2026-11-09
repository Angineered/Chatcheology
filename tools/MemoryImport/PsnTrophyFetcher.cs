using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Chatcheology.Data.Memories.Psn;

namespace Chatcheology.Tools.MemoryImport
{
    /// <summary>What psn-fetch is asked to do.</summary>
    /// <param name="PreviousList">Last month's game list (the manifest psn-build reads). Only read.</param>
    /// <param name="Definitions">The existing trophy definitions file. Only read.</param>
    /// <param name="Decisions">Angie's decisions CSV (<see cref="PsnDecisionsFile"/>). Only read.</param>
    /// <param name="AccountA">Account a's field name (its set first in every key).</param>
    /// <param name="AccountB">Account b's field name.</param>
    /// <param name="Local">The a or b name that is the local account ("me" in the API).</param>
    /// <param name="Out">A new folder, or this tool's own unfinished run.</param>
    public sealed record PsnFetchOptions(
        string PreviousList,
        string Definitions,
        string Decisions,
        string AccountA,
        string AccountB,
        string Local,
        string Out)
    {
        /// <summary>Titles per trophyTitles page (the API allows up to 800).</summary>
        public int TitlePageSize { get; init; } = 800;

        /// <summary>Trophies per trophies page.</summary>
        public int TrophyPageSize { get; init; } = 100;
    }

    /// <summary>Counts from one psn-fetch run. Counts only: no titles, set ids, account names or ids.</summary>
    public sealed class PsnFetchCounts
    {
        /// <summary>Games on the previous list.</summary>
        public int ListedBefore { get; internal set; }

        /// <summary>Candidates added because a decisions row marks them together.</summary>
        public int Added { get; internal set; }

        /// <summary>Candidates not added: a decisions row marks them excluded.</summary>
        public int NotAddedExcluded { get; internal set; }

        /// <summary>Candidates not added: a decisions row marks them uncertain.</summary>
        public int NotAddedUncertain { get; internal set; }

        /// <summary>Candidates not added: no decisions row; written to the to-review file.</summary>
        public int AwaitingReview { get; internal set; }

        /// <summary>Together rows with no pair PlayStation returns on both accounts (listed in the out folder; not a stop).</summary>
        public int TogetherNotReturned { get; internal set; }

        /// <summary>Ids in together rows PlayStation does not return for their account (listed; not a stop).</summary>
        public int TogetherIdsNotReturned { get; internal set; }

        /// <summary>Together pairs not added because a set would be in two games (to review).</summary>
        public int TogetherBlocked { get; internal set; }

        /// <summary>Together-row pairs returned on both accounts but offered as no candidate (to review).</summary>
        public int NotOffered { get; internal set; }

        /// <summary>Candidates matched by decisions rows with different statuses (a stop).</summary>
        public int DecisionConflicts { get; internal set; }

        /// <summary>Previous-list games a row marks excluded or uncertain (a stop).</summary>
        public int ListedGamesNotTogether { get; internal set; }

        /// <summary>Games on the new list.</summary>
        public int ListedNow { get; internal set; }

        /// <summary>Added games given the title list's icon (previous-list games never get one here).</summary>
        public int IconsAdded { get; internal set; }

        /// <summary>Added games PlayStation gave no icon for.</summary>
        public int AddedWithoutIcon { get; internal set; }

        /// <summary>
        /// Listed games with no icon in the new list: previous-list games listed without one (psn-build
        /// carries theirs over from the current shared-activity file) and added games PlayStation gave none.
        /// </summary>
        public int IconsNone { get; internal set; }

        /// <summary>Previous-list games PlayStation no longer returns for account a.</summary>
        public int MissingSideA { get; internal set; }

        /// <summary>Previous-list games PlayStation no longer returns for account b.</summary>
        public int MissingSideB { get; internal set; }

        /// <summary>Games whose trophy times were fetched in this run.</summary>
        public int GamesFetched { get; internal set; }

        /// <summary>Games already saved by an earlier, stopped run into the same folder.</summary>
        public int GamesAlreadySaved { get; internal set; }

        /// <summary>Earned trophies, account a, every game in the timestamps file.</summary>
        public int TrophiesA { get; internal set; }

        /// <summary>Earned trophies, account b.</summary>
        public int TrophiesB { get; internal set; }

        /// <summary>Sets in the input definitions file.</summary>
        public int DefinitionSetsBefore { get; internal set; }

        /// <summary>Sets not in the definitions file whose definitions were fetched and added in this run.</summary>
        public int DefinitionSetsFetched { get; internal set; }

        /// <summary>
        /// Held sets with fewer definitions than PlayStation's totalTrophies (new DLC trophies) whose
        /// entry was replaced by a larger fetch.
        /// </summary>
        public int DefinitionSetsRefreshed { get; internal set; }

        /// <summary>Held sets kept as they were because PlayStation returned fewer definitions than held (never shrunk).</summary>
        public int DefinitionSetsKeptLarger { get; internal set; }

        /// <summary>Sets still holding fewer definitions than totalTrophies after this run's fetch.</summary>
        public int DefinitionSetsStillShort { get; internal set; }

        /// <summary>Sets in the new definitions file.</summary>
        public int DefinitionSetsNow { get; internal set; }

        /// <summary>Definitions (trophies) fetched in this run.</summary>
        public int DefinitionsFetched { get; internal set; }

        /// <summary>psn-build check: games built.</summary>
        public int CheckGames { get; internal set; }

        /// <summary>psn-build check: events.</summary>
        public int CheckEvents { get; internal set; }

        /// <summary>psn-build check: games with events and no icon (psn-build refuses these without --allow-no-icon).</summary>
        public int CheckGamesWithEventsAndNoIcon { get; internal set; }
    }

    /// <summary>The outcome of a run.</summary>
    /// <param name="Completed">
    /// False when the run stopped before fetching: listed games missing, or decisions that conflict
    /// (the counts say which).
    /// </param>
    /// <param name="Counts">The counts.</param>
    public sealed record PsnFetchResult(bool Completed, PsnFetchCounts Counts);

    /// <summary>
    /// psn-fetch (CH-71): makes this month's PSN inputs for the refresh in a new folder. The game
    /// list is made fresh from both accounts' trophy titles: every previous-list game stays as
    /// listed, and nothing is ever added on the tool's own judgement (one set on both accounts is not
    /// "played together"; missing is better than wrong). A candidate (one set on both accounts, or
    /// the same normalised title with different sets; not listed) is added only when a row of Angie's
    /// decisions file marks it together; excluded and uncertain rows are counted, and a candidate with
    /// no row goes to the to-review CSV. Conflicting rows, a previous-list game marked excluded or
    /// uncertain, or a listed game PlayStation no longer returns stop the run before any trophy is
    /// fetched. Then each listed game's earned-trophy times (the old fetch
    /// script's <see cref="PsnSharedActivityBuilder.TimestampsFormat"/> shape), the title icon for
    /// added games only (a previous-list game keeps its entry as listed, with or without an icon:
    /// psn-build carries a missing one over from the current shared-activity file), and definitions
    /// for every listed set (previous-list or added) that the definitions file is missing or holds
    /// fewer of than PlayStation's totalTrophies (new DLC trophies). A held set is replaced only by a
    /// larger fetch, never shrunk.
    /// </summary>
    /// <remarks>
    /// The inputs are never written. Every output file is written beside itself first and then moved
    /// into place. The timestamps file is saved after each game and the definitions file after each
    /// set, so a rerun into the same folder continues. The token and the other account's id are only
    /// ever held in memory: they are in no file, message or count.
    /// </remarks>
    public static partial class PsnTrophyFetcher
    {
        /// <summary>The run state file's format.</summary>
        public const string StateFormat = "chatcheology-psn-fetch-state-v1";

        /// <summary>The decision-conflicts file's format.</summary>
        public const string ConflictsFormat = "chatcheology-psn-decision-conflicts-v1";

        /// <summary>The missing-games file's format.</summary>
        public const string MissingFormat = "chatcheology-psn-missing-games-v1";

        /// <summary>The earned-trophy times (psn-build --timestamps, refresh -PsnTimestamps).</summary>
        public const string TimestampsFile = "psn-trophy-timestamps.json";

        /// <summary>The new game list (psn-build --manifest, refresh -PsnManifest).</summary>
        public const string GameListFile = "psn-game-list.json";

        /// <summary>The new definitions file (refresh -PsnDefinitions).</summary>
        public const string DefinitionsFile = "psn-trophy-definitions.json";

        /// <summary>Candidates with no decisions row, in the decisions file's columns, newest first.</summary>
        public const string ToReviewFile = "psn-to-review.csv";

        /// <summary>Together rows none of whose pairs PlayStation returns (decisions file columns).</summary>
        public const string TogetherNotReturnedFile = "psn-together-not-returned.csv";

        /// <summary>Written only when decisions conflict or mark a listed game not together.</summary>
        public const string ConflictsFile = "psn-decision-conflicts.json";

        /// <summary>Written only when listed games are missing.</summary>
        public const string MissingFile = "psn-missing-games.json";

        /// <summary>Marks the folder as this tool's run and holds its phase.</summary>
        public const string StateFile = "psn-fetch-state.json";

        /// <summary>To-review reason: no decisions row covers the candidate.</summary>
        public const string ReasonNoRow = "no decisions row";

        /// <summary>To-review reason: a together pair whose set is already in a listed game.</summary>
        public const string ReasonSetInGame = "set already in another game";

        /// <summary>To-review reason: one together row gives several pairs that share a set.</summary>
        public const string ReasonRowPairsSeveral = "row pairs several sets";

        /// <summary>To-review reason: together pairs from different rows share a set.</summary>
        public const string ReasonSetInSeveralPairs = "set in more than one together pair";

        /// <summary>To-review reason: a together row's pair PlayStation returns that was not a candidate.</summary>
        public const string ReasonNotOffered = "together row pair not offered";

        /// <summary>Together-not-returned problem: no pair of the row is returned on both accounts.</summary>
        public const string ProblemNoPair = "no pair returned on both accounts";

        /// <summary>Together-not-returned problem: one id is not returned for its account.</summary>
        public const string ProblemIdNotReturned = "id not returned for this account";

        private const string ReasonColumn = "reason";
        private const string LineColumn = "decisions_line";
        private const string ProblemColumn = "problem";

        /// <summary>Conflict kind: decisions rows with different statuses match one candidate.</summary>
        public const string ConflictRows = "rows-with-different-statuses";

        /// <summary>Conflict kind: a previous-list game is matched by an excluded or uncertain row.</summary>
        public const string ConflictListedGame = "listed-game-not-together";

        private const string PhaseListing = "listing";
        private const string PhaseListed = "listed";
        private const string PhaseDone = "done";
        private const int MaxPages = 1000;
        private const string UtcFormat = "yyyy'-'MM'-'dd'T'HH':'mm':'ss'Z'";

        private const string TimestampsNote =
            "Individual earned trophy timestamps (UTC), made by Chatcheology MemoryImport psn-fetch. " +
            "Trophies alone are not proof of being in a game together.";

        private static readonly JsonSerializerOptions WriteOptions = new()
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        /// <summary>Trade mark, registered and copyright signs (escaped so the source stays ASCII).</summary>
        private const string TrademarkSigns = "\u2122\u00AE\u00A9";

        private sealed record SetRef(string Id, string Service);

        private sealed record ListedGame(JsonObject Node, string Key, string Title, SetRef A, SetRef B, bool HasIcon);

        private sealed record TitleEntry(string SetId, string Service, string Name, string? Icon, string? LastUpdated);

        private sealed record Candidate(TitleEntry A, TitleEntry B)
        {
            internal string Key => $"{A.SetId}|{B.SetId}";
        }

        /// <summary>Runs psn-fetch.</summary>
        /// <param name="options">What to do.</param>
        /// <param name="api">The API client (its handler is a fake one in tests).</param>
        /// <param name="prompt">Where the other account's id comes from.</param>
        /// <param name="utcNow">The clock, for the definitions file's fetchedUtc.</param>
        /// <exception cref="ArgumentException">An account name is not plain letters or digits, or the names clash.</exception>
        /// <exception cref="FormatException">An input or a PlayStation reply is not the expected shape.</exception>
        /// <exception cref="InvalidOperationException">The run was refused or a call failed; progress is saved.</exception>
        public static PsnFetchResult Run(PsnFetchOptions options, PsnApiClient api, IPsnSecretPrompt prompt, Func<DateTime> utcNow)
        {
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(api);
            ArgumentNullException.ThrowIfNull(prompt);
            ArgumentNullException.ThrowIfNull(utcNow);

            var accountA = options.AccountA;
            var accountB = options.AccountB;
            CheckAccount(accountA, "a");
            CheckAccount(accountB, "b");

            if (string.Equals(accountA, accountB, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("The two account names are the same; they must name two different accounts.");
            }

            var localIsA = string.Equals(options.Local, accountA, StringComparison.OrdinalIgnoreCase);

            if (!localIsA && !string.Equals(options.Local, accountB, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("The local account must be the account a or the account b name.");
            }

            if (options.TitlePageSize < 1 || options.TrophyPageSize < 1)
            {
                throw new ArgumentException("Page sizes must be 1 or more.");
            }

            var outFolder = Path.GetFullPath(options.Out);
            var paths = new[]
                {
                    TimestampsFile, GameListFile, DefinitionsFile, ToReviewFile, TogetherNotReturnedFile, ConflictsFile, MissingFile, StateFile,
                }
                .Select(name => Path.Combine(outFolder, name)).ToList();

            foreach (var input in new[] { options.PreviousList, options.Definitions, options.Decisions })
            {
                var full = Path.GetFullPath(input);

                if (paths.Any(path => string.Equals(path, full, StringComparison.OrdinalIgnoreCase))
                    || string.Equals(full, outFolder, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("An input file is one of the output files; the inputs are never written.");
                }
            }

            var previousBytes = ReadFile(options.PreviousList, "previous game list");
            var definitionsBytes = ReadFile(options.Definitions, "definitions file");
            var decisionsBytes = ReadFile(options.Decisions, "decisions file");
            var decisions = PsnDecisionsFile.Parse(decisionsBytes, accountA, accountB);
            var (previousRoot, previousGames) = ReadGameList(previousBytes, accountA, accountB, "previous game list");
            PsnTrophyDefinitionsReader.Parse(WithoutBom(definitionsBytes));
            var inputDefinitions = ParseNode(definitionsBytes)!.AsObject();

            var counts = new PsnFetchCounts
            {
                ListedBefore = previousGames.Count,
                DefinitionSetsBefore = SetIds(inputDefinitions).Count,
            };

            var digests = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["previousListSha256"] = Sha256(previousBytes),
                ["definitionsSha256"] = Sha256(definitionsBytes),
                ["decisionsSha256"] = Sha256(decisionsBytes),
                ["accountsSha256"] = Sha256(Encoding.UTF8.GetBytes(
                    $"{accountA.ToUpperInvariant()}|{accountB.ToUpperInvariant()}|{(localIsA ? "a" : "b")}")),
            };

            var statePath = Path.Combine(outFolder, StateFile);
            var phase = PrepareOutFolder(outFolder, statePath, digests, counts);

            string? partnerId = null;

            string Partner()
            {
                if (partnerId is null)
                {
                    var given = prompt.ReadPartnerAccountId()?.Trim() ?? string.Empty;

                    if (!PartnerIdPattern().IsMatch(given))
                    {
                        throw new InvalidOperationException(
                            "The other account's id must be 5 to 25 digits (partnerAccountId in the Postman collection). Nothing more has been fetched.");
                    }

                    partnerId = given;
                }

                return partnerId;
            }

            string UserA() => localIsA ? "me" : Partner();
            string UserB() => localIsA ? Partner() : "me";

            var gameListPath = Path.Combine(outFolder, GameListFile);

            if (phase == PhaseListing)
            {
                // The other account's id first, then the token, as the old script asked.
                Partner();
                api.EnsureToken();
                var titlesA = FetchTitles(api, UserA(), "a", options.TitlePageSize);
                var titlesB = FetchTitles(api, UserB(), "b", options.TitlePageSize);
                if (!DecideList(previousRoot, previousGames, decisions, titlesA, titlesB, accountA, accountB, counts, outFolder))
                {
                    return new PsnFetchResult(false, counts);
                }

                foreach (var stale in new[] { MissingFile, ConflictsFile })
                {
                    var stalePath = Path.Combine(outFolder, stale);

                    if (File.Exists(stalePath))
                    {
                        File.Delete(stalePath);
                    }
                }

                WriteState(statePath, PhaseListed, digests, counts);
            }

            var gameListBytes = ReadFile(gameListPath, "new game list");
            var (_, listed) = ReadGameList(gameListBytes, accountA, accountB, "new game list");
            counts.ListedNow = listed.Count;
            counts.IconsNone = listed.Count(game => !game.HasIcon);

            var timestampsBytes = FetchTimes(api, listed, accountA, accountB, UserA, UserB, options.TrophyPageSize, outFolder, counts);
            var definitionsOutBytes = FetchDefinitions(
                api, listed, inputDefinitions, options.TrophyPageSize, outFolder, utcNow, Totals(timestampsBytes, accountA, accountB), counts);

            // The outputs must be accepted as the refresh will read them.
            var build = PsnSharedActivityBuilder.Build([timestampsBytes], gameListBytes, accountA, accountB, null, allowNoIcon: true);

            if (build.Counts.GamesBuilt != listed.Count)
            {
                throw new InvalidOperationException(
                    $"The check build wrote {build.Counts.GamesBuilt} of {listed.Count} listed games. Nothing is marked finished.");
            }

            counts.CheckGames = build.Counts.GamesBuilt;
            counts.CheckEvents = build.Counts.Events;
            counts.CheckGamesWithEventsAndNoIcon = build.Counts.GamesWithEventsAndNoIcon;
            counts.DefinitionSetsNow = PsnTrophyDefinitionsReader.Parse(definitionsOutBytes).SetCount;

            WriteState(statePath, PhaseDone, digests, counts);
            return new PsnFetchResult(true, counts);
        }

        /// <summary>
        /// The title comparison key: trademark signs removed, Unicode compatibility form, runs of white
        /// space made one space, trimmed, upper case. Nothing else (no fuzzy matching).
        /// </summary>
        public static string NormaliseTitle(string title)
        {
            ArgumentNullException.ThrowIfNull(title);
            var stripped = new string(title.Where(ch => !TrademarkSigns.Contains(ch)).ToArray())
                .Normalize(NormalizationForm.FormKC);
            var builder = new StringBuilder(stripped.Length);
            var pendingSpace = false;

            foreach (var ch in stripped)
            {
                if (char.IsWhiteSpace(ch))
                {
                    pendingSpace = true;
                    continue;
                }

                if (pendingSpace && builder.Length > 0)
                {
                    builder.Append(' ');
                }

                pendingSpace = false;
                builder.Append(char.ToUpperInvariant(ch));
            }

            return builder.ToString();
        }

        [GeneratedRegex("^[0-9]{5,25}$")]
        private static partial Regex PartnerIdPattern();

        private static string PrepareOutFolder(string outFolder, string statePath, Dictionary<string, string> digests, PsnFetchCounts counts)
        {
            if (File.Exists(outFolder))
            {
                throw new InvalidOperationException("--out names a file; give a new folder.");
            }

            if (!Directory.Exists(outFolder))
            {
                try
                {
                    Directory.CreateDirectory(outFolder);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    throw new InvalidOperationException($"The out folder could not be made ({exception.GetType().Name}).");
                }

                WriteState(statePath, PhaseListing, digests, counts);
                return PhaseListing;
            }

            if (!File.Exists(statePath))
            {
                throw new InvalidOperationException(
                    "The out folder already exists and is not an unfinished psn-fetch run. Give a new folder. Nothing has been written.");
            }

            JsonObject? state;

            try
            {
                state = ParseNode(ReadFile(statePath, "run state file")) as JsonObject;
            }
            catch (JsonException)
            {
                state = null;
            }

            if (state is null)
            {
                throw new InvalidOperationException("The out folder's run state file is damaged. Give a new folder. Nothing has been written.");
            }

            if ((string?)state["format"] != StateFormat)
            {
                throw new InvalidOperationException("The out folder's run state file is not this tool's. Give a new folder. Nothing has been written.");
            }

            var phase = (string?)state["phase"];

            switch (phase)
            {
                case PhaseDone:
                    throw new InvalidOperationException("The out folder holds a finished psn-fetch run. Give a new folder. Nothing has been written.");

                case PhaseListing:
                    // Nothing was fetched into it yet; the list is made again from the current inputs.
                    return PhaseListing;

                case PhaseListed:
                    if (digests.Keys.Any(name => state[name] is null))
                    {
                        throw new InvalidOperationException(
                            "The out folder's unfinished run was made by an earlier version of psn-fetch. Use a new out folder. Nothing has been written.");
                    }

                    foreach (var (name, value) in digests)
                    {
                        if ((string?)state[name] != value)
                        {
                            throw new InvalidOperationException(
                                "The out folder's unfinished run was started with other inputs or account names. " +
                                "Run with the same ones, or give a new folder. Nothing has been written.");
                        }
                    }

                    counts.Added = (int?)state["added"] ?? 0;
                    counts.NotAddedExcluded = (int?)state["notAddedExcluded"] ?? 0;
                    counts.NotAddedUncertain = (int?)state["notAddedUncertain"] ?? 0;
                    counts.AwaitingReview = (int?)state["awaitingReview"] ?? 0;
                    counts.TogetherNotReturned = (int?)state["togetherNotReturned"] ?? 0;
                    counts.TogetherIdsNotReturned = (int?)state["togetherIdsNotReturned"] ?? 0;
                    counts.TogetherBlocked = (int?)state["togetherBlocked"] ?? 0;
                    counts.NotOffered = (int?)state["notOffered"] ?? 0;
                    counts.IconsAdded = (int?)state["iconsAdded"] ?? 0;
                    counts.AddedWithoutIcon = (int?)state["addedWithoutIcon"] ?? 0;
                    return PhaseListed;

                default:
                    throw new InvalidOperationException("The out folder's run state file is damaged. Give a new folder. Nothing has been written.");
            }
        }

        private static void WriteState(string path, string phase, Dictionary<string, string> digests, PsnFetchCounts counts)
        {
            var state = new JsonObject
            {
                ["format"] = StateFormat,
                ["phase"] = phase,
            };

            foreach (var (name, value) in digests)
            {
                state[name] = value;
            }

            state["listedBefore"] = counts.ListedBefore;
            state["added"] = counts.Added;
            state["notAddedExcluded"] = counts.NotAddedExcluded;
            state["notAddedUncertain"] = counts.NotAddedUncertain;
            state["awaitingReview"] = counts.AwaitingReview;
            state["togetherNotReturned"] = counts.TogetherNotReturned;
            state["togetherIdsNotReturned"] = counts.TogetherIdsNotReturned;
            state["togetherBlocked"] = counts.TogetherBlocked;
            state["notOffered"] = counts.NotOffered;
            state["iconsAdded"] = counts.IconsAdded;
            state["addedWithoutIcon"] = counts.AddedWithoutIcon;
            WriteJson(path, state);
        }

        private static bool DecideList(
            JsonObject previousRoot,
            List<ListedGame> previous,
            List<PsnDecision> decisions,
            Dictionary<string, TitleEntry> titlesA,
            Dictionary<string, TitleEntry> titlesB,
            string accountA,
            string accountB,
            PsnFetchCounts counts,
            string outFolder)
        {
            var missing = new JsonArray();
            var conflicts = new JsonArray();

            foreach (var game in previous)
            {
                var noA = !titlesA.ContainsKey(game.A.Id);
                var noB = !titlesB.ContainsKey(game.B.Id);

                if (noA || noB)
                {
                    counts.MissingSideA += noA ? 1 : 0;
                    counts.MissingSideB += noB ? 1 : 0;
                    missing.Add(new JsonObject
                    {
                        ["key"] = game.Key,
                        ["game"] = game.Title,
                        ["missingForAccountA"] = noA,
                        ["missingForAccountB"] = noB,
                    });
                }

                // A listed game is never taken off by the tool; a row saying otherwise is Angie's to settle.
                var notTogether = decisions.Where(d => d.Matches(game.A.Id, game.B.Id) && d.Status != PsnDecisionsFile.Together).ToList();

                if (notTogether.Count > 0)
                {
                    counts.ListedGamesNotTogether++;
                    conflicts.Add(Conflict(ConflictListedGame, game.Key, game.Title, notTogether));
                }
            }

            // Candidates: one set on both accounts, or the same normalised title with different sets
            // (only between sets that are on one account each and in no listed game: a game whose
            // versions both accounts own is covered by its same-set pairs, not by cross pairs).
            var keys = previous.Select(game => game.Key).ToHashSet(StringComparer.Ordinal);
            var listedSets = previous.SelectMany(game => new[] { game.A.Id, game.B.Id }).ToHashSet(StringComparer.Ordinal);
            var shared = titlesA.Keys.Where(titlesB.ContainsKey).ToHashSet(StringComparer.Ordinal);
            var candidates = new List<Candidate>();

            foreach (var a in titlesA.Values
                         .Where(title => shared.Contains(title.SetId))
                         .OrderBy(title => title.Name, StringComparer.Ordinal)
                         .ThenBy(title => title.SetId, StringComparer.Ordinal))
            {
                var candidate = new Candidate(a, titlesB[a.SetId]);

                if (!keys.Contains(candidate.Key))
                {
                    candidates.Add(candidate);
                }
            }

            var byTitleB = titlesB.Values
                .Where(title => !shared.Contains(title.SetId) && !listedSets.Contains(title.SetId))
                .ToLookup(title => NormaliseTitle(title.Name), StringComparer.Ordinal);

            foreach (var a in titlesA.Values
                         .Where(title => !shared.Contains(title.SetId) && !listedSets.Contains(title.SetId))
                         .OrderBy(title => title.Name, StringComparer.Ordinal)
                         .ThenBy(title => title.SetId, StringComparer.Ordinal))
            {
                var normalised = NormaliseTitle(a.Name);

                if (normalised.Length == 0)
                {
                    continue;
                }

                foreach (var b in byTitleB[normalised].OrderBy(title => title.SetId, StringComparer.Ordinal))
                {
                    candidates.Add(new Candidate(a, b));
                }
            }

            var added = new List<JsonObject>();
            var review = new List<(Candidate Candidate, string Reason)>();
            var together = new List<(Candidate Candidate, List<PsnDecision> Rows)>();

            foreach (var candidate in candidates)
            {
                var rows = decisions.Where(d => d.Matches(candidate.A.SetId, candidate.B.SetId)).ToList();
                var statuses = rows.Select(row => row.Status).Distinct(StringComparer.Ordinal).ToList();

                switch (statuses.Count)
                {
                    case 0:
                        review.Add((candidate, ReasonNoRow));
                        counts.AwaitingReview++;
                        break;

                    case > 1:
                        counts.DecisionConflicts++;
                        conflicts.Add(Conflict(ConflictRows, candidate.Key, candidate.A.Name, rows));
                        break;

                    default:
                        switch (statuses[0])
                        {
                            case PsnDecisionsFile.Together:
                                together.Add((candidate, rows));
                                break;
                            case PsnDecisionsFile.Excluded:
                                counts.NotAddedExcluded++;
                                break;
                            default:
                                counts.NotAddedUncertain++;
                                break;
                        }

                        break;
                }
            }

            // One trophy set, one game: a together pair whose set is already in a listed game, or that
            // shares a set with another together pair, is not added (none of the sharing pairs is).
            static bool SharesSet(Candidate x, Candidate y) =>
                x.A.SetId == y.A.SetId || x.A.SetId == y.B.SetId || x.B.SetId == y.A.SetId || x.B.SetId == y.B.SetId;

            foreach (var (candidate, rows) in together)
            {
                string? reason = null;

                if (listedSets.Contains(candidate.A.SetId) || listedSets.Contains(candidate.B.SetId))
                {
                    reason = ReasonSetInGame;
                }
                else
                {
                    var sharing = together.Where(other => !ReferenceEquals(other.Candidate, candidate) && SharesSet(other.Candidate, candidate)).ToList();

                    if (sharing.Count > 0)
                    {
                        reason = sharing.Any(other => other.Rows.Any(row => rows.Any(own => ReferenceEquals(own, row))))
                            ? ReasonRowPairsSeveral
                            : ReasonSetInSeveralPairs;
                    }
                }

                if (reason is null)
                {
                    added.Add(Entry(candidate.A, candidate.B, accountA, accountB));
                }
                else
                {
                    review.Add((candidate, reason));
                    counts.TogetherBlocked++;
                }
            }

            // Together rows: pairs PlayStation returns on both accounts that were offered as neither a
            // listed game nor a candidate (to review, never added, unless both sets are already in a
            // game, as the cross pairs of a game whose two versions are both added); ids not returned
            // for their account; rows with no pair returned at all. None of these stops the run.
            var offered = keys.Concat(candidates.Select(candidate => candidate.Key)).ToHashSet(StringComparer.Ordinal);
            var inGames = listedSets
                .Concat(added.SelectMany(entry => new[] { ((string)entry["key"]!).Split('|')[0], ((string)entry["key"]!).Split('|')[1] }))
                .ToHashSet(StringComparer.Ordinal);
            var notOffered = new HashSet<string>(StringComparer.Ordinal);
            var notReturned = new List<IReadOnlyList<string>>();
            var setA = PsnDecisionsFile.SetColumn(accountA);
            var setB = PsnDecisionsFile.SetColumn(accountB);

            foreach (var row in decisions.Where(d => d.Status == PsnDecisionsFile.Together))
            {
                var line = row.Row.ToString(CultureInfo.InvariantCulture);
                var anyReturned = false;

                foreach (var sa in row.SetsA.Order(StringComparer.Ordinal))
                {
                    foreach (var sb in row.SetsB.Order(StringComparer.Ordinal))
                    {
                        if (!titlesA.ContainsKey(sa) || !titlesB.ContainsKey(sb))
                        {
                            continue;
                        }

                        anyReturned = true;
                        var key = $"{sa}|{sb}";

                        if (!offered.Contains(key) && !(inGames.Contains(sa) && inGames.Contains(sb)) && notOffered.Add(key))
                        {
                            review.Add((new Candidate(titlesA[sa], titlesB[sb]), ReasonNotOffered));
                            counts.NotOffered++;
                        }
                    }
                }

                if (!anyReturned)
                {
                    counts.TogetherNotReturned++;
                    notReturned.Add([line, row.Title, row.Status, Joined(row.SetsA), Joined(row.SetsB), ProblemNoPair]);
                }

                foreach (var sa in row.SetsA.Where(id => !titlesA.ContainsKey(id)).Order(StringComparer.Ordinal))
                {
                    counts.TogetherIdsNotReturned++;
                    notReturned.Add([line, row.Title, row.Status, sa, "", ProblemIdNotReturned]);
                }

                foreach (var sb in row.SetsB.Where(id => !titlesB.ContainsKey(id)).Order(StringComparer.Ordinal))
                {
                    counts.TogetherIdsNotReturned++;
                    notReturned.Add([line, row.Title, row.Status, "", sb, ProblemIdNotReturned]);
                }
            }

            if (missing.Count > 0)
            {
                WriteJson(Path.Combine(outFolder, MissingFile), new JsonObject
                {
                    ["format"] = MissingFormat,
                    ["note"] = "Games on the previous list that PlayStation no longer returns for an account. Nothing was fetched.",
                    ["games"] = missing,
                });
            }

            if (conflicts.Count > 0)
            {
                WriteJson(Path.Combine(outFolder, ConflictsFile), new JsonObject
                {
                    ["format"] = ConflictsFormat,
                    ["note"] =
                        "Decisions that stop the run: rows with different statuses for one pair, or an excluded or uncertain " +
                        "row for a game on the previous list. Correct the decisions file (or the game list) and run again. Nothing was fetched.",
                    ["conflicts"] = conflicts,
                });
            }

            if (missing.Count > 0 || conflicts.Count > 0)
            {
                return false;
            }

            // Every previous game stays exactly as listed, icon or no icon: a game without one gets its
            // icon carried over by psn-build from the current shared-activity file (the refresh passes
            // it), so the compare shows no icon change for it. Only added games take the title icon.
            var games = new JsonArray();

            foreach (var game in previous)
            {
                games.Add(game.Node.DeepClone());
            }

            foreach (var entry in added)
            {
                games.Add(entry);

                if (entry["icon"] is not null)
                {
                    counts.IconsAdded++;
                }
                else
                {
                    counts.AddedWithoutIcon++;
                }
            }

            counts.Added = added.Count;

            var root = previousRoot.DeepClone().AsObject();
            RemoveCaseInsensitive(root, "games");
            root["games"] = games;

            WriteBytes(Path.Combine(outFolder, ToReviewFile), PsnDecisionsFile.Write(
                [PsnDecisionsFile.TitleColumn, PsnDecisionsFile.StatusColumn, setA, setB, ReasonColumn,
                 PsnDecisionsFile.LastUpdatedColumn(accountA), PsnDecisionsFile.LastUpdatedColumn(accountB)],
                review
                    .OrderByDescending(r => new[] { r.Candidate.A.LastUpdated ?? "", r.Candidate.B.LastUpdated ?? "" }.Max(StringComparer.Ordinal), StringComparer.Ordinal)
                    .ThenBy(r => r.Candidate.A.Name, StringComparer.Ordinal)
                    .ThenBy(r => r.Candidate.Key, StringComparer.Ordinal)
                    .Select(r => (IReadOnlyList<string>)
                        [r.Candidate.A.Name, "", r.Candidate.A.SetId, r.Candidate.B.SetId, r.Reason,
                         r.Candidate.A.LastUpdated ?? "", r.Candidate.B.LastUpdated ?? ""])));
            WriteBytes(Path.Combine(outFolder, TogetherNotReturnedFile), PsnDecisionsFile.Write(
                [LineColumn, PsnDecisionsFile.TitleColumn, PsnDecisionsFile.StatusColumn, setA, setB, ProblemColumn],
                notReturned));
            WriteJson(Path.Combine(outFolder, GameListFile), root);
            return true;
        }

        private static string Joined(IReadOnlySet<string> ids) => string.Join("; ", ids.Order(StringComparer.Ordinal));

        private static JsonObject Conflict(string kind, string key, string title, List<PsnDecision> rows)
        {
            var rowNumbers = new JsonArray();
            var statuses = new JsonArray();

            foreach (var row in rows)
            {
                rowNumbers.Add(row.Row);
                statuses.Add(row.Status);
            }

            return new JsonObject
            {
                ["kind"] = kind,
                ["key"] = key,
                ["game"] = title,
                ["decisionLines"] = rowNumbers,
                ["statuses"] = statuses,
            };
        }

        /// <summary>The UTC date (yyyy-MM-dd) of an ISO date-time, or null when it is missing or not one.</summary>
        private static string? DateOnlyText(string? value) =>
            value is not null && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
                ? parsed.UtcDateTime.ToString("yyyy'-'MM'-'dd", CultureInfo.InvariantCulture)
                : null;

        private static JsonObject Entry(TitleEntry a, TitleEntry b, string accountA, string accountB)
        {
            var entry = new JsonObject
            {
                ["key"] = $"{a.SetId}|{b.SetId}",
                ["game"] = a.Name,
                ["icon"] = a.Icon ?? b.Icon,
                ["sameTrophySet"] = string.Equals(a.SetId, b.SetId, StringComparison.Ordinal),
                [accountA] = new JsonObject { ["npCommunicationId"] = a.SetId, ["npServiceName"] = a.Service },
                [accountB] = new JsonObject { ["npCommunicationId"] = b.SetId, ["npServiceName"] = b.Service },
            };

            return entry;
        }

        private static void RemoveCaseInsensitive(JsonObject node, string name)
        {
            foreach (var key in node.Select(pair => pair.Key).Where(key => string.Equals(key, name, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                node.Remove(key);
            }
        }

        private static Dictionary<string, TitleEntry> FetchTitles(PsnApiClient api, string user, string side, int pageSize)
        {
            var titles = new Dictionary<string, TitleEntry>(StringComparer.Ordinal);
            var what = $"account {side}'s game list";
            var offset = 0;
            var pages = 0;
            int? total = null;

            while (true)
            {
                using var document = api.Get(
                    string.Create(CultureInfo.InvariantCulture, $"users/{Uri.EscapeDataString(user)}/trophyTitles?limit={pageSize}&offset={offset}"),
                    what);
                var root = document.RootElement;
                var pageTotal = RequireTotal(root, what);

                if (total is not null && total != pageTotal)
                {
                    throw new InvalidOperationException($"Paging of {what} is incomplete: the total changed between pages. Run again.");
                }

                total = pageTotal;

                if (!TryGet(root, "trophyTitles", out var page) || page.ValueKind != JsonValueKind.Array)
                {
                    throw new FormatException($"A page of {what} has no trophyTitles array.");
                }

                var count = 0;

                foreach (var item in page.EnumerateArray())
                {
                    var where = $"{what}, title at index {(offset + count).ToString(CultureInfo.InvariantCulture)}";

                    if (item.ValueKind != JsonValueKind.Object)
                    {
                        throw new FormatException($"{where} is not an object.");
                    }

                    var title = new TitleEntry(
                        RequireString(item, "npCommunicationId", where),
                        RequireString(item, "npServiceName", where),
                        RequireString(item, "trophyTitleName", where),
                        OptionalString(item, "trophyTitleIconUrl", where),
                        DateOnlyText(OptionalString(item, "lastUpdatedDateTime", where)));

                    if (!titles.TryAdd(title.SetId, title))
                    {
                        throw new InvalidOperationException($"{what} repeats a trophy set (the list changed while paging). Run again.");
                    }

                    count++;
                }

                pages++;
                offset += count;

                if (offset >= total)
                {
                    if (offset > total)
                    {
                        throw new InvalidOperationException($"Paging of {what} is incomplete: more titles than the total. Run again.");
                    }

                    return titles;
                }

                if (count == 0 || pages >= MaxPages)
                {
                    throw new InvalidOperationException(string.Create(
                        CultureInfo.InvariantCulture,
                        $"Paging of {what} is incomplete: got {offset} of {total}. Run again."));
                }
            }
        }

        private static byte[] FetchTimes(
            PsnApiClient api,
            List<ListedGame> listed,
            string accountA,
            string accountB,
            Func<string> userA,
            Func<string> userB,
            int pageSize,
            string outFolder,
            PsnFetchCounts counts)
        {
            var path = Path.Combine(outFolder, TimestampsFile);
            var listedKeys = listed.Select(game => game.Key).ToHashSet(StringComparer.Ordinal);
            var games = new JsonArray();
            var finished = new HashSet<string>(StringComparer.Ordinal);

            if (File.Exists(path))
            {
                JsonObject? saved;

                try
                {
                    saved = ParseNode(ReadFile(path, "saved timestamps file")) as JsonObject;
                }
                catch (JsonException)
                {
                    saved = null;
                }

                if (saved is null)
                {
                    throw new InvalidOperationException("The saved timestamps file in the out folder is damaged. Give a new folder.");
                }

                if ((string?)saved["format"] != PsnSharedActivityBuilder.TimestampsFormat || saved["games"] is not JsonArray savedGames)
                {
                    throw new InvalidOperationException("The saved timestamps file in the out folder is not this tool's. Give a new folder.");
                }

                foreach (var node in savedGames)
                {
                    var key = node?["key"]?.GetValueKind() == JsonValueKind.String ? (string?)node["key"] : null;

                    if (key is null || !listedKeys.Contains(key) || !finished.Add(key))
                    {
                        throw new InvalidOperationException(
                            "The saved timestamps file in the out folder holds a game that is not on this run's list, or one twice. Give a new folder.");
                    }

                    games.Add(node!.DeepClone());
                }

                counts.GamesAlreadySaved = finished.Count;
            }

            JsonObject Root() => new()
            {
                ["format"] = PsnSharedActivityBuilder.TimestampsFormat,
                ["sourceAccounts"] = new JsonArray(accountA, accountB),
                ["timestampsAreUTC"] = true,
                ["note"] = TimestampsNote,
                ["games"] = games.DeepClone(),
            };

            var index = 0;

            foreach (var game in listed)
            {
                index++;

                if (finished.Contains(game.Key))
                {
                    continue;
                }

                var a = FetchEarned(api, userA(), game.A, $"listed game {index}, account a", pageSize);
                var b = FetchEarned(api, userB(), game.B, $"listed game {index}, account b", pageSize);

                games.Add(new JsonObject
                {
                    ["key"] = game.Key,
                    ["game"] = game.Title,
                    ["sameTrophySet"] = string.Equals(game.A.Id, game.B.Id, StringComparison.Ordinal),
                    [accountA] = a,
                    [accountB] = b,
                });

                WriteJson(path, Root());
                finished.Add(game.Key);
                counts.GamesFetched++;
            }

            if (!File.Exists(path))
            {
                // An empty list still gets its (empty) timestamps file.
                WriteJson(path, Root());
            }

            foreach (var node in games)
            {
                counts.TrophiesA += EarnedCount(node!, accountA);
                counts.TrophiesB += EarnedCount(node!, accountB);
            }

            return ReadFile(path, "timestamps file");
        }

        private static int EarnedCount(JsonNode game, string account)
        {
            var set = game.AsObject().FirstOrDefault(pair => string.Equals(pair.Key, account, StringComparison.OrdinalIgnoreCase)).Value;
            return set?["trophies"] is JsonArray trophies ? trophies.Count : 0;
        }

        private static JsonObject FetchEarned(PsnApiClient api, string user, SetRef set, string what, int pageSize)
        {
            var trophies = new JsonArray();
            var ids = new HashSet<long>();
            var offset = 0;
            var pages = 0;
            int? total = null;

            while (true)
            {
                using var document = api.Get(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"users/{Uri.EscapeDataString(user)}/npCommunicationIds/{Uri.EscapeDataString(set.Id)}/trophyGroups/all/trophies" +
                        $"?npServiceName={Uri.EscapeDataString(set.Service)}&limit={pageSize}&offset={offset}"),
                    what);
                var root = document.RootElement;
                var pageTotal = RequireTotal(root, what);

                if (total is not null && total != pageTotal)
                {
                    throw new InvalidOperationException($"Paging of {what} is incomplete: the total changed between pages. Run again.");
                }

                total = pageTotal;

                if (!TryGet(root, "trophies", out var page) || page.ValueKind != JsonValueKind.Array)
                {
                    throw new FormatException($"A page of {what} has no trophies array.");
                }

                var count = 0;

                foreach (var item in page.EnumerateArray())
                {
                    var where = $"{what}, trophy at index {(offset + count).ToString(CultureInfo.InvariantCulture)}";
                    count++;

                    if (item.ValueKind != JsonValueKind.Object)
                    {
                        throw new FormatException($"{where} is not an object.");
                    }

                    if (!TryGet(item, "earned", out var earned) || earned.ValueKind != JsonValueKind.True)
                    {
                        continue;
                    }

                    var earnedText = OptionalString(item, "earnedDateTime", where);

                    if (earnedText is null)
                    {
                        continue;
                    }

                    if (!DateTime.TryParseExact(earnedText, UtcFormat, CultureInfo.InvariantCulture,
                            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out _))
                    {
                        throw new FormatException($"{where}: earnedDateTime is not yyyy-MM-ddTHH:mm:ssZ.");
                    }

                    var id = RequireId(item, where);

                    if (!ids.Add(id))
                    {
                        throw new FormatException($"{where} repeats an earlier trophyId.");
                    }

                    trophies.Add(new JsonObject
                    {
                        ["trophyId"] = id,
                        ["trophyType"] = RequireString(item, "trophyType", where),
                        ["earnedDateTimeUTC"] = earnedText,
                    });
                }

                pages++;
                offset += count;

                if (offset >= total)
                {
                    if (offset > total)
                    {
                        throw new InvalidOperationException($"Paging of {what} is incomplete: more trophies than the total. Run again.");
                    }

                    break;
                }

                if (count == 0 || pages >= MaxPages)
                {
                    throw new InvalidOperationException(string.Create(
                        CultureInfo.InvariantCulture,
                        $"Paging of {what} is incomplete: got {offset} of {total}. Progress has been saved; run again to continue."));
                }
            }

            return new JsonObject
            {
                ["npCommunicationId"] = set.Id,
                ["npServiceName"] = set.Service,
                ["totalTrophies"] = total,
                ["earnedCount"] = trophies.Count,
                ["trophies"] = trophies,
            };
        }

        private static byte[] FetchDefinitions(
            PsnApiClient api,
            List<ListedGame> listed,
            JsonObject inputDefinitions,
            int pageSize,
            string outFolder,
            Func<DateTime> utcNow,
            Dictionary<string, int> totals,
            PsnFetchCounts counts)
        {
            var path = Path.Combine(outFolder, DefinitionsFile);
            JsonObject current;

            if (File.Exists(path))
            {
                var saved = ReadFile(path, "saved definitions file");

                try
                {
                    PsnTrophyDefinitionsReader.Parse(saved);
                }
                catch (FormatException)
                {
                    throw new InvalidOperationException("The saved definitions file in the out folder is damaged. Give a new folder.");
                }

                current = ParseNode(saved)!.AsObject();
            }
            else
            {
                current = inputDefinitions.DeepClone().AsObject();
            }

            var sets = current.First(pair => string.Equals(pair.Key, "sets", StringComparison.OrdinalIgnoreCase)).Value!.AsArray();
            var held = HeldCounts(sets);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var needed = new List<SetRef>();

            // Every listed set (previous-list or added) whose definitions are missing, or fewer than the
            // trophies PlayStation now has in it (new DLC trophies): fetched again in full (all groups).
            foreach (var game in listed)
            {
                foreach (var set in new[] { game.A, game.B })
                {
                    if (!seen.Add(set.Id))
                    {
                        continue;
                    }

                    if (!held.TryGetValue(set.Id, out var count) || count < totals.GetValueOrDefault(set.Id))
                    {
                        needed.Add(set);
                    }
                }
            }

            var number = 0;

            foreach (var set in needed)
            {
                number++;
                var fetched = FetchSetDefinitions(api, set, $"definitions set {number}", pageSize);
                var fetchedCount = fetched["trophies"]!.AsArray().Count;
                var total = totals.GetValueOrDefault(set.Id);
                counts.DefinitionsFetched += fetchedCount;

                if (!held.TryGetValue(set.Id, out var heldCount))
                {
                    sets.Add(fetched);
                    counts.DefinitionSetsFetched++;
                    held[set.Id] = fetchedCount;
                }
                else if (fetchedCount > heldCount)
                {
                    var index = sets.Select((node, i) => (node, i)).First(pair => SetIdOf(pair.node!) == set.Id).i;
                    sets[index] = fetched;
                    counts.DefinitionSetsRefreshed++;
                    held[set.Id] = fetchedCount;
                }
                else
                {
                    // Never shrink: PlayStation gave no more than is held, so the held entry stays.
                    counts.DefinitionSetsKeptLarger += fetchedCount < heldCount ? 1 : 0;
                }

                if (held[set.Id] < total)
                {
                    counts.DefinitionSetsStillShort++;
                }

                RemoveCaseInsensitive(current, "fetchedUtc");
                current["fetchedUtc"] = utcNow().ToString(UtcFormat, CultureInfo.InvariantCulture);
                WriteJson(path, current);
            }

            if (!File.Exists(path))
            {
                WriteJson(path, current);
            }

            return ReadFile(path, "definitions file");
        }

        private static JsonObject FetchSetDefinitions(PsnApiClient api, SetRef set, string what, int pageSize)
        {
            var trophies = new JsonArray();
            var ids = new HashSet<long>();
            var offset = 0;
            var pages = 0;
            int? total = null;
            string? version = null;

            while (true)
            {
                using var document = api.Get(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"npCommunicationIds/{Uri.EscapeDataString(set.Id)}/trophyGroups/all/trophies" +
                        $"?npServiceName={Uri.EscapeDataString(set.Service)}&limit={pageSize}&offset={offset}"),
                    what);
                var root = document.RootElement;
                var pageTotal = RequireTotal(root, what);

                if (total is not null && total != pageTotal)
                {
                    throw new InvalidOperationException($"Paging of {what} is incomplete: the total changed between pages. Run again.");
                }

                total = pageTotal;
                version ??= OptionalString(root, "trophySetVersion", what);

                if (!TryGet(root, "trophies", out var page) || page.ValueKind != JsonValueKind.Array)
                {
                    throw new FormatException($"A page of {what} has no trophies array.");
                }

                var count = 0;

                foreach (var item in page.EnumerateArray())
                {
                    var where = $"{what}, trophy at index {(offset + count).ToString(CultureInfo.InvariantCulture)}";
                    count++;

                    if (item.ValueKind != JsonValueKind.Object)
                    {
                        throw new FormatException($"{where} is not an object.");
                    }

                    var id = RequireId(item, where);

                    if (!ids.Add(id))
                    {
                        throw new FormatException($"{where} repeats an earlier trophyId.");
                    }

                    // Values exactly as PlayStation returns them; a missing field becomes null, never a guess.
                    trophies.Add(new JsonObject
                    {
                        ["trophyId"] = id,
                        ["trophyType"] = RequireString(item, "trophyType", where),
                        ["trophyName"] = OptionalString(item, "trophyName", where),
                        ["trophyDetail"] = OptionalString(item, "trophyDetail", where),
                        ["trophyIconUrl"] = OptionalString(item, "trophyIconUrl", where),
                        ["trophyGroupId"] = OptionalString(item, "trophyGroupId", where),
                        ["trophyHidden"] = OptionalBool(item, "trophyHidden", where),
                    });
                }

                pages++;
                offset += count;

                if (offset >= total)
                {
                    if (offset > total)
                    {
                        throw new InvalidOperationException($"Paging of {what} is incomplete: more trophies than the total. Run again.");
                    }

                    break;
                }

                if (count == 0 || pages >= MaxPages)
                {
                    throw new InvalidOperationException(string.Create(
                        CultureInfo.InvariantCulture,
                        $"Paging of {what} is incomplete: got {offset} of {total}. Progress has been saved; run again to continue."));
                }
            }

            return new JsonObject
            {
                ["npCommunicationId"] = set.Id,
                ["npServiceName"] = set.Service,
                ["trophySetVersion"] = version,
                ["trophies"] = trophies,
            };
        }

        private static string SetIdOf(JsonNode set) =>
            (string)set.AsObject().First(pair => string.Equals(pair.Key, "npCommunicationId", StringComparison.OrdinalIgnoreCase)).Value!;

        /// <summary>Definitions held per set (the file has passed the definitions reader).</summary>
        private static Dictionary<string, int> HeldCounts(JsonArray sets) =>
            sets.ToDictionary(
                set => SetIdOf(set!),
                set => set!.AsObject().First(pair => string.Equals(pair.Key, "trophies", StringComparison.OrdinalIgnoreCase)).Value!.AsArray().Count,
                StringComparer.Ordinal);

        /// <summary>The largest totalTrophies the timestamps file records per set (either account).</summary>
        private static Dictionary<string, int> Totals(byte[] timestamps, string accountA, string accountB)
        {
            var totals = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (var game in ParseNode(timestamps)!["games"]!.AsArray())
            {
                foreach (var account in new[] { accountA, accountB })
                {
                    var set = game!.AsObject().First(pair => string.Equals(pair.Key, account, StringComparison.OrdinalIgnoreCase)).Value!;
                    var id = (string)set["npCommunicationId"]!;
                    var total = (int)set["totalTrophies"]!;
                    totals[id] = Math.Max(total, totals.GetValueOrDefault(id));
                }
            }

            return totals;
        }

        private static HashSet<string> SetIds(JsonObject definitions)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var sets = definitions.FirstOrDefault(pair => string.Equals(pair.Key, "sets", StringComparison.OrdinalIgnoreCase)).Value;

            foreach (var set in sets?.AsArray() ?? [])
            {
                var id = set!.AsObject().FirstOrDefault(pair => string.Equals(pair.Key, "npCommunicationId", StringComparison.OrdinalIgnoreCase)).Value;
                ids.Add((string)id!);
            }

            return ids;
        }

        private static (JsonObject Root, List<ListedGame> Games) ReadGameList(byte[] json, string accountA, string accountB, string what)
        {
            JsonDocument document;

            try
            {
                document = JsonDocument.Parse(WithoutBom(json));
            }
            catch (JsonException)
            {
                throw new FormatException($"The {what} is not valid JSON.");
            }

            var games = new List<ListedGame>();
            var nodes = ParseNode(json)!;

            using (document)
            {
                var root = document.RootElement;

                if (root.ValueKind != JsonValueKind.Object || !TryGet(root, "games", out var gamesElement) || gamesElement.ValueKind != JsonValueKind.Array)
                {
                    throw new FormatException($"The {what} has no games array.");
                }

                var gameNodes = nodes.AsObject().First(pair => string.Equals(pair.Key, "games", StringComparison.OrdinalIgnoreCase)).Value!.AsArray();
                var keys = new HashSet<string>(StringComparer.Ordinal);
                var index = 0;

                foreach (var game in gamesElement.EnumerateArray())
                {
                    var where = $"The {what}'s game at index {index.ToString(CultureInfo.InvariantCulture)}";

                    if (game.ValueKind != JsonValueKind.Object)
                    {
                        throw new FormatException($"{where} is not an object.");
                    }

                    var key = RequireString(game, "key", where);
                    var title = RequireString(game, "game", where);
                    var a = RequireSet(game, accountA, where, "a");
                    var b = RequireSet(game, accountB, where, "b");
                    var icon = OptionalString(game, "icon", where);

                    if (!string.Equals(key, $"{a.Id}|{b.Id}", StringComparison.Ordinal))
                    {
                        throw new FormatException($"{where}: its key is not account a's trophy set then account b's, joined by '|'. Check --account-a.");
                    }

                    if (TryGet(game, "sameTrophySet", out var same)
                        && (same.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                            || same.GetBoolean() != string.Equals(a.Id, b.Id, StringComparison.Ordinal)))
                    {
                        throw new FormatException($"{where}: sameTrophySet is not true or false, or disagrees with its two trophy sets.");
                    }

                    if (!keys.Add(key))
                    {
                        throw new FormatException($"{where} repeats an earlier game's key.");
                    }

                    games.Add(new ListedGame(gameNodes[index]!.AsObject(), key, title, a, b, !string.IsNullOrWhiteSpace(icon)));
                    index++;
                }
            }

            return (nodes.AsObject(), games);
        }

        private static SetRef RequireSet(JsonElement game, string account, string where, string side)
        {
            if (!TryGet(game, account, out var set) || set.ValueKind != JsonValueKind.Object)
            {
                throw new FormatException($"{where} has no object for account {side}.");
            }

            return new SetRef(
                RequireString(set, "npCommunicationId", $"{where}, account {side}"),
                RequireString(set, "npServiceName", $"{where}, account {side}"));
        }

        private static int RequireTotal(JsonElement root, string what)
        {
            if (root.ValueKind != JsonValueKind.Object
                || !TryGet(root, "totalItemCount", out var total)
                || total.ValueKind != JsonValueKind.Number
                || !total.TryGetInt32(out var value)
                || value < 0)
            {
                throw new FormatException($"A page of {what} has no totalItemCount.");
            }

            return value;
        }

        private static long RequireId(JsonElement item, string where)
        {
            if (TryGet(item, "trophyId", out var element)
                && element.ValueKind == JsonValueKind.Number
                && element.TryGetInt64(out var id)
                && id >= 0)
            {
                return id;
            }

            throw new FormatException($"{where} has no whole-number trophyId.");
        }

        private static string RequireString(JsonElement element, string name, string where)
        {
            if (TryGet(element, name, out var value)
                && value.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(value.GetString()))
            {
                return value.GetString()!;
            }

            throw new FormatException($"{where} has no {name} text.");
        }

        /// <summary>Text, or null when the field is missing or null; any other type is refused.</summary>
        private static string? OptionalString(JsonElement element, string name, string where)
        {
            if (!TryGet(element, name, out var value) || value.ValueKind == JsonValueKind.Null)
            {
                return null;
            }

            return value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : throw new FormatException($"{where} has a {name} that is neither text nor null.");
        }

        private static bool? OptionalBool(JsonElement element, string name, string where)
        {
            if (!TryGet(element, name, out var value) || value.ValueKind == JsonValueKind.Null)
            {
                return null;
            }

            return value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => throw new FormatException($"{where} has a {name} that is neither true, false nor null."),
            };
        }

        /// <summary>Case-insensitive property lookup; two properties differing only by case are refused.</summary>
        private static bool TryGet(JsonElement element, string name, out JsonElement value)
        {
            var found = false;
            value = default;

            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    if (found)
                    {
                        throw new FormatException("An input has two fields whose names differ only by case.");
                    }

                    value = property.Value;
                    found = true;
                }
            }

            return found;
        }

        private static void CheckAccount(string account, string which)
        {
            if (string.IsNullOrWhiteSpace(account) || !account.All(char.IsAsciiLetterOrDigit))
            {
                throw new ArgumentException($"Account {which} must be plain letters or digits.");
            }
        }

        private static ReadOnlyMemory<byte> WithoutBom(byte[] bytes) =>
            bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble) ? bytes.AsMemory(Encoding.UTF8.Preamble.Length) : bytes;

        private static JsonNode? ParseNode(byte[] bytes) => JsonNode.Parse(WithoutBom(bytes).Span);

        private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

        private static byte[] ReadFile(string path, string what)
        {
            try
            {
                return File.ReadAllBytes(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // The framework message may quote the path.
                throw new InvalidOperationException($"The {what} could not be read ({exception.GetType().Name}).");
            }
        }

        private static void WriteJson(string path, JsonNode node) =>
            WriteBytes(path, Encoding.UTF8.GetBytes(node.ToJsonString(WriteOptions)));

        private static void WriteBytes(string path, byte[] bytes)
        {
            var temp = path + ".tmp";

            try
            {
                File.WriteAllBytes(temp, bytes);
                File.Move(temp, path, overwrite: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException($"An output file could not be written ({exception.GetType().Name}). Progress so far is saved.");
            }
        }
    }
}
