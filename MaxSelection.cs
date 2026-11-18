namespace Corrector;

internal sealed class MaxSelection(CutGesture gesture, uint sequence) : KeyboardSelection(gesture, sequence)
{
    internal async Task Prepare(string? cut)
    {
        cut = cut?.ReplaceLineEndings("\n");
        ExplicitSelection = cut != null;
        // Qt can restore a cut with either a selection or a caret at one edge.
        if (await TryCopy() is string selected && selected.Length > 0)
        {
            if (cut != null && selected != cut) throw new InvalidOperationException("MAX восстановил другое выделение.");
            Text = selected;
            ExplicitSelection = true;
            return;
        }

        Key(Home, ctrl:true, shift:true);
        string prefix = await TryCopy() ?? "";
        if (cut == null)
        {
            int start = DraftField.BlockStart(prefix);
            Text = prefix[start..];
            if (string.IsNullOrWhiteSpace(Text)) throw new InvalidOperationException("Перед курсором пустая строка.");
            // Qt does not expose this caret to UIA. The prefix alone cannot
            // distinguish end of line from a caret just before more message text.
            Key(0x41, ctrl:true);
            string document = await Copy();
            if (!document.StartsWith(prefix, StringComparison.Ordinal))
                throw new InvalidOperationException("Текст в MAX изменился.");
            string suffix = document[prefix.Length..];
            EnterAfterPaste = DraftField.AtLineEnd(suffix);
            if (start == 0 && suffix.Length == 0) return; // Select All is the target.
            Key(End, ctrl:true);
            await MoveCharacters(suffix, true, false);
            if (start == 0) Key(Home, ctrl:true, shift:true);
            else await MoveCharacters(Text, true, true);
        }
        else
        {
            if (prefix.Length != 0) Key(Right);
            Key(End, ctrl:true, shift:true);
            string suffix = await TryCopy() ?? "";
            if (suffix.Length != 0) Key(Left);
            bool before = prefix.EndsWith(cut, StringComparison.Ordinal);
            bool after = suffix.StartsWith(cut, StringComparison.Ordinal);
            if (before == after) throw new InvalidOperationException("Не удалось определить границы вырезанного текста в MAX.");
            Text = cut;
            await MoveCharacters(cut, before, true);
        }
        if (await Copy() != Text) throw new InvalidOperationException("MAX выделил другой фрагмент.");
    }

    internal override async Task<bool> ReplaceVerified(string replacement, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (await Copy() != Text) return false;
        Check();
        string owner = Guid.NewGuid().ToString("N");
        ClipboardText.Write(replacement, owner);
        clipboard = Native.GetClipboardSequenceNumber();
        try
        {
            Key(0x56, ctrl:true);
            // A successful Qt paste leaves a collapsed caret. Copy at that caret
            // must produce nothing, including when the correction is unchanged.
            if (!string.IsNullOrEmpty(await TryCopy())) return false;
            string inserted = replacement.ReplaceLineEndings("\n");
            await MoveCharacters(inserted, true, true);
            if (await Copy() != inserted) return false;
            Key(Right);
            token.ThrowIfCancellationRequested();
            if (EnterAfterPaste) Key(0x0D);
            return true;
        }
        finally { ClipboardText.RestoreIfOwned(Text, clipboard, owner); }
    }
}
