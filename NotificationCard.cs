using System.Runtime.InteropServices;
using W = System.Windows;
using UI = System.Windows.Controls;
using M = System.Windows.Media;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Corrector;

internal enum NoticeKind { Info, Working, Success, Warning }

internal sealed class NotificationCard : IDisposable
{
    readonly W.Window window;
    readonly UI.TextBlock heading = new() { FontSize = 15, FontWeight = W.FontWeights.SemiBold };
    readonly UI.TextBlock detail = new() { FontSize = 13, LineHeight = 19, TextWrapping = W.TextWrapping.Wrap,
        TextTrimming = W.TextTrimming.CharacterEllipsis, Margin = new W.Thickness(0, 2, 0, 0) };
    readonly System.Windows.Shapes.Path glyph = new() { StrokeThickness = 1.8, StrokeStartLineCap = M.PenLineCap.Round,
        StrokeEndLineCap = M.PenLineCap.Round, StrokeLineJoin = M.PenLineJoin.Round };
    readonly UI.Border badge, card;
    readonly DispatcherTimer lifetime = new();
    bool fading;
    nint handle;

    [DllImport("user32.dll")] static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);

    internal NotificationCard(Action? onClick = null)
    {
        var copy = new UI.StackPanel();
        copy.Children.Add(heading);
        copy.Children.Add(detail);
        var layout = new UI.Grid();
        layout.ColumnDefinitions.Add(new() { Width = W.GridLength.Auto });
        layout.ColumnDefinitions.Add(new());
        var icon = new UI.Canvas { Width = 24, Height = 24 };
        icon.Children.Add(glyph);
        badge = new UI.Border { Width = 36, Height = 36, CornerRadius = new W.CornerRadius(12), Child = icon,
            HorizontalAlignment = W.HorizontalAlignment.Left, VerticalAlignment = W.VerticalAlignment.Center,
            Padding = new W.Thickness(6), Margin = new W.Thickness(0, 0, 14, 0) };
        layout.Children.Add(badge);
        UI.Grid.SetColumn(copy, 1);
        layout.Children.Add(copy);
        card = CardTheme.Frame(layout, new W.Thickness(16, 12, 20, 13));
        heading.SetResourceReference(UI.TextBlock.ForegroundProperty, "CardText");
        detail.SetResourceReference(UI.TextBlock.ForegroundProperty, "CardSecondary");
        badge.SetResourceReference(UI.Border.BackgroundProperty, "CardTint");
        glyph.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "CardAccent");
        window = new W.Window { WindowStyle = W.WindowStyle.None, ResizeMode = W.ResizeMode.NoResize,
            AllowsTransparency = true, Background = M.Brushes.Transparent, ShowInTaskbar = false,
            ShowActivated = false, Topmost = true, Focusable = false, SizeToContent = W.SizeToContent.Height,
            Width = 380, FontFamily = new M.FontFamily("Segoe UI"), UseLayoutRounding = true,
            Title = "Corrector — уведомление", Content = card };
        window.SourceInitialized += (_, _) => {
            var helper = new WindowInteropHelper(window);
            handle = helper.Handle;
            var source = HwndSource.FromHwnd(handle);
            source.AddHook(NoActivate);
            // The notification stays out of Alt+Tab and leaves mouse input with
            // the editor unless the legacy editor supplied an explicit action.
            int style = GetWindowLong(handle, -20) | 0x08000000 | 0x80;
            SetWindowLong(handle, -20, onClick == null ? style | 0x20 : style);
        };
        if (onClick != null) card.MouseLeftButtonUp += (_, _) => { Hide(); onClick(); };
        lifetime.Tick += (_, _) => {
            if (fading || !Animate) { Hide(); return; }
            fading = true;
            Fade(0, 160);
            lifetime.Interval = TimeSpan.FromMilliseconds(180);
        };
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] static extern int GetWindowLong(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] static extern int SetWindowLong(nint hwnd, int index, int value);
    static nint NoActivate(nint hwnd, int message, nint w, nint l, ref bool handled)
    {
        if (message != 0x0021) return 0;
        handled = true;
        return 3; // MA_NOACTIVATE
    }

    static bool Animate => W.SystemParameters.ClientAreaAnimation && !SystemInformation.HighContrast;

    internal void Present(string title, string message, NoticeKind kind = NoticeKind.Info)
    {
        bool warning = kind == NoticeKind.Warning;
        CardTheme.Apply(card, warning);
        glyph.Data = M.Geometry.Parse(kind switch {
            NoticeKind.Success => "M 4.8,12 L 9.6,16.8 19.3,7.2",
            NoticeKind.Working => "M 4,19 L 8.5,18 19,7.5 16.5,5 6,15.5 Z M 14.5,7 L 17,9.5",
            NoticeKind.Warning => "M 12,5.8 L 12,13.5 M 12,17.4 L 12,17.6",
            _ => "M 12,5.8 L 12,6 M 10,10 L 12,10 12,18 M 10,18 L 14,18"
        });
        heading.Text = title;
        detail.Text = message;
        var screen = Screen.FromHandle(Native.GetForegroundWindow()).WorkingArea;
        bool appearing = !window.IsVisible;
        new WindowInteropHelper(window).EnsureHandle();
        // Move the hidden window to the target monitor before measuring in DIPs.
        if (Screen.FromHandle(handle).WorkingArea != screen)
            SetWindowPos(handle, 0, screen.Left + screen.Width / 2, screen.Bottom - 120, 0, 0, 0x15);
        var dpi = M.VisualTreeHelper.GetDpi(window);
        window.Width = Math.Min(390, screen.Width / dpi.DpiScaleX);
        detail.MaxHeight = Math.Max(38, screen.Height / dpi.DpiScaleY - 130);
        card.Measure(new W.Size(window.Width, double.PositiveInfinity));
        int width = (int)Math.Ceiling(window.Width * dpi.DpiScaleX);
        int height = (int)Math.Ceiling(card.DesiredSize.Height * dpi.DpiScaleY);
        SetWindowPos(handle, 0, screen.Left + (screen.Width - width) / 2,
            screen.Bottom - height - (int)(10 * dpi.DpiScaleY), 0, 0, 0x15);
        lifetime.Stop();
        fading = false;
        window.BeginAnimation(W.UIElement.OpacityProperty, null);
        window.Opacity = appearing && Animate ? 0 : 1;
        if (appearing) window.Show();
        if (window.Opacity == 0) Fade(1, 130);
        lifetime.Interval = TimeSpan.FromMilliseconds(Math.Clamp(2600 + message.Length * 42, warning ? 6500 : 3500, 12000));
        lifetime.Start();
    }

    void Fade(double to, int milliseconds) => window.BeginAnimation(W.UIElement.OpacityProperty,
        new DoubleAnimation(to, TimeSpan.FromMilliseconds(milliseconds)));
    void Hide() { lifetime.Stop(); window.Hide(); }
    public void Dispose() { lifetime.Stop(); window.Close(); }
}
