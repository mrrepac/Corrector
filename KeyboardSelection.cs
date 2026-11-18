using System.Globalization;

namespace Corrector;

// Editors that expose no usable UIA range can still confirm a selection via Copy.
internal abstract class KeyboardSelection(CutGesture gesture, uint sequence)
{
    protected const ushort Home = 0x24, End = 0x23, Left = 0x25, Right = 0x27;
    readonly string windowTitle = Native.WindowTitle(gesture.Window);
    protected uint clipboard = sequence;
    internal uint Sequence => clipboard;
    internal string Text { get; private protected set; } = "";
    internal bool ExplicitSelection { get; private protected set; }

    internal bool EnterAfterPaste { get; private protected set; }

    internal void RestoreClipboard(string fallback) => ClipboardText.RestoreIfOwned(fallback, clipboard, null);

    protected void Check()
    {
        if (!gesture.IsCurrent || Native.WindowTitle(gesture.Window) != windowTitle ||
            clipboard != Native.GetClipboardSequenceNumber())
            throw new InvalidOperationException("Поле, ввод или буфер изменились. Проверка остановлена.");
    }

    protected void Key(ushort key, bool ctrl=false, bool shift=false)
    {
        Check();
        Native.Chord(key, ctrl, shift);
    }

    protected async Task<string?> TryCopy(ushort shortcut = 0x43, int polls = 40)
    {
        Key(shortcut, ctrl:true);
        for (int i=0; i<polls; i++)
        {
            await Task.Delay(15);
            if (!gesture.IsCurrent || Native.WindowTitle(gesture.Window) != windowTitle)
                throw new InvalidOperationException("Активное поле или ввод изменились.");
            uint next = Native.GetClipboardSequenceNumber();
            if (next == clipboard) continue;
            if (!Native.ClipboardFrom(gesture.Window))
                throw new InvalidOperationException("Буфер изменился в другом приложении.");
            string? value = ClipboardText.Read();
            if (next != Native.GetClipboardSequenceNumber()) continue;
            clipboard = next;
            return value?.ReplaceLineEndings("\n");
        }
        return null;
    }

    protected virtual async Task<string> Copy() => await TryCopy() ??
        throw new InvalidOperationException("Редактор не подтвердил копирование.");

    protected async Task MoveCharacters(string text, bool backwards, bool shift)
    {
        int count = new StringInfo(text).LengthInTextElements;
        while (count > 0)
        {
            int batch = Math.Min(count, 128);
            Check();
            Native.Arrows(backwards ? Left : Right, batch, shift);
            count -= batch;
            await Task.Delay(5);
        }
    }

    internal abstract Task<bool> ReplaceVerified(string replacement, CancellationToken token);
}
