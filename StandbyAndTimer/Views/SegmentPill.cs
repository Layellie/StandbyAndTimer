using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace StandbyAndTimer.Views;

/// <summary>
/// The selected tab's highlight in SegmentTabControlStyle as one piece that
/// slides to the newly selected tab. It is a render transform animated for
/// 200 ms on a selection change only: nothing runs before or after, and with
/// Windows' animations turned off it moves at once.
/// </summary>
public static class SegmentPill
{
    private static readonly Duration SlideTime = TimeSpan.FromMilliseconds(200);

    /// <summary>Set on the highlight; the TabControl whose selection it follows.</summary>
    public static readonly DependencyProperty TabControlProperty = DependencyProperty.RegisterAttached(
        "TabControl", typeof(TabControl), typeof(SegmentPill), new PropertyMetadata(null, OnTabControlChanged));

    public static TabControl? GetTabControl(DependencyObject d) => (TabControl?)d.GetValue(TabControlProperty);
    public static void SetTabControl(DependencyObject d, TabControl? value) => d.SetValue(TabControlProperty, value);

    /// <summary>Set on the header panel (the UniformGrid) next to the highlight.</summary>
    public static readonly DependencyProperty HostProperty = DependencyProperty.RegisterAttached(
        "Host", typeof(UniformGrid), typeof(SegmentPill), new PropertyMetadata(null));

    public static UniformGrid? GetHost(DependencyObject d) => (UniformGrid?)d.GetValue(HostProperty);
    public static void SetHost(DependencyObject d, UniformGrid? value) => d.SetValue(HostProperty, value);

    private static void OnTabControlChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement pill || e.NewValue is not TabControl tabs) return;
        var move = new TranslateTransform();
        pill.RenderTransform = move;

        tabs.SelectionChanged += (_, args) =>
        {
            // SelectionChanged bubbles up from lists and combo boxes on the pages.
            if (ReferenceEquals(args.OriginalSource, tabs)) Place(pill, tabs, move, animate: true);
        };
        pill.Loaded += (_, _) =>
        {
            if (GetHost(pill) is { } host)
                host.SizeChanged += (_, _) => Place(pill, tabs, move, animate: false);
            Place(pill, tabs, move, animate: false);
        };
    }

    /// <summary>X of the highlight for the selected tab: tabs share the width equally.</summary>
    public static double TargetX(int selectedIndex, int count, double width) =>
        count <= 0 || selectedIndex < 0 ? 0 : width / count * selectedIndex;

    private static void Place(FrameworkElement pill, TabControl tabs, TranslateTransform move, bool animate)
    {
        if (GetHost(pill) is not { } host || host.ActualWidth <= 0) return;
        int count = tabs.Items.Count;
        pill.Visibility = tabs.SelectedIndex < 0 || count == 0 ? Visibility.Hidden : Visibility.Visible;
        if (count == 0) return;
        pill.Width = host.ActualWidth / count;
        // Whole pixels, like the tabs themselves (layout rounding): a fraction blurs the edges.
        double x = Math.Round(TargetX(tabs.SelectedIndex, count, host.ActualWidth));

        if (animate && SystemParameters.ClientAreaAnimation && pill.IsVisible)
        {
            double from = move.X;                      // where it is now, mid-slide included
            var slide = new DoubleAnimation(from, x, SlideTime)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior   = FillBehavior.Stop,   // the animation ends; X keeps the value below
            };
            move.X = x;                                // the base value the animation lands on
            move.BeginAnimation(TranslateTransform.XProperty, slide);
        }
        else
        {
            move.BeginAnimation(TranslateTransform.XProperty, null);
            move.X = x;
        }
    }
}
