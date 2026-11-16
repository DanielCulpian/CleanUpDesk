using Microsoft.Data.Sqlite;

namespace CleanUpDesk.Core;
public sealed class IndexStore
{
    readonly string connectionString;
    /// <summary>Opens the local index database and creates the tables and indexes needed by scans, searches, and history.</summary>
    public IndexStore(string database)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(database))!);
        connectionString = new SqliteConnectionStringBuilder { DataSource = database, Pooling = true }.ToString();
        using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS entries(path TEXT PRIMARY KEY COLLATE NOCASE,parent TEXT COLLATE NOCASE,name TEXT,extension TEXT,isdir INTEGER,size INTEGER,created INTEGER,modified INTEGER,attributes INTEGER,volume TEXT);
            CREATE INDEX IF NOT EXISTS ix_parent ON entries(parent);
            CREATE INDEX IF NOT EXISTS ix_volume ON entries(volume);
            CREATE INDEX IF NOT EXISTS ix_size ON entries(size);
            CREATE INDEX IF NOT EXISTS ix_modified ON entries(modified);
            CREATE INDEX IF NOT EXISTS ix_extension ON entries(extension);
            CREATE INDEX IF NOT EXISTS ix_parent_volume_directory ON entries(parent,volume,isdir,path);
            CREATE INDEX IF NOT EXISTS ix_name_fold ON entries(fold(name),path);
            CREATE TABLE IF NOT EXISTS volumes(id TEXT PRIMARY KEY,root TEXT,label TEXT,total INTEGER,free INTEGER);
            CREATE TABLE IF NOT EXISTS scans(root TEXT PRIMARY KEY,date INTEGER,processed INTEGER,skipped INTEGER,status TEXT);
            CREATE TABLE IF NOT EXISTS history(date INTEGER,path TEXT,status TEXT,detail TEXT);
            CREATE TABLE IF NOT EXISTS search_work(root TEXT,volume TEXT,policy TEXT,recursive INTEGER,directories INTEGER,PRIMARY KEY(root,volume,policy,recursive));
            """; cmd.ExecuteNonQuery();
    }
    /// <summary>Opens a SQLite connection and registers invariant Unicode name folding for indexed queries.</summary>
    SqliteConnection Open() { var c = new SqliteConnection(connectionString); c.Open(); c.CreateFunction<string, string>("fold", s => (s ?? "").ToUpperInvariant(), isDeterministic: true); return c; }
    /// <summary>Persists available volume identities and their last known capacity metadata.</summary>
    public void RememberVolumes(IEnumerable<Volume> volumes)
    {
        using var c = Open(); foreach (var v in volumes) { using var q = c.CreateCommand(); q.CommandText = "INSERT OR REPLACE INTO volumes VALUES($id,$root,$label,$total,$free)"; q.Parameters.AddWithValue("$id", v.Id); q.Parameters.AddWithValue("$root", v.Root); q.Parameters.AddWithValue("$label", v.Label); q.Parameters.AddWithValue("$total", v.Total); q.Parameters.AddWithValue("$free", v.Free); q.ExecuteNonQuery(); }
    }
    /// <summary>Returns previously analyzed or refreshed roots for the explicit global index update.</summary>
    public List<string> IndexedLocations()
    {
        using var c = Open(); using var q = c.CreateCommand(); q.CommandText = "SELECT root FROM scans UNION SELECT root FROM search_work";
        using var r = q.ExecuteReader(); var roots = new List<string>(); while (r.Read()) roots.Add(r.GetString(0)); return roots;
    }
    /// <summary>Reads saved volume metadata without claiming that the devices are currently connected.</summary>
    public List<Volume> Volumes()
    {
        using var c = Open(); using var q = c.CreateCommand(); q.CommandText = "SELECT * FROM volumes ORDER BY root"; using var r = q.ExecuteReader(); var a = new List<Volume>(); while (r.Read()) a.Add(new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt64(3), r.GetInt64(4), false)); return a;
    }
    /// <summary>Writes indexed entries in one transaction without reading file contents.</summary>
    public void WriteBatch(IEnumerable<Entry> entries)
    {
        using var c = Open(); using var t = c.BeginTransaction(); using var q = c.CreateCommand(); q.Transaction = t;
        q.CommandText = "INSERT OR REPLACE INTO entries VALUES($p,$parent,$n,$e,$d,$s,$c,$m,$a,$v)";
        foreach (var n in new[] { "$p", "$parent", "$n", "$e", "$d", "$s", "$c", "$m", "$a", "$v" }) q.Parameters.Add(new SqliteParameter(n, null));
        foreach (var e in entries) { object[] values = [e.Path, e.Parent, e.Name, e.Extension, e.IsDirectory ? 1 : 0, e.Size, e.Created, e.Modified, e.Attributes, e.VolumeId]; for (int i = 0; i < values.Length; i++) q.Parameters[i].Value = values[i]; q.ExecuteNonQuery(); } t.Commit();
    }
    /// <summary>Atomically reconciles one directory snapshot, removing obsolete descendants when required.</summary>
    public void RefreshDirectory(string path, string volume, IEnumerable<DirectoryRefreshItem> items, CancellationToken token)
    {
        using var c = Open(); using var transaction = c.BeginTransaction();
        using var seen = c.CreateCommand(); seen.Transaction = transaction;
        seen.CommandText = "CREATE TEMP TABLE IF NOT EXISTS seen(path TEXT PRIMARY KEY COLLATE NOCASE); CREATE TEMP TABLE IF NOT EXISTS obsolete(path TEXT PRIMARY KEY COLLATE NOCASE); CREATE TEMP TABLE IF NOT EXISTS changed_directories(path TEXT PRIMARY KEY COLLATE NOCASE); DELETE FROM seen; DELETE FROM obsolete; DELETE FROM changed_directories;"; seen.ExecuteNonQuery();
        seen.CommandText = "INSERT OR IGNORE INTO seen VALUES($path)"; seen.Parameters.AddWithValue("$path", "");
        using var insert = c.CreateCommand(); insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO entries VALUES($p,$parent,$n,$e,$d,$s,$c,$m,$a,$v)
            ON CONFLICT(path) DO UPDATE SET path=excluded.path,parent=excluded.parent,name=excluded.name,extension=excluded.extension,
            isdir=excluded.isdir,size=excluded.size,created=excluded.created,modified=excluded.modified,attributes=excluded.attributes,volume=excluded.volume
            WHERE entries.path COLLATE BINARY IS NOT excluded.path COLLATE BINARY OR (entries.parent,entries.name,entries.extension,entries.isdir,entries.size,entries.created,entries.modified,entries.attributes,entries.volume)
            IS NOT (excluded.parent,excluded.name,excluded.extension,excluded.isdir,excluded.size,excluded.created,excluded.modified,excluded.attributes,excluded.volume);
            """;
        foreach (var name in new[] { "$p", "$parent", "$n", "$e", "$d", "$s", "$c", "$m", "$a", "$v" }) insert.Parameters.Add(new SqliteParameter(name, null));
        using var changed = c.CreateCommand(); changed.Transaction = transaction;
        changed.CommandText = "INSERT OR IGNORE INTO changed_directories SELECT path FROM entries WHERE path=$path COLLATE NOCASE AND volume=$volume AND isdir=1";
        changed.Parameters.AddWithValue("$path", ""); changed.Parameters.AddWithValue("$volume", volume);
        foreach (var item in items)
        {
            token.ThrowIfCancellationRequested();
            if (!string.Equals(System.IO.Path.GetDirectoryName(item.Path), path, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("El elemento no es hijo directo de la carpeta actualizada.");
            seen.Parameters[0].Value = item.Path; seen.ExecuteNonQuery();
            if (item.Entry is not { } e) continue; // On isolated errors retain previous metadata instead of assuming disappearance.
            if (e.VolumeId != volume || e.Path != item.Path) throw new ArgumentException("El elemento no corresponde al ámbito actualizado.");
            if (!e.IsDirectory) { changed.Parameters[0].Value = e.Path; changed.ExecuteNonQuery(); }
            object[] values = [e.Path, e.Parent, e.Name, e.Extension, e.IsDirectory ? 1 : 0, e.Size, e.Created, e.Modified, e.Attributes, e.VolumeId];
            for (int i = 0; i < values.Length; i++) insert.Parameters[i].Value = values[i];
            insert.ExecuteNonQuery();
        }
        token.ThrowIfCancellationRequested();
        using var reconcile = c.CreateCommand(); reconcile.Transaction = transaction;
        reconcile.CommandText = """
            INSERT INTO obsolete SELECT path FROM entries
            WHERE parent=$parent COLLATE NOCASE AND volume=$volume
            AND NOT EXISTS (SELECT 1 FROM seen WHERE seen.path=entries.path COLLATE NOCASE);
            SELECT (SELECT count(*) FROM obsolete)+(SELECT count(*) FROM changed_directories);
            """;
        reconcile.Parameters.AddWithValue("$parent", path); reconcile.Parameters.AddWithValue("$volume", volume);
        long obsoleteCount = (long)reconcile.ExecuteScalar()!;
        if (obsoleteCount > 0)
        {
            reconcile.CommandText = """
                DELETE FROM entries WHERE volume=$volume AND path LIKE $children ESCAPE '!' AND (
                  EXISTS (SELECT 1 FROM obsolete WHERE entries.path=obsolete.path COLLATE NOCASE
                   OR substr(entries.path,1,length(obsolete.path)+1)=obsolete.path || char(92) COLLATE NOCASE)
                  OR EXISTS (SELECT 1 FROM changed_directories WHERE
                   substr(entries.path,1,length(changed_directories.path)+1)=changed_directories.path || char(92) COLLATE NOCASE));
                """;
            reconcile.Parameters.AddWithValue("$children", Escape(path.TrimEnd('\\') + "\\") + "%"); reconcile.ExecuteNonQuery();
        }
        token.ThrowIfCancellationRequested(); transaction.Commit();
    }
    /// <summary>Removes a path and its indexed descendants after a completed file operation.</summary>
    public void RemoveTree(string path)
    {
        using var c = Open(); using var q = c.CreateCommand(); q.CommandText = "DELETE FROM entries WHERE path=$p COLLATE NOCASE OR path LIKE $child ESCAPE '!'"; q.Parameters.AddWithValue("$p", path); q.Parameters.AddWithValue("$child", Escape(path.TrimEnd('\\') + "\\") + "%"); q.ExecuteNonQuery();
    }
    /// <summary>Escapes literal characters for parameterized SQLite LIKE patterns.</summary>
    public static string Escape(string s) => s.Replace("!", "!!").Replace("%", "!%").Replace("_", "!_");
    /// <summary>Reads a bounded keyset page within the advanced-analysis scope, optionally prefiltering empty candidates.</summary>
    public List<Entry> AdvancedCandidates(SearchFilter scope, string after, bool includeDirectories, bool emptyOnly = false)
    {
        using var c = Open(); using var q = c.CreateCommand();
        var conditions = new List<string> { "path>$after COLLATE NOCASE" }; q.Parameters.AddWithValue("$after", after);
        if (!includeDirectories) conditions.Add("isdir=0");
        // One known child proves a folder is nonempty; do not count or traverse descendants.
        // Do not filter children by scope or exclusions: any child makes the folder nonempty.
        if (emptyOnly) conditions.Add("((isdir=0 AND size=0) OR (isdir=1 AND NOT EXISTS (SELECT 1 FROM entries child WHERE child.parent=entries.path AND child.volume=entries.volume AND child.path<>entries.path COLLATE NOCASE)))");
        if (scope.Folder.Length > 0) { var folder = Paths.Normalize(scope.Folder); conditions.Add("(parent=$folder OR parent LIKE $children ESCAPE '!')"); q.Parameters.AddWithValue("$folder", folder); q.Parameters.AddWithValue("$children", Escape(folder.TrimEnd('\\') + "\\") + "%"); }
        if (scope.Volumes.Length > 0)
        {
            var names = new List<string>(); foreach (var volume in scope.Volumes.Distinct()) { var name = "$v" + names.Count; names.Add(name); q.Parameters.AddWithValue(name, volume); }
            conditions.Add("volume IN (" + string.Join(',', names) + ")");
        }
        q.CommandText = "SELECT * FROM entries WHERE " + string.Join(" AND ", conditions) + " ORDER BY path COLLATE NOCASE LIMIT 500";
        using var r = q.ExecuteReader(); var entries = new List<Entry>(); while (r.Read()) entries.Add(new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt64(4) != 0, r.GetInt64(5), r.GetInt64(6), r.GetInt64(7), r.GetInt32(8), r.GetString(9))); return entries;
    }
    /// <summary>Streams files in indexed size order so rejected candidates can be replaced without repeated counts or OFFSET pages.</summary>
    public IEnumerable<Entry> LargestCandidates(SearchFilter scope, CancellationToken token)
    {
        using var c = Open(); using var q = c.CreateCommand(); token.ThrowIfCancellationRequested();
        // Migrate a large index on the first top-files worker, rather than during WPF startup.
        q.CommandText = "CREATE INDEX IF NOT EXISTS ix_largest ON entries(size DESC,path) WHERE isdir=0; CREATE INDEX IF NOT EXISTS ix_volume_largest ON entries(volume,size DESC,path) WHERE isdir=0"; q.ExecuteNonQuery(); token.ThrowIfCancellationRequested();
        var conditions = new List<string> { "isdir=0" };
        if (scope.Folder.Length > 0) { var folder = Paths.Normalize(scope.Folder); conditions.Add(scope.Recursive ? "(parent=$folder OR parent LIKE $children ESCAPE '!')" : "parent=$folder"); q.Parameters.AddWithValue("$folder", folder); if (scope.Recursive) q.Parameters.AddWithValue("$children", Escape(folder.TrimEnd('\\') + "\\") + "%"); }
        if (scope.Volumes.Length > 0) { var names = new List<string>(); foreach (var volume in scope.Volumes.Distinct()) { var name = "$v" + names.Count; names.Add(name); q.Parameters.AddWithValue(name, volume); } conditions.Add("volume IN (" + string.Join(',', names) + ")"); }
        // Use an ordered cursor without a global COUNT, OFFSET, or materializing every file.
        string index = scope.Volumes.Distinct().Count() == 1 ? "ix_volume_largest" : "ix_largest";
        q.CommandText = "SELECT * FROM entries INDEXED BY " + index + " WHERE " + string.Join(" AND ", conditions) + " ORDER BY size DESC,path";
        token.ThrowIfCancellationRequested(); using var r = q.ExecuteReader();
        while (r.Read()) { token.ThrowIfCancellationRequested(); yield return new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt64(4) != 0, r.GetInt64(5), r.GetInt64(6), r.GetInt64(7), r.GetInt32(8), r.GetString(9)); }
    }
    /// <summary>Streams indexed files from repeated-name or repeated-size buckets within the selected scope.</summary>
    public IEnumerable<Entry> DuplicateIndexCandidates(SearchFilter scope, bool byName, CancellationToken token)
    {
        using var c = Open(); using var q = c.CreateCommand(); q.CommandText = "PRAGMA temp_store=FILE"; q.ExecuteNonQuery();
        var conditions = new List<string> { "isdir=0" };
        if (scope.Folder.Length > 0) { var folder = Paths.Normalize(scope.Folder); conditions.Add("(parent=$folder OR parent LIKE $children ESCAPE '!')"); q.Parameters.AddWithValue("$folder", folder); q.Parameters.AddWithValue("$children", Escape(folder.TrimEnd('\\') + "\\") + "%"); }
        if (scope.Volumes.Length > 0)
        {
            var names = new List<string>(); foreach (var v in scope.Volumes.Distinct()) { var key = "$v" + names.Count; names.Add(key); q.Parameters.AddWithValue(key, v); }
            conditions.Add("volume IN (" + string.Join(',', names) + ")");
        }
        string keyColumn = byName ? "fold(name)" : "size";
        q.CommandText = "WITH scoped AS MATERIALIZED (SELECT * FROM entries WHERE " + string.Join(" AND ", conditions) + "), keys AS (SELECT " + keyColumn + " k FROM scoped GROUP BY " + keyColumn + " HAVING count(*)>1) SELECT * FROM scoped WHERE " + keyColumn + " IN (SELECT k FROM keys) ORDER BY path COLLATE NOCASE";
        token.ThrowIfCancellationRequested(); using var r = q.ExecuteReader();
        while (r.Read()) { token.ThrowIfCancellationRequested(); yield return new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt64(4) != 0, r.GetInt64(5), r.GetInt64(6), r.GetInt64(7), r.GetInt32(8), r.GetString(9)); }
    }
    // Discover the tree without counting or sorting the files in each directory.
    /// <summary>Reads a bounded keyset page of immediate indexed subdirectories for traversal planning.</summary>
    public List<string> ChildDirectories(string parent, string volume, string after = "")
    {
        using var c = Open(); using var q = c.CreateCommand();
        q.CommandText = "SELECT path FROM entries WHERE parent=$parent AND volume=$volume AND isdir=1 AND path>$after COLLATE NOCASE ORDER BY path COLLATE NOCASE LIMIT 500";
        q.Parameters.AddWithValue("$parent", parent); q.Parameters.AddWithValue("$volume", volume); q.Parameters.AddWithValue("$after", after);
        using var r = q.ExecuteReader(); var paths = new List<string>(); while (r.Read()) paths.Add(r.GetString(0)); return paths;
    }
    /// <summary>Applies parameterized AND/OR filters and returns one bounded, globally ordered result page and its indexed count.</summary>
    public SearchPage Search(SearchFilter f, string sort = "name", bool descending = false, int page = 0, int pageSize = 200, bool directoriesFirst = false)
    {
        if (page < 0 || pageSize is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(page));
        if (f.MinSize > f.MaxSize || f.CreatedFrom > f.CreatedTo || f.ModifiedFrom > f.ModifiedTo) throw new ArgumentException("El mínimo no puede superar el máximo.");
        using var c = Open(); using var q = c.CreateCommand(); var conditions = new List<string>();
        // Summary: Adds a parameterized filter predicate and value to the current SQL query.
        void Add(string sql, string key, object value) { conditions.Add(sql); q.Parameters.AddWithValue(key, value); }
        if (f.Name.Length > 0) Add(f.Exact ? "fold(name)=$name" : "fold(name) LIKE $name ESCAPE '!'", "$name", f.Exact ? f.Name.ToUpperInvariant() : "%" + Escape(f.Name.ToUpperInvariant()) + "%");
        // Summary: Builds an OR selection within one filter category using separate SQL parameters.
        void Many(string column, string[] values) { if (values.Length == 0) return; var names = new List<string>(); foreach (var v in values.Distinct()) { string k = "$x" + q.Parameters.Count; q.Parameters.AddWithValue(k, v); names.Add(k); } conditions.Add(column + " IN (" + string.Join(',', names) + ")"); }
        Many("extension", f.Extensions.Select(e => "." + e.Trim().TrimStart('.').ToUpperInvariant()).ToArray()); Many("volume", f.Volumes);
        if (f.IsDirectory.HasValue) Add("isdir=$dir", "$dir", f.IsDirectory.Value ? 1 : 0);
        if (f.MinSize.HasValue) Add("isdir=0 AND size >= $min", "$min", f.MinSize.Value);
        if (f.MaxSize.HasValue) Add("isdir=0 AND size <= $max", "$max", f.MaxSize.Value);
        if (f.CreatedFrom.HasValue) Add("created >= $cf", "$cf", f.CreatedFrom.Value);
        if (f.CreatedTo.HasValue) Add("created <= $ct", "$ct", f.CreatedTo.Value);
        if (f.ModifiedFrom.HasValue) Add("modified >= $mf", "$mf", f.ModifiedFrom.Value);
        if (f.ModifiedTo.HasValue) Add("modified <= $mt", "$mt", f.ModifiedTo.Value);
        if (f.Hidden.HasValue) Add("(attributes & 2)=$hidden", "$hidden", f.Hidden.Value ? 2 : 0);
        if (f.System.HasValue) Add("(attributes & 4)=$system", "$system", f.System.Value ? 4 : 0);
        if (f.Folder.Length > 0) { var p = Paths.Normalize(f.Folder); Add(f.Recursive ? "parent=$folder COLLATE NOCASE OR parent LIKE $desc ESCAPE '!'" : "parent=$folder COLLATE NOCASE", "$folder", p); if (f.Recursive) q.Parameters.AddWithValue("$desc", Escape(p.TrimEnd('\\') + "\\") + "%"); }
        var where = conditions.Count == 0 ? "" : " WHERE " + string.Join(" AND ", conditions.Select(s => "(" + s + ")"));
        // The expression index stores normalized names. Global searches
        // avoid executing a .NET function for each row on every fragment query.
        string source = f.Name.Length > 0 && f.Folder.Length == 0 ? "entries INDEXED BY ix_name_fold" : "entries";
        q.CommandText = "SELECT count(*) FROM " + source + where; long total = (long)q.ExecuteScalar()!;
        var column = sort switch { "size" => "size", "modified" => "modified", "type" => "extension", "path" => "path", _ => "fold(name)" };
        q.CommandText = "SELECT * FROM " + source + where + " ORDER BY " + (directoriesFirst ? "isdir DESC," : "") + column + (descending ? " DESC" : " ASC") + ",path LIMIT $limit OFFSET $offset"; q.Parameters.AddWithValue("$limit", pageSize); q.Parameters.AddWithValue("$offset", (long)page * pageSize);
        using var r = q.ExecuteReader(); var list = new List<Entry>(); while (r.Read()) list.Add(new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt64(4) != 0, r.GetInt64(5), r.GetInt64(6), r.GetInt64(7), r.GetInt32(8), r.GetString(9))); return new(list, total);
    }
    /// <summary>Records the scan time, actual coverage, omission count, and cancellation status.</summary>
    public void ScanFinished(ScanReport s)
    {
        using var c = Open(); using var q = c.CreateCommand(); q.CommandText = "INSERT OR REPLACE INTO scans VALUES($r,$d,$p,$s,$state)"; q.Parameters.AddWithValue("$r", s.Root); q.Parameters.AddWithValue("$d", DateTime.UtcNow.Ticks); q.Parameters.AddWithValue("$p", s.Processed); q.Parameters.AddWithValue("$s", s.Skipped); q.Parameters.AddWithValue("$state", s.Cancelled ? "Cancelado · índice parcial" : s.Skipped > 0 ? "Finalizado con omisiones" : "Finalizado"); q.ExecuteNonQuery();
    }
    /// <summary>Formats the most recent recorded scan outcomes for the history view.</summary>
    public List<string> ScanHistory() { using var c = Open(); using var q = c.CreateCommand(); q.CommandText = "SELECT root,date,processed,skipped,status FROM scans ORDER BY date DESC"; using var r = q.ExecuteReader(); var a = new List<string>(); while (r.Read()) a.Add($"{r.GetString(0)} · {new DateTime(r.GetInt64(1), DateTimeKind.Utc).ToLocalTime():g} · {r.GetInt64(2):N0} elementos · {r.GetInt64(3):N0} omitidos · {r.GetString(4)}"); return a; }
    /// <summary>Appends one file-operation outcome without storing file contents.</summary>
    public void Log(OperationResult r) { using var c = Open(); using var q = c.CreateCommand(); q.CommandText = "INSERT INTO history VALUES($d,$p,$s,$detail)"; q.Parameters.AddWithValue("$d", DateTime.UtcNow.Ticks); q.Parameters.AddWithValue("$p", r.Path); q.Parameters.AddWithValue("$s", r.Status); q.Parameters.AddWithValue("$detail", r.Detail); q.ExecuteNonQuery(); }
    /// <summary>Reads at most one thousand recent operation records for display.</summary>
    public List<string> History() { using var c = Open(); using var q = c.CreateCommand(); q.CommandText = "SELECT * FROM history ORDER BY date DESC LIMIT 1000"; using var r = q.ExecuteReader(); var a = new List<string>(); while (r.Read()) a.Add($"{new DateTime(r.GetInt64(0), DateTimeKind.Utc).ToLocalTime():g} · {r.GetString(2)} · {r.GetString(1)} · {r.GetString(3)}"); return a; }
    /// <summary>Deletes history records older than the configured bounded retention period.</summary>
    public void Prune(int days) { using var c = Open(); using var q = c.CreateCommand(); q.CommandText = "DELETE FROM history WHERE date < $cut"; q.Parameters.AddWithValue("$cut", DateTime.UtcNow.AddDays(-Math.Clamp(days, 1, 365)).Ticks); q.ExecuteNonQuery(); }
    /// <summary>Clears and compacts index metadata while leaving original files untouched.</summary>
    public void ClearIndex() { using var c = Open(); using var q = c.CreateCommand(); q.CommandText = "DELETE FROM entries; DELETE FROM scans; DELETE FROM volumes; DELETE FROM search_work; VACUUM;"; q.ExecuteNonQuery(); }
    /// <summary>Clears and compacts operation history without changing indexed files.</summary>
    public void ClearHistory() { using var c = Open(); using var q = c.CreateCommand(); q.CommandText = "DELETE FROM history; VACUUM;"; q.ExecuteNonQuery(); }
    /// <summary>Returns the volume identities represented by the current index.</summary>
    public HashSet<string> IndexedVolumes() { using var c = Open(); using var q = c.CreateCommand(); q.CommandText = "SELECT DISTINCT volume FROM entries"; using var r = q.ExecuteReader(); var ids = new HashSet<string>(); while (r.Read()) ids.Add(r.GetString(0)); return ids; }
    /// <summary>Clears cached entries while retaining auxiliary database metadata.</summary>
    public void ClearEntries() { using var c = Open(); using var q = c.CreateCommand(); q.CommandText = "DELETE FROM entries"; q.ExecuteNonQuery(); }
    /// <summary>Reads the roots that have recorded recursive scan outcomes.</summary>
    public List<string> ScannedRoots() { using var c = Open(); using var q = c.CreateCommand(); q.CommandText = "SELECT root FROM scans"; using var r = q.ExecuteReader(); var roots = new List<string>(); while (r.Read()) roots.Add(r.GetString(0)); return roots; }
    /// <summary>Looks up completed traversal statistics for the same root, policy, and recursion mode.</summary>
    public long? PreviousSearchWork(SearchRefreshRoot root, string policy)
    {
        using var c = Open(); using var q = c.CreateCommand(); q.CommandText = "SELECT directories FROM search_work WHERE root=$root AND volume=$volume AND policy=$policy AND recursive=$recursive";
        q.Parameters.AddWithValue("$root", root.Path); q.Parameters.AddWithValue("$volume", root.VolumeId); q.Parameters.AddWithValue("$policy", policy); q.Parameters.AddWithValue("$recursive", root.Recursive);
        return q.ExecuteScalar() is long n ? n : null;
    }
    /// <summary>Persists completed traversal statistics used by later search-time estimates.</summary>
    public void RememberSearchWork(SearchRefreshRoot root, string policy, long directories)
    {
        using var c = Open(); using var q = c.CreateCommand(); q.CommandText = "INSERT OR REPLACE INTO search_work VALUES($root,$volume,$policy,$recursive,$directories)";
        q.Parameters.AddWithValue("$root", root.Path); q.Parameters.AddWithValue("$volume", root.VolumeId); q.Parameters.AddWithValue("$policy", policy); q.Parameters.AddWithValue("$recursive", root.Recursive); q.Parameters.AddWithValue("$directories", directories); q.ExecuteNonQuery();
    }
}
