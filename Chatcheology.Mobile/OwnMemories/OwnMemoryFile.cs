using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Chatcheology.Archive;
using Chatcheology.Mobile.Presentation;

namespace Chatcheology.Mobile.OwnMemories
{
    /// <summary>Why a memories file was not saved or not loaded.</summary>
    public enum OwnMemoryFileProblem
    {
        /// <summary>Save: there are no memories to save.</summary>
        NoMemories,

        /// <summary>Past a limit (<see cref="OwnMemoryFileLimits"/>).</summary>
        TooLarge,

        /// <summary>Load: not a memories file (no <c>memories.json</c>, or not its format marker).</summary>
        NotAMemoriesFile,

        /// <summary>Load: written by a newer version of the format.</summary>
        NewerVersion,

        /// <summary>Load: damaged or changed (zip structure, CRC, a photo whose bytes are not its name, JSON).</summary>
        Damaged,

        /// <summary>Load: a zip feature this app does not read (encryption, zip64, a data descriptor, another method).</summary>
        NotSupported,
    }

    /// <summary>A memories file was refused; nothing was changed.</summary>
    public sealed class OwnMemoryFileException : Exception
    {
        /// <summary>Creates the refusal.</summary>
        public OwnMemoryFileException(OwnMemoryFileProblem problem)
            : base("The memories file was refused: " + problem + ".")
        {
            Problem = problem;
        }

        /// <summary>Why.</summary>
        public OwnMemoryFileProblem Problem { get; }
    }

    /// <summary>
    /// The limits of a memories file, the same for saving and loading so a saved file always loads (CH-54 step 3).
    /// </summary>
    /// <remarks>
    /// <para>Photo: 20 MB (D-29), as when a photo is added. JSON: 10 MB, far above 10,000 memories at the form's longest
    /// fields with 10 links each. Memories: 10,000. Entries: 20,001 (the JSON and up to 20,000 photos).</para>
    /// <para>Total unpacked: 2 GiB. Photos do not shrink in a zip, so this is about the size of the file: some 400 phone
    /// photos of 5 MB. It stays below 4 GB, so no zip64 is ever needed, and it bounds what a crafted file can make the app
    /// write into its own folder while loading (deleted again after). A larger collection is refused when saving, with a
    /// plain message, rather than written as a file that would not load.</para>
    /// </remarks>
    public sealed record OwnMemoryFileLimits
    {
        /// <summary>The limits in use.</summary>
        public static OwnMemoryFileLimits Default { get; } = new();

        /// <summary>The largest photo (D-29).</summary>
        public long MaxPhotoBytes { get; init; } = OwnPhotoIntake.MaxPhotoBytes;

        /// <summary>The largest <c>memories.json</c>.</summary>
        public long MaxJsonBytes { get; init; } = 10 * 1024 * 1024;

        /// <summary>The most bytes all entries unpack to together.</summary>
        public long MaxTotalBytes { get; init; } = 2L * 1024 * 1024 * 1024;

        /// <summary>The most entries.</summary>
        public int MaxEntries { get; init; } = 20_001;

        /// <summary>The most memories.</summary>
        public int MaxMemories { get; init; } = 10_000;

        /// <summary>The most bytes read from the file: the unpacked total plus room for headers (skipped entries count here).</summary>
        public long MaxFileBytes => MaxTotalBytes + MaxJsonBytes + (64L * 1024 * 1024);
    }

    /// <summary>What a save wrote.</summary>
    /// <param name="Memories">Memories saved.</param>
    /// <param name="Photos">Photo files saved.</param>
    /// <param name="PhotosLeftOut">Photos left out: missing, unreadable, or failing the location check (should be none).</param>
    public sealed record OwnMemorySaveResult(int Memories, int Photos, int PhotosLeftOut);

    /// <summary>What a load did. Nothing already on the phone is ever changed.</summary>
    /// <param name="Added">Memories added.</param>
    /// <param name="AlreadyHere">Memories already here, the same.</param>
    /// <param name="AlreadyHereDifferent">Memories already here but different: the phone's copy was kept.</param>
    /// <param name="Refused">Memories that did not pass the checks (form rules, links, identity, a photo missing or refused).</param>
    /// <param name="Ignored">Entries of the zip that are not part of a memories file.</param>
    /// <param name="PhotosAdded">Photo files added to the phone.</param>
    public sealed record OwnMemoryLoadResult(int Added, int AlreadyHere, int AlreadyHereDifferent, int Refused, int Ignored, int PhotosAdded);

    /// <summary>
    /// Save to a file / Load from a file of the phone owner's own memories (CH-54 step 3). Pure .NET: Android gives the
    /// streams of the system Save and Open dialogs.
    /// </summary>
    /// <remarks>
    /// <para><strong>Format.</strong> A zip (<see cref="OwnMemoryZip"/>) with exactly <c>memories.json</c> and
    /// <c>photos/&lt;64 lower-case hex&gt;.&lt;jpg|png|webp|gif|bmp&gt;</c>, the stored (location-free, D-37) photo bytes
    /// named by their SHA-256. The JSON: <c>format</c> = <see cref="FormatName"/>, <c>version</c> = <see cref="FormatVersion"/>,
    /// <c>created</c> (UTC, seconds), <c>memories</c> (each: <c>id</c>, <c>start</c>, <c>end</c>, <c>precision</c>, <c>title</c>,
    /// <c>summary</c>, <c>everyYear</c>, <c>photos</c> [<c>file</c>, <c>caption</c>] in order, <c>links</c> [<c>url</c>,
    /// <c>label</c>] in order) and <c>muted</c> (the memory ids whose Notify is off). Nothing else: no source, row ids, paths,
    /// device, layer switch, reminders or posted day.</para>
    /// <para><strong>Load.</strong> Everything is untrusted. Only the names above are used, never as a path; others are
    /// skipped and counted. Limits are counted while reading (<see cref="OwnMemoryFileLimits"/>). Each photo's SHA-256 must
    /// be its name, then it goes through the same pure rebuild and location re-check as a picked photo and is kept under
    /// the hash of the rebuilt bytes. Each memory passes the form rules and <see cref="ExternalLinkRules"/>; one that does
    /// not, or that names a photo not in the file or refused, is not loaded (counted). Photos are staged in
    /// <c>files/own/.incoming-load-&lt;guid&gt;/</c> (deleted on every path, swept if left), then one transaction adds the
    /// new memories. A refusal of the file itself changes nothing. A memory whose id is already here is never
    /// overwritten. Mutes in the file apply only to memories this load adds.</para>
    /// </remarks>
    public static partial class OwnMemoryFile
    {
        /// <summary>The format marker.</summary>
        public const string FormatName = "chatcheology-own-memories";

        /// <summary>The version this code writes; a higher one is refused.</summary>
        public const int FormatVersion = 1;

        /// <summary>The JSON entry's name.</summary>
        public const string JsonName = "memories.json";

        /// <summary>The photos folder inside the zip.</summary>
        public const string PhotoPrefix = "photos/";

        /// <summary>The MIME type for the system dialogs.</summary>
        public const string MimeType = "application/zip";

        private const string DateFormat = "yyyy'-'MM'-'dd";

        /// <summary>The name the Save dialog suggests: <c>my-memories-YYYY-MM-DD.zip</c>.</summary>
        public static string SuggestedName(DateOnly today) =>
            "my-memories-" + today.ToString(DateFormat, CultureInfo.InvariantCulture) + ".zip";

        /// <summary>Whether <paramref name="name"/> is a photo entry's name: <c>photos/</c> and a stored photo's name.</summary>
        public static bool IsPhotoEntryName(string? name) =>
            name is not null && name.StartsWith(PhotoPrefix, StringComparison.Ordinal) && OwnMemoryRules.IsPhotoFileName(name[PhotoPrefix.Length..]);

        // ---------------------------------------------------------------- Save

        /// <summary>
        /// Writes every own memory, its photos and the Notify mutes to <paramref name="target"/>, front to back. Each photo is
        /// read from the store and checked again (its name is its hash, its format is its extension, the CH-59 location
        /// check finds nothing); one that fails is left out of the file and of its memory, and counted.
        /// </summary>
        /// <exception cref="OwnMemoryFileException"><see cref="OwnMemoryFileProblem.NoMemories"/>, or past a limit; nothing was written.</exception>
        public static OwnMemorySaveResult Save(OwnMemoryStore store, Stream target, DateTime nowUtc, OwnMemoryFileLimits? limits = null)
        {
            ArgumentNullException.ThrowIfNull(store);
            ArgumentNullException.ThrowIfNull(target);
            limits ??= OwnMemoryFileLimits.Default;

            var memories = store.List();
            var muted = store.Settings.Muted();

            if (memories.Count == 0)
            {
                throw new OwnMemoryFileException(OwnMemoryFileProblem.NoMemories);
            }

            var files = memories.SelectMany(m => m.Photos).Select(p => p.FileName).Distinct(StringComparer.Ordinal).ToList();

            if (memories.Count > limits.MaxMemories || files.Count + 1 > limits.MaxEntries
                || files.Sum(name => SizeOf(Path.Combine(store.PhotosFolder, name))) + limits.MaxJsonBytes > limits.MaxTotalBytes)
            {
                throw new OwnMemoryFileException(OwnMemoryFileProblem.TooLarge);
            }

            var writer = new OwnMemoryZip.Writer(target);
            var saved = new HashSet<string>(StringComparer.Ordinal);

            foreach (var name in files)
            {
                if (ReadForSave(store.PhotosFolder, name, limits) is { } bytes)
                {
                    writer.AddStored(PhotoPrefix + name, bytes);
                    saved.Add(name);
                }
            }

            var json = Json(memories, muted, saved, nowUtc);

            if (json.LongLength > limits.MaxJsonBytes)
            {
                throw new OwnMemoryFileException(OwnMemoryFileProblem.TooLarge);
            }

            writer.AddDeflated(JsonName, json);
            writer.Finish();

            return new OwnMemorySaveResult(memories.Count, saved.Count, files.Count - saved.Count);
        }

        private static long SizeOf(string path)
        {
            try
            {
                return File.Exists(path) ? new FileInfo(path).Length : 0;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return 0;
            }
        }

        /// <summary>A stored photo's bytes when they still pass every check, or null (left out).</summary>
        private static byte[]? ReadForSave(string folder, string name, OwnMemoryFileLimits limits)
        {
            byte[] bytes;

            try
            {
                var path = Path.Combine(folder, name);

                if (!File.Exists(path) || new FileInfo(path).Length > limits.MaxPhotoBytes)
                {
                    return null;
                }

                bytes = File.ReadAllBytes(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return null;
            }

            var format = PhotoShare.Sniff(bytes.AsSpan(0, Math.Min(bytes.Length, PhotoShare.HeaderLength)));

            return OwnPhotoIntake.ExtensionFor(format) is { } extension
                && OwnPhotoIntake.StoredName(bytes, format) == name
                && name.EndsWith("." + extension, StringComparison.Ordinal)
                && !OwnPhotoIntake.MayStillCarryLocation(bytes, format)
                    ? bytes
                    : null;
        }

        private static byte[] Json(IReadOnlyList<OwnMemory> memories, IReadOnlySet<Guid> muted, IReadOnlySet<string> saved, DateTime nowUtc)
        {
            using var buffer = new MemoryStream();

            using (var json = new Utf8JsonWriter(buffer))
            {
                json.WriteStartObject();
                json.WriteString("format", FormatName);
                json.WriteNumber("version", FormatVersion);
                json.WriteString("created", nowUtc.ToUniversalTime().ToString("yyyy'-'MM'-'dd'T'HH':'mm':'ss'Z'", CultureInfo.InvariantCulture));
                json.WriteStartArray("memories");

                foreach (var memory in memories)
                {
                    json.WriteStartObject();
                    json.WriteString("id", OwnMemoryStore.GuidText(memory.Id));
                    json.WriteString("start", memory.Start.ToString(DateFormat, CultureInfo.InvariantCulture));

                    if (memory.End is { } end)
                    {
                        json.WriteString("end", end.ToString(DateFormat, CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        json.WriteNull("end");
                    }

                    json.WriteString("precision", memory.Precision.ToString());
                    json.WriteString("title", memory.Title);
                    WriteOptional(json, "summary", memory.Summary);
                    json.WriteBoolean("everyYear", memory.EveryYear);
                    json.WriteStartArray("photos");

                    foreach (var photo in memory.Photos.Where(p => saved.Contains(p.FileName)))
                    {
                        json.WriteStartObject();
                        json.WriteString("file", photo.FileName);
                        WriteOptional(json, "caption", photo.Caption);
                        json.WriteEndObject();
                    }

                    json.WriteEndArray();
                    json.WriteStartArray("links");

                    foreach (var link in memory.Links)
                    {
                        json.WriteStartObject();
                        json.WriteString("url", link.Url);
                        WriteOptional(json, "label", link.Label);
                        json.WriteEndObject();
                    }

                    json.WriteEndArray();
                    json.WriteEndObject();
                }

                json.WriteEndArray();
                json.WriteStartArray("muted");

                foreach (var memory in memories.Where(m => muted.Contains(m.Id)))
                {
                    json.WriteStringValue(OwnMemoryStore.GuidText(memory.Id));
                }

                json.WriteEndArray();
                json.WriteEndObject();
            }

            return buffer.ToArray();
        }

        private static void WriteOptional(Utf8JsonWriter json, string name, string? value)
        {
            if (value is null)
            {
                json.WriteNull(name);
            }
            else
            {
                json.WriteString(name, value);
            }
        }

        // ---------------------------------------------------------------- Load

        /// <summary>
        /// Reads a memories file from <paramref name="source"/> to its end and adds the memories that are not here yet, all
        /// in one transaction. See the class remarks for every check.
        /// </summary>
        /// <exception cref="OwnMemoryFileException">The file itself is refused; nothing was changed.</exception>
        /// <exception cref="IOException">Reading the file or writing the phone's storage failed; nothing was changed.</exception>
        public static OwnMemoryLoadResult Load(OwnMemoryStore store, Stream source, OwnMemoryFileLimits? limits = null)
        {
            ArgumentNullException.ThrowIfNull(store);
            ArgumentNullException.ThrowIfNull(source);
            limits ??= OwnMemoryFileLimits.Default;

            var stage = store.BeginLoadStage();

            try
            {
                byte[]? json = null;

                // The file's photo name -> the staged (rebuilt) name, or null when the photo was refused.
                var photos = new Dictionary<string, string?>(StringComparer.Ordinal);

                var ignored = OwnMemoryZip.Read(
                    source,
                    limits,
                    name => name == JsonName ? limits.MaxJsonBytes : IsPhotoEntryName(name) ? limits.MaxPhotoBytes : null,
                    name => name == JsonName,
                    (name, data) =>
                    {
                        if (name == JsonName)
                        {
                            json = data;
                        }
                        else
                        {
                            var file = name[PhotoPrefix.Length..];
                            photos[file] = Stage(stage, file, data, limits);
                        }
                    });

                if (json is null)
                {
                    throw new OwnMemoryFileException(OwnMemoryFileProblem.NotAMemoriesFile);
                }

                (IReadOnlyList<LoadCandidate> Candidates, IReadOnlySet<Guid> Muted, int Refused) parsed;

                try
                {
                    parsed = Parse(json, photos, limits);
                }
                catch (Exception exception) when (exception is not OwnMemoryFileException)
                {
                    // Step 3 review M1: anything the JSON reader throws on text it accepted (a lone surrogate escape makes
                    // GetString throw InvalidOperationException) is a damaged file, never a silent failure.
                    throw new OwnMemoryFileException(OwnMemoryFileProblem.Damaged);
                }

                var (candidates, muted, refused) = parsed;
                var commit = store.CommitLoad(candidates, muted, stage);

                return commit with { Refused = refused, Ignored = ignored };
            }
            finally
            {
                store.EndLoadStage(stage);
            }
        }

        /// <summary>
        /// Checks one photo of the file and writes its rebuilt bytes into the stage: its name must be the SHA-256 of its
        /// bytes (otherwise the file is damaged); its format must be its extension and it must pass the same pure rebuild
        /// and location re-check as a picked photo (otherwise it is refused: null, and the memories naming it are not loaded).
        /// </summary>
        private static string? Stage(string stage, string file, byte[] data, OwnMemoryFileLimits limits)
        {
            if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(data)), file[..64], StringComparison.Ordinal))
            {
                throw new OwnMemoryFileException(OwnMemoryFileProblem.Damaged);
            }

            if (OwnPhotoIntake.CleanWithoutPlatform(data, out var format, out _, limits.MaxPhotoBytes) is not { } rebuilt
                || !file.EndsWith("." + OwnPhotoIntake.ExtensionFor(format), StringComparison.Ordinal))
            {
                return null;
            }

            var name = OwnPhotoIntake.StoredName(rebuilt, format);
            var path = Path.Combine(stage, name);

            if (!File.Exists(path))
            {
                File.WriteAllBytes(path, rebuilt);
            }

            return name;
        }

        /// <summary>One memory of a file that passed every check, with its photos named as staged.</summary>
        internal sealed record LoadCandidate(Guid Id, OwnMemoryDraft Draft);

        private static (IReadOnlyList<LoadCandidate> Candidates, IReadOnlySet<Guid> Muted, int Refused) Parse(
            byte[] json, IReadOnlyDictionary<string, string?> photos, OwnMemoryFileLimits limits)
        {
            JsonDocument document;

            try
            {
                document = JsonDocument.Parse(json, new JsonDocumentOptions
                {
                    MaxDepth = 6,
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    AllowDuplicateProperties = false,
                });
            }
            catch (JsonException)
            {
                throw new OwnMemoryFileException(OwnMemoryFileProblem.Damaged);
            }

            using (document)
            {
                var root = document.RootElement;

                if (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty("format", out var format)
                    || format.ValueKind != JsonValueKind.String
                    || format.GetString() != FormatName
                    || !root.TryGetProperty("version", out var versionElement)
                    || versionElement.ValueKind != JsonValueKind.Number)
                {
                    throw new OwnMemoryFileException(OwnMemoryFileProblem.NotAMemoriesFile);
                }

                // Step 3 review N2: any number above this version (however large, or written as 2.0) is a newer version;
                // only the plain integer 1 is this one; anything else (0, 1.0, 1.5, a string) is not a memories file.
                if (versionElement.TryGetDouble(out var number)
                        ? number > FormatVersion
                        : !versionElement.GetRawText().StartsWith('-'))
                {
                    throw new OwnMemoryFileException(OwnMemoryFileProblem.NewerVersion);
                }

                if (versionElement.GetRawText() != FormatVersion.ToString(CultureInfo.InvariantCulture))
                {
                    throw new OwnMemoryFileException(OwnMemoryFileProblem.NotAMemoriesFile);
                }

                if (!root.TryGetProperty("memories", out var list) || list.ValueKind != JsonValueKind.Array)
                {
                    throw new OwnMemoryFileException(OwnMemoryFileProblem.Damaged);
                }

                if (list.GetArrayLength() > limits.MaxMemories)
                {
                    throw new OwnMemoryFileException(OwnMemoryFileProblem.TooLarge);
                }

                var candidates = new List<LoadCandidate>();
                var seen = new HashSet<Guid>();
                var refused = 0;

                foreach (var element in list.EnumerateArray())
                {
                    // The same identity twice is not something this app writes: the file is refused.
                    if (element.ValueKind == JsonValueKind.Object
                        && element.TryGetProperty("id", out var idElement)
                        && idElement.ValueKind == JsonValueKind.String
                        && ParseId(idElement.GetString()) is { } seenId
                        && !seen.Add(seenId))
                    {
                        throw new OwnMemoryFileException(OwnMemoryFileProblem.Damaged);
                    }

                    if (Candidate(element, photos) is { } candidate)
                    {
                        candidates.Add(candidate);
                    }
                    else
                    {
                        refused++;
                    }
                }

                var muted = new HashSet<Guid>();

                if (root.TryGetProperty("muted", out var mutes))
                {
                    if (mutes.ValueKind != JsonValueKind.Array)
                    {
                        throw new OwnMemoryFileException(OwnMemoryFileProblem.Damaged);
                    }

                    foreach (var mute in mutes.EnumerateArray())
                    {
                        if (mute.ValueKind == JsonValueKind.String && ParseId(mute.GetString()) is { } id)
                        {
                            muted.Add(id);
                        }
                    }
                }

                return (candidates, muted, refused);
            }
        }

        /// <summary>One memory of the file as a draft, or null when it breaks a rule or names a photo not usable.</summary>
        private static LoadCandidate? Candidate(JsonElement element, IReadOnlyDictionary<string, string?> photos)
        {
            if (element.ValueKind != JsonValueKind.Object
                || !TryString(element, "id", required: true, out var idText) || ParseId(idText) is not { } id
                || !TryString(element, "start", required: true, out var startText) || ParseDate(startText) is not { } start
                || !TryString(element, "end", required: false, out var endText)
                || !TryString(element, "precision", required: true, out var precisionText)
                || !TryString(element, "title", required: true, out var title)
                || !TryString(element, "summary", required: false, out var summary)
                || !element.TryGetProperty("everyYear", out var everyYear) || everyYear.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                return null;
            }

            DateOnly? end = null;

            if (endText is not null)
            {
                if (ParseDate(endText) is not { } parsed)
                {
                    return null;
                }

                end = parsed;
            }

            MemoryDatePrecision precision;

            switch (precisionText)
            {
                case "Day":
                    precision = MemoryDatePrecision.Day;
                    break;
                case "Month":
                    precision = MemoryDatePrecision.Month;
                    break;
                default:
                    return null;
            }

            var drafts = new List<OwnPhotoDraft>();

            if (element.TryGetProperty("photos", out var photoList))
            {
                if (photoList.ValueKind != JsonValueKind.Array)
                {
                    return null;
                }

                foreach (var photo in photoList.EnumerateArray())
                {
                    if (photo.ValueKind != JsonValueKind.Object
                        || !TryString(photo, "file", required: true, out var file)
                        || !TryString(photo, "caption", required: false, out var caption)
                        || !OwnMemoryRules.IsPhotoFileName(file)
                        || !photos.TryGetValue(file!, out var staged)
                        || staged is null)
                    {
                        return null;
                    }

                    drafts.Add(new OwnPhotoDraft(staged, caption));
                }
            }

            var links = new List<OwnLink>();

            if (element.TryGetProperty("links", out var linkList))
            {
                if (linkList.ValueKind != JsonValueKind.Array)
                {
                    return null;
                }

                foreach (var link in linkList.EnumerateArray())
                {
                    if (link.ValueKind != JsonValueKind.Object
                        || !TryString(link, "url", required: true, out var url)
                        || !TryString(link, "label", required: false, out var label))
                    {
                        return null;
                    }

                    links.Add(new OwnLink(url!, label));
                }
            }

            var draft = new OwnMemoryDraft
            {
                Start = start,
                End = end,
                Precision = precision,
                Title = title!,
                Summary = summary,
                EveryYear = everyYear.ValueKind == JsonValueKind.True,
                Photos = drafts,
                Links = links,
            };

            return OwnMemoryRules.Validate(draft).Count == 0 ? new LoadCandidate(id, draft) : null;
        }

        /// <summary>A string property: false when it is of another type, or missing while required. Null when null or missing.</summary>
        private static bool TryString(JsonElement element, string name, bool required, out string? value)
        {
            value = null;

            if (!element.TryGetProperty(name, out var property))
            {
                return !required;
            }

            switch (property.ValueKind)
            {
                case JsonValueKind.String:
                    value = property.GetString();
                    return true;
                case JsonValueKind.Null:
                    return !required;
                default:
                    return false;
            }
        }

        /// <summary>A memory id in exactly the form this app writes: lower-case, with hyphens, not empty.</summary>
        private static Guid? ParseId(string? text) =>
            text is not null && IdForm().IsMatch(text) && Guid.TryParseExact(text, "D", out var id) && id != Guid.Empty ? id : null;

        private static DateOnly? ParseDate(string? text) =>
            DateOnly.TryParseExact(text, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;

        [GeneratedRegex("^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\\z", RegexOptions.CultureInvariant)]
        private static partial Regex IdForm();

        // ---------------------------------------------------------------- Words

        /// <summary>What Save says before it opens the dialog.</summary>
        public const string SaveWarning = "This file holds your memories and photos (without locations). Anyone who has the file can see them.";

        /// <summary>What Load says before it opens the dialog.</summary>
        public const string LoadWarning = "Memories you deleted since this file was saved will come back.";

        /// <summary>"Saved N memories and M photos." and, if any, how many photos were left out.</summary>
        public static string MessageFor(OwnMemorySaveResult result)
        {
            ArgumentNullException.ThrowIfNull(result);

            var text = $"Saved {Count(result.Memories, "memory", "memories")} and {Count(result.Photos, "photo", "photos")}.";

            return result.PhotosLeftOut > 0
                ? text + $" Left out: {Count(result.PhotosLeftOut, "photo", "photos")} that could not be checked."
                : text;
        }

        /// <summary>"Added N. Already here: M. Already here but different (yours kept): K." and the refused and ignored counts, if any.</summary>
        public static string MessageFor(OwnMemoryLoadResult result)
        {
            ArgumentNullException.ThrowIfNull(result);

            var text = string.Create(
                CultureInfo.CurrentCulture,
                $"Added {result.Added}. Already here: {result.AlreadyHere}. Already here but different (yours kept): {result.AlreadyHereDifferent}.");

            if (result.Refused > 0)
            {
                text += $" Not loaded: {Count(result.Refused, "memory", "memories")} that did not pass the checks.";
            }

            if (result.Ignored > 0)
            {
                text += $" Ignored: {Count(result.Ignored, "item", "items")} that {(result.Ignored == 1 ? "is" : "are")} not part of a memories file.";
            }

            return text;
        }

        /// <summary>What a refused save says.</summary>
        public static string SaveMessageFor(OwnMemoryFileProblem problem) => problem switch
        {
            OwnMemoryFileProblem.NoMemories => "There are no memories to save.",
            OwnMemoryFileProblem.TooLarge => "Your memories are too large for one file (more than 2 GB).",
            _ => "Your memories couldn't be saved to that file. Try again.",
        };

        /// <summary>What a refused load says: the file couldn't be loaded, why, and that nothing changed.</summary>
        public static string LoadMessageFor(OwnMemoryFileProblem problem) => "This file couldn't be loaded: " + problem switch
        {
            OwnMemoryFileProblem.NotAMemoriesFile => "it is not a Chatcheology memories file",
            OwnMemoryFileProblem.NewerVersion => "it was saved by a newer version of Chatcheology",
            OwnMemoryFileProblem.TooLarge => "it is larger than this app accepts",
            OwnMemoryFileProblem.NotSupported => "it uses a zip feature this app doesn't read",
            _ => "it is damaged or was changed",
        } + ". Nothing was changed.";

        /// <summary>Counts for the log: never a title, a name or a path.</summary>
        public static string DescribeCounts(OwnMemoryLoadResult result) =>
            string.Create(
                CultureInfo.InvariantCulture,
                $"added={result.Added} same={result.AlreadyHere} different={result.AlreadyHereDifferent} refused={result.Refused} ignored={result.Ignored} photos={result.PhotosAdded}");

        /// <summary>Counts for the log: never a title, a name or a path.</summary>
        public static string DescribeCounts(OwnMemorySaveResult result) =>
            string.Create(CultureInfo.InvariantCulture, $"memories={result.Memories} photos={result.Photos} leftOut={result.PhotosLeftOut}");

        private static string Count(int count, string one, string many) =>
            count.ToString(CultureInfo.CurrentCulture) + " " + (count == 1 ? one : many);
    }
}
