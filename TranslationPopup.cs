using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Markup;
using W = System.Windows;
using UI = System.Windows.Controls;
using M = System.Windows.Media;
using Input = System.Windows.Input;

namespace Corrector;

internal sealed class TranslationPopup : W.Window
{
    readonly UI.Border card;
    readonly UI.TextBox body;
    readonly UI.Button copy;
    readonly UI.TextBlock caption;
    nint handle;

    [DllImport("user32.dll")]
    static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);

    internal TranslationPopup(string title)
    {
        Title = title;
        WindowStyle = W.WindowStyle.None;
        ResizeMode = W.ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = M.Brushes.Transparent;
        ShowActivated = false;
        ShowInTaskbar = false;
        Topmost = true;
        UseLayoutRounding = true;
        FontFamily = new M.FontFamily("Segoe UI");

        using var stream = typeof(TranslationPopup).Assembly.GetManifestResourceStream("Corrector.TranslationCard.xaml")
            ?? throw new InvalidOperationException("Не найден макет перевода.");
        var layout = (UI.Grid)XamlReader.Load(stream);
        card = CardTheme.Frame(layout, new W.Thickness(20, 16, 20, 16));
        Content = card;
        CardTheme.Apply(card);
        ((UI.TextBlock)layout.FindName("Heading")).Text = title;
        body = (UI.TextBox)layout.FindName("Body");
        copy = (UI.Button)layout.FindName("Copy");
        caption = (UI.TextBlock)layout.FindName("CopyCaption");
        copy.Click += (_, _) => {
            try { ClipboardText.Write(body.Text); caption.Text = "Скопировано"; }
            catch (Exception ex) { W.MessageBox.Show(this, ex.Message, Title); }
        };
        ((UI.Button)layout.FindName("Dismiss")).Click += (_, _) => Close();
        ((UI.Grid)layout.FindName("Header")).MouseLeftButtonDown += (_, e) => {
            if (e.ButtonState == Input.MouseButtonState.Pressed) DragMove();
        };
        ((UI.Primitives.Thumb)layout.FindName("Grip")).DragDelta += (_, e) => {
            var area = Screen.FromHandle(handle).WorkingArea;
            var origin = PointToScreen(new W.Point());
            var dpi = M.VisualTreeHelper.GetDpi(this);
            Width = Math.Clamp(Width + e.HorizontalChange, MinWidth, Math.Max(MinWidth, (area.Right - origin.X) / dpi.DpiScaleX));
            Height = Math.Clamp(Height + e.VerticalChange, MinHeight, Math.Max(MinHeight, (area.Bottom - origin.Y) / dpi.DpiScaleY));
        };
        PreviewKeyDown += (_, e) => { if (e.Key == Input.Key.Escape) { e.Handled = true; Close(); } };
        System.Windows.Forms.Integration.ElementHost.EnableModelessKeyboardInterop(this);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        handle = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(handle).AddHook((nint hwnd, int msg, nint w, nint l, ref bool handled) => {
            if (msg is 0x001A or 0x031A or 0x0015) CardTheme.Apply(card);
            return 0;
        });
        var point = System.Windows.Forms.Cursor.Position;
        var area = Screen.FromPoint(point).WorkingArea;
        if (Screen.FromHandle(handle).WorkingArea != area)
            SetWindowPos(handle, 0, point.X, point.Y, 0, 0, 0x15);
        var dpi = M.VisualTreeHelper.GetDpi(this);
        Fit(area, point.X + 12 * dpi.DpiScaleX, point.Y + 18 * dpi.DpiScaleY);
    }

    double MeasureHeight(double width)
    {
        card.Measure(new W.Size(width, double.PositiveInfinity));
        return Math.Ceiling(card.DesiredSize.Height);
    }

    void Fit(Rectangle area, double originX, double originY)
    {
        var dpi = M.VisualTreeHelper.GetDpi(this);
        double availableWidth = Math.Floor(area.Width / dpi.DpiScaleX);
        double availableHeight = Math.Floor(area.Height / dpi.DpiScaleY);
        MinWidth = Math.Min(354, availableWidth);
        MinHeight = Math.Min(190, availableHeight);

        string text = body.Text;
        var sample = new UI.TextBlock { Text = text.Length <= 2000 ? text : text[..2000],
            FontFamily = FontFamily, FontSize = body.FontSize };
        sample.Measure(new W.Size(double.PositiveInfinity, double.PositiveInfinity));
        double readingWidth = Math.Min(670, availableWidth);
        double width = Math.Clamp(Math.Ceiling(sample.DesiredSize.Width) + 84, MinWidth, readingWidth);
        double height = MeasureHeight(width);
        if (height > availableHeight && width < availableWidth)
        {
            width = Math.Min(availableWidth, Math.Ceiling(width * height / availableHeight));
            height = MeasureHeight(width);
            if (height > availableHeight && width < availableWidth)
            {
                width = availableWidth;
                height = MeasureHeight(width);
            }
            if (height > availableHeight) width = readingWidth;
        }
        Width = width;
        Height = Math.Clamp(height, MinHeight, availableHeight);
        int right = area.Right - (int)Math.Ceiling(Width * dpi.DpiScaleX);
        int bottom = area.Bottom - (int)Math.Ceiling(Height * dpi.DpiScaleY);
        SetWindowPos(handle, 0, Math.Clamp((int)originX, area.Left, Math.Max(area.Left, right)),
            Math.Clamp((int)originY, area.Top, Math.Max(area.Top, bottom)), 0, 0, 0x15);
    }

    internal void Display(string text, bool complete = false)
    {
        body.Text = text.ReplaceLineEndings();
        body.Select(0, 0);
        body.ScrollToHome();
        copy.IsEnabled = complete;
        caption.Text = "Скопировать";
        if (IsVisible)
        {
            var origin = PointToScreen(new W.Point());
            Fit(Screen.FromHandle(handle).WorkingArea, origin.X, origin.Y);
        }
    }
}
