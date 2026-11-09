using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Chatcheology.Tools.MemoryImport.Tests
{
    /// <summary>One synthetic trophy title on an account's list. Every value is invented.</summary>
    internal sealed record FakeTitle(string SetId, string Name, string? Icon = null, string Service = "trophy", string? LastUpdated = null);

    /// <summary>One synthetic trophy in a set: its id, type and, per account user, the earned time (UTC).</summary>
    internal sealed record FakeTrophy(long Id, string Type = "bronze", string Group = "default");

    /// <summary>
    /// A fake PlayStation trophy API behind an <see cref="HttpMessageHandler"/>: no network. It
    /// serves trophyTitles per user, earned trophies per (user, set) and title definitions per set,
    /// honouring limit and offset, checks the bearer token, and records every request.
    /// </summary>
    internal sealed class FakePsnServer : HttpMessageHandler
    {
        internal const string Root = "https://m.np.playstation.com/api/trophy/v1/";

        /// <summary>Titles per API user ("me" or the numeric id).</summary>
        internal Dictionary<string, List<FakeTitle>> Titles { get; } = new(StringComparer.Ordinal);

        /// <summary>Trophies per set (title definitions).</summary>
        internal Dictionary<string, List<FakeTrophy>> Sets { get; } = new(StringComparer.Ordinal);

        /// <summary>Definitions served for a set instead of <see cref="Sets"/> (to make them disagree).</summary>
        internal Dictionary<string, List<FakeTrophy>> DefinitionSets { get; } = new(StringComparer.Ordinal);

        /// <summary>Earned times per (user, set, trophy id).</summary>
        internal Dictionary<(string User, string Set, long Id), DateTime> Earned { get; } = [];

        /// <summary>The token the server accepts.</summary>
        internal string Token { get; set; } = "";

        /// <summary>Extra items claimed in totalItemCount (a lying total: incomplete paging).</summary>
        internal int TotalTitlesBonus { get; set; }

        /// <summary>Extra items claimed in totalItemCount of user trophy pages.</summary>
        internal int TotalTrophiesBonus { get; set; }

        /// <summary>Runs before the normal answer; a non-null reply is returned instead.</summary>
        internal Func<HttpRequestMessage, int, HttpResponseMessage?>? Override { get; set; }

        /// <summary>Every request: path and query, and the Authorization header value.</summary>
        internal List<(string Url, string? Authorization)> Requests { get; } = [];

        internal void AddEarned(string user, string set, long id, DateTime utc) => Earned[(user, set, id)] = utc;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(Send(request, cancellationToken));

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            Assert.StartsWith(Root, url, StringComparison.Ordinal);
            var authorization = request.Headers.Authorization?.ToString();
            Requests.Add((url[Root.Length..], authorization));

            var overridden = Override?.Invoke(request, Requests.Count);

            if (overridden is not null)
            {
                return overridden;
            }

            if (authorization != "Bearer " + Token)
            {
                return Reply(HttpStatusCode.Unauthorized, """{"error":{"message":"Invalid access token"}}""");
            }

            var uri = request.RequestUri!;
            var segments = uri.AbsolutePath["/api/trophy/v1/".Length..].Split('/').Select(Uri.UnescapeDataString).ToArray();
            var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Split('=', 2))
                .ToDictionary(part => part[0], part => Uri.UnescapeDataString(part[1]), StringComparer.Ordinal);
            var limit = query.TryGetValue("limit", out var l) ? int.Parse(l, CultureInfo.InvariantCulture) : 100;
            var offset = query.TryGetValue("offset", out var o) ? int.Parse(o, CultureInfo.InvariantCulture) : 0;

            // users/{user}/trophyTitles
            if (segments is ["users", var user, "trophyTitles"])
            {
                var all = Titles.TryGetValue(user, out var titles) ? titles : [];
                var page = new JsonArray();

                foreach (var title in all.Skip(offset).Take(limit))
                {
                    page.Add(new JsonObject
                    {
                        ["npServiceName"] = title.Service,
                        ["npCommunicationId"] = title.SetId,
                        ["trophySetVersion"] = "01.00",
                        ["trophyTitleName"] = title.Name,
                        ["trophyTitleIconUrl"] = title.Icon,
                        ["trophyTitlePlatform"] = title.Service == "trophy2" ? "PS5" : "PS4",
                        ["lastUpdatedDateTime"] = title.LastUpdated,
                    });
                }

                return Json(new JsonObject { ["trophyTitles"] = page, ["totalItemCount"] = all.Count + TotalTitlesBonus });
            }

            // users/{user}/npCommunicationIds/{set}/trophyGroups/all/trophies
            if (segments is ["users", var who, "npCommunicationIds", var set, "trophyGroups", "all", "trophies"])
            {
                if (!Sets.TryGetValue(set, out var trophies))
                {
                    return Reply(HttpStatusCode.NotFound, "{}");
                }

                var page = new JsonArray();

                foreach (var trophy in trophies.Skip(offset).Take(limit))
                {
                    var earned = Earned.TryGetValue((who, set, trophy.Id), out var time);
                    page.Add(new JsonObject
                    {
                        ["trophyId"] = trophy.Id,
                        ["trophyHidden"] = false,
                        ["earned"] = earned,
                        ["earnedDateTime"] = earned ? time.ToString("yyyy'-'MM'-'dd'T'HH':'mm':'ss'Z'", CultureInfo.InvariantCulture) : null,
                        ["trophyType"] = trophy.Type,
                        ["trophyRare"] = 3,
                        ["trophyEarnedRate"] = "12.5",
                    });
                }

                return Json(new JsonObject { ["trophies"] = page, ["totalItemCount"] = trophies.Count + TotalTrophiesBonus });
            }

            // npCommunicationIds/{set}/trophyGroups/all/trophies (definitions, no user)
            if (segments is ["npCommunicationIds", var defSet, "trophyGroups", "all", "trophies"])
            {
                if (!DefinitionSets.TryGetValue(defSet, out var trophies) && !Sets.TryGetValue(defSet, out trophies))
                {
                    return Reply(HttpStatusCode.NotFound, "{}");
                }

                var page = new JsonArray();

                foreach (var trophy in trophies.Skip(offset).Take(limit))
                {
                    page.Add(new JsonObject
                    {
                        ["trophyId"] = trophy.Id,
                        ["trophyHidden"] = trophy.Id == 3,
                        ["trophyType"] = trophy.Type,
                        ["trophyName"] = trophy.Id == 3 ? null : $"Invented Trophy {trophy.Id}",
                        ["trophyDetail"] = "Invented detail.",
                        ["trophyIconUrl"] = $"https://image.api.playstation.com/zz/{defSet}-{trophy.Id}.png",
                        ["trophyGroupId"] = trophy.Group,
                    });
                }

                return Json(new JsonObject
                {
                    ["trophySetVersion"] = "01.02",
                    ["hasTrophyGroups"] = false,
                    ["trophies"] = page,
                    ["totalItemCount"] = trophies.Count,
                });
            }

            return Reply(HttpStatusCode.NotFound, "{}");
        }

        internal static HttpResponseMessage Reply(HttpStatusCode status, string body) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

        private static HttpResponseMessage Json(JsonNode node) => Reply(HttpStatusCode.OK, node.ToJsonString());
    }

    /// <summary>A fake prompt: tokens in order, and the other account's id.</summary>
    internal sealed class FakePrompt(string partnerId, params string[] tokens) : IPsnSecretPrompt
    {
        private readonly Queue<string> _tokens = new(tokens);

        internal int TokensAsked { get; private set; }

        internal int PartnerAsked { get; private set; }

        public string ReadToken(bool renewal)
        {
            TokensAsked++;
            return _tokens.Count > 0 ? _tokens.Dequeue() : "";
        }

        public string ReadPartnerAccountId()
        {
            PartnerAsked++;
            return partnerId;
        }
    }
}
