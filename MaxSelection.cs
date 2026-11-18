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

        if (cut != null)
        {
            // Qt can collapse the selection on Undo. Compare only the adjacent
            // range with the known cut, and require exactly one matching side.
            async Task<string?> Neighbor(bool backwards)
            {
                Key(backwards ? Left : Right, shift:true);
                var candidate = await TryCopy();
                if (!string.IsNullOrEmpty(candidate)) Key(backwards ? Right : Left);
                return candidate;
            }
            string? left = await Neighbor(true), right = await Neighbor(false);
            bool before = !string.IsNullOrEmpty(left) && cut.EndsWith(left, StringComparison.Ordinal);
            bool after = !string.IsNullOrEmpty(right) && cut.StartsWith(right, StringComparison.Ordinal);
            if (before == after) throw new InvalidOperationException("Выдели фрагмент в MAX и повтори.");
            await MoveCharacters(cut, before, true);
            if (await Copy() != cut) throw new InvalidOperationException("Выделение в MAX изменилось.");
            Text = cut;
            return;
        }

        Key(Right, shift:true);
        string? next = await TryCopy();
        if (!string.IsNullOrEmpty(next))
        {
            Key(Left);
            EnterAfterPaste = DraftField.AtLineEnd(next.AsSpan());
        }
        Key(Left, shift:true);
        var previous = await TryCopy();
        if (string.IsNullOrEmpty(previous) || System.Globalization.StringInfo.GetNextTextElementLength(previous) != previous.Length)
            throw new InvalidOperationException("Не удалось подтвердить курсор. Выдели текст в MAX и повтори.");
        // When Shift+Right at EOF cannot move, Shift+Left selects one grapheme.
        // Inside a line the same two keys collapse the range, even if Copy timed out.
        if (string.IsNullOrEmpty(next)) EnterAfterPaste = true;
        await SelectBlockBackwards(previous);
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
