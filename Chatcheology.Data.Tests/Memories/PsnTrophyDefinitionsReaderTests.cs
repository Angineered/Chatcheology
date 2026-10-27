using System.Text.Json.Nodes;
using Chatcheology.Data.Memories.Psn;
using Chatcheology.Tests.Memories;

namespace Chatcheology.Data.Tests.Memories
{
    public class PsnTrophyDefinitionsReaderTests
    {
        private static PsnTrophyDefinitions Parse(SyntheticTrophyDefinitions file) => PsnTrophyDefinitionsReader.Parse(file.ToBytes());

        private static FormatException Refused(Action<JsonObject> tamper)
        {
            var file = SyntheticTrophyDefinitions.Standard();
            file.Tamper = tamper;
            return Assert.Throws<FormatException>(() => Parse(file));
        }

        private static JsonObject Trophy(JsonObject root, int set, int trophy) =>
            root["sets"]![set]!["trophies"]![trophy]!.AsObject();

        [Fact]
        public void Standard_ReadsEveryDefinition_AsWritten()
        {
            var result = Parse(SyntheticTrophyDefinitions.Standard());

            Assert.Equal(2, result.SetCount);
            Assert.Equal(4, result.Definitions.Count);
            Assert.Equal(
                new PsnTrophyDefinition("NPWR99999_00", "1", "bronze", "Invented First Step", "Invented detail.", false, "default",
                    SyntheticTrophyDefinitions.QuestIconOne),
                result.Definitions[0]);
            Assert.Equal(
                new PsnTrophyDefinition("NPWR99999_00", "3", "gold", null, null, true, "default", SyntheticTrophyDefinitions.QuestIconThree),
                result.Definitions[2]);
        }

        [Fact]
        public void Text_IsKeptAsWritten_IncludingEmptyAndSpaces()
        {
            var file = SyntheticTrophyDefinitions.Standard();
            file.Tamper = root =>
            {
                Trophy(root, 0, 0)["trophyName"] = "  Spaced name ";
                Trophy(root, 0, 1)["trophyDetail"] = string.Empty;
                Trophy(root, 0, 1)["trophyHidden"] = null;
            };

            var result = Parse(file);

            Assert.Equal("  Spaced name ", result.Definitions[0].Name);
            Assert.Equal(string.Empty, result.Definitions[1].Detail);
            Assert.Null(result.Definitions[1].Hidden);
        }

        [Fact]
        public void FieldNames_MatchIgnoringCase()
        {
            var file = SyntheticTrophyDefinitions.Standard();
            file.Tamper = root =>
            {
                var trophy = Trophy(root, 0, 0);
                var name = trophy["trophyName"]!.GetValue<string>();
                trophy.Remove("trophyName");
                trophy["TROPHYNAME"] = name;
            };

            Assert.Equal("Invented First Step", Parse(file).Definitions[0].Name);
        }

        [Fact]
        public void OtherFormat_IsRefused()
        {
            var file = SyntheticTrophyDefinitions.Standard();
            file.Format = "chatcheology-psn-trophy-definitions-v2";

            var exception = Assert.Throws<FormatException>(() => Parse(file));

            Assert.Contains("format", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void NotJson_IsRefused() =>
            Assert.Throws<FormatException>(() => PsnTrophyDefinitionsReader.Parse("{not json"u8.ToArray()));

        [Theory]
        [InlineData("fetchedUtc")]
        [InlineData("sets")]
        public void MissingRootField_Stops(string field) => Refused(root => root.Remove(field));

        [Theory]
        [InlineData("npCommunicationId", "Set at index 1")]
        [InlineData("npServiceName", "Set at index 1")]
        [InlineData("trophySetVersion", "Set at index 1")]
        [InlineData("trophies", "Set at index 1")]
        public void MissingSetField_StopsWithTheSetIndex(string field, string where)
        {
            var exception = Refused(root => root["sets"]![1]!.AsObject().Remove(field));

            Assert.StartsWith(where, exception.Message, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData("trophyId")]
        [InlineData("trophyType")]
        [InlineData("trophyName")]
        [InlineData("trophyDetail")]
        [InlineData("trophyIconUrl")]
        [InlineData("trophyGroupId")]
        [InlineData("trophyHidden")]
        public void MissingTrophyField_StopsWithTheSetAndTrophyIndex(string field)
        {
            var exception = Refused(root => Trophy(root, 0, 2).Remove(field));

            Assert.StartsWith("Set at index 0, trophy at index 2", exception.Message, StringComparison.Ordinal);
        }

        public static TheoryData<string, JsonNode?> WrongTypes => new()
        {
            { "trophyId", JsonValue.Create("1") },
            { "trophyId", JsonValue.Create(1.5) },
            { "trophyId", JsonValue.Create(-1) },
            { "trophyId", null },
            { "trophyType", JsonValue.Create(3) },
            { "trophyType", JsonValue.Create(" ") },
            { "trophyType", null },
            { "trophyName", JsonValue.Create(7) },
            { "trophyDetail", JsonValue.Create(true) },
            { "trophyIconUrl", new JsonArray() },
            { "trophyGroupId", JsonValue.Create(0) },
            { "trophyHidden", JsonValue.Create("false") },
            { "trophyHidden", JsonValue.Create(0) },
        };

        [Theory]
        [MemberData(nameof(WrongTypes))]
        public void WrongTrophyFieldType_StopsWithTheSetAndTrophyIndex(string field, JsonNode? value)
        {
            var exception = Refused(root => Trophy(root, 1, 0)[field] = value?.DeepClone());

            Assert.StartsWith("Set at index 1, trophy at index 0", exception.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("NPWR", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void SetIdNotText_Stops()
        {
            var exception = Refused(root => root["sets"]![0]!["npCommunicationId"] = 5);

            Assert.StartsWith("Set at index 0", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void DuplicateTrophyInASet_StopsWithTheIndex()
        {
            var exception = Refused(root => Trophy(root, 0, 2)["trophyId"] = 1);

            Assert.StartsWith("Set at index 0, trophy at index 2", exception.Message, StringComparison.Ordinal);
            Assert.Contains("repeats", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void RepeatedSet_StopsWithTheIndex()
        {
            var exception = Refused(root => root["sets"]![1]!["npCommunicationId"] = "NPWR99999_00");

            Assert.StartsWith("Set at index 1", exception.Message, StringComparison.Ordinal);
            Assert.Contains("repeats", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void TwoFieldsDifferingOnlyByCase_Stop() =>
            Refused(root => Trophy(root, 0, 0)["TrophyName"] = "Invented copy");
    }
}
