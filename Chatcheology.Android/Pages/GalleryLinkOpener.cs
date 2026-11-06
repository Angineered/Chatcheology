using Chatcheology.Mobile.Memories;

namespace Chatcheology.Android.Pages
{
    /// <summary>
    /// Opens external gallery links (CH-48) of a memory or a noted date: a choice when there are several, then
    /// "Opens outside Chatcheology" with the host and Open / Cancel. Open checks the stored address again with the
    /// shared rules (<see cref="GalleryLinks.Open"/>) and hands only its launch form to another app. Nothing about
    /// a link is logged.
    /// </summary>
    internal static class GalleryLinkOpener
    {
        private static bool _busy;

        /// <summary>One link: the confirmation; several: a choice first.</summary>
        internal static async Task ChooseAndOpenAsync(Page page, IReadOnlyList<GalleryLink> links)
        {
            if (links.Count == 0 || _busy)
            {
                return;
            }

            _busy = true;

            try
            {
                var link = links[0];

                if (links.Count > 1)
                {
                    var choices = GalleryLinks.Choices(links);
                    var chosen = await page.DisplayActionSheetAsync("Links", "Cancel", null, [.. choices]);
                    var index = chosen is null ? -1 : choices.ToList().IndexOf(chosen);

                    if (index < 0)
                    {
                        return;
                    }

                    link = links[index];
                }

                await ConfirmAndOpenAsync(page, link);
            }
            catch (Exception)
            {
                // A tap handler: nothing here may end the app.
            }
            finally
            {
                _busy = false;
            }
        }

        /// <summary>One link: the confirmation, then the second check and the hand-over.</summary>
        internal static async Task OpenAsync(Page page, GalleryLink link)
        {
            if (_busy)
            {
                return;
            }

            _busy = true;

            try
            {
                await ConfirmAndOpenAsync(page, link);
            }
            catch (Exception)
            {
                // A tap handler: nothing here may end the app.
            }
            finally
            {
                _busy = false;
            }
        }

        private static async Task ConfirmAndOpenAsync(Page page, GalleryLink link)
        {
            if (GalleryLinks.ConfirmMessage(link) is not { } host)
            {
                await page.DisplayAlertAsync("Open", GalleryLinks.RefusedText, "OK");
                return;
            }

            if (!await page.DisplayAlertAsync(GalleryLinks.ConfirmTitle, host, GalleryLinks.ConfirmOpen, GalleryLinks.ConfirmCancel))
            {
                return;
            }

            var result = GalleryLinks.Open(link, host, launch => ExternalOpener.OpenWebLink(launch) == OpenResult.Opened);

            if (GalleryLinks.ResultText(result) is { } text)
            {
                await page.DisplayAlertAsync("Open", text, "OK");
            }
        }
    }
}
