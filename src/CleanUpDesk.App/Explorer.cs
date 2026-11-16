using System.Runtime.InteropServices;
namespace CleanUpDesk.App;
internal static class Explorer
{
    /// <summary>Creates a shell item identifier for an existing filesystem path.</summary>
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern IntPtr ILCreateFromPath(string path);
    /// <summary>Releases a shell item identifier allocated by the Windows shell.</summary>
    [DllImport("shell32.dll")] static extern void ILFree(IntPtr pidl);
    /// <summary>Opens a shell folder and selects its item without building a command from user input.</summary>
    [DllImport("shell32.dll")] static extern int SHOpenFolderAndSelectItems(IntPtr pidl, uint count, IntPtr children, uint flags);
    /// <summary>Reveals the requested filesystem item in Explorer using shell APIs and deterministic resource cleanup.</summary>
    public static void Select(string path) { var pidl = ILCreateFromPath(path); if (pidl == IntPtr.Zero) throw new IOException("Windows no pudo abrir esta ruta."); try { Marshal.ThrowExceptionForHR(SHOpenFolderAndSelectItems(pidl, 0, IntPtr.Zero, 0)); } finally { ILFree(pidl); } }
}
