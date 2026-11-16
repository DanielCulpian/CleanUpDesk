using Microsoft.Data.Sqlite;

namespace CleanUpDesk.Core;
public enum AdvancedKind { DuplicateName, DuplicateSize, DuplicateHash, Empty, Largest }
public enum KeepRule { Newest, Oldest, Original }
/// <summary>Associates indexed file metadata with a duplicate group, identity, hash, and retention decision.</summary>
public sealed record AdvancedFinding(Entry File, string GroupKey, string Identity, string? Hash, bool Keep, bool Original)
{
    public string Name => File.Name;
    public string Path => File.Path;
    public string SizeText => File.IsDirectory ? Format.Bytes(0) : File.SizeText;
    public string ModifiedText => File.ModifiedText;
    public string GroupText => GroupKey.StartsWith("H:") ? "SHA256 · " + GroupKey[2..14] : GroupKey.StartsWith("N:") ? "Nombre · " + File.Name : GroupKey.StartsWith("S:") ? "Tamaño · " + File.SizeText : GroupKey;
    public string Decision => Keep ? Original ? "Conservar · original elegida" : "Conservar" : Original ? "Original elegida · por revisar" : "Por revisar";
    public bool IsDirectory => File.IsDirectory;
    public bool HasWarning => File.HasWarning;
    public FileRisk Risk => File.Risk;
    public string Availability => File.Availability;
}
/// <summary>Carries a bounded findings page and its total result count.</summary>
public sealed record AdvancedPage(IReadOnlyList<AdvancedFinding> Items, long Total);
/// <summary>Carries complete aggregate statistics for one duplicate group independently of its loaded file page.</summary>
public sealed record DuplicateGroupInfo(string Key, string Name, long Count, long Size, long Kept);
/// <summary>Carries a bounded group-summary page and complete group/file totals.</summary>
public sealed record DuplicateGroupPage(IReadOnlyList<DuplicateGroupInfo> Items, long Groups, long Files);
/// <summary>Carries a completed partial fingerprint and any validated reusable full hash for batched persistence.</summary>
public sealed record SampleUpdate(string Path, string Sample, long? Stamp, string? FullHash, bool Cached);
/// <summary>Associates a candidate with its partial fingerprint and native change stamp.</summary>
public sealed record SampledFinding(AdvancedFinding Finding, string Sample, long? Stamp);
/// <summary>Stores identity, metadata, change stamp, sample, and full hash for conservative reuse.</summary>
public sealed record CachedFingerprint(string Path, string Identity, long Size, long Created, long Modified, long Stamp, string Sample, string Hash);
public sealed class AdvancedResultStore
{
    readonly string connection;
    /// <summary>Creates or migrates the local advanced-results database and bounded fingerprint cache.</summary>
    public AdvancedResultStore(string database)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(database))!);
        connection = new SqliteConnectionStringBuilder { DataSource = database, Pooling = true }.ToString();
        using var c = Open(); using var q = c.CreateCommand(); q.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS sources(path TEXT PRIMARY KEY COLLATE NOCASE,parent TEXT,name TEXT,extension TEXT,isdir INTEGER,size INTEGER,created INTEGER,modified INTEGER,attributes INTEGER,volume TEXT,identity TEXT UNIQUE,hash TEXT,name_fold TEXT);
            CREATE INDEX IF NOT EXISTS source_size ON sources(size,isdir);
            CREATE INDEX IF NOT EXISTS source_name ON sources(name_fold);
            CREATE INDEX IF NOT EXISTS source_hash ON sources(hash);
            CREATE TABLE IF NOT EXISTS hash_sizes(size INTEGER PRIMARY KEY);
            CREATE TABLE IF NOT EXISTS sample_groups(size INTEGER,sample TEXT,PRIMARY KEY(size,sample));
            CREATE TABLE IF NOT EXISTS fingerprint_cache(path TEXT PRIMARY KEY COLLATE NOCASE,identity TEXT,size INTEGER,created INTEGER,modified INTEGER,stamp INTEGER,sample TEXT,hash TEXT,scheme TEXT,lastused INTEGER);
            CREATE TABLE IF NOT EXISTS findings(path TEXT PRIMARY KEY COLLATE NOCASE,parent TEXT,name TEXT,extension TEXT,isdir INTEGER,size INTEGER,created INTEGER,modified INTEGER,attributes INTEGER,volume TEXT,groupkey TEXT,identity TEXT,hash TEXT,keep INTEGER,original INTEGER);
            CREATE INDEX IF NOT EXISTS finding_groups ON findings(groupkey,modified,path);
            CREATE INDEX IF NOT EXISTS finding_size ON findings(size DESC,path);
            """; q.ExecuteNonQuery();
        q.CommandText = "PRAGMA table_info(sources)"; var columns = new HashSet<string>(); using (var r = q.ExecuteReader()) while (r.Read()) columns.Add(r.GetString(1));
        if (!columns.Contains("sample")) { q.CommandText = "ALTER TABLE sources ADD COLUMN sample TEXT"; q.ExecuteNonQuery(); }
        if (!columns.Contains("change_stamp")) { q.CommandText = "ALTER TABLE sources ADD COLUMN change_stamp INTEGER"; q.ExecuteNonQuery(); }
        q.CommandText = "CREATE INDEX IF NOT EXISTS source_sample ON sources(size,sample); CREATE INDEX IF NOT EXISTS cache_age ON fingerprint_cache(lastused)"; q.ExecuteNonQuery();
    }
    /// <summary>Opens SQLite with disk-backed temporary storage and a bounded page cache.</summary>
    SqliteConnection Open()
    {
        var c = new SqliteConnection(connection); c.Open(); using var q = c.CreateCommand();
        q.CommandText = "PRAGMA temp_store=FILE; PRAGMA cache_size=-4096;"; q.ExecuteNonQuery(); return c;
    }
    /// <summary>Clears working findings and optionally erases cached fingerprints and compacts the database.</summary>
    public void Clear(bool compact = false) { using var c = Open(); using var q = c.CreateCommand(); q.CommandText = "DELETE FROM sources; DELETE FROM findings; DELETE FROM hash_sizes; DELETE FROM sample_groups;" + (compact ? " DELETE FROM fingerprint_cache; VACUUM;" : ""); q.ExecuteNonQuery(); }
    /// <summary>Batches candidate metadata transactionally while rejecting repeated physical identities.</summary>
    public int AddSources(IEnumerable<AdvancedFinding> rows, CancellationToken token)
    {
        using var c = Open(); using var tx = c.BeginTransaction(); using var q = c.CreateCommand(); q.Transaction = tx;
        q.CommandText = "INSERT OR IGNORE INTO sources(path,parent,name,extension,isdir,size,created,modified,attributes,volume,identity,hash,name_fold) VALUES($p,$parent,$name,$ext,$dir,$size,$created,$modified,$attr,$volume,$identity,$hash,$fold)";
        string[] names = ["$p", "$parent", "$name", "$ext", "$dir", "$size", "$created", "$modified", "$attr", "$volume", "$identity", "$hash", "$fold"];
        foreach (var name in names) q.Parameters.Add(new SqliteParameter(name, null)); int added = 0;
        foreach (var row in rows)
        {
            token.ThrowIfCancellationRequested(); var e = row.File;
            object[] values = [e.Path, e.Parent, e.Name, e.Extension, e.IsDirectory, e.Size, e.Created, e.Modified, e.Attributes, e.VolumeId, row.Identity, (object?)row.Hash ?? DBNull.Value, e.Name.ToUpperInvariant()];
            for (int i = 0; i < values.Length; i++) q.Parameters[i].Value = values[i]; added += q.ExecuteNonQuery();
        }
        token.ThrowIfCancellationRequested(); tx.Commit(); return added;
    }
    const string HashCandidates = "isdir=0 AND size IN (SELECT size FROM hash_sizes)";
    /// <summary>Materializes repeated-size buckets and measures the possible full-hash workload.</summary>
    public (long Files, long Bytes) HashWork()
    {
        using var c = Open(); using var tx = c.BeginTransaction(); using var q = c.CreateCommand(); q.Transaction = tx;
        q.CommandText = "DELETE FROM hash_sizes; INSERT INTO hash_sizes SELECT size FROM sources WHERE isdir=0 GROUP BY size HAVING count(*)>1"; q.ExecuteNonQuery();
        q.CommandText = "SELECT count(*),coalesce(sum(size),0) FROM sources WHERE " + HashCandidates;
        (long Files, long Bytes) work; using (var r = q.ExecuteReader()) { r.Read(); work = (r.GetInt64(0), r.GetInt64(1)); } tx.Commit(); return work;
    }
    /// <summary>Reads the next bounded page of repeated-size candidates for sampling.</summary>
    public IReadOnlyList<AdvancedFinding> HashPage(string after)
    {
        using var c = Open(); using var q = c.CreateCommand();
        q.CommandText = "SELECT path,parent,name,extension,isdir,size,created,modified,attributes,volume,'',identity,hash,0,0 FROM sources WHERE " + HashCandidates + " AND path>$after COLLATE NOCASE ORDER BY path COLLATE NOCASE LIMIT 500"; q.Parameters.AddWithValue("$after", after);
        using var r = q.ExecuteReader(); var rows = new List<AdvancedFinding>(); while (r.Read()) rows.Add(Read(r)); return rows;
    }
    /// <summary>Commits completed full SHA256 values as a cancellable batch.</summary>
    public void SetHashes(IEnumerable<(string Path, string Hash)> hashes, CancellationToken token)
    {
        using var c = Open(); using var tx = c.BeginTransaction(); using var q = c.CreateCommand(); q.Transaction = tx;
        q.CommandText = "UPDATE sources SET hash=$hash WHERE path=$path"; q.Parameters.AddWithValue("$path", ""); q.Parameters.AddWithValue("$hash", "");
        foreach (var hash in hashes) { token.ThrowIfCancellationRequested(); q.Parameters[0].Value = hash.Path; q.Parameters[1].Value = hash.Hash; q.ExecuteNonQuery(); }
        token.ThrowIfCancellationRequested(); tx.Commit();
    }
    const string CacheScheme = "sha256-head-tail-4096-v1";
    /// <summary>Loads unexpired compatible fingerprints only for the supplied candidate page.</summary>
    public Dictionary<string, CachedFingerprint> CachedFor(IReadOnlyList<AdvancedFinding> rows)
    {
        var result = new Dictionary<string, CachedFingerprint>(StringComparer.OrdinalIgnoreCase); if (rows.Count == 0) return result;
        using var c = Open(); using var q = c.CreateCommand(); var names = new List<string>();
        foreach (var row in rows) { var name = "$p" + names.Count; names.Add(name); q.Parameters.AddWithValue(name, row.Path); }
        q.Parameters.AddWithValue("$scheme", CacheScheme);
        q.Parameters.AddWithValue("$cutoff", DateTime.UtcNow.AddDays(-30).Ticks);
        q.CommandText = "SELECT path,identity,size,created,modified,stamp,sample,hash FROM fingerprint_cache WHERE scheme=$scheme AND lastused >= $cutoff AND path IN (" + string.Join(',', names) + ")";
        using var r = q.ExecuteReader(); while (r.Read()) { var item = new CachedFingerprint(r.GetString(0), r.GetString(1), r.GetInt64(2), r.GetInt64(3), r.GetInt64(4), r.GetInt64(5), r.GetString(6), r.GetString(7)); result.Add(item.Path, item); } return result;
    }
    /// <summary>Commits partial hashes, change stamps, and validated reusable full hashes.</summary>
    public void SetSamples(IEnumerable<SampleUpdate> samples, CancellationToken token)
    {
        using var c = Open(); using var tx = c.BeginTransaction(); using var q = c.CreateCommand(); q.Transaction = tx;
        q.CommandText = "UPDATE sources SET sample=$sample,change_stamp=$stamp,hash=$hash WHERE path=$path";
        foreach (var name in new[] { "$sample", "$stamp", "$hash", "$path" }) q.Parameters.Add(new SqliteParameter(name, null));
        using var touched = c.CreateCommand(); touched.Transaction = tx; touched.CommandText = "UPDATE fingerprint_cache SET lastused=$now WHERE path=$path"; touched.Parameters.AddWithValue("$now", DateTime.UtcNow.Ticks); touched.Parameters.AddWithValue("$path", "");
        foreach (var sample in samples)
        {
            token.ThrowIfCancellationRequested(); q.Parameters[0].Value = sample.Sample; q.Parameters[1].Value = (object?)sample.Stamp ?? DBNull.Value; q.Parameters[2].Value = (object?)sample.FullHash ?? DBNull.Value; q.Parameters[3].Value = sample.Path; q.ExecuteNonQuery();
            if (sample.Cached) { touched.Parameters[1].Value = sample.Path; touched.ExecuteNonQuery(); }
        }
        token.ThrowIfCancellationRequested(); tx.Commit();
    }
    /// <summary>Selects repeated sample buckets and estimates the remaining complete-file reads.</summary>
    public long PrepareFullHashes()
    {
        using var c = Open(); using var q = c.CreateCommand(); q.CommandText = "DELETE FROM sample_groups; INSERT INTO sample_groups SELECT size,sample FROM sources WHERE sample IS NOT NULL GROUP BY size,sample HAVING count(*)>1; SELECT coalesce(sum(size),0) FROM sources WHERE hash IS NULL AND (size,sample) IN (SELECT size,sample FROM sample_groups)"; return (long)q.ExecuteScalar()!;
    }
    /// <summary>Reads a bounded keyset page that still requires complete SHA256 verification.</summary>
    public IReadOnlyList<SampledFinding> FullHashPage(string after)
    {
        using var c = Open(); using var q = c.CreateCommand(); q.CommandText = "SELECT path,parent,name,extension,isdir,size,created,modified,attributes,volume,'',identity,hash,0,0,sample,change_stamp FROM sources WHERE hash IS NULL AND (size,sample) IN (SELECT size,sample FROM sample_groups) AND path>$after COLLATE NOCASE ORDER BY path COLLATE NOCASE LIMIT 500"; q.Parameters.AddWithValue("$after", after);
        using var r = q.ExecuteReader(); var rows = new List<SampledFinding>(); while (r.Read()) rows.Add(new(Read(r), r.GetString(15), r.IsDBNull(16) ? null : r.GetInt64(16))); return rows;
    }
    /// <summary>Persists completed, reusable fingerprints transactionally without storing file contents.</summary>
    public void SaveFingerprints(IEnumerable<CachedFingerprint> fingerprints, CancellationToken token)
    {
        using var c = Open(); using var tx = c.BeginTransaction(); using var q = c.CreateCommand(); q.Transaction = tx;
        q.CommandText = "INSERT OR REPLACE INTO fingerprint_cache VALUES($path,$identity,$size,$created,$modified,$stamp,$sample,$hash,$scheme,$now)";
        foreach (var name in new[] { "$path", "$identity", "$size", "$created", "$modified", "$stamp", "$sample", "$hash", "$scheme", "$now" }) q.Parameters.Add(new SqliteParameter(name, null));
        foreach (var item in fingerprints)
        {
            token.ThrowIfCancellationRequested(); object[] values = [item.Path, item.Identity, item.Size, item.Created, item.Modified, item.Stamp, item.Sample, item.Hash, CacheScheme, DateTime.UtcNow.Ticks];
            for (int i = 0; i < values.Length; i++) q.Parameters[i].Value = values[i]; q.ExecuteNonQuery();
        }
        token.ThrowIfCancellationRequested(); tx.Commit();
    }
    /// <summary>Expires unused fingerprints after thirty days and caps the cache at one hundred thousand entries.</summary>
    public void TrimFingerprints()
    {
        using var c = Open(); using var q = c.CreateCommand(); q.CommandText = "DELETE FROM fingerprint_cache WHERE lastused<$cutoff; DELETE FROM fingerprint_cache WHERE path IN (SELECT path FROM fingerprint_cache ORDER BY lastused DESC LIMIT -1 OFFSET 100000)"; q.Parameters.AddWithValue("$cutoff", DateTime.UtcNow.AddDays(-30).Ticks); q.ExecuteNonQuery();
    }
    /// <summary>Builds final findings from successful candidates without publishing incomplete duplicate groups.</summary>
    public void Complete(AdvancedKind kind, CancellationToken token)
    {
        using var c = Open(); using var tx = c.BeginTransaction(); using var q = c.CreateCommand(); q.Transaction = tx;
        string key = kind switch { AdvancedKind.DuplicateName => "'N:'||name_fold", AdvancedKind.DuplicateSize => "'S:'||size", AdvancedKind.DuplicateHash => "'H:'||hash", AdvancedKind.Empty => "CASE WHEN isdir=1 THEN 'Carpeta vacía' ELSE 'Archivo de 0 bytes' END", _ => "'Top 100 · índice'" };
        string where = kind switch
        {
            AdvancedKind.DuplicateName => "name_fold IN (SELECT name_fold FROM sources GROUP BY name_fold HAVING count(*)>1)",
            AdvancedKind.DuplicateSize => "size IN (SELECT size FROM sources GROUP BY size HAVING count(*)>1)",
            AdvancedKind.DuplicateHash => "hash IS NOT NULL AND hash IN (SELECT hash FROM sources WHERE hash IS NOT NULL GROUP BY hash HAVING count(*)>1)",
            _ => "1=1"
        };
        token.ThrowIfCancellationRequested(); q.CommandText = "DELETE FROM findings; INSERT INTO findings SELECT path,parent,name,extension,isdir,size,created,modified,attributes,volume," + key + ",identity,hash,0,0 FROM sources WHERE " + where; q.ExecuteNonQuery(); token.ThrowIfCancellationRequested(); tx.Commit();
    }
    /// <summary>Returns at most two hundred findings in deterministic group or size order.</summary>
    public AdvancedPage Page(int page, bool largest = false)
    {
        if (page < 0) throw new ArgumentOutOfRangeException(nameof(page));
        using var c = Open(); using var q = c.CreateCommand(); q.CommandText = "SELECT count(*) FROM findings"; long total = (long)q.ExecuteScalar()!;
        q.CommandText = "SELECT * FROM findings ORDER BY " + (largest ? "size DESC,path" : "groupkey,path") + " LIMIT 200 OFFSET $offset"; q.Parameters.AddWithValue("$offset", (long)page * 200);
        using var r = q.ExecuteReader(); var rows = new List<AdvancedFinding>(); while (r.Read()) rows.Add(Read(r)); return new(rows, total);
    }
    /// <summary>Returns ten complete group summaries and global group/file counts for the current page.</summary>
    public DuplicateGroupPage DuplicateGroups(int page)
    {
        if (page < 0) throw new ArgumentOutOfRangeException(nameof(page));
        using var c = Open(); using var q = c.CreateCommand(); q.CommandText = "SELECT count(DISTINCT groupkey),count(*) FROM findings";
        long groups, files; using (var r = q.ExecuteReader()) { r.Read(); groups = r.GetInt64(0); files = r.GetInt64(1); }
        q.CommandText = "SELECT groupkey,min(name),count(*),sum(size),sum(keep) FROM findings GROUP BY groupkey ORDER BY groupkey LIMIT 10 OFFSET $offset"; q.Parameters.AddWithValue("$offset", page * 10L);
        using var reader = q.ExecuteReader(); var rows = new List<DuplicateGroupInfo>(); while (reader.Read()) rows.Add(new(reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4)));
        return new(rows, groups, files);
    }
    /// <summary>Reads fifty files from one parameterized duplicate group using its path index.</summary>
    public IReadOnlyList<AdvancedFinding> DuplicateGroupItems(string key, int page)
    {
        if (page < 0) throw new ArgumentOutOfRangeException(nameof(page));
        using var c = Open(); using var q = c.CreateCommand(); q.CommandText = "CREATE INDEX IF NOT EXISTS finding_group_path ON findings(groupkey,path)"; q.ExecuteNonQuery();
        q.CommandText = "SELECT * FROM findings WHERE groupkey=$key ORDER BY path LIMIT 50 OFFSET $offset"; q.Parameters.AddWithValue("$key", key); q.Parameters.AddWithValue("$offset", page * 50L);
        using var r = q.ExecuteReader(); var rows = new List<AdvancedFinding>(); while (r.Read()) rows.Add(Read(r)); return rows;
    }
    /// <summary>Counts the distinct groups in the current findings.</summary>
    public long Groups() { using var c = Open(); using var q = c.CreateCommand(); q.CommandText = "SELECT count(DISTINCT groupkey) FROM findings"; return (long)q.ExecuteScalar()!; }
    /// <summary>Marks exactly one keeper per group according to the selected retention rule without deleting anything.</summary>
    public void ApplyRule(KeepRule rule)
    {
        using var c = Open(); using var tx = c.BeginTransaction(); using var q = c.CreateCommand(); q.Transaction = tx;
        if (rule == KeepRule.Original)
        {
            q.CommandText = "SELECT count(*) FROM (SELECT groupkey FROM findings GROUP BY groupkey HAVING sum(original)<>1)";
            if ((long)q.ExecuteScalar()! > 0) throw new InvalidOperationException("Marca una copia original en cada grupo antes de aplicar esta regla.");
            q.CommandText = "UPDATE findings SET keep=original";
        }
        else
        {
            string direction = rule == KeepRule.Newest ? "DESC" : "ASC";
            q.CommandText = "WITH ranked AS (SELECT path,row_number() OVER(PARTITION BY groupkey ORDER BY modified " + direction + ",created ASC,path COLLATE NOCASE) rn FROM findings) UPDATE findings SET keep=CASE WHEN path IN (SELECT path FROM ranked WHERE rn=1) THEN 1 ELSE 0 END";
        }
        q.ExecuteNonQuery(); tx.Commit();
    }
    /// <summary>Marks the user-selected original and keeper within its group.</summary>
    public void MarkOriginal(string path)
    {
        using var c = Open(); using var q = c.CreateCommand(); q.CommandText = "UPDATE findings SET original=CASE WHEN path=$path THEN 1 ELSE 0 END,keep=CASE WHEN path=$path THEN 1 ELSE 0 END WHERE groupkey=(SELECT groupkey FROM findings WHERE path=$path)"; q.Parameters.AddWithValue("$path", path); q.ExecuteNonQuery();
    }
    /// <summary>Returns at most five hundred non-keeper paths from groups that have a chosen keeper.</summary>
    public IReadOnlyList<string> Surplus()
    {
        using var c = Open(); using var q = c.CreateCommand(); q.CommandText = "SELECT path FROM findings f WHERE keep=0 AND EXISTS (SELECT 1 FROM findings k WHERE k.groupkey=f.groupkey AND k.keep=1) ORDER BY groupkey,path LIMIT 500";
        using var r = q.ExecuteReader(); var rows = new List<string>(); while (r.Read()) rows.Add(r.GetString(0)); return rows;
    }
    /// <summary>Removes completed deletion results and discards duplicate groups that no longer have multiple copies.</summary>
    public void RemoveCompleted(IEnumerable<string> paths, bool duplicates)
    {
        using var c = Open(); using var tx = c.BeginTransaction(); using var q = c.CreateCommand(); q.Transaction = tx;
        q.CommandText = "DELETE FROM findings WHERE path=$path"; q.Parameters.AddWithValue("$path", "");
        foreach (var path in paths) { q.Parameters[0].Value = path; q.ExecuteNonQuery(); }
        if (duplicates) { q.CommandText = "DELETE FROM findings WHERE groupkey IN (SELECT groupkey FROM findings GROUP BY groupkey HAVING count(*)<2)"; q.ExecuteNonQuery(); }
        tx.Commit();
    }
    /// <summary>Creates a bounded deletion plan only when each selected duplicate group retains exactly one unselected keeper.</summary>
    public AdvancedDeletionPlan Plan(AdvancedKind kind, IEnumerable<string> paths)
    {
        var targets = new List<AdvancedFinding>(); var keepers = new Dictionary<string, AdvancedFinding>();
        var selection = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(); if (selection.Length is 0 or > 500) throw new InvalidOperationException("Selecciona entre 1 y 500 elementos.");
        using var c = Open(); using var tx = c.BeginTransaction(); using var q = c.CreateCommand(); q.Transaction = tx;
        foreach (var path in selection)
        {
            q.Parameters.Clear(); q.CommandText = "SELECT * FROM findings WHERE path=$path"; q.Parameters.AddWithValue("$path", path);
            AdvancedFinding row; using (var r = q.ExecuteReader()) { if (!r.Read()) throw new IOException("Los resultados cambiaron; analiza de nuevo."); row = Read(r); }
            if (kind is AdvancedKind.DuplicateName or AdvancedKind.DuplicateSize or AdvancedKind.DuplicateHash)
            {
                if (row.Keep) throw new InvalidOperationException("La selección incluye una copia marcada para conservar.");
                q.CommandText = "SELECT * FROM findings WHERE groupkey=$group AND keep=1"; q.Parameters.AddWithValue("$group", row.GroupKey);
                using var r = q.ExecuteReader(); if (!r.Read()) throw new InvalidOperationException("Elige qué copia conservar en cada grupo seleccionado.");
                var keeper = Read(r); if (r.Read() || selection.Contains(keeper.Path, StringComparer.OrdinalIgnoreCase)) throw new InvalidOperationException("Cada grupo debe conservar exactamente una copia fuera de la selección."); keepers[row.GroupKey] = keeper;
            }
            targets.Add(row);
        }
        tx.Commit(); return new(kind, targets, keepers.Values.ToArray());
    }
    /// <summary>Maps one findings row to its immutable file metadata and retention decision.</summary>
    static AdvancedFinding Read(SqliteDataReader r) => new(new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt64(4) != 0, r.GetInt64(5), r.GetInt64(6), r.GetInt64(7), r.GetInt32(8), r.GetString(9)), r.GetString(10), r.GetString(11), r.IsDBNull(12) ? null : r.GetString(12), r.GetInt64(13) != 0, r.GetInt64(14) != 0);
}
