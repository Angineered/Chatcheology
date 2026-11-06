using Chatcheology.Archive;
using Chatcheology.Mobile.OwnMemories;

namespace Chatcheology.Mobile.Synthetic
{
    /// <summary>
    /// Invented own memories (CH-54 step 2) that a Debug build adds with a synthetic install, so the emulator shows the
    /// phone owner's own memories: one yearly on today's date, one with two photos (one of them a JPEG with an invented
    /// location, stored without it), and a copy of the synthetic archive's anniversary (a duplicate, shown twice).
    /// </summary>
    /// <remarks>
    /// Compiled into the tests and, as a linked file, into Debug builds of the Android app only; a Release build does not
    /// contain it. Every title, caption, date and link is invented; the link is on a reserved example domain.
    /// </remarks>
    internal static class SyntheticOwnMemories
    {
        /// <summary>The yearly one's title.</summary>
        internal const string YearlyTitle = "Invented picnic day";

        /// <summary>The one with photos.</summary>
        internal const string PhotosTitle = "Invented lake walk";

        /// <summary>The copy of the synthetic archive's yearly anniversary.</summary>
        internal const string DuplicateTitle = "Our synthetic anniversary";

        /// <summary>
        /// Adds the three memories when the store has none (so a second install adds nothing). Returns how many were added.
        /// </summary>
        /// <param name="store">The own store.</param>
        /// <param name="today">The phone's date: the yearly memory falls on it.</param>
        /// <param name="normaliser">The platform's photo step, or none.</param>
        internal static async Task<int> SeedAsync(OwnMemoryStore store, DateOnly today, IPhotoNormaliser? normaliser = null)
        {
            ArgumentNullException.ThrowIfNull(store);

            if (store.Count() > 0)
            {
                return 0;
            }

            var intake = new OwnPhotoIntake(store.PhotosFolder, normaliser);
            using var lease = store.LeasePhotos();
            var lake = await intake.AddAsync(new MemoryStream(SyntheticImage.Png(320, 240, (0x2E, 0x6F, 0x9E), (0xE8, 0xD5, 0x9A))));
            var shore = await intake.AddAsync(new MemoryStream(SyntheticJpeg.WithLocation()));
            var photos = new List<OwnPhotoDraft>();

            if (lake.FileName is { } first && lease.TryHold(first))
            {
                photos.Add(new OwnPhotoDraft(first, "Invented lake at noon"));
            }

            if (shore.FileName is { } second && lease.TryHold(second))
            {
                photos.Add(new OwnPhotoDraft(second, null));
            }

            store.Add(new OwnMemoryDraft
            {
                Title = YearlyTitle,
                Start = new DateOnly(2023, today.Month, Math.Min(today.Day, DateTime.DaysInMonth(2023, today.Month))),
                Precision = MemoryDatePrecision.Day,
                Summary = "An invented yearly memory on this phone.",
                EveryYear = true,
            });

            store.Add(new OwnMemoryDraft
            {
                Title = PhotosTitle,
                Start = new DateOnly(2025, 3, 20),
                Precision = MemoryDatePrecision.Day,
                Summary = "An invented walk around an invented lake.",
                Photos = photos,
                Links = [new OwnLink("https://example.org/invented-lake-walk", "Invented album")],
            });

            store.Add(new OwnMemoryDraft
            {
                Title = DuplicateTitle,
                Start = new DateOnly(2016, 3, 14),
                Precision = MemoryDatePrecision.Day,
                Summary = "An invented note.",
            });

            return 3;
        }
    }
}
