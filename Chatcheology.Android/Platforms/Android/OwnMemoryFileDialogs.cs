using System.Diagnostics;
using Android.Provider;
using Chatcheology.Mobile.OwnMemories;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Android
{
    /// <summary>
    /// Save to a file / Load from a file of the own memories (CH-54 step 3) through the system Save and Open dialogs. The
    /// file is written and read straight through the dialog's stream: no copy outside the app, no permission. The address
    /// and the file's name are never logged; the log says counts only.
    /// </summary>
    internal static class OwnMemoryFileDialogs
    {
        /// <summary>The types the Open dialog offers: zips under the names providers give them.</summary>
        private static readonly string[] OpenTypes = [OwnMemoryFile.MimeType, "application/x-zip-compressed", "application/x-zip", "application/octet-stream"];

        /// <summary>Asks where to save, then saves. The message to show, or none when cancelled.</summary>
        internal static async Task<string?> SaveAsync(OwnMemoryStore store)
        {
            if (MainActivity.Current is not { } activity)
            {
                return null;
            }

            var uri = await activity.CreateDocumentAsync(OwnMemoryFile.MimeType, OwnMemoryFile.SuggestedName(DateOnly.FromDateTime(DateTime.Now)));

            if (uri is null)
            {
                return null;
            }

            var watch = Stopwatch.StartNew();
            var resolver = activity.ContentResolver;

            try
            {
                var result = await Task.Run(() =>
                {
                    // "wt": the file is written from its start, whatever the provider held there before.
                    using var output = resolver?.OpenOutputStream(uri, "wt") ?? throw new IOException();
                    return OwnMemoryFile.Save(store, output, DateTime.UtcNow);
                });

                AppState.LogTiming("own-memories-saved", watch.Elapsed, OwnMemoryFile.DescribeCounts(result));
                return OwnMemoryFile.MessageFor(result);
            }
            catch (Exception exception)
            {
                // A half-written file is no use to anyone: it is removed (best effort).
                await Task.Run(() => DeleteQuietly(resolver, uri));
                AppState.LogTiming("own-memories-saved", watch.Elapsed, "status=failed " + exception.GetType().Name);

                return exception is OwnMemoryFileException refused
                    ? OwnMemoryFile.SaveMessageFor(refused.Problem)
                    : OwnMemoryFile.SaveMessageFor(OwnMemoryFileProblem.Damaged);
            }
        }

        /// <summary>Asks which file, then loads it. The message to show and whether anything was added; none when cancelled.</summary>
        internal static async Task<(string Message, bool Added)?> LoadAsync(OwnMemoryStore store)
        {
            if (MainActivity.Current is not { } activity)
            {
                return null;
            }

            var uri = await activity.OpenDocumentAsync(OpenTypes);

            if (uri is null)
            {
                return null;
            }

            var watch = Stopwatch.StartNew();
            var resolver = activity.ContentResolver;

            try
            {
                var result = await Task.Run(() =>
                {
                    using var input = resolver?.OpenInputStream(uri) ?? throw new IOException();
                    return OwnMemoryFile.Load(store, input);
                });

                AppState.LogTiming("own-memories-loaded", watch.Elapsed, OwnMemoryFile.DescribeCounts(result));
                return (OwnMemoryFile.MessageFor(result), result.Added > 0);
            }
            catch (OwnMemoryFileException refused)
            {
                AppState.LogTiming("own-memories-loaded", watch.Elapsed, "status=refused problem=" + refused.Problem);
                return (OwnMemoryFile.LoadMessageFor(refused.Problem), false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SqliteException
                or OwnMemoriesStoreException or OwnMemoryValidationException or Java.Lang.Exception)
            {
                AppState.LogTiming("own-memories-loaded", watch.Elapsed, "status=failed " + exception.GetType().Name);
                return ("This file couldn't be loaded. Nothing was changed.", false);
            }
        }

        private static void DeleteQuietly(global::Android.Content.ContentResolver? resolver, global::Android.Net.Uri uri)
        {
            try
            {
                if (resolver is not null)
                {
                    DocumentsContract.DeleteDocument(resolver, uri);
                }
            }
            catch (Exception)
            {
                // The provider may not allow it; the file stays, possibly incomplete.
            }
        }
    }
}
