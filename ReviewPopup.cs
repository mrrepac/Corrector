using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Interop;
using System.Windows.Markup;
using D = System.Windows.Documents;
using Input = System.Windows.Input;
using M = System.Windows.Media;
using UI = System.Windows.Controls;
using W = System.Windows;

namespace Corrector;

internal sealed class ReviewPopup : W.Window
{
    readonly UI.RichTextBox before;

    internal ReviewPopup(string original, string corrected, bool needsReview)
    {
        Title = needsReview ? "Проверь исправление перед вставкой" : "Последняя корректура";
        ShowActivated = !needsReview;
        ShowInTaskbar = true;
        UseLayoutRounding = true;
        FontFamily = new M.FontFamily("Segoe UI");
        FontSize = 14;
        Width = 680;
        Height = 530;
        SetResourceReference(BackgroundProperty, "CardSurface");

        using var stream = typeof(ReviewPopup).Assembly.GetManifestResourceStream("Corrector.ReviewCard.xaml")
            ?? throw new InvalidOperationException("Не найден макет просмотра исправлений.");
        var layout = (UI.Grid)XamlReader.Load(stream);
        Content = layout;
        before = (UI.RichTextBox)layout.FindName("Before");
        var after = (UI.RichTextBox)layout.FindName("After");
        var status = (UI.TextBlock)layout.FindName("Status");
        var copy = (UI.Button)layout.FindName("Copy");
        var diff = ReviewDiff.Compare(original, corrected);
        status.Text = original == corrected ? "Тексты совпадают. Исправлений нет." :
            "Удалённое зачёркнуто, добавленное подчёркнуто.";

        // Pure insertions and deletions have no highlighted run on one side.
        // Start both panes at the same source position preceding the first edit.
        int firstEdit = Math.Min(UnchangedPrefix(diff.Before), UnchangedPrefix(diff.After));
        var beforePosition = Fill(before, original, diff.Before, firstEdit, removed: true);
        var afterPosition = Fill(after, corrected, diff.After, firstEdit, removed: false);
        if (original != corrected && firstEdit > 0)
            ContentRendered += (_, _) => { Reveal(before, beforePosition); Reveal(after, afterPosition); };

        copy.Click += (_, _) => {
            try { ClipboardText.Write(corrected); copy.Content = "Скопировано"; }
            catch (Exception ex) { W.MessageBox.Show(this, ex.Message, "Не удалось скопировать текст"); }
        };
        PreviewKeyDown += (_, e) => {
            if (e.Key == Input.Key.Escape) { e.Handled = true; Close(); }
        };
        System.Windows.Forms.Integration.ElementHost.EnableModelessKeyboardInterop(this);
    }

    static int UnchangedPrefix(IReadOnlyList<ReviewSpan> spans) =>
        spans.TakeWhile(span => !span.Changed).Sum(span => span.Text.Length);

    sealed record Piece(D.Inline Inline, int Start, int Length);

    static D.TextPointer Fill(UI.RichTextBox box, string source, IReadOnlyList<ReviewSpan> spans, int firstEdit, bool removed)
    {
        var paragraph = new D.Paragraph { Margin = new W.Thickness(0) };
        var document = new D.FlowDocument(paragraph) {
            PagePadding = new W.Thickness(0), FontFamily = box.FontFamily, FontSize = box.FontSize
        };
        document.SetResourceReference(D.TextElement.ForegroundProperty, "CardText");
        var pieces = new List<Piece>();
        D.TextPointer? firstPosition = null;
        int offset = 0;
        foreach (var span in spans)
        {
            foreach (Match part in Regex.Matches(span.Text, @"[^\r\n\f\u0085\u2028\u2029]+|\r\n|[\r\n\f\u0085\u2028\u2029]"))
            {
                D.Inline inline;
                if (part.Value[0] is '\r' or '\n' or '\f' or '\u0085' or '\u2028' or '\u2029')
                {
                    if (span.Changed)
                    {
                        var marker = new D.Run("↵") { ToolTip = removed ? "Удалён перенос строки" : "Добавлен перенос строки" };
                        Mark(marker, removed);
                        paragraph.Inlines.Add(marker);
                    }
                    inline = new D.LineBreak();
                }
                else
                {
                    var run = new D.Run(part.Value);
                    if (span.Changed) Mark(run, removed);
                    inline = run;
                }
                paragraph.Inlines.Add(inline);
                pieces.Add(new(inline, offset, part.Length));
                if (firstPosition == null && firstEdit >= offset && firstEdit < offset + part.Length)
                    firstPosition = inline is D.Run ? inline.ContentStart.GetPositionAtOffset(firstEdit - offset) : inline.ElementStart;
                offset += part.Length;
            }
        }
        box.Document = document;
        // Copy the original characters underlying the selection, excluding the
        // visual ↵ markers and WPF's implicit final paragraph terminator.
        box.CommandBindings.Add(new Input.CommandBinding(Input.ApplicationCommands.Copy, (_, e) => {
            int start = SourceOffset(box.Selection.Start, pieces, source.Length);
            int end = SourceOffset(box.Selection.End, pieces, source.Length);
            try { if (end > start) ClipboardText.Write(source[start..end]); }
            catch (Exception ex) { W.MessageBox.Show(W.Window.GetWindow(box), ex.Message, "Не удалось скопировать текст"); }
            e.Handled = true;
        }, (_, e) => { e.CanExecute = !box.Selection.IsEmpty; e.Handled = true; }));
        return firstPosition ?? paragraph.ContentEnd;
    }

    static int SourceOffset(D.TextPointer position, List<Piece> pieces, int length)
    {
        int low = 0, high = pieces.Count;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            if (pieces[middle].Inline.ElementEnd.CompareTo(position) <= 0) low = middle + 1;
            else high = middle;
        }
        if (low == pieces.Count) return length;
        var piece = pieces[low];
        if (position.CompareTo(piece.Inline.ContentStart) <= 0) return piece.Start;
        if (piece.Inline is D.LineBreak) return piece.Start + piece.Length;
        return piece.Start + Math.Min(piece.Length, piece.Inline.ContentStart.GetOffsetToPosition(position));
    }

    static void Mark(D.Run run, bool removed)
    {
        run.SetResourceReference(D.TextElement.BackgroundProperty, "CardTint");
        run.SetResourceReference(D.TextElement.ForegroundProperty, "CardAccent");
        run.TextDecorations = removed ? W.TextDecorations.Strikethrough : W.TextDecorations.Underline;
    }

    static void Reveal(UI.RichTextBox box, D.TextPointer position)
    {
        var rect = position.GetCharacterRect(D.LogicalDirection.Forward);
        if (!rect.IsEmpty && (rect.Top < 0 || rect.Bottom > box.ViewportHeight))
            box.ScrollToVerticalOffset(Math.Max(0, box.VerticalOffset + rect.Top - box.ViewportHeight / 3));
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(nint hwnd, int attr, ref int value, int size);
    [DllImport("user32.dll")]
    static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);

    protected override void OnSourceInitialized(EventArgs e)
    {
        // Automatic reviews must be visible above the editor while it still owns
        // keyboard input. Once the user opens the review it behaves normally.
        Topmost = !ShowActivated;
        if (Topmost) Activated += (_, _) => Topmost = false;
        base.OnSourceInitialized(e);
        var handle = new WindowInteropHelper(this).Handle;
        void Theme()
        {
            CardTheme.Apply(this);
            CardTheme.Apply(before, warning: true);
            int dark = ((M.SolidColorBrush)Resources["CardSurface"]).Color.R < 128 ? 1 : 0;
            DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
        }
        Theme();
        HwndSource.FromHwnd(handle).AddHook((nint hwnd, int msg, nint w, nint l, ref bool handled) => {
            if (msg is 0x001A or 0x031A or 0x0015) Theme();
            return 0;
        });
        var area = Screen.FromHandle(Native.GetForegroundWindow()).WorkingArea;
        if (Screen.FromHandle(handle).WorkingArea != area)
            SetWindowPos(handle, 0, area.Left, area.Top, 0, 0, 0x15);
        var dpi = M.VisualTreeHelper.GetDpi(this);
        Width = Math.Min(Width, area.Width / dpi.DpiScaleX);
        Height = Math.Min(Height, area.Height / dpi.DpiScaleY);
        MinWidth = Math.Min(360, Width);
        MinHeight = Math.Min(280, Height);
        SetWindowPos(handle, 0, area.Left + (area.Width - (int)(Width * dpi.DpiScaleX)) / 2,
            area.Top + (area.Height - (int)(Height * dpi.DpiScaleY)) / 2, 0, 0, 0x15);
    }
}
