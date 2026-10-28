using System.IO;
using Chatcheology.Desktop.Services;

namespace Chatcheology.Desktop.Tests
{
    /// <summary>
    /// The desktop's remembered memory layer choices (CH-42 step 5): a damaged settings file of any kind
    /// reads as no choices, and a write afterwards still works.
    /// </summary>
    public sealed class LayerSettingsTests : IDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "Chatcheology.Desktop.Tests", Guid.NewGuid().ToString("N"));
        private readonly string _path;
        private readonly string _workspace;

        public LayerSettingsTests()
        {
            Directory.CreateDirectory(_folder);
            _path = Path.Combine(_folder, "desktop-layers.json");
            _workspace = Path.Combine(_folder, "workspace.db");
        }

        public void Dispose() => Directory.Delete(_folder, recursive: true);

        public static TheoryData<string> Damaged => new()
        {
            "not json at all {",
            "",
            "[1, 2, 3]",
            "42",
            "null",
            "{\"KEY\": null}",
            "{\"KEY\": 5}",
            "{\"KEY\": \"text\"}",
            "{\"KEY\": {\"Layers\": null, \"FiltersOff\": null}}",
            "{\"KEY\": {\"Layers\": \"on\", \"FiltersOff\": 3}}",
            "{\"KEY\": {\"Layers\": {\"5\": null}}}",
            "{\"KEY\": {\"Layers\": [true]}}",
            "{\"KEY\": {\"FiltersOff\": [null, 7, {}]}}",
        };

        [Theory]
        [MemberData(nameof(Damaged))]
        public void ADamagedFile_ReadsAsNoChoices_AndCanBeWrittenAgain(string content)
        {
            File.WriteAllText(_path, content.Replace("KEY", LayerSettings.KeyFor(_workspace), StringComparison.Ordinal));
            var settings = new LayerSettings(_path);

            var choices = settings.Read(_workspace);

            Assert.Empty(choices.Layers);
            Assert.Empty(choices.FiltersOff);

            settings.Write(_workspace, new LayerChoices(
                new Dictionary<long, bool> { [5] = true }, new HashSet<(long, string)> { (5, "strong") }));

            var again = settings.Read(_workspace);
            Assert.True(again.Layers[5]);
            Assert.Contains((5L, "strong"), again.FiltersOff);
        }

        [Fact]
        public void NullFilterStrings_AreSkipped_AndTheRestIsKept()
        {
            File.WriteAllText(
                _path,
                "{\"" + LayerSettings.KeyFor(_workspace) + "\": {\"Layers\": {\"5\": true, \"x\": false}, \"FiltersOff\": [null, \"5:strong\", \"bad\", \"5:\"]}}");

            var choices = new LayerSettings(_path).Read(_workspace);

            Assert.Equal(new Dictionary<long, bool> { [5] = true }, choices.Layers);
            Assert.Equal([(5L, "strong")], choices.FiltersOff);
        }

        [Fact]
        public void AnotherWorkspacesNullEntry_DoesNotStopThisOne()
        {
            File.WriteAllText(_path, "{\"someone-else\": null}");
            var settings = new LayerSettings(_path);

            settings.Write(_workspace, new LayerChoices(new Dictionary<long, bool> { [2] = false }, new HashSet<(long, string)>()));

            Assert.False(settings.Read(_workspace).Layers[2]);
            Assert.Empty(settings.Read(Path.Combine(_folder, "other.db")).Layers);
        }
    }
}
