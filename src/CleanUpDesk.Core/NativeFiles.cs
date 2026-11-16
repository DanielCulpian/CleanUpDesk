using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CleanUpDesk.Core;
public static class NativeFiles
{
    /// <summary>Adds the extended local-path prefix only for native calls that require long-path support.</summary>
    static string NativePath(string path) => path.Length >= 248 && !path.StartsWith(@"\\", StringComparison.Ordinal)
        ? @"\\?\" + System.IO.Path.GetFullPath(path) : path;
    // Request DELETE access with OPEN_EXISTING, without DELETE_ON_CLOSE or any deletion operation.
    /// <summary>Probes DELETE access on an existing target without deleting it, changing attributes, or requesting elevation.</summary>
    public static DeleteAccess ProbeDeleteAccess(string path)
    {
        using var handle = CreateFile(NativePath(path), 0x00010000, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (!handle.IsInvalid) return DeleteAccess.Allowed;
        return Marshal.GetLastWin32Error() switch { 5 or 1314 => DeleteAccess.Denied, 32 or 33 => DeleteAccess.InUse, 2 or 3 or 21 => DeleteAccess.Unavailable, _ => DeleteAccess.Unknown };
    }
    [StructLayout(LayoutKind.Sequential)] struct Info { public uint Attr; public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write; public uint Serial, SizeHigh, SizeLow, Links, IdHigh, IdLow; }
    /// <summary>Opens an existing native file handle with explicit sharing and access flags.</summary>
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    /// <summary>Reads stable native identity metadata from an open file handle.</summary>
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetFileInformationByHandle(SafeFileHandle handle, out Info info);
    [StructLayout(LayoutKind.Sequential)] struct BasicInfo { public long Created, Accessed, Written, Changed; public uint Attributes; }
    /// <summary>Reads the requested extended native metadata class from an existing handle.</summary>
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass, out BasicInfo info, uint size);
    /// <summary>Reads the native metadata-change timestamp or disables fingerprint reuse when it is unavailable.</summary>
    public static long? ChangeStamp(string path)
    {
        using var h = CreateFile(NativePath(path), 0, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (h.IsInvalid || !GetFileInformationByHandleEx(h, 0, out var info, (uint)Marshal.SizeOf<BasicInfo>()) || info.Changed <= 0) return null;
        return info.Changed;
    }
    /// <summary>Reads volume serial, file index, and creation identity to detect replacement or physical aliases.</summary>
    public static string Identity(string path)
    {
        using var h = CreateFile(NativePath(path), 0, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (h.IsInvalid || !GetFileInformationByHandle(h, out var i)) throw new Win32Exception(Marshal.GetLastWin32Error(), "No se pudo comprobar la identidad del elemento.");
        return $"{i.Serial:X8}:{i.IdHigh:X8}{i.IdLow:X8}:{i.Creation.dwHighDateTime:X8}{i.Creation.dwLowDateTime:X8}";
    }
}
public sealed class WindowsDeleteBackend : IDeleteBackend
{
    /// <summary>Uses Windows recycling when requested and refuses silent fallback to permanent deletion.</summary>
    public void Delete(string path, bool directory, DeleteMode mode)
    {
        if (mode == DeleteMode.Permanent) { Permanent(path); return; }
        object? obj = null; IShellItem? item = null;
        try
        {
            obj = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("3ad05575-8857-4850-9277-11b85bdb8e09"))!); var op = (IFileOperation)obj!;
            op.SetOperationFlags(0x00080000 | 0x00100000 | 0x20000000 | 0x0400 | 0x0004 | 0x0010 | 0x2000);
            var iid = typeof(IShellItem).GUID; Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out item));
            var sink = new RecycleSink(); op.DeleteItem(item, sink); op.PerformOperations(); op.GetAnyOperationsAborted(out bool aborted);
            if (sink.Refused || aborted || sink.Result < 0 || !sink.Completed) throw new IOException("No se pudo enviar a la Papelera. No se ha autorizado eliminación permanente; revisa de nuevo si deseas ese modo.");
        }
        finally { if (item != null) Marshal.ReleaseComObject(item); if (obj != null) Marshal.ReleaseComObject(obj); }
    }
    /// <summary>Creates a shell item for the exact path supplied to the reviewed shell operation.</summary>
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)] static extern int SHCreateItemFromParsingName(string path, IntPtr context, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IShellItem item);
    /// <summary>Deletes a validated tree item-by-item without opaque recursive deletion or following reparse points.</summary>
    static void Permanent(string path)
    {
        // Never use Directory.Delete(recursive:true): validate each path and do not traverse links.
        Paths.NoReparseAncestors(path);
        if ((File.GetAttributes(path) & FileAttributes.Directory) == 0) { File.Delete(path); return; }
        foreach (var child in Directory.EnumerateFileSystemEntries(path)) Permanent(child);
        Paths.NoReparseAncestors(path); Directory.Delete(path, false);
    }
}
[ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IShellItem { 
    /// <summary>Declares the shell-item handler binding slot required by the native COM interface layout.</summary>
    void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid iid, out IntPtr ppv); 
    /// <summary>Declares the shell-item parent retrieval slot required by the native COM interface layout.</summary>
    void GetParent(out IShellItem parent); 
    /// <summary>Declares the shell-item display-name retrieval slot required by the native COM interface layout.</summary>
    void GetDisplayName(uint sigdn, out IntPtr name); 
    /// <summary>Declares the shell-item attribute retrieval slot required by the native COM interface layout.</summary>
    void GetAttributes(uint mask, out uint attrs); 
    /// <summary>Declares shell-item comparison while preserving the Windows COM method order.</summary>
    void Compare(IShellItem other, uint hint, out int order); }
[ComImport, Guid("947aab5f-0a5c-4c13-b4d6-4bf7836fc9f8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IFileOperation
{
    /// <summary>Declares operation-progress subscription in the native shell operation interface.</summary>
    void Advise(IFileOperationProgressSink sink, out uint cookie); 
        /// <summary>Declares operation-progress unsubscription in the native shell operation interface.</summary>
        void Unadvise(uint cookie); 
        /// <summary>Configures explicit shell flags, including the requirement to recycle rather than silently delete permanently.</summary>
        void SetOperationFlags(uint flags); 
        /// <summary>Declares the native progress-message slot; normal cleanup does not customize shell text.</summary>
        void SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message); 
        /// <summary>Declares the native progress-dialog slot required by the shell COM layout.</summary>
        void SetProgressDialog(IntPtr dialog); 
        /// <summary>Declares the native property-store slot required by the shell COM layout.</summary>
        void SetProperties(IntPtr array); 
        /// <summary>Associates the shell operation with an owner window when required.</summary>
        void SetOwnerWindow(uint hwnd);
    /// <summary>Declares the native single-item property-application slot; cleanup does not queue this operation.</summary>
    void ApplyPropertiesToItem(IShellItem item); 
        /// <summary>Declares the native multi-item property-application slot; cleanup does not queue this operation.</summary>
        void ApplyPropertiesToItems(IntPtr items);
    /// <summary>Declares native single-item renaming; cleanup does not queue rename operations.</summary>
    void RenameItem(IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr sink); 
        /// <summary>Declares native multi-item renaming; cleanup does not queue rename operations.</summary>
        void RenameItems(IntPtr items, [MarshalAs(UnmanagedType.LPWStr)] string name);
    /// <summary>Declares native single-item moving; cleanup does not queue move operations.</summary>
    void MoveItem(IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr sink); 
        /// <summary>Declares native multi-item moving; cleanup does not queue move operations.</summary>
        void MoveItems(IntPtr items, IShellItem destination);
    /// <summary>Declares native single-item copying; cleanup does not queue copy operations.</summary>
    void CopyItem(IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr sink); 
        /// <summary>Declares native multi-item copying; cleanup does not queue copy operations.</summary>
        void CopyItems(IntPtr items, IShellItem destination);
    /// <summary>Queues deletion of the exact reviewed shell item with a recycle-enforcing progress sink.</summary>
    void DeleteItem(IShellItem item, IFileOperationProgressSink sink); 
        /// <summary>Declares the native multi-item deletion slot while preserving COM interface order.</summary>
        void DeleteItems(IntPtr items); 
        /// <summary>Declares native item creation; cleanup does not create filesystem items through this interface.</summary>
        void NewItem(IShellItem destination, uint attributes, [MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPWStr)] string template, IntPtr sink); 
        /// <summary>Executes the queued native shell operations on the dedicated STA thread.</summary>
        void PerformOperations(); 
        /// <summary>Reports whether the shell aborted any queued operation.</summary>
        void GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
}
[ComVisible(true), Guid("04b0f1a7-9490-44bc-96e1-4296a31252e2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IFileOperationProgressSink
{
    /// <summary>Acknowledges the shell operation-start callback without initiating other work.</summary>
    [PreserveSig] int StartOperations(); 
        /// <summary>Records an overall shell-operation failure when Windows reports one.</summary>
        [PreserveSig] int FinishOperations(int hr);
    /// <summary>Acknowledges the unused shell pre-rename callback without queuing any operation.</summary>
    [PreserveSig] int PreRenameItem(uint flags, IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string name); 
        /// <summary>Acknowledges the unused shell post-rename callback without changing application state.</summary>
        [PreserveSig] int PostRenameItem(uint flags, IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string name, int hr, IShellItem newItem);
    /// <summary>Acknowledges the unused shell pre-move callback without queuing any operation.</summary>
    [PreserveSig] int PreMoveItem(uint flags, IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name); 
        /// <summary>Acknowledges the unused shell post-move callback without changing application state.</summary>
        [PreserveSig] int PostMoveItem(uint flags, IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name, int hr, IShellItem newItem);
    /// <summary>Acknowledges the unused shell pre-copy callback without queuing any operation.</summary>
    [PreserveSig] int PreCopyItem(uint flags, IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name); 
        /// <summary>Acknowledges the unused shell post-copy callback without changing application state.</summary>
        [PreserveSig] int PostCopyItem(uint flags, IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name, int hr, IShellItem newItem);
    /// <summary>Rejects a shell deletion callback unless Windows confirms the recycle-operation flag.</summary>
    [PreserveSig] int PreDeleteItem(uint flags, IShellItem item); 
        /// <summary>Records the shell deletion result for the exact queued item.</summary>
        [PreserveSig] int PostDeleteItem(uint flags, IShellItem item, int hr, IShellItem newItem);
    /// <summary>Acknowledges the unused shell pre-new callback without queuing any operation.</summary>
    [PreserveSig] int PreNewItem(uint flags, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name); 
        /// <summary>Acknowledges the unused shell post-new callback without changing application state.</summary>
        [PreserveSig] int PostNewItem(uint flags, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPWStr)] string template, uint attributes, int hr, IShellItem newItem);
    /// <summary>Acknowledges shell progress; application-level progress is reported by the tracked coordinator.</summary>
    [PreserveSig] int UpdateProgress(uint total, uint done); 
        /// <summary>Acknowledges the unused shell progress-timer reset callback.</summary>
        [PreserveSig] int ResetTimer(); 
        /// <summary>Acknowledges the unused shell progress-timer pause callback.</summary>
        [PreserveSig] int PauseTimer(); 
        /// <summary>Acknowledges the unused shell progress-timer resume callback.</summary>
        [PreserveSig] int ResumeTimer();
}
[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
public sealed class RecycleSink : IFileOperationProgressSink
{
    public bool Refused, Completed; public int Result;
    /// <summary>Rejects a shell deletion callback unless Windows confirms the recycle-operation flag.</summary>
    public int PreDeleteItem(uint flags, IShellItem item) { if ((flags & 0x80) != 0) return 0; Refused = true; return unchecked((int)0x80004004); }
    /// <summary>Records the shell deletion result for the exact queued item.</summary>
    public int PostDeleteItem(uint flags, IShellItem item, int hr, IShellItem newItem) { Completed = true; Result = hr; return 0; }
    /// <summary>Acknowledges the shell operation-start callback without initiating other work.</summary>
    public int StartOperations() => 0; 
        /// <summary>Records an overall shell-operation failure when Windows reports one.</summary>
        public int FinishOperations(int hr) { if (hr < 0) Result = hr; return 0; }
    /// <summary>Acknowledges the unused shell pre-rename callback without queuing any operation.</summary>
    public int PreRenameItem(uint f, IShellItem i, string n) => 0; 
        /// <summary>Acknowledges the unused shell post-rename callback without changing application state.</summary>
        public int PostRenameItem(uint f, IShellItem i, string n, int h, IShellItem ni) => 0;
    /// <summary>Acknowledges the unused shell pre-move callback without queuing any operation.</summary>
    public int PreMoveItem(uint f, IShellItem i, IShellItem d, string n) => 0; 
        /// <summary>Acknowledges the unused shell post-move callback without changing application state.</summary>
        public int PostMoveItem(uint f, IShellItem i, IShellItem d, string n, int h, IShellItem ni) => 0;
    /// <summary>Acknowledges the unused shell pre-copy callback without queuing any operation.</summary>
    public int PreCopyItem(uint f, IShellItem i, IShellItem d, string n) => 0; 
        /// <summary>Acknowledges the unused shell post-copy callback without changing application state.</summary>
        public int PostCopyItem(uint f, IShellItem i, IShellItem d, string n, int h, IShellItem ni) => 0;
    /// <summary>Acknowledges the unused shell pre-new callback without queuing any operation.</summary>
    public int PreNewItem(uint f, IShellItem d, string n) => 0; 
        /// <summary>Acknowledges the unused shell post-new callback without changing application state.</summary>
        public int PostNewItem(uint f, IShellItem d, string n, string t, uint a, int h, IShellItem ni) => 0;
    /// <summary>Acknowledges shell progress; application-level progress is reported by the tracked coordinator.</summary>
    public int UpdateProgress(uint t, uint d) => 0; 
        /// <summary>Acknowledges the unused shell progress-timer reset callback.</summary>
        public int ResetTimer() => 0; 
        /// <summary>Acknowledges the unused shell progress-timer pause callback.</summary>
        public int PauseTimer() => 0; 
        /// <summary>Acknowledges the unused shell progress-timer resume callback.</summary>
        public int ResumeTimer() => 0;
}
