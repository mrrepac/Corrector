using System.Diagnostics;
using System.Globalization;

namespace Corrector;

// CodeMirror restores the original selection on Undo, but Ctrl+C at a bare caret
// copies the whole line too. The anchor and head distinguish those two cases.
internal sealed class ObsidianSelection(CutGesture gesture, uint sequence) : KeyboardSelection(gesture, sequence)
{
    const ushort CopyKey = 0x43, InsertKey = 0x2D;
    ushort copyKey = CopyKey;

    protected override async Task<string> Copy()
    {
        string? text = await TryCopy(copyKey);
        if (text != null) return text;

        // Retry at the same selection. Electron also supports Ctrl+Insert when
        // an editor command consumes Ctrl+C. Give a busy renderer time to reply.
        ushort alternate = copyKey == CopyKey ? InsertKey : CopyKey;
        text = await TryCopy(alternate, polls:160);
        if (text == null)
            throw new InvalidOperationException("Obsidian не передал текст через Ctrl+C и Ctrl+Insert.");
        copyKey = alternate;
        return text;
    }

    internal static bool IsObsidian(IntPtr window)
    {
        using var process = Process.GetProcessById((int)Native.ProcessOf(window));
        return process.ProcessName.Equals("Obsidian", StringComparison.OrdinalIgnoreCase);
    }

    // One Shift+Left either extends a backward selection by a grapheme or
    // shortens a forward one. A bare caret can only select one grapheme.
    internal static bool ProvesSelection(string cut,string left)
    {
        if (left==cut || left.Length==0 || StringInfo.GetNextTextElementLength(left)==left.Length) return false;
        if (left.EndsWith(cut,StringComparison.Ordinal) && left.Length-cut.Length==StringInfo.GetNextTextElementLength(left)) return true;
        return cut.StartsWith(left,StringComparison.Ordinal) && left.Length<cut.Length &&
            StringInfo.GetNextTextElementLength(cut.AsSpan(left.Length))==cut.Length-left.Length;
    }

    internal static bool IsBareCaret(string cut, string left) =>
        left.Length > 0 && StringInfo.GetNextTextElementLength(left) == left.Length &&
        left != cut && (new StringInfo(cut).LengthInTextElements >= 3 ||
            (cut.Length > 0 && !cut.StartsWith(left, StringComparison.Ordinal)));

    internal async Task Prepare(string cut)
    {
        cut=cut.ReplaceLineEndings("\n");
        // Undo should already have restored the range. Check it before moving
        // either endpoint. A native whole-line cut has no embedded line break.
        if (await Copy() != cut)
            throw new InvalidOperationException("Obsidian не восстановил вырезанный текст.");
        if (cut.Contains('\n'))
        {
            Text=cut;
            ExplicitSelection=true;
            return;
        }
        Key(Left,shift:true);
        string left=await Copy();
        if (IsBareCaret(cut,left))
        {
            await SelectBlockBackwards(left);
            EnterAfterPaste = Text[(Text.LastIndexOf('\n') + 1)..] == cut;
            return;
        }
        if (ProvesSelection(cut,left))
        {
            Key(Right,shift:true);
            if (await Copy()!=cut) throw new InvalidOperationException("Не удалось подтвердить выделение в Obsidian.");
            Text=cut;
            ExplicitSelection=true;
            return;
        }
        // An ambiguous short range can also be a native whole-line cut.
        // Never copy the rest of the note to disambiguate it.
        if (left != cut)
        {
            Key(Right,shift:true);
            if (await Copy() == cut)
            {
                Key(Right,shift:true);
                var right = await Copy();
                if (right.StartsWith(cut,StringComparison.Ordinal) && right.Length > cut.Length &&
                    StringInfo.GetNextTextElementLength(right.AsSpan(cut.Length)) == right.Length-cut.Length)
                {
                    Key(Left,shift:true);
                    if (await Copy() == cut) { Text=cut; ExplicitSelection=true; return; }
                }
            }
        }
        throw new InvalidOperationException("Границы текста неоднозначны. Выдели фрагмент вручную и повтори.");
    }

    internal override async Task<bool> ReplaceVerified(string replacement, CancellationToken token)
    {
        if (await Copy()!=Text) {RestoreClipboard(Text);return false;}
        token.ThrowIfCancellationRequested();
        Check();
        string owner=Guid.NewGuid().ToString("N");
        ClipboardText.Write(replacement,owner);
        clipboard=Native.GetClipboardSequenceNumber();
        try
        {
            Key(0x56,ctrl:true);
            await Task.Delay(100,token);
            string inserted=replacement.ReplaceLineEndings("\n");
            int[] elements = StringInfo.ParseCombiningCharacters(inserted);
            if (elements.Length == 0) return false;
            Key(Left,shift:true);
            if (await Copy()!=inserted[elements[^1]..]) return false;
            await MoveCharacters(inserted[..elements[^1]],true,true);
            if (await Copy()!=inserted) return false;
            Key(Right);
            token.ThrowIfCancellationRequested();
            if (EnterAfterPaste) Key(0x0D);
            return true;
        }
        finally {ClipboardText.RestoreIfOwned(Text,clipboard,owner);}
    }
}
