using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Navigation;
using Chatcheology.Core.Text;

namespace Chatcheology.Desktop.Controls
{
    /// <summary>
    /// Renders a scanned message into a <see cref="TextBlock"/>, with its web and email addresses
    /// followable.
    /// </summary>
    /// <remarks>
    /// <para><strong>Nothing happens until somebody clicks.</strong> No address is contacted,
    /// resolved, previewed or fetched by being displayed. The only network activity this can cause
    /// is whatever the reader's own browser or mail client does after they choose to follow a link.
    /// </para>
    /// <para><strong>The scheme is checked twice.</strong>
    /// <see cref="MessageTextScanner"/> only ever produces <c>http</c>, <c>https</c> and
    /// <c>mailto</c> targets, and this checks again immediately before handing one to the shell.
    /// <c>UseShellExecute</c> will launch a registered handler for any scheme it is given, so the
    /// check that matters is the one nearest the call.</para>
    /// <para>An attached property rather than a control, because what is wanted is an ordinary
    /// <see cref="TextBlock"/> — wrapping, selection and layout unchanged — whose inlines happen to
    /// include hyperlinks.</para>
    /// </remarks>
    public static class LinkedText
    {
        /// <summary>
        /// The scanned message to render. Setting it replaces the block's inlines.
        /// </summary>
        public static readonly DependencyProperty SpansProperty =
            DependencyProperty.RegisterAttached(
                "Spans",
                typeof(IReadOnlyList<MessageTextSpan>),
                typeof(LinkedText),
                new PropertyMetadata(null, OnSpansChanged));

        /// <summary>
        /// Raised when following a link failed, so the window can say so where the reader is
        /// looking.
        /// </summary>
        /// <remarks>
        /// Bubbles, so one handler on the window covers every message on the page. A link that will
        /// not open is a fact about this machine — no browser registered, a mail client that is not
        /// installed — and is reported rather than thrown.
        /// </remarks>
        public static readonly RoutedEvent LinkFailedEvent = EventManager.RegisterRoutedEvent(
            "LinkFailed",
            RoutingStrategy.Bubble,
            typeof(RoutedEventHandler),
            typeof(LinkedText));

        /// <summary>Sets <see cref="SpansProperty"/>.</summary>
        public static void SetSpans(DependencyObject element, IReadOnlyList<MessageTextSpan>? value) =>
            element.SetValue(SpansProperty, value);

        /// <summary>Reads <see cref="SpansProperty"/>.</summary>
        public static IReadOnlyList<MessageTextSpan>? GetSpans(DependencyObject element) =>
            (IReadOnlyList<MessageTextSpan>?)element.GetValue(SpansProperty);

        /// <summary>What went wrong when a link could not be followed.</summary>
        public sealed class LinkFailedEventArgs : RoutedEventArgs
        {
            internal LinkFailedEventArgs(string message)
                : base(LinkFailedEvent) => Message = message;

            /// <summary>A sentence describing the failure, fit to show the reader.</summary>
            public string Message { get; }
        }

        private static void OnSpansChanged(
            DependencyObject element, DependencyPropertyChangedEventArgs e)
        {
            if (element is not TextBlock block)
            {
                return;
            }

            block.Inlines.Clear();

            if (e.NewValue is not IReadOnlyList<MessageTextSpan> spans)
            {
                return;
            }

            foreach (var span in spans)
            {
                if (span.Kind == MessageSpanKind.Text || span.Target is not { } target)
                {
                    AddText(block, span.Text);

                    continue;
                }

                AddLink(block, span.Text, target);
            }
        }

        /// <remarks>
        /// Line breaks are emitted as <see cref="LineBreak"/> inlines rather than left inside a
        /// <see cref="Run"/>, so a multi-line message breaks where it was written to whatever else
        /// shares the block.
        /// </remarks>
        private static void AddText(TextBlock block, string text)
        {
            var lines = text.Split('\n');

            for (var index = 0; index < lines.Length; index++)
            {
                if (index > 0)
                {
                    block.Inlines.Add(new LineBreak());
                }

                if (lines[index].Length > 0)
                {
                    block.Inlines.Add(new Run(lines[index]));
                }
            }
        }

        private static void AddLink(TextBlock block, string text, string target)
        {
            // Refused here as well as in the scanner. A target that cannot be turned into an
            // absolute address of a scheme this viewer follows is shown as the text it is.
            if (!MessageTextScanner.IsFollowable(target)
                || !Uri.TryCreate(target, UriKind.Absolute, out var uri))
            {
                AddText(block, text);

                return;
            }

            var link = new Hyperlink(new Run(text))
            {
                NavigateUri = uri,
                ToolTip = target,
            };

            link.RequestNavigate += OnRequestNavigate;

            block.Inlines.Add(link);
        }

        private static void OnRequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            e.Handled = true;

            if (sender is not Hyperlink link)
            {
                return;
            }

            if (Follow(e.Uri) is { } failure)
            {
                link.RaiseEvent(new LinkFailedEventArgs(failure));
            }
        }

        /// <summary>
        /// Hands <paramref name="uri"/> to whatever this machine has registered for it.
        /// </summary>
        /// <returns>
        /// <see langword="null"/> when the request was handed over, or a message describing why it
        /// could not be.
        /// </returns>
        private static string? Follow(Uri uri)
        {
            // The last check before the shell. UseShellExecute launches a registered handler for
            // whatever scheme it is given, so nothing but these three ever reaches it.
            if (!MessageTextScanner.IsFollowable(uri.AbsoluteUri))
            {
                return "That address is not a kind this viewer opens.";
            }

            try
            {
                using var process = Process.Start(
                    new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });

                return null;
            }
            catch (Win32Exception)
            {
                return uri.Scheme == Uri.UriSchemeMailto
                    ? "This machine has no mail client registered for email links."
                    : "This machine has no browser registered for web links.";
            }
            catch (Exception exception) when (
                exception is InvalidOperationException
                    or IOException
                    or UnauthorizedAccessException)
            {
                return "That link could not be opened.";
            }
        }
    }
}
