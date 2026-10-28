using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Chatcheology.Desktop.Services
{
    /// <summary>
    /// The memory layer switches and filters chosen for one workspace (CH-42 step 5).
    /// </summary>
    /// <param name="Layers">Each collection switched by the operator, on or off; any other keeps its own default.</param>
    /// <param name="FiltersOff">The filters turned off, as (collection, filter word); every other filter is on.</param>
    internal sealed record LayerChoices(
        IReadOnlyDictionary<long, bool> Layers, IReadOnlySet<(long CollectionID, string Filter)> FiltersOff)
    {
        /// <summary>Nothing chosen: every layer at its default, every filter on.</summary>
        internal static LayerChoices None { get; } = new(new Dictionary<long, bool>(), new HashSet<(long, string)>());
    }

    /// <summary>
    /// Where the desktop remembers memory layer choices per workspace (CH-42 step 5): one small file in
    /// the operator's local application data, never in the workspace or the memories file.
    /// </summary>
    /// <remarks>
    /// <para><strong>No path is written.</strong> A workspace is keyed by the SHA-256 of its full path, so
    /// the file names no folder and no file. It holds collection numbers, on/off and filter words only.</para>
    /// <para><strong>Never in the way.</strong> A missing, unreadable or damaged file reads as no choices,
    /// and a failed write is ignored: the layers then simply start at their defaults next time.</para>
    /// </remarks>
    internal sealed class LayerSettings
    {
        private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

        private readonly string _path;
        private readonly object _lock = new();

        /// <param name="path">The settings file; the default one in local application data when null.</param>
        internal LayerSettings(string? path = null)
        {
            _path = path ?? DefaultPath;
        }

        /// <summary><c>%LOCALAPPDATA%\Chatcheology\desktop-layers.json</c>.</summary>
        internal static string DefaultPath =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Chatcheology",
                "desktop-layers.json");

        /// <summary>The settings file.</summary>
        internal string FilePath => _path;

        /// <summary>The key of a workspace: the SHA-256 of its full path, ignoring case.</summary>
        internal static string KeyFor(string workspacePath) =>
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
                Path.GetFullPath(workspacePath).ToUpperInvariant())));

        /// <summary>The choices remembered for a workspace, or none.</summary>
        internal LayerChoices Read(string workspacePath)
        {
            lock (_lock)
            {
                try
                {
                    return ReadAll().TryGetValue(KeyFor(workspacePath), out var stored) && stored is not null
                        ? ToChoices(stored)
                        : LayerChoices.None;
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    // Whatever is wrong with the file, the layers start at their defaults.
                    return LayerChoices.None;
                }
            }
        }

        /// <summary>Remembers the choices for a workspace. A failure is ignored.</summary>
        internal void Write(string workspacePath, LayerChoices choices)
        {
            ArgumentNullException.ThrowIfNull(choices);

            lock (_lock)
            {
                Dictionary<string, StoredChoices?> all;

                try
                {
                    all = ReadAll();
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    all = [];
                }

                all[KeyFor(workspacePath)] = new StoredChoices
                {
                    Layers = choices.Layers
                        .OrderBy(p => p.Key)
                        .ToDictionary(p => p.Key.ToString(CultureInfo.InvariantCulture), p => p.Value),
                    FiltersOff = choices.FiltersOff
                        .OrderBy(f => f.CollectionID).ThenBy(f => f.Filter, StringComparer.Ordinal)
                        .Select(f => (string?)(f.CollectionID.ToString(CultureInfo.InvariantCulture) + ":" + f.Filter))
                        .ToList(),
                };

                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

                    var temporary = _path + ".tmp";
                    File.WriteAllText(temporary, JsonSerializer.Serialize(all, Options), new UTF8Encoding(false));
                    File.Move(temporary, _path, overwrite: true);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // Not remembered; the layers start at their defaults next time.
                }
            }
        }

        /// <summary>
        /// Drops a deleted collection's switch and filters from a workspace's choices (CH-46). Nothing else
        /// changes; a failure is ignored (the choices of a collection that is not there are never used).
        /// </summary>
        internal void Forget(string workspacePath, long collectionID)
        {
            lock (_lock)
            {
                var choices = Read(workspacePath);

                if (!choices.Layers.ContainsKey(collectionID) && choices.FiltersOff.All(f => f.CollectionID != collectionID))
                {
                    return;
                }

                Write(
                    workspacePath,
                    new LayerChoices(
                        choices.Layers.Where(p => p.Key != collectionID).ToDictionary(p => p.Key, p => p.Value),
                        choices.FiltersOff.Where(f => f.CollectionID != collectionID).ToHashSet()));
            }
        }

        private Dictionary<string, StoredChoices?> ReadAll()
        {
            try
            {
                if (!File.Exists(_path))
                {
                    return [];
                }

                return JsonSerializer.Deserialize<Dictionary<string, StoredChoices?>>(File.ReadAllText(_path)) ?? [];
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException
                                                  or NotSupportedException or ArgumentException)
            {
                return [];
            }
        }

        private static LayerChoices ToChoices(StoredChoices stored)
        {
            var layers = new Dictionary<long, bool>();

            foreach (var (key, value) in stored.Layers ?? [])
            {
                if (key is not null && long.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
                {
                    layers[id] = value;
                }
            }

            var off = new HashSet<(long, string)>();

            foreach (var text in stored.FiltersOff ?? [])
            {
                if (text is null)
                {
                    continue;
                }

                var colon = text.IndexOf(':', StringComparison.Ordinal);

                if (colon > 0
                    && long.TryParse(text.AsSpan(0, colon), NumberStyles.None, CultureInfo.InvariantCulture, out var id)
                    && colon < text.Length - 1)
                {
                    off.Add((id, text[(colon + 1)..]));
                }
            }

            return new LayerChoices(layers, off);
        }

        private sealed class StoredChoices
        {
            public Dictionary<string, bool>? Layers { get; set; }

            public List<string?>? FiltersOff { get; set; }
        }
    }
}
