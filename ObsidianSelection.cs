using System.Diagnostics;
using System.Globalization;

namespace Corrector;

// CodeMirror restores the original selection on Undo, but Ctrl+C at a bare caret
// copies the whole line too. The anchor and head distinguish those two cases.
internal sealed class ObsidianSelection(CutGesture gesture, uint sequence) : KeyboardSelection(gesture, sequence)
{
    string? note;
    string? beforeBlock;
    int start, end;
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

    async Task<string> All()
    {
        Key(0x41, ctrl:true);
        return await Copy();
    }

    // For a collapsed range CodeMirror copies the current line. For every other
    // range it copies the Markdown between its two endpoints.
    static string Copied(string doc, int anchor, int head)
    {
        if (anchor!=head) return doc[Math.Min(anchor,head)..Math.Max(anchor,head)];
        int l=anchor==0 ? 0 : doc.LastIndexOf('\n',anchor-1)+1;
        int r=doc.IndexOf('\n',anchor);
        return doc[l..(r<0?doc.Length:r)];
    }

    static int Next(string doc,int pos) =>
        pos==doc.Length ? pos : pos+StringInfo.GetNextTextElementLength(doc.AsSpan(pos));

    internal sealed record Probe(string Left,string Right,string Prefix,string First,string Suffix);

    internal static IEnumerable<(int Anchor,int Head)> Candidates(string doc,string cut,Probe probe)
    {
        int first=Next(doc,0);
        var chars=StringInfo.ParseCombiningCharacters(doc);
        foreach (int a in new[]{0,probe.Prefix.Length,doc.Length}.Distinct())
        {
            if (a>doc.Length || Copied(doc,a,0)!=probe.Prefix || Copied(doc,a,first)!=probe.First ||
                Copied(doc,a,doc.Length)!=probe.Suffix) continue;
            foreach (int h in new[]{a,a-cut.Length,a+cut.Length}.Distinct())
            {
                if (h<0 || h>doc.Length || Copied(doc,a,h)!=cut) continue;
                int i=Array.BinarySearch(chars,h);
                if (i<0 && h!=doc.Length) continue;
                int left=h==0 ? 0 : chars[(h==doc.Length ? chars.Length : i)-1];
                int right=Next(doc,Next(doc,left));
                if (Copied(doc,a,left)==probe.Left && Copied(doc,a,right)==probe.Right) yield return (a,h);
            }
        }
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
        new StringInfo(cut).LengthInTextElements >= 3 && left != cut;

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
        Key(Right,shift:true);
        // A range of three or more graphemes cannot shrink to one in one step.
        // At a bare caret Shift+Left selects the preceding grapheme instead.
        if (IsBareCaret(cut,left))
        {
            Key(Home, ctrl:true, shift:true);
            string toCaret = await Copy();
            int blockStart = DraftField.BlockStart(toCaret);
            Text = toCaret[blockStart..];
            if (string.IsNullOrWhiteSpace(Text))
                throw new InvalidOperationException("Перед курсором пустая строка.");
            beforeBlock = toCaret[..blockStart];
            EnterAfterPaste = toCaret[(toCaret.LastIndexOf('\n') + 1)..] == cut;
            if (blockStart != 0)
            {
                Key(Right);
                await MoveCharacters(Text, true, true);
                if (await Copy() != Text) throw new InvalidOperationException("Obsidian выделил другой фрагмент.");
            }
            return;
        }
        if (ProvesSelection(cut,left))
        {
            if (await Copy()!=cut) throw new InvalidOperationException("Не удалось восстановить точное выделение в Obsidian.");
            Text=cut;
            ExplicitSelection=true;
            return;
        }
        Key(Right,shift:true);
        string right=await Copy();
        Key(Home,ctrl:true,shift:true);
        string prefix=await Copy();
        Key(Right,shift:true);
        string first=await Copy();
        Key(End,ctrl:true,shift:true);
        string suffix=await Copy();
        var probe=new Probe(left,right,prefix,first,suffix);

        // At either document boundary the editor may supply a whole line instead
        // of the empty prefix/suffix. Read the entire note only to break a tie.
        var matches=(from doc in new[]{prefix+suffix,prefix,suffix}.Distinct()
                     from pos in Candidates(doc,cut,probe)
                     select (Doc:doc,pos.Anchor,pos.Head)).ToList();
        bool selectedAll=false;
        if (matches.Select(m=>m.Doc).Distinct().Skip(1).Any())
        {
            string actual=await All();
            matches.RemoveAll(m=>m.Doc!=actual);
            selectedAll=true;
        }
        if (matches.Count==0) throw new InvalidOperationException("Не удалось сверить выделение в Obsidian.");
        var ranges=matches.Select(m=>(Start:m.Anchor==m.Head ? DraftField.BlockStart(m.Doc[..m.Head]) : Math.Min(m.Anchor,m.Head),
                                      End:Math.Max(m.Anchor,m.Head))).Distinct().ToList();
        if (ranges.Count!=1) throw new InvalidOperationException("Не удалось однозначно определить выделение в Obsidian.");
        note=matches[0].Doc;
        (start,end)=ranges[0];
        ExplicitSelection=matches.All(m=>m.Anchor!=m.Head);
        EnterAfterPaste=matches.All(m=>m.Anchor==m.Head && DraftField.AtLineEnd(m.Doc.AsSpan(m.Head)));
        int anchor=matches[0].Anchor;
        Text=note[start..end];

        // The last probe left an anchored selection through EOF. A subsequent
        // Select All, or several equivalent anchors, requires an absolute boundary.
        if (selectedAll || matches.Any(m=>m.Anchor!=anchor))
        {
            bool nearerStart=start<=note.Length-end;
            Key(nearerStart?Home:End,ctrl:true);
            string distance=nearerStart ? note[..start] : note[end..];
            await MoveCharacters(distance,!nearerStart,false);
            anchor=nearerStart ? start : end;
        }
        else Key(anchor==note.Length ? End : Left,ctrl:anchor==note.Length);
        if (string.IsNullOrWhiteSpace(Text)) throw new InvalidOperationException("Перед курсором пустая строка.");
        await MoveCharacters(Text,anchor==end,true);
        if (await Copy()!=Text) throw new InvalidOperationException("Obsidian выделил другой фрагмент.");
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
            if (beforeBlock != null)
            {
                Key(Home, ctrl:true, shift:true);
                if (await Copy() != beforeBlock + inserted) return false;
                Key(Right);
            }
            else if (note==null)
            {
                // Confirm both a collapsed paste caret and the exact new text.
                int[] elements=StringInfo.ParseCombiningCharacters(inserted);
                Key(Left,shift:true);
                if (await Copy()!=inserted[elements[^1]..]) return false;
                if (elements.Length>128)
                {
                    // Keep the anchor at the paste caret. One range-to-start
                    // command avoids flooding CodeMirror with arrow events.
                    Key(Home,ctrl:true,shift:true);
                    if (!(await Copy()).EndsWith(inserted,StringComparison.Ordinal)) return false;
                }
                else if (elements.Length>1)
                {
                    await MoveCharacters(inserted[..elements[^1]],true,true);
                    if (await Copy()!=inserted) return false;
                }
                Key(Right);
            }
            else
            {
                string prefix=note[..start]+inserted;
                string suffix=note[end..];
                Key(Home,ctrl:true,shift:true);
                if (await Copy()!=prefix) return false;
                Key(End,ctrl:true,shift:true);
                string expectedSuffix=suffix.Length==0 ? Copied(prefix,prefix.Length,prefix.Length) : suffix;
                if (await Copy()!=expectedSuffix) return false;
                Key(suffix.Length==0 ? End : Left,ctrl:suffix.Length==0);
            }
            token.ThrowIfCancellationRequested();
            if (EnterAfterPaste) Key(0x0D);
            return true;
        }
        finally {ClipboardText.RestoreIfOwned(Text,clipboard,owner);}
    }
}
