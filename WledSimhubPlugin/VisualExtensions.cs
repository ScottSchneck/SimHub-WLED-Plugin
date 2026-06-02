using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace WledSimHubPlugin
{
    public static class VisualExtensions
    {
        public static T FindAncestor<T>(this DependencyObject current) where T : DependencyObject
        {
            while (current != null && !(current is T))
                current = VisualTreeHelper.GetParent(current);
            return current as T;
        }
    }

    public class LedStripPreview : FrameworkElement
    {
        private Brush _idleBrush = Brushes.Black;

        public static readonly DependencyProperty TotalLedCountProperty =
            DependencyProperty.Register(nameof(TotalLedCount), typeof(int), typeof(LedStripPreview),
                new FrameworkPropertyMetadata(1, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty SegmentsProperty =
            DependencyProperty.Register(nameof(Segments), typeof(ObservableCollection<LedSegment>), typeof(LedStripPreview),
                new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnSegmentsChanged));

        public static readonly DependencyProperty IdleColorProperty =
            DependencyProperty.Register(nameof(IdleColor), typeof(string), typeof(LedStripPreview),
                new FrameworkPropertyMetadata("#000000", FrameworkPropertyMetadataOptions.AffectsRender, OnIdleColorChanged));

        private static void OnIdleColorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
            => ((LedStripPreview)d)._idleBrush = MakeBrush((string)e.NewValue);

        private static void OnSegmentsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var preview = (LedStripPreview)d;
            if (e.OldValue is ObservableCollection<LedSegment> old)
            {
                old.CollectionChanged -= preview.SegmentsCollectionChanged;
                foreach (var seg in old) seg.PropertyChanged -= preview.SegmentPropertyChanged;
            }
            if (e.NewValue is ObservableCollection<LedSegment> next)
            {
                next.CollectionChanged += preview.SegmentsCollectionChanged;
                foreach (var seg in next) seg.PropertyChanged += preview.SegmentPropertyChanged;
            }
        }

        private void SegmentsCollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.OldItems != null) foreach (LedSegment s in e.OldItems) s.PropertyChanged -= SegmentPropertyChanged;
            if (e.NewItems != null) foreach (LedSegment s in e.NewItems) s.PropertyChanged += SegmentPropertyChanged;
            InvalidateVisual();
        }

        private void SegmentPropertyChanged(object sender, PropertyChangedEventArgs e) => InvalidateVisual();

        public int TotalLedCount
        {
            get => (int)GetValue(TotalLedCountProperty);
            set => SetValue(TotalLedCountProperty, value);
        }

        public ObservableCollection<LedSegment> Segments
        {
            get => (ObservableCollection<LedSegment>)GetValue(SegmentsProperty);
            set => SetValue(SegmentsProperty, value);
        }

        public string IdleColor { get => (string)GetValue(IdleColorProperty); set => SetValue(IdleColorProperty, value); }

        protected override void OnRender(DrawingContext dc)
        {
            int    total   = Math.Max(1, TotalLedCount);
            double w       = ActualWidth;
            double h       = ActualHeight;
            double slotW   = w / total;
            double radius  = Math.Min(slotW * 0.42, h / 2.0);
            double yCenter = h / 2.0;

            var segs = Segments;

            // Build one brush per segment for this render pass (avoids per-LED brush creation)
            Brush[] segBrushes = null;
            if (segs != null && segs.Count > 0)
                segBrushes = segs.Select(s => MakeBrush(s.HexColor)).ToArray();

            for (int i = 0; i < total; i++)
            {
                Brush brush = _idleBrush;
                if (segs != null)
                    for (int s = 0; s < segs.Count; s++)
                        if (i >= segs[s].StartPixel && i < segs[s].StopPixel) { brush = segBrushes[s]; break; }

                dc.DrawEllipse(brush, null, new Point((i + 0.5) * slotW, yCenter), radius, radius);
            }
        }

        private static Brush MakeBrush(string hex)
        {
            try { var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex ?? "#000000")); b.Freeze(); return b; }
            catch { return Brushes.Transparent; }
        }
    }

    public class IndexConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is ListBoxItem item)
            {
                var lb = item.FindAncestor<ListBox>();
                if (lb != null)
                {
                    int idx = lb.ItemContainerGenerator.IndexFromContainer(item);
                    if (idx >= 0) return (idx + 1).ToString();
                }
            }
            return "";
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    public class BoolToOpacityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool b = value is bool bv && bv;
            bool invert = parameter?.ToString() == "Invert";
            return (invert ? !b : b) ? 1.0 : 0.35;
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    public class InverseBoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is bool b && b ? Visibility.Collapsed : Visibility.Visible;
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
    }
}
