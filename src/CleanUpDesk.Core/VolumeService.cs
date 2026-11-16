using System.Runtime.InteropServices;
using System.Text;

namespace CleanUpDesk.Core;
public sealed class VolumeService
{
    /// <summary>Resolves the Windows volume GUID associated with a mounted root when available.</summary>
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool GetVolumeNameForVolumeMountPoint(string root, StringBuilder name, int size);
    /// <summary>Detects fixed/removable drives and distinguishes ready, empty-reader, unavailable, and access-denied states.</summary>
    public IReadOnlyList<Volume> Detect()
    {
        var result = new List<Volume>();
        foreach (var d in DriveInfo.GetDrives().Where(d => d.DriveType is DriveType.Fixed or DriveType.Removable))
        {
            var id = new StringBuilder(1024); bool stable = GetVolumeNameForVolumeMountPoint(d.RootDirectory.FullName, id, id.Capacity); string identity = stable ? id.ToString() : "letter:" + d.Name;
            try
            {
                bool ready = d.IsReady;
                result.Add(new(identity, d.Name, ready ? d.VolumeLabel : "", ready ? d.TotalSize : 0, ready ? d.AvailableFreeSpace : 0, ready)
                {
                    Connection = ready ? VolumeConnection.Ready : d.DriveType == DriveType.Removable ? VolumeConnection.NoMedia : VolumeConnection.Error,
                    Reason = ready ? "" : d.DriveType == DriveType.Removable ? "Windows reconoce el dispositivo, pero no hay un medio disponible. Puede ser un lector vacío o una memoria no preparada." : "Windows no puede leer la unidad. Puede estar bloqueada o no estar preparada."
                });
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                result.Add(new(identity, d.Name, "", 0, 0, false) { Connection = e is UnauthorizedAccessException ? VolumeConnection.AccessDenied : VolumeConnection.Error, Reason = e.Message });
            }
        }
        return result;
    }
    /// <summary>Resolves the currently detected local volume containing the supplied path.</summary>
    public Volume ForPath(string path) => Detect().FirstOrDefault(v => string.Equals(v.Root, System.IO.Path.GetPathRoot(path), StringComparison.OrdinalIgnoreCase)) ?? throw new IOException("Unidad desconectada o no compatible.");
    /// <summary>Verifies that a path still belongs to the available stable volume identity reviewed earlier.</summary>
    public bool Matches(string path, string id) { try { var v = ForPath(path); return v.Available && v.Id == id; } catch (IOException) { return false; } }
    /// <summary>Combines live and indexed volumes while preserving disconnected metadata without inventing usable empty-reader drives.</summary>
    public static IReadOnlyList<Volume> Merge(IEnumerable<Volume> detected, IEnumerable<Volume> remembered, ISet<string> indexed)
    {
        var live = detected.ToArray();
        // Do not turn remembered empty-reader letters into fictitious disconnected volumes.
        return live.Concat(remembered.Where(v => indexed.Contains(v.Id) && !live.Any(l => l.Id == v.Id))
            .Select(v => v with { Available = false, Connection = VolumeConnection.Disconnected })).ToArray();
    }
}
