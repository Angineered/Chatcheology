using System.Text;
using System.Text.Json.Nodes;

namespace Chatcheology.Tests.Memories
{
    /// <summary>One synthetic trophy definition. Every value is invented.</summary>
    internal sealed record SyntheticDefinition(
        int Id,
        string Type,
        string? Name,
        string? Detail = "Invented detail.",
        bool? Hidden = false,
        string? Group = "default",
        string? Icon = null);

    /// <summary>A synthetic set of definitions.</summary>
    internal sealed record SyntheticDefinitionSet(string SetId, List<SyntheticDefinition> Trophies, string ServiceName = "trophy");

    /// <summary>
    /// Builds a synthetic trophy definitions file in the exact source shape
    /// (<c>chatcheology-psn-trophy-definitions-v1</c>). Set IDs match <see cref="SyntheticPsnFile"/>.
    /// </summary>
    internal sealed class SyntheticTrophyDefinitions
    {
        internal const string QuestIconOne = "https://image.api.playstation.com/zz/questtrophy1.png";
        internal const string QuestIconTwo = "https://image.api.playstation.com/zz/questtrophy2.png";
        internal const string QuestIconThree = "https://image.api.playstation.com/zz/questtrophy3.png";
        internal const string RacerIconFive = "https://psnobj.prod.dl.playstation.net/zz/racertrophy5.png";

        internal string Format { get; set; } = "chatcheology-psn-trophy-definitions-v1";

        internal List<SyntheticDefinitionSet> Sets { get; } = [];

        /// <summary>Called on the root just before serialising, to break the file on purpose.</summary>
        internal Action<JsonObject>? Tamper { get; set; }

        /// <summary>
        /// Quest set: trophies 1 (bronze), 2 (silver) used by both accounts' items, and 3 (gold,
        /// hidden, no name) used by no item. Racer local set: trophy 5 (bronze). The Racer other set
        /// (item 6) has no definitions. With <see cref="SyntheticPsnFile.Standard"/>: 6 items, 5 with a
        /// definition, 1 without.
        /// </summary>
        internal static SyntheticTrophyDefinitions Standard()
        {
            var file = new SyntheticTrophyDefinitions();
            file.Sets.Add(new SyntheticDefinitionSet(SyntheticPsnFile.Quest.LocalSet,
            [
                new SyntheticDefinition(1, "bronze", "Invented First Step", Icon: QuestIconOne),
                new SyntheticDefinition(2, "silver", "Invented Second Wind", Icon: QuestIconTwo),
                new SyntheticDefinition(3, "gold", null, null, true, Icon: QuestIconThree),
            ]));
            file.Sets.Add(new SyntheticDefinitionSet(SyntheticPsnFile.Racer.LocalSet,
            [
                new SyntheticDefinition(5, "bronze", "Invented Fast Lap", Icon: RacerIconFive),
            ], "trophy2"));
            return file;
        }

        internal string ToJson()
        {
            var sets = new JsonArray();

            foreach (var set in Sets)
            {
                var trophies = new JsonArray();

                foreach (var t in set.Trophies)
                {
                    trophies.Add(new JsonObject
                    {
                        ["trophyId"] = t.Id,
                        ["trophyType"] = t.Type,
                        ["trophyName"] = t.Name,
                        ["trophyDetail"] = t.Detail,
                        ["trophyIconUrl"] = t.Icon,
                        ["trophyGroupId"] = t.Group,
                        ["trophyHidden"] = t.Hidden,
                    });
                }

                sets.Add(new JsonObject
                {
                    ["npCommunicationId"] = set.SetId,
                    ["npServiceName"] = set.ServiceName,
                    ["trophySetVersion"] = "01.00",
                    ["trophies"] = trophies,
                });
            }

            var root = new JsonObject
            {
                ["format"] = Format,
                ["fetchedUtc"] = "2026-10-27T12:00:00Z",
                ["sets"] = sets,
            };

            Tamper?.Invoke(root);
            return root.ToJsonString();
        }

        internal byte[] ToBytes() => Encoding.UTF8.GetBytes(ToJson());

        internal string WriteTo(string folder, string fileName = "trophy-definitions.json")
        {
            var path = Path.Combine(folder, fileName);
            File.WriteAllText(path, ToJson());
            return path;
        }
    }
}
