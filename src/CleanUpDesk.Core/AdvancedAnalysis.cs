using System.Security.Cryptography;

namespace CleanUpDesk.Core;
/// <summary>Carries actual advanced-analysis progress, phase, read volume, and reusable-hash counts.</summary>
public sealed record AdvancedProgress(string Path, long Processed, long Hashed, long Bytes, long? TotalBytes) { public string Phase { get; init; } = "Analizando"; public long Cached { get; init; } }
/// <summary>Describes completed advanced findings, omissions, groups, and measured content reads.</summary>
public sealed record AdvancedReport(AdvancedKind Kind, long Processed, long Skipped, long Findings, long Groups, long BytesRead) { public long Sampled { get; init; } public long Cached { get; init; } public long FullReads { get; init; } }
/// <summary>Creates the advanced-analysis coordinator with injectable availability, identity, and emptiness probes.</summary>
public sealed class AdvancedAnalysisService(IndexStore index, AdvancedResultStore results, Func<string, bool>? available = null, Func<string, string>? getIdentity = null, Func<string, long?>? changeStamp = null, Func<string, bool>? directoryEmpty = null)
{
    readonly Func<string, string> readIdentity = getIdentity ?? NativeFiles.Identity;
    readonly Func<string, long?> readStamp = changeStamp ?? NativeFiles.ChangeStamp;
    /// <summary>Builds advanced findings from indexed candidates with bounded I/O, cancellation, exclusions, and optional eligibility checks.</summary>
    public async Task<AdvancedReport> AnalyzeAsync(AdvancedKind kind, SearchFilter scope, IEnumerable<string> exclusions, CancellationToken token, IProgress<AdvancedProgress>? progress = null, Func<Entry, bool>? include = null)
    {
        var excluded = exclusions.Select(Paths.Normalize).ToArray();
        var volumes = available == null ? await Task.Run(() => new VolumeService().Detect().Where(v => v.Available).ToArray(), token) : [];
        // Summary: Checks whether an entry belongs to an available volume with the expected identity.
        bool Online(Entry e) => available?.Invoke(e.Path) ?? volumes.Any(v => v.Id == e.VolumeId && Paths.Within(e.Path, v.Root));
        return await Task.Run(async () =>
        {
            results.Clear(); long processed = 0, skipped = 0, hashed = 0, bytes = 0, sampled = 0, cached = 0, fullReads = 0; string phase = "Prefiltro del índice"; var clock = System.Diagnostics.Stopwatch.StartNew(); long last = -200;
            // Summary: Throttles real progress reports without inventing a percentage for unknown work.
            void Publish(string path, long? total = null, bool force = false)
            {
                if (!force && clock.ElapsedMilliseconds - last < 200) return; last = clock.ElapsedMilliseconds; progress?.Report(new(path, processed, hashed, bytes, total) { Phase = phase, Cached = cached });
            }
            try
            {
                if (kind == AdvancedKind.Largest)
                {
                    token.ThrowIfCancellationRequested();
                    // The indexed top 100 retains offline results, marked as unavailable in the view.
                    var rows = new List<AdvancedFinding>();
                    foreach (var entry in index.LargestCandidates(scope, token))
                    {
                            token.ThrowIfCancellationRequested(); if (excluded.Any(e => Paths.Within(entry.Path, e)) || (include != null && (!Online(entry) || !include(entry)))) { skipped++; continue; }
                            string identity = "unavailable:" + entry.Path;
                            try { if (Online(entry)) { Paths.NoReparseAncestors(entry.Path); identity = readIdentity(entry.Path); } }
                            catch (Exception e) when (Scanner.IsReadError(e)) { skipped++; if (include != null) continue; }
                            if (rows.Any(r => r.Identity == identity)) { skipped++; continue; }
                            rows.Add(new(entry, "", identity, null, false, false)); processed++; Publish(entry.Path); if (rows.Count == 100) break;
                    }
                    results.AddSources(rows, token);
                }
                else
                {
                    // Summary: Enumerates duplicate or empty candidates through bounded index queries.
                    IEnumerable<Entry> Entries()
                    {
                        if (kind != AdvancedKind.Empty) { foreach (var entry in index.DuplicateIndexCandidates(scope, kind == AdvancedKind.DuplicateName, token)) yield return entry; yield break; }
                        string after = "";
                        phase = "Comprobando candidatos vacíos";
                        while (true) { token.ThrowIfCancellationRequested(); var page = index.AdvancedCandidates(scope, after, true, emptyOnly: true); foreach (var entry in page) yield return entry; if (page.Count < 500) break; after = page[^1].Path; }
                    }
                    var batch = new List<AdvancedFinding>(500);
                    foreach (var indexed in Entries())
                    {
                        token.ThrowIfCancellationRequested(); processed++; Publish(indexed.Path);
                        if (!Online(indexed) || excluded.Any(e => Paths.Within(indexed.Path, e))) { skipped++; continue; }
                        try
                        {
                            Paths.NoReparseAncestors(indexed.Path); var current = Paths.ReadEntry(indexed.Path, indexed.VolumeId);
                            if (kind == AdvancedKind.Empty && (current.IsDirectory ? !(directoryEmpty ?? Paths.IsDirectoryEmpty)(current.Path) : current.Size != 0)) continue;
                            if (include != null && !include(current)) { skipped++; continue; }
                            batch.Add(new(current, "", readIdentity(current.Path), null, false, false));
                        }
                        catch (Exception e) when (Scanner.IsReadError(e)) { skipped++; }
                        if (batch.Count == 500) { skipped += batch.Count - results.AddSources(batch, token); batch.Clear(); }
                    }
                    skipped += batch.Count - results.AddSources(batch, token);
                    if (kind == AdvancedKind.DuplicateHash)
                    {
                        phase = "Huellas parciales"; results.HashWork(); string hashAfter = ""; Publish("Inicio del muestreo", force: true);
                        while (true)
                        {
                            token.ThrowIfCancellationRequested(); var page = results.HashPage(hashAfter); var updates = new List<SampleUpdate>(); var prior = results.CachedFor(page);
                            foreach (var row in page)
                            {
                                token.ThrowIfCancellationRequested();
                                try
                                {
                                    AdvancedDeletionPlan.VerifyMetadata(row);
                                    bool small = row.File.Size <= 8192;
                                    // Disable read-ahead buffering for the two sparse samples.
                                    using var stream = new FileStream(row.Path, FileMode.Open, FileAccess.Read, FileShare.Read, small ? 65536 : 1, FileOptions.Asynchronous | (small ? FileOptions.SequentialScan : FileOptions.RandomAccess));
                                    if (stream.Length != row.File.Size) throw new IOException("El tamaño cambió durante el análisis.");
                                    long? before = small ? null : readStamp(row.Path);
                                    var sample = small ? await HashAsync(stream, token, n => { bytes += n; Publish(row.Path, force: bytes == n); })
                                        : await SampleAsync(stream, token, n => { bytes += n; Publish(row.Path, force: bytes == n); });
                                    AdvancedDeletionPlan.VerifyMetadata(row); long? after = small ? null : readStamp(row.Path);
                                    if (before.HasValue && before != after) throw new IOException("El elemento cambió durante el muestreo.");
                                    long? stamp = before.HasValue && before == after ? before : null;
                                    bool hit = !small && stamp.HasValue && prior.TryGetValue(row.Path, out var old) && old.Identity == row.Identity && old.Size == row.File.Size && old.Created == row.File.Created && old.Modified == row.File.Modified && old.Stamp == stamp.Value && old.Sample == sample;
                                    string? full = small ? sample : hit ? prior[row.Path].Hash : null;
                                    updates.Add(new(row.Path, sample, stamp, full, hit)); sampled++; if (hit) cached++; if (small) fullReads++; if (full != null) hashed++; Publish(row.Path);
                                }
                                catch (Exception e) when (Scanner.IsReadError(e)) { skipped++; }
                            }
                            results.SetSamples(updates, token);
                            if (page.Count < 500) break; hashAfter = page[^1].Path;
                        }
                        phase = "SHA256 completo"; long total = bytes + results.PrepareFullHashes(); hashAfter = ""; Publish("Verificando candidatos restantes", total, true);
                        while (true)
                        {
                            token.ThrowIfCancellationRequested(); var page = results.FullHashPage(hashAfter); var hashes = new List<(string Path, string Hash)>(); var fingerprints = new List<CachedFingerprint>();
                            foreach (var candidate in page)
                            {
                                token.ThrowIfCancellationRequested(); var row = candidate.Finding;
                                try
                                {
                                    AdvancedDeletionPlan.VerifyMetadata(row); long? before = readStamp(row.Path);
                                    if (candidate.Stamp.HasValue && before != candidate.Stamp) throw new IOException("El elemento cambió entre las etapas del análisis.");
                                    using var stream = new FileStream(row.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
                                    var hash = await HashAsync(stream, token, n => { bytes += n; Publish(row.Path, total); });
                                    AdvancedDeletionPlan.VerifyMetadata(row); long? after = readStamp(row.Path);
                                    if (before.HasValue && before != after) throw new IOException("El elemento cambió mientras se calculaba SHA256.");
                                    hashes.Add((row.Path, hash)); hashed++; fullReads++;
                                    if (candidate.Stamp.HasValue && candidate.Stamp == after) fingerprints.Add(new(row.Path, row.Identity, row.File.Size, row.File.Created, row.File.Modified, candidate.Stamp.Value, candidate.Sample, hash));
                                    Publish(row.Path, total);
                                }
                                catch (Exception e) when (Scanner.IsReadError(e)) { skipped++; }
                            }
                            results.SetHashes(hashes, token); results.SaveFingerprints(fingerprints, token);
                            if (page.Count < 500) break; hashAfter = page[^1].Finding.Path;
                        }
                    }
                }
                token.ThrowIfCancellationRequested(); results.Complete(kind, token); results.TrimFingerprints(); phase = "Terminado"; Publish("Finalizado", force: true);
                return new AdvancedReport(kind, processed, skipped, results.Page(0).Total, results.Groups(), bytes) { Sampled = sampled, Cached = cached, FullReads = fullReads };
            }
            catch { results.Clear(); throw; }
        }, token);
    }
    /// <summary>Hashes the first and last four KiB to reject large unequal files before a full-content hash.</summary>
    static async Task<string> SampleAsync(FileStream stream, CancellationToken token, Action<int> consumed)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(4096);
        try
        {
            foreach (var offset in new[] { 0L, stream.Length - 4096 })
            {
                stream.Position = offset; int done = 0;
                while (done < 4096) { int read = await stream.ReadAsync(buffer.AsMemory(done, 4096 - done), token); if (read == 0) throw new EndOfStreamException("El archivo cambió durante el muestreo."); done += read; consumed(read); }
                sha.AppendData(buffer, 0, 4096);
            }
            return Convert.ToHexString(sha.GetHashAndReset());
        }
        finally { System.Buffers.ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }
    /// <summary>Computes SHA256 with a bounded pooled buffer and cancellation-aware streaming reads.</summary>
    internal static async Task<string> HashAsync(Stream stream, CancellationToken token, Action<int>? consumed = null)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(65536);
        try
        {
            while (true) { token.ThrowIfCancellationRequested(); int read = await stream.ReadAsync(buffer.AsMemory(0, 65536), token); if (read == 0) break; sha.AppendData(buffer, 0, read); consumed?.Invoke(read); }
            return Convert.ToHexString(sha.GetHashAndReset());
        }
        finally { System.Buffers.ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }
}

/// <summary>An immutable plan that retains copies, revalidates results, and rejects folders that are no longer empty.</summary>
public sealed class AdvancedDeletionPlan
{
    readonly AdvancedKind kind;
    readonly IReadOnlyList<AdvancedFinding> targets, keepers;
    readonly Dictionary<string, FileStream> held = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> activeTargets = new(StringComparer.OrdinalIgnoreCase);
    CancellationToken cancellation;
    bool leased;
    /// <summary>Captures immutable target and keeper snapshots for an advanced deletion review.</summary>
    public AdvancedDeletionPlan(AdvancedKind kind, IReadOnlyList<AdvancedFinding> targets, IReadOnlyList<AdvancedFinding> keepers) { this.kind = kind; this.targets = targets.ToArray(); this.keepers = keepers.ToArray(); }
    public IReadOnlyList<string> Paths => targets.Select(t => t.Path).ToArray();
    public bool RequiresPermanentFolders => kind == AdvancedKind.Empty && targets.Any(t => t.File.IsDirectory);
    /// <summary>Revalidates reviewed targets and holds verified keeper streams until the deletion batch finishes.</summary>
    public async Task<IDisposable> AcquireAsync(CancellationToken token, IEnumerable<string>? retained = null)
    {
        if (leased) throw new InvalidOperationException("Este plan ya se está utilizando."); cancellation = token;
        try
        {
            var selected = (retained ?? Paths).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (selected.Count == 0 || selected.Any(p => !targets.Any(t => t.Path.Equals(p, StringComparison.OrdinalIgnoreCase)))) throw new IOException("La revisión contiene destinos fuera del plan.");
            activeTargets.UnionWith(selected); var groups = targets.Where(t => selected.Contains(t.Path)).Select(t => t.GroupKey).ToHashSet();
            foreach (var keeper in keepers.Where(k => groups.Contains(k.GroupKey)))
            {
                token.ThrowIfCancellationRequested(); VerifyMetadata(keeper);
                var stream = new FileStream(keeper.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan); held.Add(keeper.Path, stream);
                if (kind == AdvancedKind.DuplicateHash && await AdvancedAnalysisService.HashAsync(stream, token) != keeper.Hash) throw new IOException("La copia a conservar cambió de contenido. Analiza de nuevo.");
                VerifyMetadata(keeper);
            }
            leased = true; return new Lease(this);
        }
        catch { Release(); throw; }
    }
    /// <summary>Rechecks target identity and contents or emptiness immediately before the backend acts.</summary>
    public void VerifyTarget(string path)
    {
        cancellation.ThrowIfCancellationRequested(); if (!leased || !activeTargets.Contains(path)) throw new IOException("Falta una revisión final vigente para este destino.");
        var row = targets.SingleOrDefault(t => t.Path.Equals(path, StringComparison.OrdinalIgnoreCase)) ?? throw new IOException("Destino fuera del plan revisado.");
        VerifyMetadata(row);
        if (kind is AdvancedKind.DuplicateName or AdvancedKind.DuplicateSize or AdvancedKind.DuplicateHash)
        {
            var keeper = keepers.SingleOrDefault(k => k.GroupKey == row.GroupKey) ?? throw new IOException("Falta una copia a conservar.");
            if (!held.ContainsKey(keeper.Path) || targets.Any(t => t.Path.Equals(keeper.Path, StringComparison.OrdinalIgnoreCase))) throw new IOException("La copia a conservar no está protegida por el plan.");
            VerifyMetadata(keeper);
            if (kind == AdvancedKind.DuplicateHash)
            {
                using var stream = new FileStream(row.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
                using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(65536);
                try
                {
                    int read; while ((read = stream.Read(buffer, 0, 65536)) != 0) { cancellation.ThrowIfCancellationRequested(); sha.AppendData(buffer, 0, read); }
                    if (Convert.ToHexString(sha.GetHashAndReset()) != row.Hash) throw new IOException("La copia seleccionada cambió de contenido. Analiza de nuevo.");
                }
                finally { System.Buffers.ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
                VerifyMetadata(row);
            }
        }
        if (kind == AdvancedKind.Empty && (row.File.IsDirectory ? !CleanUpDesk.Core.Paths.IsDirectoryEmpty(path) : new FileInfo(path).Length != 0)) throw new IOException("El elemento dejó de estar vacío. No se eliminará.");
    }
    /// <summary>Rejects replaced, changed, unavailable, or reparse-point targets using the reviewed metadata and native identity.</summary>
    internal static void VerifyMetadata(AdvancedFinding row)
    {
        CleanUpDesk.Core.Paths.NoReparseAncestors(row.Path); var now = CleanUpDesk.Core.Paths.ReadEntry(row.Path, row.File.VolumeId);
        if (NativeFiles.Identity(row.Path) != row.Identity || now.IsDirectory != row.File.IsDirectory || now.Size != row.File.Size || now.Modified != row.File.Modified || now.Created != row.File.Created)
            throw new IOException("Un elemento cambió o no está disponible. Actualiza el índice y analiza de nuevo.");
    }
    /// <summary>Disposes keeper streams and clears the one-operation authorization state.</summary>
    void Release() { foreach (var stream in held.Values) stream.Dispose(); held.Clear(); activeTargets.Clear(); leased = false; }
    /// <summary>Creates a keeper-stream lease whose disposal releases the advanced deletion authorization.</summary>
    sealed class Lease(AdvancedDeletionPlan plan) : IDisposable { 
        /// <summary>Releases the keeper lease when execution completes, fails, or is cancelled.</summary>
        public void Dispose() => plan.Release(); }
}
/// <summary>Wraps a deletion backend with the reviewed advanced target and keeper protections.</summary>
public sealed class AdvancedDeletionBackend(AdvancedDeletionPlan plan, IDeleteBackend backend) : IDeleteBackend
{
    /// <summary>Revalidates an advanced target and delegates files or deletes a still-empty directory without recursion.</summary>
    public void Delete(string path, bool directory, DeleteMode mode)
    {
        plan.VerifyTarget(path);
        if (directory && plan.RequiresPermanentFolders)
        {
            if (mode != DeleteMode.Permanent) throw new IOException("Para una carpeta vacía se requiere elegir eliminación permanente explícitamente. No se ha cambiado el modo de borrado.");
            Directory.Delete(path, false); // Windows atomically rejects a folder that already contains entries.
            return;
        }
        backend.Delete(path, directory, mode);
    }
}
