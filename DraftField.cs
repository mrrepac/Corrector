using System.Windows.Automation;
using System.Windows.Automation.Text;

namespace Corrector;

internal sealed class NoDraftFieldException() : InvalidOperationException("Не удалось определить курсор в редактируемом поле.");

internal sealed record DraftField(AutomationElement Element, string Prefix, string Text, bool ExplicitSelection, bool EnterAfterPaste)
{
    internal static bool AtLineEnd(ReadOnlySpan<char> suffix) =>
        suffix.IsEmpty || suffix[0] is '\r' or '\n' or '\u2028' or '\u2029';

    // Walk logical lines. A visual wrap is part of the same paragraph.
    internal static int BlockStart(string prefix)
    {
        int end = prefix.Length;
        while (true)
        {
            int start = end;
            while (start > 0 && prefix[start - 1] is not ('\r' or '\n' or '\u2028' or '\u2029')) start--;
            if (prefix.AsSpan(start, end - start).IsWhiteSpace())
                return end == prefix.Length ? end : end + (prefix.AsSpan(end).StartsWith("\r\n") ? 2 : 1);
            if (start == 0) return 0;
            end = start - 1;
            if (end > 0 && prefix[end] == '\n' && prefix[end - 1] == '\r') end--;
        }
    }

    static TextPatternRange? SingleSelection(TextPattern pattern)
    {
        var ranges = pattern.GetSelection();
        return ranges.Length == 1 ? ranges[0] : null;
    }

    // On web pages focus can be on a link or a text node inside the document.
    internal static string? VisibleSelection(CutGesture gesture)
    {
        var process = Native.ProcessOf(gesture.Window);
        for (var element = AutomationElement.FocusedElement;
             element != null && element.Current.ProcessId == process;
             element = TreeWalker.RawViewWalker.GetParent(element))
        {
            if (element.Current.IsPassword)
                throw new InvalidOperationException("Проверка в поле пароля недоступна.");
            if (element.TryGetCurrentPattern(TextPattern.Pattern, out var supported))
                return SingleSelection((TextPattern)supported)?.GetText(-1);
        }
        return null;
    }

    static string Before(TextPattern pattern, TextPatternRange selection)
    {
        var prefix = pattern.DocumentRange;
        prefix.MoveEndpointByRange(TextPatternRangeEndpoint.End, selection, TextPatternRangeEndpoint.Start);
        return prefix.GetText(-1);
    }

    internal static bool SameText(string left, string right) =>
        left.ReplaceLineEndings("\n") == right.ReplaceLineEndings("\n");

    static TextPatternRange? Adjacent(TextPatternRange caret, string text, bool backwards)
    {
        // Providers count CRLF and compound characters differently. Accept only an exact match.
        string normalized = text.ReplaceLineEndings("\n");
        int[] lengths = [text.Length, normalized.Length, new System.Globalization.StringInfo(normalized).LengthInTextElements];
        foreach (int length in lengths.Distinct())
        {
            var range = caret.Clone();
            range.MoveEndpointByUnit(backwards ? TextPatternRangeEndpoint.Start : TextPatternRangeEndpoint.End,
                TextUnit.Character, backwards ? -length : length);
            if (SameText(range.GetText(-1), text)) return range;
        }
        return null;
    }

    internal static DraftField Select(CutGesture gesture, string? cut, uint clipboard)
    {
        var element = AutomationElement.FocusedElement;
        if (element?.Current.IsPassword == true)
            throw new InvalidOperationException("Проверка в поле пароля недоступна.");
        if (element == null || element.Current.ProcessId != Native.ProcessOf(gesture.Window) ||
            !element.Current.IsEnabled ||
            (element.Current.ControlType != ControlType.Edit && element.Current.ControlType != ControlType.Document) ||
            !element.TryGetCurrentPattern(TextPattern.Pattern, out var supported))
            throw new NoDraftFieldException();
        var pattern = (TextPattern)supported;
        var selection = SingleSelection(pattern) ?? throw new InvalidOperationException("Нужен один курсор или одно выделение.");
        bool writable = element.TryGetCurrentPattern(ValuePattern.Pattern, out var value)
            ? !((ValuePattern)value).Current.IsReadOnly
            : selection.GetAttributeValue(TextPattern.IsReadOnlyAttribute) is false;
        if (!writable) throw new NoDraftFieldException();

        string prefix = Before(pattern, selection);
        string selected = selection.GetText(-1);
        bool explicitSelection = selected.Length > 0;
        bool enterAfterPaste = false;
        if (selected.Length > 0)
        {
            if (cut != null && !SameText(cut, selected))
                throw new InvalidOperationException("Редактор не восстановил исходное выделение.");
        }
        else
        {
            TextPatternRange? block = null;
            if (cut != null)
            {
                var tail = pattern.DocumentRange;
                tail.MoveEndpointByRange(TextPatternRangeEndpoint.Start, selection, TextPatternRangeEndpoint.End);
                string left = prefix.ReplaceLineEndings("\n");
                string right = tail.GetText(-1).ReplaceLineEndings("\n");
                int end = right.IndexOf('\n');
                string line = left[(left.LastIndexOf('\n') + 1)..] + (end < 0 ? right : right[..end]);
                // Whole-line cuts may restore the caret in the middle of that line.
                // Qt also restores explicit cuts with a collapsed caret at one edge.
                if (line != cut.Trim('\r', '\n'))
                {
                    block = Adjacent(selection, cut, true) ?? Adjacent(selection, cut, false) ??
                        throw new InvalidOperationException("Не удалось подтвердить восстановление вырезанного фрагмента.");
                    explicitSelection = true;
                }
            }
            if (block == null)
            {
                if (cut == null)
                {
                    // No native cut and an empty selection identify a bare caret.
                    // Undo of an explicit whole-line selection can look identical
                    // to a line cut in providers that collapse restored selections.
                    var suffix = pattern.DocumentRange;
                    suffix.MoveEndpointByRange(TextPatternRangeEndpoint.Start, selection, TextPatternRangeEndpoint.End);
                    enterAfterPaste = AtLineEnd(suffix.GetText(1));
                }
                int start = BlockStart(prefix);
                selected = prefix[start..];
                if (selected.Length == 0) throw new InvalidOperationException("Перед курсором пустая строка.");
                var range = pattern.DocumentRange;
                range.MoveEndpointByRange(TextPatternRangeEndpoint.End, selection, TextPatternRangeEndpoint.Start);
                block = start == 0 ? range : Adjacent(selection, selected, true) ?? range.FindText(selected, true, false);
                if (block == null || block.CompareEndpoints(TextPatternRangeEndpoint.End, selection, TextPatternRangeEndpoint.Start) != 0 ||
                    !SameText(block.GetText(-1), selected))
                    throw new InvalidOperationException("Не удалось выделить абзац до курсора.");
            }
            selected = block.GetText(-1);
            prefix = Before(pattern, block);
            if (!gesture.IsCurrent || !element.Equals(AutomationElement.FocusedElement) || Native.GetClipboardSequenceNumber() != clipboard)
                throw new InvalidOperationException("Поле, ввод или буфер изменились.");
            block.Select();
        }
        return new DraftField(element, prefix, selected, explicitSelection, enterAfterPaste);
    }

    internal bool CollapseToEnd(CutGesture gesture, uint sequence)
    {
        var pattern = (TextPattern)Element.GetCurrentPattern(TextPattern.Pattern);
        var selection = SingleSelection(pattern);
        if (selection == null || !SameText(selection.GetText(-1), Text) ||
            !SameText(Before(pattern, selection), Prefix)) return false;
        selection.MoveEndpointByRange(TextPatternRangeEndpoint.Start, selection, TextPatternRangeEndpoint.End);
        if (!Element.Equals(AutomationElement.FocusedElement) || !gesture.IsCurrent ||
            sequence != Native.GetClipboardSequenceNumber()) return false;
        selection.Select();
        return true;
    }

    internal bool Matches(string expectedPrefix, string expectedSelection)
    {
        if (!Element.Equals(AutomationElement.FocusedElement)) return false;
        var pattern = (TextPattern)Element.GetCurrentPattern(TextPattern.Pattern);
        var selection = SingleSelection(pattern);
        return selection != null && SameText(selection.GetText(-1), expectedSelection) &&
            SameText(Before(pattern, selection), expectedPrefix) && Element.Equals(AutomationElement.FocusedElement);
    }
}
