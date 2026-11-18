using System.Runtime.InteropServices;

namespace Corrector;

// Plain Unicode text and the ownership token use native clipboard formats, without
// OLE/.NET object serialization. The same representation works in the bundled EXE.
internal static class ClipboardText
{
    [DllImport("user32.dll")] static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll")] static extern bool CloseClipboard();
    [DllImport("user32.dll")] static extern bool EmptyClipboard();
    [DllImport("user32.dll")] static extern IntPtr GetClipboardData(uint format);
    [DllImport("user32.dll")] static extern IntPtr SetClipboardData(uint format,IntPtr memory);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern uint RegisterClipboardFormat(string name);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalAlloc(uint flags,UIntPtr size);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalLock(IntPtr handle);
    [DllImport("kernel32.dll")] static extern bool GlobalUnlock(IntPtr handle);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalFree(IntPtr handle);
    [ThreadStatic] static Control? window;
    static uint OwnerFormat=>RegisterClipboardFormat("ABC.PasteOwner.Unicode.v1");
    static void Open()
    {
        window??=new Control();
        for(int attempt=0;attempt<25;attempt++)
        {
            if(OpenClipboard(window.Handle))return;
            Thread.Sleep(15);
        }
        throw new ExternalException("Буфер обмена занят.");
    }
    static string? ReadLocked(uint format)
    {
        IntPtr memory=GetClipboardData(format);if(memory==IntPtr.Zero)return null;
        IntPtr pointer=GlobalLock(memory);if(pointer==IntPtr.Zero)throw new ExternalException("Не удалось прочитать буфер.");
        try{return Marshal.PtrToStringUni(pointer);}finally{GlobalUnlock(memory);}
    }
    static IntPtr Allocate(string text)
    {
        byte[] bytes=System.Text.Encoding.Unicode.GetBytes(text+'\0');
        IntPtr memory=GlobalAlloc(2,(UIntPtr)bytes.Length);if(memory==IntPtr.Zero)throw new OutOfMemoryException();
        IntPtr pointer=GlobalLock(memory);if(pointer==IntPtr.Zero){GlobalFree(memory);throw new ExternalException("Не удалось подготовить буфер.");}
        try{Marshal.Copy(bytes,0,pointer,bytes.Length);}finally{GlobalUnlock(memory);}return memory;
    }
    static void WriteLocked(string text,string? owner)
    {
        IntPtr memory=Allocate(text),token=owner==null?IntPtr.Zero:Allocate(owner);
        try
        {
            if(!EmptyClipboard())throw new ExternalException("Не удалось обновить буфер.");
            if(SetClipboardData(13,memory)==IntPtr.Zero)throw new ExternalException("Не удалось записать исходник в буфер.");memory=IntPtr.Zero;
            if(token!=IntPtr.Zero){if(SetClipboardData(OwnerFormat,token)==IntPtr.Zero)throw new ExternalException("Не удалось пометить вставку.");token=IntPtr.Zero;}
        }
        finally{if(memory!=IntPtr.Zero)GlobalFree(memory);if(token!=IntPtr.Zero)GlobalFree(token);}
    }
    public static string? Read(){Open();try{return ReadLocked(13);}finally{CloseClipboard();}}
    public static void Write(string text,string? owner=null){Open();try{WriteLocked(text,owner);}finally{CloseClipboard();}}
    public static bool RestoreIfOwned(string text,uint sequence,string? owner)
    {
        Open();try
        {
            if(Native.GetClipboardSequenceNumber()!=sequence && (owner==null||ReadLocked(OwnerFormat)!=owner))return false;
            WriteLocked(text,null);return true;
        }
        finally{CloseClipboard();}
    }
    public static bool Owns(string owner){Open();try{return ReadLocked(OwnerFormat)==owner;}finally{CloseClipboard();}}
}
