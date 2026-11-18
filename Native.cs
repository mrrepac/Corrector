using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Corrector;

internal static class Native
{
    internal static Action<ushort>? InputForClipboardTest;
    internal static readonly UIntPtr InputMarker = new(0x41424331u);
    [DllImport("user32.dll",SetLastError=true)] public static extern bool RegisterHotKey(IntPtr hwnd,int id,uint modifiers,uint key);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hwnd,int id);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr window,System.Text.StringBuilder text,int length);
    public static string WindowTitle(IntPtr window)
    {
        var text=new System.Text.StringBuilder(512);
        GetWindowText(window,text,text.Capacity);
        return text.ToString();
    }
    [DllImport("user32.dll")] public static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] static extern IntPtr GetClipboardOwner();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window,out uint process);
    public static uint ProcessOf(IntPtr window) { GetWindowThreadProcessId(window,out uint id);return id; }
    public static bool ClipboardFrom(IntPtr window) => window!=IntPtr.Zero && ProcessOf(GetClipboardOwner())==ProcessOf(window);
    [DllImport("user32.dll")] internal static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll")] internal static extern bool CloseClipboard();
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] static extern uint SendInput(uint n,Input[] inputs,int size);
    [DllImport("user32.dll")] static extern bool GetGUIThreadInfo(uint id,ref GuiInfo info);
    [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LastInput info);
    [StructLayout(LayoutKind.Sequential)] struct LastInput { public uint size,time; }
    [StructLayout(LayoutKind.Sequential)] struct GuiInfo { public int size,flags;public IntPtr active,focus,capture,menu,move,caret;public int left,top,right,bottom; }
    [StructLayout(LayoutKind.Sequential)] struct Input { public uint type; public InputUnion u; }
    [StructLayout(LayoutKind.Explicit)] struct InputUnion { [FieldOffset(0)] public Keyboard keyboard;[FieldOffset(0)] public Mouse mouse; }
    [StructLayout(LayoutKind.Sequential)] struct Keyboard { public ushort key,scan;public uint flags,time;public UIntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] struct Mouse { public int x,y;public uint data,flags,time;public UIntPtr extra; }
    public static IntPtr Focus { get { var info=new GuiInfo {size=Marshal.SizeOf<GuiInfo>()};return GetGUIThreadInfo(0,ref info)?info.focus:IntPtr.Zero; } }
    public static uint InputTime { get {var info=new LastInput {size=(uint)Marshal.SizeOf<LastInput>()};GetLastInputInfo(ref info);return info.time;} }
    public static async Task ReleaseModifiers()
    {
        for(int i=0;i<200;i++)
        {
            if(new[]{0x10,0x11,0x12,0x5B,0x5C}.All(k=>(GetAsyncKeyState(k)&0x8000)==0))return;
            await Task.Delay(20);
        }
        throw new InvalidOperationException("Отпусти клавиши и попробуй ещё раз.");
    }
    public static void CtrlKey(ushort key,bool markOwnInput=true) => Chord(key,true,false,markOwnInput);
    public static void Enter() => Chord(0x0D);
    static Input Key(ushort key,bool up,bool marked=true) => new() {type=1,u=new InputUnion {
        keyboard=new Keyboard {key=key,flags=(up?2u:0u) | (key is >=0x21 and <=0x28 or 0x2D ? 1u : 0u),extra=marked?InputMarker:UIntPtr.Zero}}};
    static void Send(List<Input> inputs)
    {
        if(SendInput((uint)inputs.Count,inputs.ToArray(),Marshal.SizeOf<Input>())!=inputs.Count)
            throw new InvalidOperationException("Windows не разрешила ввод в это окно.");
    }
    public static void Chord(ushort key,bool ctrl=false,bool shift=false,bool markOwnInput=true)
    {
        if(InputForClipboardTest!=null){InputForClipboardTest(key);return;}
        var inputs=new List<Input>(6);
        if(ctrl)inputs.Add(Key(0x11,false,markOwnInput));
        if(shift)inputs.Add(Key(0x10,false,markOwnInput));
        inputs.Add(Key(key,false,markOwnInput));inputs.Add(Key(key,true,markOwnInput));
        if(shift)inputs.Add(Key(0x10,true,markOwnInput));
        if(ctrl)inputs.Add(Key(0x11,true,markOwnInput));
        Send(inputs);
    }
    public static void Arrows(ushort key,int count,bool shift)
    {
        var inputs=new List<Input>(2*count+2);
        if(shift)inputs.Add(Key(0x10,false));
        for(int i=0;i<count;i++){inputs.Add(Key(key,false));inputs.Add(Key(key,true));}
        if(shift)inputs.Add(Key(0x10,true));
        Send(inputs);
    }
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] static extern IntPtr CreateJobObject(IntPtr attributes,string? name);
    [DllImport("kernel32.dll")] static extern bool SetInformationJobObject(IntPtr job,int type,IntPtr info,uint size);
    [DllImport("kernel32.dll")] static extern bool AssignProcessToJobObject(IntPtr job,IntPtr process);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [StructLayout(LayoutKind.Sequential)] struct BasicLimit {public long processTime,jobTime;public uint flags;public UIntPtr min,max;public uint active;public UIntPtr affinity;public uint priority,scheduling;}
    [StructLayout(LayoutKind.Sequential)] struct IoCounters {public ulong a,b,c,d,e,f;}
    [StructLayout(LayoutKind.Sequential)] struct ExtendedLimit { public BasicLimit basic;public IoCounters io;public UIntPtr processMemory,jobMemory,peakProcess,peakJob; }
    internal sealed class Job : IDisposable
    {
        IntPtr handle=CreateJobObject(IntPtr.Zero,null);
        public Job()
        {
            var info=new ExtendedLimit {basic=new BasicLimit {flags=0x2000}};
            int size=Marshal.SizeOf<ExtendedLimit>();var ptr=Marshal.AllocHGlobal(size);
            try{Marshal.StructureToPtr(info,ptr,false);if(handle==IntPtr.Zero||!SetInformationJobObject(handle,9,ptr,(uint)size))throw new Win32Exception();}
            finally{Marshal.FreeHGlobal(ptr);}
        }
        public void Add(Process p){if(!AssignProcessToJobObject(handle,p.Handle))throw new Win32Exception();}
        public void Dispose(){if(handle!=IntPtr.Zero){CloseHandle(handle);handle=IntPtr.Zero;}}
    }
}

internal sealed record Selection(string Text,IntPtr Window,IntPtr Focus,uint InputTime);

internal static class SelectedText
{
    const string ClipboardOwnerFormat="ABC.InternalPasteOwner";
    internal static async Task<object> ClipboardRegression()
    {
        var userClipboard=Snapshot();
        const string source="Если будет дождь мы останемся дома.";
        const string result="Если будет дождь, мы останемся дома.";
        try
        {
            Clipboard.SetText(source);
            var saved=Snapshot();
            string owner=SetOwnedText(result);uint sequence=Native.GetClipboardSequenceNumber();
            string before=Thread.CurrentThread.GetApartmentState().ToString();
            await Task.Delay(350);
            await Restore(saved,sequence,owner,source);
            bool simpleRestored=Clipboard.GetText()==source;
            using var editor=new TextBox{Text=source,Multiline=true};_=editor.Handle;editor.SelectAll();editor.Cut();editor.Undo();
            bool restoredInEditor=editor.Text==source;
            Native.InputForClipboardTest=key=>{if(key==0x43)editor.Copy();else if(key==0x56)editor.Paste();else if(key==0x5A)editor.Undo();else throw new InvalidOperationException("Unexpected test key");};
            var selection=await Capture();
            bool replaced=await Replace(selection,result,requireNoInput:false,clipboardOriginal:source);
            bool resultInEditor=editor.Text==result;
            bool originalInClipboard=Clipboard.GetText()==source;
            owner=SetOwnedText(result);sequence=Native.GetClipboardSequenceNumber();
            Clipboard.SetText("ABC test: newer clipboard");
            await Restore(saved,sequence,owner,source);
            bool newerCopyPreserved=Clipboard.GetText()=="ABC test: newer clipboard";
            owner=SetOwnedText(result);sequence=Native.GetClipboardSequenceNumber();
            var acquired=new TaskCompletionSource<bool>();
            var locker=Task.Run(()=>{bool locked=false;for(int i=0;i<30&&!locked;i++){locked=Native.OpenClipboard(IntPtr.Zero);if(!locked)Thread.Sleep(30);}acquired.SetResult(locked);if(locked){try{Thread.Sleep(1800);}finally{Native.CloseClipboard();}}});
            bool lockAcquired=await acquired.Task;
            await Restore(saved,sequence,owner,source);
            await locker;
            bool restoresAfterContention=Clipboard.GetText()==source;
            editor.SelectAll();editor.Paste();bool ctrlVRestoresSource=editor.Text==source;
            return new {before,after=Thread.CurrentThread.GetApartmentState().ToString(),simpleRestored,restoredInEditor,replaced,resultInEditor,originalInClipboard,newerCopyPreserved,lockAcquired,restoresAfterContention,ctrlVRestoresSource};
        }
        finally
        {
            Native.InputForClipboardTest=null;
            if(Clipboard.ContainsText() && (Clipboard.GetText()==source||Clipboard.GetText()==result||Clipboard.GetText()=="ABC test: newer clipboard"))
            {if(userClipboard==null)Clipboard.Clear();else Clipboard.SetDataObject(userClipboard,true);}
        }
    }
    static string SetOwnedText(string text)
    {
        string owner=Guid.NewGuid().ToString("N");
        ClipboardText.Write(text,owner);return owner;
    }
    public static async Task<bool> PasteAtCaret(Selection target,string replacement,uint clipboardSequence)
    {
        if(Native.GetForegroundWindow()!=target.Window||Native.Focus!=target.Focus||Native.InputTime!=target.InputTime||Native.GetClipboardSequenceNumber()!=clipboardSequence)return false;
        var backup=Snapshot();uint owned=0;string? owner=null;
        try
        {
            if(Native.GetForegroundWindow()!=target.Window||Native.Focus!=target.Focus||Native.InputTime!=target.InputTime)return false;
            owner=SetOwnedText(replacement);owned=Native.GetClipboardSequenceNumber();
            Native.CtrlKey(0x56);
            await Task.Delay(350);
            return true;
        }
        finally{if(owned!=0)await Restore(backup,owned,owner);}
    }
    internal static DataObject? Snapshot()
    {
        var current=Clipboard.GetDataObject();if(current==null)return null;
        var copy=new DataObject();
        foreach(string format in current.GetFormats(false))
        {
            var data=current.GetData(format,false);
            if(data is MemoryStream memory)data=new MemoryStream(memory.ToArray());
            if(data!=null)copy.SetData(format,false,data);
        }
        return copy;
    }
    static async Task Restore(DataObject? saved,uint ownedSequence,string? owner=null,string? originalText=null)
    {
        // Clipboard owners can hold it briefly while processing WM_PASTE. Never silently
        // abandon the original, and recheck ownership on every retry to protect newer copies.
        for(int attempt=0;attempt<20;attempt++)
        {
            try
            {
                if(originalText!=null){ClipboardText.RestoreIfOwned(originalText,ownedSequence,owner);return;}
                uint current=Native.GetClipboardSequenceNumber();
                if(current!=ownedSequence)
                {
                    if(owner==null)return;
                    if(!ClipboardText.Owns(owner))return;
                }
                if(saved==null)Clipboard.Clear();else Clipboard.SetDataObject(saved,true,1,20);
                return;
            }
            catch(ExternalException) when(attempt<19){await Task.Delay(120);}
            catch(ExternalException ex){throw new IOException("Не удалось вернуть исходник в занятый буфер. Он сохранён в меню C → «Скопировать исходник».",ex);}
        }
    }
    public static async Task<bool> ReplaceVerified(CutGesture gesture, DraftField field, string original, string replacement,
        uint sequence, CancellationToken token)
    {
        bool current() => !token.IsCancellationRequested && gesture.IsCurrent;
        if (!current() || !await Task.Run(() => field.Matches(field.Prefix, field.Text)) ||
            !current() || Native.GetClipboardSequenceNumber() != sequence) return false;

        string owner = SetOwnedText(replacement);
        uint owned = Native.GetClipboardSequenceNumber();
        try
        {
            if (!current()) return false;
            Native.CtrlKey(0x56);
            string expected = field.Prefix + replacement;
            for (int attempt = 0; attempt < 20; attempt++)
            {
                await Task.Delay(50, token);
                if (!current() || !ClipboardText.Owns(owner)) return false;
                bool pasted = await Task.Run(() => field.Matches(expected, ""));
                if (!pasted) continue;
                if (!ClipboardText.Owns(owner) || !current()) return false;
                if (field.EnterAfterPaste) Native.Enter();
                return true;
            }
            return false;
        }
        finally { await Restore(null, owned, owner, original); }
    }

    public static async Task<Selection> Capture(CutGesture? gesture=null,bool keepCopy=false)
    {
        await Native.ReleaseModifiers();
        var window=Native.GetForegroundWindow();var focus=Native.Focus;
        if(gesture!=null && !gesture.IsCurrent)throw new InvalidOperationException("Поле или ввод изменились.");
        var saved=Snapshot();uint before=Native.GetClipboardSequenceNumber(), owned=before;
        try
        {
            Native.CtrlKey(0x43);
            for(int i=0;i<40;i++)
            {
                await Task.Delay(25);
                if(Native.GetForegroundWindow()!=window||Native.Focus!=focus||gesture?.Invalidated==true)throw new InvalidOperationException("Окно или ввод изменились во время копирования. Попробуй ещё раз.");
                owned=Native.GetClipboardSequenceNumber();
                if(owned==before)continue;
                if(gesture!=null && !Native.ClipboardFrom(window))
                {
                    owned=before;
                    throw new InvalidOperationException("Буфер изменился в другом приложении. Повтори команду.");
                }
                string text=ClipboardText.Read()??throw new InvalidOperationException("В выделении нет обычного текста.");
                if(string.IsNullOrWhiteSpace(text))throw new InvalidOperationException("Сначала выдели текст.");
                if(keepCopy)owned=before;
                return new Selection(text,window,focus,Native.InputTime);
            }
            throw new InvalidOperationException("В поле нет текста для копирования или редактор не поддерживает выделение.");
        }
        finally {if(owned!=before)await Restore(saved,owned);}
    }
    public static async Task<bool> Replace(Selection selection,string replacement,bool requireNoInput=true,string? clipboardOriginal=null,CutGesture? gesture=null)
    {
        if(Native.GetForegroundWindow()!=selection.Window||Native.Focus!=selection.Focus)return false;
        if(requireNoInput && Native.InputTime!=selection.InputTime)return false;
        var fresh=await Capture(gesture);
        if(fresh.Window!=selection.Window||fresh.Focus!=selection.Focus||fresh.Text!=selection.Text)return false;
        var saved=Snapshot();uint owned=0;string? owner=null;
        try
        {
            if(Native.GetForegroundWindow()!=selection.Window||Native.Focus!=selection.Focus||gesture?.Invalidated==true)return false;
            owner=SetOwnedText(replacement);owned=Native.GetClipboardSequenceNumber();
            Native.CtrlKey(0x56);
            await Task.Delay(350);
            return true;
        }
        finally {if(owned!=0)await Restore(saved,owned,owner,clipboardOriginal);}
    }
}
