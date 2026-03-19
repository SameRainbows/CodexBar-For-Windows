using System.Globalization;
using System.Collections;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using CodexBar.App.ViewModels;
using CodexBar.Core.Models;
using WinForms = System.Windows.Forms;

namespace CodexBar.App.Views;

/// <summary>
/// Popup window anchored to system tray area.
/// Auto-hides when focus is lost.
/// </summary>
public partial class UsagePopupWindow : Window
{
    private const double PreferredWidth = 380;
    private const double PreferredHeight = 480;
    private const double EdgeMargin = 8;

    public event EventHandler? RefreshRequested;
    public event EventHandler? SettingsRequested;

    public UsagePopupWindow()
    {
        InitializeComponent();
    }

    /// <summary>Show popup near the system tray notification area.</summary>
    public void ShowNearTray()
    {
        // Use monitor under cursor, but convert Win32 pixel coordinates to WPF DIPs.
        var cursor = WinForms.Cursor.Position;
        var screen = WinForms.Screen.FromPoint(cursor);
        var dpi = VisualTreeHelper.GetDpi(this);

        var work = ToDipRect(screen.WorkingArea, dpi);
        var bounds = ToDipRect(screen.Bounds, dpi);
        var edge = DetectTaskbarEdge(work, bounds);

        // Force popup size to remain fully visible on the selected monitor.
        var maxWidth = Math.Max(280, work.Width - (EdgeMargin * 2));
        var maxHeight = Math.Max(240, work.Height - (EdgeMargin * 2));
        Width = Math.Min(PreferredWidth, maxWidth);
        Height = Math.Min(PreferredHeight, maxHeight);
        MaxWidth = maxWidth;
        MaxHeight = maxHeight;

        switch (edge)
        {
            case TaskbarEdge.Top:
                Left = work.Right - Width - EdgeMargin;
                Top = work.Top + EdgeMargin;
                break;
            case TaskbarEdge.Left:
                Left = work.Left + EdgeMargin;
                Top = work.Bottom - Height - EdgeMargin;
                break;
            case TaskbarEdge.Right:
                Left = work.Right - Width - EdgeMargin;
                Top = work.Bottom - Height - EdgeMargin;
                break;
            case TaskbarEdge.Bottom:
            default:
                Left = work.Right - Width - EdgeMargin;
                Top = work.Bottom - Height - EdgeMargin;
                break;
        }

        // Final clamp to guarantee full visibility.
        Left = Clamp(Left, work.Left + EdgeMargin, work.Right - Width - EdgeMargin);
        Top = Clamp(Top, work.Top + EdgeMargin, work.Bottom - Height - EdgeMargin);

        Show();
        Activate();
    }

    private void Window_Deactivated(object sender, EventArgs e)
    {
        // Auto-hide when the user clicks elsewhere
        Hide();
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshRequested?.Invoke(this, EventArgs.Empty);
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        SettingsRequested?.Invoke(this, EventArgs.Empty);
    }

    private static double Clamp(double value, double min, double max)
    {
        if (value < min) return min;
        if (value > max) return max;
        return value;
    }

    private static Rect ToDipRect(System.Drawing.Rectangle pxRect, DpiScale dpi)
    {
        return new Rect(
            pxRect.Left / dpi.DpiScaleX,
            pxRect.Top / dpi.DpiScaleY,
            pxRect.Width / dpi.DpiScaleX,
            pxRect.Height / dpi.DpiScaleY);
    }

    private static TaskbarEdge DetectTaskbarEdge(Rect work, Rect bounds)
    {
        var leftInset = Math.Max(0, work.Left - bounds.Left);
        var topInset = Math.Max(0, work.Top - bounds.Top);
        var rightInset = Math.Max(0, bounds.Right - work.Right);
        var bottomInset = Math.Max(0, bounds.Bottom - work.Bottom);

        var maxInset = Math.Max(Math.Max(leftInset, topInset), Math.Max(rightInset, bottomInset));
        if (maxInset <= 0.5)
            return TaskbarEdge.Bottom; // auto-hide/unknown: default to common Windows tray location.

        if (bottomInset == maxInset) return TaskbarEdge.Bottom;
        if (topInset == maxInset) return TaskbarEdge.Top;
        if (rightInset == maxInset) return TaskbarEdge.Right;
        return TaskbarEdge.Left;
    }

    private enum TaskbarEdge
    {
        Left,
        Top,
        Right,
        Bottom,
    }
}

/// <summary>
/// Converts a percentage (0-100) + container width to pixel width for progress bars.
/// </summary>
public sealed class PercentToWidthConverter : IMultiValueConverter
{
    public static readonly PercentToWidthConverter Instance = new();

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length >= 2
            && values[0] is double percent
            && values[1] is double containerWidth)
        {
            return Math.Max(0, Math.Min(containerWidth, containerWidth * percent / 100.0));
        }

        return 0.0;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

/// <summary>
/// Converts a Quota object to display text.
/// </summary>
public sealed class QuotaToTextConverter : IValueConverter
{
    public static readonly QuotaToTextConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is Quota quota)
            return quota.ToDisplayString();

        return "No data";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

/// <summary>
/// Converts provider status enum to short UI text.
/// </summary>
public sealed class ProviderStatusTextConverter : IValueConverter
{
    public static readonly ProviderStatusTextConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not ProviderStatus status)
            return "● Unknown";

        return status switch
        {
            ProviderStatus.Available => "● Available",
            ProviderStatus.Fetching => "● Refreshing",
            ProviderStatus.Error => "● Error",
            ProviderStatus.Stale => "● Stale",
            ProviderStatus.Unavailable => "● Unavailable",
            _ => "● Unknown",
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

/// <summary>
/// Converts provider status enum to themed status color.
/// </summary>
public sealed class ProviderStatusBrushConverter : IValueConverter
{
    public static readonly ProviderStatusBrushConverter Instance = new();

    private static readonly Brush Success = new SolidColorBrush(Color.FromRgb(74, 222, 128));
    private static readonly Brush Pending = new SolidColorBrush(Color.FromRgb(251, 191, 36));
    private static readonly Brush Error = new SolidColorBrush(Color.FromRgb(248, 113, 113));
    private static readonly Brush Subtle = new SolidColorBrush(Color.FromRgb(144, 144, 168));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not ProviderStatus status)
            return Subtle;

        return status switch
        {
            ProviderStatus.Available => Success,
            ProviderStatus.Fetching => Pending,
            ProviderStatus.Error => Error,
            ProviderStatus.Stale => Pending,
            ProviderStatus.Unavailable => Subtle,
            _ => Subtle,
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

/// <summary>
/// Converts null/empty strings to Visibility.Collapsed.
/// </summary>
public sealed class EmptyStringToVisibilityConverter : IValueConverter
{
    public static readonly EmptyStringToVisibilityConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return string.IsNullOrWhiteSpace(value as string)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

/// <summary>
/// Converts collection values with at least one item to Visibility.Visible.
/// </summary>
public sealed class CollectionHasItemsToVisibilityConverter : IValueConverter
{
    public static readonly CollectionHasItemsToVisibilityConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is IEnumerable enumerable)
        {
            foreach (var _ in enumerable)
                return Visibility.Visible;
        }

        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

/// <summary>
/// Converts null/empty collections to Visibility.Visible.
/// </summary>
public sealed class CollectionEmptyToVisibilityConverter : IValueConverter
{
    public static readonly CollectionEmptyToVisibilityConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is IEnumerable enumerable)
        {
            foreach (var _ in enumerable)
                return Visibility.Collapsed;
        }

        return Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

/// <summary>
/// Converts reset timestamp to human-readable UI text.
/// </summary>
public sealed class ModelQuotaResetTextConverter : IValueConverter
{
    public static readonly ModelQuotaResetTextConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not DateTimeOffset resetAt)
            return "Reset unknown";

        var delta = resetAt - DateTimeOffset.UtcNow;
        if (delta <= TimeSpan.Zero)
            return "Resetting now";
        if (delta.TotalDays >= 1)
            return $"Refreshes in {(int)delta.TotalDays}d {delta.Hours}h";
        if (delta.TotalHours >= 1)
            return $"Refreshes in {(int)delta.TotalHours}h {delta.Minutes}m";
        return $"Refreshes in {Math.Max(1, delta.Minutes)}m";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

/// <summary>
/// Converts remaining percentage to a status color brush.
/// </summary>
public sealed class RemainingPercentBrushConverter : IValueConverter
{
    public static readonly RemainingPercentBrushConverter Instance = new();

    private static readonly Brush Green = new SolidColorBrush(Color.FromRgb(74, 222, 128));
    private static readonly Brush Yellow = new SolidColorBrush(Color.FromRgb(251, 191, 36));
    private static readonly Brush Red = new SolidColorBrush(Color.FromRgb(248, 113, 113));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not double remaining)
            return Red;

        return remaining switch
        {
            <= 10 => Red,
            <= 30 => Yellow,
            _ => Green,
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
