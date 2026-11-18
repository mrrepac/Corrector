using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Corrector;

internal sealed record CutGesture(IntPtr Window,IntPtr Focus,uint ClipboardBefore)
{
    public volatile bool Invalidated;
    public bool IsCurrent => !Invalidated && Window==Native.GetForegroundWindow() && Focus==Native.Focus;
}

// First Ctrl+X is entirely native. Only the second press is consumed.
internal sealed class DoubleCutHook : IDisposable
{
    delegate IntPtr HookProc(int code,IntPtr message,IntPtr data);
    [DllImport("user32.dll",SetLastError=true)] static extern IntPtr SetWindowsHookEx(int type,HookProc callback,IntPtr module,uint thread);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook,int code,IntPtr message,IntPtr data);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] static extern IntPtr GetModuleHandle(string? name);
    [StructLayout(LayoutKind.Sequential)] struct KeyboardEvent {public uint key,scan,flags,time;public UIntPtr extra;}
    readonly HookProc keyboardCallback,mouseCallback;
    // Clipboard owners and UI Automation providers can stall the tray thread.
    // Windows requires low-level hook callbacks to keep pumping messages.
    readonly Thread worker;
    readonly TaskCompletionSource<bool> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    Control? dispatcher;
    IntPtr keyboard,mouse;
    bool xDown,suppressUp;
    long firstTime;
    CutGesture? first;
    CutGesture? detected;
    public event Action<CutGesture>? Detected;
    public DoubleCutHook()
    {
        keyboardCallback=OnKeyboard;mouseCallback=OnMouse;
        worker = new Thread(Run) { IsBackground=true, Name="Corrector keyboard hook" };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
        ready.Task.GetAwaiter().GetResult();
    }

    void Run()
    {
        try
        {
            using var control = new Control();
            _ = control.Handle;
            dispatcher = control;
            keyboard=SetWindowsHookEx(13,keyboardCallback,GetModuleHandle(null),0);
            if (keyboard==IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            mouse=SetWindowsHookEx(14,mouseCallback,GetModuleHandle(null),0);
            if (mouse==IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            ready.SetResult(true);
            Application.Run();
        }
        catch (Exception ex) when (!ready.Task.IsCompleted) { ready.SetException(ex); }
        finally
        {
            if (keyboard!=IntPtr.Zero) UnhookWindowsHookEx(keyboard);
            if (mouse!=IntPtr.Zero) UnhookWindowsHookEx(mouse);
        }
    }
    IntPtr OnMouse(int code,IntPtr message,IntPtr data)
    {
        // Clicks and wheel input between presses invalidate the old cut location.
        if(code>=0 && message.ToInt32()!=0x0200){first=null;if(detected!=null)detected.Invalidated=true;}
        return CallNextHookEx(mouse,code,message,data);
    }
    IntPtr OnKeyboard(int code,IntPtr message,IntPtr data)
    {
        if(code<0)return CallNextHookEx(keyboard,code,message,data);
        var input=Marshal.PtrToStructure<KeyboardEvent>(data);
        if(input.extra==Native.InputMarker)return CallNextHookEx(keyboard,code,message,data);
        bool down=message.ToInt32() is 0x100 or 0x104;
        bool up=message.ToInt32() is 0x101 or 0x105;
        if(down && detected!=null)detected.Invalidated=true;
        if(input.key==0x58)
        {
            if(up){xDown=false;if(suppressUp){suppressUp=false;return (IntPtr)1;}}
            if(down)
            {
                if(xDown)return suppressUp?(IntPtr)1:CallNextHookEx(keyboard,code,message,data);
                xDown=true;
                bool chord=(Native.GetAsyncKeyState(0x11)&0x8000)!=0 && (Native.GetAsyncKeyState(0x12)&0x8000)==0 && (Native.GetAsyncKeyState(0x10)&0x8000)==0 && (Native.GetAsyncKeyState(0x5B)&0x8000)==0 && (Native.GetAsyncKeyState(0x5C)&0x8000)==0;
                if(chord)
                {
                    long now=Environment.TickCount64;
                    if(first!=null && now-firstTime<=700 && first.Window==Native.GetForegroundWindow() && first.Focus==Native.Focus)
                    {
                        var gesture=first;first=null;suppressUp=true;detected=gesture;
                        Detected?.Invoke(gesture);return (IntPtr)1;
                    }
                    first=new CutGesture(Native.GetForegroundWindow(),Native.Focus,Native.GetClipboardSequenceNumber());firstTime=now;
                }
                else first=null;
            }
        }
        else if(down && input.key is not (0x11 or 0xA2 or 0xA3))first=null;
        return CallNextHookEx(keyboard,code,message,data);
    }
    public void Dispose()
    {
        if (!worker.IsAlive) return;
        dispatcher!.BeginInvoke((Action)Application.ExitThread);
        worker.Join();
    }
}
