using System.Collections.Specialized;
using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Widget;
using AndroidX.RecyclerView.Widget;
using Chatcheology.Mobile.Conversation;
using AColor = Android.Graphics.Color;
using AView = Android.Views.View;
using AViewGroup = Android.Views.ViewGroup;
using GravityFlags = Android.Views.GravityFlags;

namespace Chatcheology.Android.Controls
{
    /// <summary>
    /// The native list's adapter: builds Android views for separators and bubbles, and passes each
    /// page insert on as one range insert so the RecyclerView keeps its anchor.
    /// </summary>
    internal sealed class ConversationAdapter : RecyclerView.Adapter
    {
        private const int SeparatorType = 0;
        private const int TheirsType = 1;
        private const int MineType = 2;
        private const int SystemType = 3;

        private static readonly AColor BubbleTheirs = AColor.ParseColor("#FFFFFF");
        private static readonly AColor BubbleMine = AColor.ParseColor("#D9FDD3");
        private static readonly AColor BubbleSystem = AColor.ParseColor("#FFF5C4");
        private static readonly AColor SeparatorBackground = AColor.ParseColor("#E1F0F7");
        private static readonly AColor CardBackground = AColor.ParseColor("#F0F2F5");
        private static readonly AColor MutedText = AColor.ParseColor("#667781");
        private static readonly AColor SenderText = AColor.ParseColor("#2E6B5E");

        private readonly Context _context;
        private readonly ConversationRows _rows;
        private readonly float _density;
        private readonly BitmapCache _bitmaps = new(capacity: 48);

        internal ConversationAdapter(Context context, ConversationRows rows)
        {
            _context = context;
            _rows = rows;
            _density = context.Resources?.DisplayMetrics?.Density ?? 1f;
            _rows.CollectionChanged += OnRowsChanged;
        }

        /// <inheritdoc />
        public override int ItemCount => _rows.Count;

        /// <summary>Stops following the rows.</summary>
        internal void Detach() => _rows.CollectionChanged -= OnRowsChanged;

        /// <inheritdoc />
        public override int GetItemViewType(int position) => _rows[position] switch
        {
            DateSeparatorRow => SeparatorType,
            MessageRow { IsSystem: true } => SystemType,
            MessageRow { IsMine: true } => MineType,
            _ => TheirsType,
        };

        /// <inheritdoc />
        public override RecyclerView.ViewHolder OnCreateViewHolder(AViewGroup parent, int viewType) =>
            viewType == SeparatorType ? CreateSeparator() : CreateBubble(viewType);

        /// <inheritdoc />
        public override void OnBindViewHolder(RecyclerView.ViewHolder holder, int position)
        {
            switch (holder, _rows[position])
            {
                case (SeparatorHolder separator, DateSeparatorRow row):
                    separator.Text.Text = row.Text;
                    break;

                case (BubbleHolder bubble, MessageRow row):
                    BindBubble(bubble, row);
                    break;
            }
        }

        private void OnRowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            switch (e.Action)
            {
                case NotifyCollectionChangedAction.Add when e.NewItems is not null:
                    NotifyItemRangeInserted(e.NewStartingIndex, e.NewItems.Count);
                    break;

                case NotifyCollectionChangedAction.Remove when e.OldItems is not null:
                    NotifyItemRangeRemoved(e.OldStartingIndex, e.OldItems.Count);
                    break;

                default:
                    NotifyDataSetChanged();
                    break;
            }
        }

        // -------------------------------------------------------------------------------------
        // Views.
        // -------------------------------------------------------------------------------------

        private SeparatorHolder CreateSeparator()
        {
            var root = new FrameLayout(_context)
            {
                LayoutParameters = new RecyclerView.LayoutParams(AViewGroup.LayoutParams.MatchParent, AViewGroup.LayoutParams.WrapContent),
            };
            root.SetPadding(0, Dp(8), 0, Dp(8));

            var text = new TextView(_context) { TextSize = 12 };
            text.SetTextColor(MutedText);
            text.SetPadding(Dp(10), Dp(4), Dp(10), Dp(4));
            text.Background = Rounded(SeparatorBackground);

            root.AddView(text, new FrameLayout.LayoutParams(AViewGroup.LayoutParams.WrapContent, AViewGroup.LayoutParams.WrapContent, GravityFlags.CenterHorizontal));

            return new SeparatorHolder(root, text);
        }

        private BubbleHolder CreateBubble(int viewType)
        {
            var root = new FrameLayout(_context)
            {
                LayoutParameters = new RecyclerView.LayoutParams(AViewGroup.LayoutParams.MatchParent, AViewGroup.LayoutParams.WrapContent),
            };

            var bubble = new LinearLayout(_context) { Orientation = Orientation.Vertical };
            bubble.SetPadding(Dp(8), Dp(6), Dp(8), Dp(6));

            var (colour, gravity, left, right) = viewType switch
            {
                MineType => (BubbleMine, GravityFlags.End, 56, 8),
                SystemType => (BubbleSystem, GravityFlags.CenterHorizontal, 32, 32),
                _ => (BubbleTheirs, GravityFlags.Start, 8, 56),
            };

            bubble.Background = Rounded(colour);

            var bubbleLayout = new FrameLayout.LayoutParams(AViewGroup.LayoutParams.WrapContent, AViewGroup.LayoutParams.WrapContent, gravity);
            bubbleLayout.SetMargins(Dp(left), Dp(2), Dp(right), Dp(2));
            root.AddView(bubble, bubbleLayout);

            var sender = new TextView(_context) { TextSize = 13 };
            sender.SetTextColor(SenderText);
            sender.SetTypeface(null, TypefaceStyle.Bold);
            sender.Visibility = viewType == TheirsType ? global::Android.Views.ViewStates.Visible : global::Android.Views.ViewStates.Gone;

            var attachments = new LinearLayout(_context) { Orientation = Orientation.Vertical };

            var text = new TextView(_context) { TextSize = viewType == SystemType ? 12 : 15 };
            text.SetTextColor(viewType == SystemType ? MutedText : AColor.Black);

            var time = new TextView(_context) { TextSize = 11 };
            time.SetTextColor(MutedText);
            time.Visibility = viewType == SystemType ? global::Android.Views.ViewStates.Gone : global::Android.Views.ViewStates.Visible;

            // Explicit wrap-content sizes: a vertical LinearLayout's default is match-parent width,
            // which in a wrap-content bubble shrinks the text to the width of the time line.
            bubble.AddView(sender, WrapContent());
            bubble.AddView(attachments, WrapContent());
            bubble.AddView(text, WrapContent());
            bubble.AddView(time, new LinearLayout.LayoutParams(AViewGroup.LayoutParams.WrapContent, AViewGroup.LayoutParams.WrapContent) { Gravity = GravityFlags.End });

            return new BubbleHolder(root, sender, attachments, text, time);
        }

        private void BindBubble(BubbleHolder holder, MessageRow row)
        {
            holder.Sender.Text = row.SenderText;
            holder.Text.Text = row.Text;
            holder.Text.Visibility = row.HasText ? global::Android.Views.ViewStates.Visible : global::Android.Views.ViewStates.Gone;
            holder.Time.Text = row.TimeText;

            holder.Attachments.RemoveAllViews();
            holder.Attachments.Visibility = row.Attachments.Count > 0 ? global::Android.Views.ViewStates.Visible : global::Android.Views.ViewStates.Gone;

            var width = Dp(AttachmentCardView.ImageWidth);

            foreach (var card in row.Attachments)
            {
                var layout = new LinearLayout.LayoutParams(width, AViewGroup.LayoutParams.WrapContent);
                layout.SetMargins(0, Dp(2), 0, Dp(2));

                if (card.Kind == AttachmentCardKind.Image && card.FullPath is { } path)
                {
                    // Final height from the recorded size, before anything is decoded.
                    layout.Height = Dp(card.HeightFor(AttachmentCardView.ImageWidth));

                    var image = new ImageView(_context);
                    image.SetScaleType(ImageView.ScaleType.CenterCrop);
                    image.SetBackgroundColor(CardBackground);
                    holder.Attachments.AddView(image, layout);

                    ShowImage(image, path, width);
                }
                else
                {
                    var label = new TextView(_context) { Text = card.Heading, TextSize = 14 };
                    label.SetTextColor(MutedText);
                    label.SetPadding(Dp(10), Dp(8), Dp(10), Dp(8));
                    label.SetBackgroundColor(CardBackground);
                    holder.Attachments.AddView(label, layout);
                }
            }
        }

        /// <summary>Decodes off the UI thread at the shown width, then sets the bitmap if the view still wants it.</summary>
        private void ShowImage(ImageView view, string path, int width)
        {
            view.Tag = new Java.Lang.String(path);

            if (_bitmaps.Get(path) is { } cached)
            {
                view.SetImageBitmap(cached);
                return;
            }

            _ = Task.Run(() =>
            {
                var bitmap = Decode(path, width);

                view.Post(() =>
                {
                    if (bitmap is null)
                    {
                        return;
                    }

                    _bitmaps.Put(path, bitmap);

                    if (view.Tag?.ToString() == path)
                    {
                        view.SetImageBitmap(bitmap);
                    }
                });
            });
        }

        private static Bitmap? Decode(string path, int width)
        {
            try
            {
                var bounds = new BitmapFactory.Options { InJustDecodeBounds = true };
                BitmapFactory.DecodeFile(path, bounds);

                var sample = 1;

                while (bounds.OutWidth / (sample * 2) >= width)
                {
                    sample *= 2;
                }

                return BitmapFactory.DecodeFile(path, new BitmapFactory.Options { InSampleSize = sample });
            }
            catch (Java.Lang.Exception)
            {
                return null;
            }
        }

        private GradientDrawable Rounded(AColor colour)
        {
            var drawable = new GradientDrawable();
            drawable.SetColor(colour);
            drawable.SetCornerRadius(Dp(8));

            return drawable;
        }

        private int Dp(double value) => (int)Math.Round(value * _density);

        private static LinearLayout.LayoutParams WrapContent() =>
            new(AViewGroup.LayoutParams.WrapContent, AViewGroup.LayoutParams.WrapContent);

        private sealed class SeparatorHolder(AView view, TextView text) : RecyclerView.ViewHolder(view)
        {
            internal TextView Text { get; } = text;
        }

        private sealed class BubbleHolder(AView view, TextView sender, LinearLayout attachments, TextView text, TextView time)
            : RecyclerView.ViewHolder(view)
        {
            internal TextView Sender { get; } = sender;

            internal LinearLayout Attachments { get; } = attachments;

            internal TextView Text { get; } = text;

            internal TextView Time { get; } = time;
        }

        /// <summary>A small most-recently-used cache of decoded bitmaps, by file path.</summary>
        private sealed class BitmapCache(int capacity)
        {
            private readonly LinkedList<(string Path, Bitmap Bitmap)> _order = new();
            private readonly Dictionary<string, LinkedListNode<(string Path, Bitmap Bitmap)>> _byPath = new(StringComparer.Ordinal);

            internal Bitmap? Get(string path)
            {
                if (!_byPath.TryGetValue(path, out var node))
                {
                    return null;
                }

                _order.Remove(node);
                _order.AddFirst(node);

                return node.Value.Bitmap;
            }

            internal void Put(string path, Bitmap bitmap)
            {
                if (_byPath.ContainsKey(path))
                {
                    return;
                }

                _byPath[path] = _order.AddFirst((path, bitmap));

                if (_order.Count > capacity && _order.Last is { } oldest)
                {
                    _order.RemoveLast();
                    _byPath.Remove(oldest.Value.Path);
                }
            }
        }
    }
}
