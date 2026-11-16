using CleanUpDesk.Core;

// Framework-free test executable; creates and removes only its own randomly named fixture.
var fixtureBase = Path.GetTempPath();
if (args.Contains("--workspace-fixtures"))
{
    if (!File.Exists(Path.Combine(Environment.CurrentDirectory, "CleanUpDesk.slnx"))) throw new IOException("Ejecuta las pruebas desde la raíz del proyecto.");
    fixtureBase = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, ".tools", "test-fixtures"));
}
var fixture = Path.GetFullPath(Path.Combine(fixtureBase, "CleanUpDesk-tests-" + Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(fixture); int passed = 0, failed = 0;
// Summary: Runs one named fixture regression, records its outcome, and allows the remaining cases to continue.
async Task Test(string name, Func<Task> test) { try { await test(); Console.WriteLine("PASS " + name); passed++; } catch (Exception e) { Console.WriteLine("FAIL " + name + " · " + e); failed++; } }
// Summary: Fails the current fixture regression when the expected invariant is false.
void Assert(bool condition, string text = "Aserción fallida") { if (!condition) throw new Exception(text); }
// Summary: Asserts that the supplied unsafe or invalid operation is rejected.
void Throws(Action action) { try { action(); } catch { return; } throw new Exception("Se esperaba un rechazo."); }
// Summary: Creates an explicitly owned test subdirectory beneath the current random fixture root.
string NewDir(string name) { var p = Path.GetFullPath(Path.Combine(fixture, name)); Directory.CreateDirectory(p); return p; }
var store = new IndexStore(Path.Combine(fixture, "index.db")); var volumeService = new VolumeService();
try
{
    await Test("Filtros AND, extensiones OR, límites inclusivos de tamaño y fechas, Unicode y parámetros", () =>
    {
        var root = NewDir("filtros"); long date = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;
        // Summary: Builds synthetic indexed entry metadata for the current bounded fixture scenario.
        Entry E(string name, long size, long time, string volume = "v1") => new(Path.Combine(root, name), root, name, Path.GetExtension(name).ToUpperInvariant(), false, size, time, time, 0, volume);
        store.WriteBatch([E("ÁRBOL.pdf", 20, date), E("árbol.docx", 30, date), E("árbol.txt", 30, date), E("Otro.pdf", 30, date, "v2"), E("x' OR 1=1 --.pdf", 40, date)]);
        var f = new SearchFilter { Name = "árbol", Extensions = ["pdf", "docx"], MinSize = 20, MaxSize = 30, ModifiedFrom = date, ModifiedTo = date, CreatedFrom = date, CreatedTo = date, Volumes = ["v1"] };
        Assert(store.Search(f).Total == 2); Assert(store.Search(f with { MinSize = 21 }).Total == 1); Assert(store.Search(f with { ModifiedFrom = date + 1, ModifiedTo = date + 2 }).Total == 0);
        Assert(store.Search(new() { Name = "x' OR 1=1 --.pdf", Exact = true }).Total == 1); Assert(store.Search(new() { Name = "%" }).Total == 0);
        Throws(() => store.Search(f with { MinSize = 100, MaxSize = 1 })); return Task.CompletedTask;
    });
    await Test("Rutas duplicadas, descendientes solapados y límites de prefijo", () =>
    {
        var a = Path.Combine(fixture, "a"); Assert(Paths.Collapse([a, a.ToUpperInvariant(), Path.Combine(a, "child"), a + "b"]).Length == 2); Assert(!Paths.Within(a + "b", a)); Throws(() => Paths.Normalize(@"C:\file:stream")); return Task.CompletedTask;
    });
    await Test("Análisis real Unicode, rutas largas y reconstrucción del alcance", async () =>
    {
        var root = NewDir("análisis_日本語"); var deep = root; for (int i = 0; i < 8; i++) deep = Path.Combine(deep, new string('a', 34)); Directory.CreateDirectory(deep); File.WriteAllText(Path.Combine(deep, "niño_文件.txt"), "fixture");
        var scanner = new Scanner(store); var report = await scanner.ScanAsync(root, "long", true, [], null, default); Assert(!report.Cancelled && report.Processed >= 9); Assert(store.Search(new() { Name = "niño_文件.txt", Volumes = ["long"] }).Total == 1);
        await scanner.ScanAsync(root, "long", true, [deep], null, default); Assert(store.Search(new() { Name = "niño_文件.txt", Volumes = ["long"] }).Total == 0);
    });
    await Test("Permiso denegado y error de lectura no detienen el análisis (inyección)", async () =>
    {
        var root = NewDir("errores"); var scanner = new Scanner(store, new ErrorTree(root)); var report = await scanner.ScanAsync(root, "err", true, [], null, default); Assert(report.Skipped == 2 && report.Processed == 1);
    });
    await Test("Cancelación durante el recorrido conserva alcance parcial", async () =>
    {
        var root = NewDir("cancel"); using var cts = new CancellationTokenSource(); var scanner = new Scanner(store, new CancelTree(root, cts)); var report = await scanner.ScanAsync(root, "cancel", true, [], null, cts.Token); Assert(report.Cancelled && report.Processed < 10000);
    });
    await Test("Unidad desconectada: resultados retenidos sin disponibilidad falsa", () =>
    {
        var root = NewDir("offline"); store.WriteBatch([new(Path.Combine(root, "x.txt"), root, "x.txt", ".TXT", false, 1, 1, 1, 0, "disconnected-fixture")]); Assert(store.Search(new() { Volumes = ["disconnected-fixture"] }).Total == 1); Assert(!volumeService.Matches(root, "disconnected-fixture")); return Task.CompletedTask;
    });
    await Test("Protección de raíces, ubicaciones críticas y contenedores", () =>
    {
        var critical = NewDir("critical"); var policy = new SafetyPolicy([critical]); Throws(() => policy.Check(Path.GetPathRoot(fixture)!)); Throws(() => policy.Check(critical)); Throws(() => policy.Check(fixture)); Throws(() => policy.Check(Environment.GetFolderPath(Environment.SpecialFolder.Windows))); return Task.CompletedTask;
    });
    await Test("Puntos de reanálisis omitidos (inyección sin privilegios)", async () =>
    {
        var root = NewDir("reparse"); var report = await new Scanner(store, new ReparseTree(root)).ScanAsync(root, "link", true, [], null, default); Assert(report.Skipped == 1 && report.Processed == 0);
    });
    await Test("Doble confirmación, frase, permiso de un uso, cambio de identidad e índice", async () =>
    {
        var root = NewDir("delete"); var file = Path.Combine(root, "solo-prueba.txt"); File.WriteAllText(file, "test"); var volume = volumeService.ForPath(file); var backend = new FixtureBackend(root); var service = new DeletionService(store, volumeService, new SafetyPolicy(), backend);
        await new Scanner(store).ScanAsync(root, volume.Id, true, [], null, default);
        var review = await service.PrepareAsync([file, file], DeleteMode.Permanent); Assert(review.Items.Count == 1); Throws(() => service.Confirm(review, true, "ELIMINAR DEFINITIVAMENTE"));
        service.CompleteReview(review, [file]); Throws(() => service.Confirm(review, false)); Throws(() => service.Confirm(review, true, "mal")); Assert(File.Exists(file));
        var permit = service.Confirm(review, true, "ELIMINAR DEFINITIVAMENTE"); var result = await service.ExecuteAsync(permit, false, default); Assert(result.Single().Status == "Eliminado"); Assert(!File.Exists(file)); Assert(store.Search(new() { Folder = root }).Total == 0);
        bool rejected = false; try { await service.ExecuteAsync(permit, false, default); } catch { rejected = true; } Assert(rejected);
        File.WriteAllText(file, "a"); var changed = await service.PrepareAsync([file], DeleteMode.Permanent); service.CompleteReview(changed, [file]); File.WriteAllText(file, "contenido cambiado"); var changedResult = await service.ExecuteAsync(service.Confirm(changed, true, "ELIMINAR DEFINITIVAMENTE"), false, default); Assert(changedResult.Single().Status == "Error" && File.Exists(file));
    });
    await Test("Fallo de Papelera nunca llama al modo permanente", async () =>
    {
        var root = NewDir("no-fallback"); var file = Path.Combine(root, "x"); File.WriteAllText(file, "test"); var backend = new RefusingBackend(); var service = new DeletionService(store, volumeService, new SafetyPolicy(), backend); var review = await service.PrepareAsync([file], DeleteMode.Recycle); service.CompleteReview(review, [file]); var result = await service.ExecuteAsync(service.Confirm(review, true), false, default); Assert(result.Single().Status == "Error" && File.Exists(file)); Assert(backend.Modes.SequenceEqual([DeleteMode.Recycle]));
        var sink = new RecycleSink(); Assert(sink.PreDeleteItem(0, null!) < 0); Assert(sink.Refused); Assert(new RecycleSink().PreDeleteItem(0x80, null!) == 0);
    });
    await Test("Temporales limitados a raíz autorizada, raíz conservada", async () =>
    {
        var root = NewDir("temp-scope"); var outside = NewDir("outside"); var file = Path.Combine(root, "old.tmp"); File.WriteAllText(file, "fixture"); var other = Path.Combine(outside, "old.tmp"); File.WriteAllText(other, "fixture"); var policy = new SafetyPolicy(tempRoots: [root]); Throws(() => policy.Check(root, true)); Throws(() => policy.Check(other, true)); policy.Check(file, true);
        var service = new DeletionService(store, volumeService, policy, new FixtureBackend(root)); var review = await service.PrepareAsync([file], DeleteMode.Permanent, true); service.CompleteReview(review, [file]); await service.ExecuteAsync(service.Confirm(review, true, "ELIMINAR DEFINITIVAMENTE"), true, default); Assert(Directory.Exists(root) && File.Exists(other));
    });
    await Test("Explorar por niveles, Unicode, carpetas primero y paginación sin analizar descendientes", async () =>
    {
        var root = NewDir("exploración"); var child = Path.Combine(root, "Niño_áéíóú"); Directory.CreateDirectory(child); File.WriteAllText(Path.Combine(child, "dentro.txt"), "fixture");
        for (int i = 0; i < 220; i++) File.WriteAllText(Path.Combine(root, $"archivo-{i:D3}.txt"), "fixture");
        var cache = new IndexStore(Path.Combine(fixture, "browser-test.db")); var browser = new DirectoryBrowser(cache);
        var result = await browser.LoadAsync(root, "browse-fixture", true, default); Assert(result.Count == 221);
        var first = browser.Page(); Assert(first.Total == 221 && first.Items.Count == 200 && first.Items[0].IsDirectory);
        Assert(browser.Page(1).Items.Count == 21); Assert(first.Items.All(e => e.Name != "dentro.txt"));
        await browser.LoadAsync(child, "browse-fixture", true, default); Assert(browser.Page().Items.Single().Name == "dentro.txt");
        await browser.LoadAsync(root, "browse-fixture", true, default); Assert(browser.Page(name: "ARCHIVO-219").Total == 1);
        Assert(store.Search(new() { Volumes = ["browse-fixture"] }).Total == 0, "Explorar no debe mezclar su caché con el índice recursivo."); browser.Reset(); Assert(browser.Page().Total == 0);
    });
    await Test("Explorador omite errores y enlaces; cancelación no publica una instantánea parcial", async () =>
    {
        var root = NewDir("browse-errors"); var cache = new IndexStore(Path.Combine(fixture, "browser-errors.db"));
        var reader = new DirectoryBrowser(cache, new ErrorTree(root)); var result = await reader.LoadAsync(root, "v", true, default); Assert(result.Count == 1 && result.Skipped == 2);
        var links = new DirectoryBrowser(cache, new ReparseTree(root)); var report = await links.LoadAsync(root, "v", true, default); Assert(report.Count == 0 && report.Skipped == 1);
        using var cts = new CancellationTokenSource(); var cancelled = new DirectoryBrowser(cache, new CancelTree(root, cts)); bool rejected = false;
        try { await cancelled.LoadAsync(root, "v", true, cts.Token); } catch (OperationCanceledException) { rejected = true; }
        Assert(rejected && cancelled.Page().Total == 0);
    });
    await Test("Distingue lectores sin medio de discos desconectados con índice", () =>
    {
        var online = new Volume("c", @"C:\", "Sistema", 100, 40, true);
        var empty = new Volume("reader", @"D:\", "", 0, 0, false) { Connection = VolumeConnection.NoMedia };
        var remembered = new[] { new Volume("unused", @"E:\", "Vacío antiguo", 0, 0, false), new Volume("usb", @"G:\", "Trabajo", 100, 20, false) };
        var merged = VolumeService.Merge([online, empty], remembered, new HashSet<string> { "usb" });
        Assert(merged.Count == 3 && merged.Single(v => v.Id == "reader").State.Contains("sin medio"));
        Assert(merged.Single(v => v.Id == "usb").Connection == VolumeConnection.Disconnected && !merged.Any(v => v.Id == "unused")); return Task.CompletedTask;
    });
    await Test("Buscar borrrar encuentra borrrar.txt recién creado en Escritorio con índice vacío", async () =>
    {
        var root = NewDir("Desktop-regresión"); var index = new IndexStore(Path.Combine(fixture, "desktop-search.db"));
        var name = new SearchFilter { Name = "borrrar" };
        Assert(index.Search(name).Total == 0);
        var file = Path.Combine(root, "borrrar.txt"); File.WriteAllText(file, "fixture");
        var refresher = new SearchIndexRefresher(index);
        var report = await refresher.RefreshAsync([new(root, "desktop-volume")], false, [], default);
        Assert(report.Single().Completed && report.Single().Updated == 1);
        Assert(index.Search(name).Items.Single().Path == file);
        Assert(index.Search(name with { Name = "BORRRAR", Extensions = ["txt"], Volumes = ["desktop-volume"], IsDirectory = false }).Total == 1);
        Assert(index.Search(name with { Volumes = ["otro-disco"] }).Total == 0);
        Assert(index.Search(name with { Exact = true }).Total == 0);
        Assert(index.Search(name with { Name = "borrrar.txt", Exact = true }).Total == 1);
        Assert(index.ScanHistory().Count == 0, "Actualizar un nivel no debe presentarse como un análisis recursivo completo.");
        File.WriteAllText(Path.Combine(root, "borrrar-nuevo.txt"), "otro fixture");
        await refresher.RefreshAsync([new(root, "desktop-volume")], false, [], default);
        Assert(index.Search(name).Total == 2, "Debe descubrir creaciones posteriores y funcionar con conexiones SQLite reutilizadas.");
        File.Delete(file); await refresher.RefreshAsync([new(root, "desktop-volume")], false, [], default);
        Assert(index.Search(name).Items.Single().Name == "borrrar-nuevo.txt");
    });
    await Test("Actualizar Escritorio respeta exclusiones, ocultos y enlaces sin perder otros ámbitos", async () =>
    {
        var root = NewDir("desktop-scope"); var other = NewDir("desktop-other");
        var hidden = Path.Combine(root, "borrrar-oculto.txt"); File.WriteAllText(hidden, "fixture"); File.SetAttributes(hidden, FileAttributes.Hidden);
        var excluded = Path.Combine(root, "borrrar-excluido.txt"); File.WriteAllText(excluded, "fixture");
        var sub = Path.Combine(root, "Subcarpeta"); Directory.CreateDirectory(sub); File.WriteAllText(Path.Combine(sub, "borrrar-descendiente.txt"), "fixture");
        var index = new IndexStore(Path.Combine(fixture, "desktop-scope.db")); await new Scanner(index).ScanAsync(root, "v", true, [], null, default);
        index.WriteBatch([new(Path.Combine(other, "borrrar.txt"), other, "borrrar.txt", ".TXT", false, 1, 1, 1, 0, "other")]);
        var refresher = new SearchIndexRefresher(index); await refresher.RefreshAsync([new(root, "v")], false, [excluded], default);
        Assert(index.Search(new() { Name = "borrrar-oculto" }).Total == 0 && index.Search(new() { Name = "borrrar-excluido" }).Total == 0);
        Assert(index.Search(new() { Name = "borrrar-descendiente" }).Total == 1, "Debe conservar los descendientes ya indexados de carpetas presentes.");
        Assert(index.Search(new() { Volumes = ["other"] }).Total == 1);
        var empty = new IndexStore(Path.Combine(fixture, "desktop-nested.db"));
        await new SearchIndexRefresher(empty).RefreshAsync([new(root, "v"), new(sub, "v")], true, [], default);
        Assert(empty.Search(new() { Name = "borrrar-descendiente" }).Total == 1, "Las instantáneas de padre e hijo no se deben colapsar.");
        var moved = Path.Combine(other, "movida"); Assert(Paths.Within(sub, fixture) && Paths.Within(moved, fixture));
        Directory.Move(sub, moved); File.WriteAllText(sub, "fixture");
        await new SearchIndexRefresher(empty).RefreshAsync([new(root, "v")], true, [], default);
        Assert(empty.Search(new() { Name = "borrrar-descendiente" }).Total == 0 && !empty.Search(new() { Name = "Subcarpeta", Exact = true }).Items.Single().IsDirectory, "Cambiar una carpeta por un archivo debe retirar los descendientes anteriores del índice.");
        var linkReport = await new SearchIndexRefresher(empty, new ReparseTree(root)).RefreshAsync([new(root, "v")], true, [], default);
        Assert(linkReport.Single().Skipped == 1 && empty.Search(new() { Folder = root }).Total == 0);
        File.SetAttributes(hidden, FileAttributes.Normal);
    });
    await Test("Error o cancelación al refrescar búsqueda conserva la instantánea anterior", async () =>
    {
        var root = NewDir("desktop-errors"); var index = new IndexStore(Path.Combine(fixture, "desktop-errors.db"));
        var original = new Entry(Path.Combine(root, "anterior.txt"), root, "anterior.txt", ".TXT", false, 1, 1, 1, 0, "v"); index.WriteBatch([original]);
        using var cts = new CancellationTokenSource(); bool cancelled = false;
        try { await new SearchIndexRefresher(index, new CancelTree(root, cts)).RefreshAsync([new(root, "v")], true, [], cts.Token); } catch (OperationCanceledException) { cancelled = true; }
        Assert(cancelled && index.Search(new() { Folder = root }).Items.Single().Name == "anterior.txt");
        var reports = await new SearchIndexRefresher(index, new InterruptedTree(root)).RefreshAsync([new(root, "v")], true, [], default);
        Assert(!reports.Single().Completed && index.Search(new() { Folder = root }).Items.Single().Name == "anterior.txt");
        index.WriteBatch([original with { Path = Path.Combine(root, "denied"), Name = "denied" }]);
        var isolated = await new SearchIndexRefresher(index, new ErrorTree(root)).RefreshAsync([new(root, "v")], true, [], default);
        Assert(isolated.Single().Completed && isolated.Single().Skipped == 2 && index.Search(new() { Folder = root }).Items.Select(e => e.Name).Order().SequenceEqual(new[] { "denied", "ok" }));
    });
    await Test("Mover borrrar.txt de Escritorio a Documentos y a una subcarpeta mantiene una sola ruta actual", async () =>
    {
        var desktop = NewDir("session-profile/Escritorio"); var docs = NewDir("session-profile/Documentos");
        var source = Path.Combine(desktop, "borrrar.txt"); var target = Path.Combine(docs, "borrrar.txt"); File.WriteAllText(source, "fixture");
        var index = new IndexStore(Path.Combine(fixture, "session-move.db")); var tree = new CountingTree();
        var feeds = new Dictionary<string, FakeChangeFeed>(StringComparer.OrdinalIgnoreCase);
        using var session = new SearchIndexSession(index, tree, (p, _) => feeds[p] = new());
        SearchRefreshRoot[] roots = [new(desktop, "v"), new(docs, "v")];
        await session.RefreshAsync(roots, true, [], default); Assert(index.Search(new() { Name = "borrrar" }).Items.Single().Path == source);
        int initialReads = tree.Reads; await session.RefreshAsync(roots, true, [], default); Assert(tree.Reads == initialReads, "Una consulta sin cambios debe reutilizar el índice, sin recorrer los árboles otra vez.");
        Assert(Paths.Within(source, fixture) && Paths.Within(target, fixture)); File.Move(source, target);
        foreach (var feed in feeds.Values) feed.Signal(new(target, true, source));
        await session.RefreshAsync(roots, true, [], default); Assert(index.Search(new() { Name = "borrrar" }).Items.Single().Path == target);
        var nested = Path.Combine(docs, "Trabajo", "Informes"); Directory.CreateDirectory(nested);
        var deepFile = Path.Combine(nested, "borrrar.txt"); Assert(Paths.Within(deepFile, fixture)); File.Move(target, deepFile);
        feeds[docs].Signal(new(Path.Combine(docs, "Trabajo"), true)); feeds[docs].Signal(new(deepFile, true, target));
        await session.RefreshAsync(roots, true, [], default); Assert(index.Search(new() { Name = "borrrar" }).Items.Single().Path == deepFile);
        Assert(index.Search(new() { Name = "borrrar", Folder = desktop }).Total == 0);
        session.Dispose();
        var offlineFolder = Path.Combine(docs, "Cambio-con-app-cerrada"); Directory.CreateDirectory(offlineFolder);
        var offlineFile = Path.Combine(offlineFolder, "borrrar.txt"); Assert(Paths.Within(deepFile, fixture) && Paths.Within(offlineFile, fixture)); File.Move(deepFile, offlineFile);
        using var restarted = new SearchIndexSession(index, tree, (_, _) => new FakeChangeFeed());
        await restarted.RefreshAsync(roots, true, [], default); Assert(index.Search(new() { Name = "borrrar" }).Items.Single().Path == offlineFile, "Una nueva sesión debe reconciliar los cambios ocurridos con la aplicación cerrada.");
    });
    await Test("Interrupción de vigilancia y cambio de política fuerzan reconciliación recursiva", async () =>
    {
        var root = NewDir("session-overflow"); var nested = Path.Combine(root, "Subcarpeta"); Directory.CreateDirectory(nested);
        var file = Path.Combine(nested, "borrrar.txt"); var index = new IndexStore(Path.Combine(fixture, "session-overflow.db"));
        var tree = new CountingTree(); FakeChangeFeed? feed = null;
        using var session = new SearchIndexSession(index, tree, (_, _) => feed = new());
        await session.RefreshAsync([new(root, "v")], true, [], default); int before = tree.Reads;
        File.WriteAllText(file, "fixture"); feed!.Break();
        await session.RefreshAsync([new(root, "v")], true, [], default); Assert(tree.Reads > before && index.Search(new() { Name = "borrrar" }).Total == 1);
        await session.RefreshAsync([new(root, "v")], true, [nested], default); Assert(index.Search(new() { Name = "borrrar" }).Total == 0);
        await session.RefreshAsync([new(root, "v")], true, [], default); Assert(index.Search(new() { Name = "borrrar" }).Total == 1);
        var collapsed = SearchLocations.Collapse([new(root, "v"), new(nested, "v")]); Assert(collapsed.Count == 1);
    });
    await Test("Vigilancia real de Windows detecta movimiento entre carpetas de una fixture", async () =>
    {
        var root = NewDir("session-live"); var desktop = Path.Combine(root, "Desktop"); var docs = Path.Combine(root, "Documents"); Directory.CreateDirectory(desktop); Directory.CreateDirectory(docs);
        var source = Path.Combine(desktop, "borrrar.txt"); var target = Path.Combine(docs, "borrrar.txt"); File.WriteAllText(source, "fixture");
        var index = new IndexStore(Path.Combine(fixture, "session-live.db")); FileSystemChangeFeed? live = null;
        using var session = new SearchIndexSession(index, feeds: (p, recursive) => live = new FileSystemChangeFeed(p, recursive));
        await session.RefreshAsync([new(root, "v")], true, [], default);
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        live!.Changed += c => { if (string.Equals(c.Path, target, StringComparison.OrdinalIgnoreCase)) observed.TrySetResult(); };
        Assert(Paths.Within(source, fixture) && Paths.Within(target, fixture)); File.Move(source, target);
        await observed.Task.WaitAsync(TimeSpan.FromSeconds(8));
        await session.RefreshAsync([new(root, "v")], true, [], default);
        Assert(index.Search(new() { Name = "borrrar" }).Items.Single().Path == target);
    });
    await Test("Se pueden consultar coincidencias provisionales mientras otra carpeta sigue preparándose", async () =>
    {
        var first = NewDir("preview/A"); var slow = NewDir("preview/Z-lenta"); File.WriteAllText(Path.Combine(first, "borrrar.txt"), "fixture");
        var index = new IndexStore(Path.Combine(fixture, "preview-results.db")); using var reader = new BlockingTree(slow);
        using var session = new SearchIndexSession(index, reader, (_, _) => new FakeChangeFeed());
        var pending = session.RefreshAsync([new(first, "v"), new(slow, "v")], true, [], default);
        try
        {
            Assert(await Task.Run(() => reader.Entered.Wait(TimeSpan.FromSeconds(8))), "La preparación no llegó a la carpeta lenta de prueba.");
            Assert(!pending.IsCompleted, "La preparación debe seguir en curso.");
            Assert(index.Search(new() { Name = "borrrar" }).Items.Single().Path == Path.Combine(first, "borrrar.txt"), "La consulta debe mostrar lo ya encontrado sin esperar a terminar todas las ubicaciones.");
        }
        finally { reader.Release.Set(); await pending; }
    });
    await Test("Todos los discos incluye C y F; una selección explícita y una carpeta restringen el alcance", () =>
    {
        Volume[] units = [new("c", @"C:\", "Sistema", 100, 50, true), new("f", @"F:\", "Datos", 200, 100, true), new("empty", @"D:\", "", 0, 0, false)];
        Assert(SearchScopePlanner.Plan(new(), units).Select(r => r.VolumeId).Order().SequenceEqual(new[] { "c", "f" }));
        Assert(SearchScopePlanner.Plan(new() { Volumes = ["f"] }, units).Single().Path == @"F:\");
        var folder = SearchScopePlanner.Plan(new() { Folder = @"F:\Datos", Recursive = false }, units).Single(); Assert(folder.VolumeId == "f" && !folder.Recursive);
        Assert(SearchScopePlanner.Plan(new() { Folder = @"F:\Datos", Volumes = ["c"] }, units).Count == 0); return Task.CompletedTask;
    });
    await Test("Mover de C a F se publica aunque una subcarpeta de C siga ocupada", async () =>
    {
        var c = NewDir("virtual-drives/C"); var f = NewDir("virtual-drives/F"); var slow = Path.Combine(c, "Lenta");
        var source = Path.Combine(c, "borrrar.txt"); var target = Path.Combine(f, "borrrar.txt"); File.WriteAllText(source, "fixture");
        var index = new IndexStore(Path.Combine(fixture, "cross-drives.db")); var feeds = new Dictionary<string, FakeChangeFeed>(StringComparer.OrdinalIgnoreCase);
        using var reader = new BlockingTree(slow); using var session = new SearchIndexSession(index, reader, (p, _) => feeds[p] = new());
        SearchRefreshRoot[] roots = [new(c, "C-fixture"), new(f, "F-fixture")]; await session.RefreshAsync(roots, true, [], default);
        Directory.CreateDirectory(slow); feeds[c].Signal(new(slow, true));
        Assert(Paths.Within(source, fixture) && Paths.Within(target, fixture)); File.Move(source, target);
        foreach (var feed in feeds.Values) feed.Signal(new(target, true, source));
        var ongoing = session.RefreshAsync(roots, true, [], default);
        try
        {
            Assert(await Task.Run(() => reader.Entered.Wait(TimeSpan.FromSeconds(8))));
            var match = index.Search(new() { Name = "borrrar" }).Items.Single(); Assert(!ongoing.IsCompleted && match.Path == target && match.VolumeId == "F-fixture");
        }
        finally { reader.Release.Set(); await ongoing; }
    });
    await Test("ETA distingue falta de datos, estimación inicial e historial sin inventar un total conocido", () =>
    {
        var eta = new SearchTimeEstimator(); Assert(eta.RemainingText(TimeSpan.FromSeconds(10)).Contains("calculando"));
        eta.Observe(new("x", 2, 50, null)); Assert(eta.RemainingText(TimeSpan.FromSeconds(10)).Contains("calculando"));
        eta.Observe(new("x", 10, 20, null)); Assert(eta.RemainingText(TimeSpan.FromSeconds(10)).Contains("desconocido"));
        eta.Observe(new("x", 10, 20, 80)); Assert(eta.RemainingText(TimeSpan.FromSeconds(10)).Contains("anterior"));
        eta.Observe(new("viejo", 2, 500, null)); Assert(eta.RemainingText(TimeSpan.FromSeconds(10)).Contains("anterior"));
        eta.Observe(new("x", 11, 0, 0)); Assert(eta.RemainingText(TimeSpan.FromSeconds(10)).Contains("finalizando")); return Task.CompletedTask;
    });
    await Test("El historial de trabajo para ETA sobrevive a una sesión y se borra con el índice", async () =>
    {
        var root = NewDir("eta-history"); Directory.CreateDirectory(Path.Combine(root, "Sub")); var index = new IndexStore(Path.Combine(fixture, "eta-history.db"));
        var samples = new List<SearchPreparationProgress>(); var progress = new ImmediateProgress<SearchPreparationProgress>(p => samples.Add(p));
        using (var first = new SearchIndexSession(index, feeds: (_, _) => new FakeChangeFeed())) await first.RefreshAsync([new(root, "v")], true, [], default, workProgress: progress);
        Assert(samples[0].HistoricalPendingDirectories == null); samples.Clear();
        using (var second = new SearchIndexSession(index, feeds: (_, _) => new FakeChangeFeed())) await second.RefreshAsync([new(root, "v")], true, [], default, workProgress: progress);
        Assert(samples[0].HistoricalPendingDirectories > 0); index.ClearIndex(); samples.Clear();
        using (var third = new SearchIndexSession(index, feeds: (_, _) => new FakeChangeFeed())) await third.RefreshAsync([new(root, "v")], true, [], default, workProgress: progress);
        Assert(samples[0].HistoricalPendingDirectories == null);
    });
    await Test("Exploración muestra bytes de archivos de otra unidad y el cálculo agrega tamaños de carpetas", async () =>
    {
        var root = NewDir("file-sizes-F"); File.WriteAllBytes(Path.Combine(root, "datos.bin"), new byte[4096]);
        var nested = Path.Combine(root, "Carpeta"); Directory.CreateDirectory(nested); File.WriteAllBytes(Path.Combine(nested, "dentro.bin"), new byte[1100]);
        var cache = new IndexStore(Path.Combine(fixture, "file-sizes-F.db")); var browser = new DirectoryBrowser(cache); await browser.LoadAsync(root, "F-fixture", true, default);
        var file = browser.Page().Items.Single(e => !e.IsDirectory); Assert(file.Size == 4096 && file.SizeText == Format.Bytes(4096) && file.VolumeId == "F-fixture");
        var folder = browser.Page().Items.Single(e => e.IsDirectory); Assert(folder.SizeText == "Sin calcular");
        var measured = await Scanner.FolderSize(nested, default); folder = folder with { FolderLogicalSize = measured.Size, FolderSizePartial = measured.Skipped > 0 };
        Assert(measured.Size == 1100 && folder.SizeText == Format.Bytes(1100)); Assert((folder with { FolderSizePartial = true }).SizeText.Contains("parcial"));
    });
    await Test("Recorrido por clave: más de 500 subcarpetas, Unicode, otros padres y volúmenes", () =>
    {
        var root = NewDir("keyset"); var index = new IndexStore(Path.Combine(fixture, "keyset.db"));
        index.WriteBatch(Enumerable.Range(0, 1203).Select(i => new Entry(Path.Combine(root, $"niño-{i:D4}"), root, $"niño-{i:D4}", "", true, 0, 1, 1, 0, "v")));
        index.WriteBatch([new(Path.Combine(root, "archivo.txt"), root, "archivo.txt", ".TXT", false, 1, 1, 1, 0, "v"), new(Path.Combine(root, "otra"), root, "otra", "", true, 0, 1, 1, 0, "otra-unidad")]);
        var paths = new List<string>(); string after = "";
        while (true) { var batch = index.ChildDirectories(root.ToUpperInvariant(), "v", after); Assert(batch.Count <= 500); paths.AddRange(batch); if (batch.Count < 500) break; after = batch[^1]; }
        Assert(paths.Count == 1203 && paths.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1203);
        Assert(index.ChildDirectories(root + "-otro", "v").Count == 0); return Task.CompletedTask;
    });
    await Test("Preparación enumera una sola vez cada carpeta y reutiliza la sesión sin cambios", async () =>
    {
        var root = NewDir("single-pass"); Directory.CreateDirectory(Path.Combine(root, "Sub"));
        var tree = new CountingTree(); var index = new IndexStore(Path.Combine(fixture, "single-pass.db"));
        using var session = new SearchIndexSession(index, tree, (_, _) => new FakeChangeFeed());
        await session.RefreshAsync([new(root, "v")], true, [], default); Assert(tree.Reads == 2, "No debe releer la raíz después de sembrarla.");
        await session.RefreshAsync([new(root, "v")], true, [], default); Assert(tree.Reads == 2);
    });
    await Test("Consulta de fragmentos reutiliza nombres normalizados sin llamadas .NET por fila", () =>
    {
        var database = Path.Combine(fixture, "name-index.db"); var index = new IndexStore(database); var root = NewDir("name-index");
        index.WriteBatch(Enumerable.Range(0, 50000).Select(i => new Entry(Path.Combine(root, $"{i}-niño.txt"), root, $"{i}-niño.txt", ".TXT", false, i, 1, 1, 0, "v")));
        using var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={database}"); c.Open(); int callbacks = 0;
        c.CreateFunction<string, string>("fold", s => { callbacks++; return s.ToUpperInvariant(); }, isDeterministic: true);
        using var q = c.CreateCommand(); q.Parameters.AddWithValue("$name", "%NIÑO%");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        q.CommandText = "SELECT count(*) FROM entries NOT INDEXED WHERE fold(name) LIKE $name"; var before = q.ExecuteScalar(); var oldTime = watch.Elapsed; Assert(callbacks == 50000);
        callbacks = 0; watch.Restart(); q.CommandText = "SELECT count(*) FROM entries INDEXED BY ix_name_fold WHERE fold(name) LIKE $name"; var after = q.ExecuteScalar(); var newTime = watch.Elapsed;
        Assert(Equals(before, after) && callbacks == 0, "La consulta debe usar la expresión almacenada, sin recalcular cada nombre.");
        Assert(index.Search(new() { Name = "NIÑO", Volumes = ["v"] }).Total == 50000);
        Console.WriteLine($"  50.000 nombres: normalización por fila {oldTime.TotalMilliseconds:0.0} ms; índice normalizado {newTime.TotalMilliseconds:0.0} ms (medición local, sin promesa de porcentaje).");
        return Task.CompletedTask;
    });
    await Test("Caché HTTP: perfiles conocidos, antigüedad, bloqueos y navegadores abiertos", async () =>
    {
        var local = NewDir("browser-local");
        var chrome = Path.Combine(local, @"Google\Chrome\User Data\Default"); var cache = Path.Combine(chrome, @"Cache\Cache_Data"); Directory.CreateDirectory(cache);
        var firefox = Path.Combine(local, @"Mozilla\Firefox\Profiles\fixture.default\cache2\entries"); Directory.CreateDirectory(firefox);
        // Summary: Creates or ages the fixture file required to exercise conservative cleanup or old-file detection.
        void Old(string path) { File.WriteAllText(path, "fixture"); File.SetCreationTimeUtc(path, DateTime.UtcNow.AddDays(-40)); File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-40)); }
        var old = Path.Combine(cache, "old"); Old(old); var locked = Path.Combine(cache, "locked"); Old(locked); File.WriteAllText(Path.Combine(cache, "recent"), "recent");
        var cookies = Path.Combine(chrome, "Cookies"); Old(cookies); var f = Path.Combine(firefox, "old"); Old(f);
        var provider = new BrowserCacheProvider(local, _ => false);
        using (File.Open(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var preview = await provider.AnalyzeAsync(14, default);
            Assert(preview.Files.Paths.Count == 2 && preview.Files.Paths.Contains(old) && preview.Files.Paths.Contains(f));
            Assert(!preview.Files.Paths.Contains(cookies) && preview.Files.Skipped >= 2 && preview.Targets.All(t => !preview.Files.Paths.Contains(t.Root)));
        }
        var active = await new BrowserCacheProvider(local, p => p == "chrome").AnalyzeAsync(14, default);
        Assert(active.Files.Paths.SequenceEqual([f]) && active.Detail.Contains("Chrome"));
    });
    await Test("Caché: backend bloquea raíces, archivos personales y apertura del navegador después de revisar", () =>
    {
        var root = NewDir("cache-guard"); var file = Path.Combine(root, "data"); File.WriteAllText(file, "fixture");
        var backend = new RecordingBackend(); var targets = new[] { new BrowserCacheTarget("Prueba", root, "fixture") };
        var guard = new BrowserCacheBackend(targets, backend, _ => true); Throws(() => guard.Delete(file, false, DeleteMode.Recycle)); Assert(backend.Calls == 0);
        guard = new(targets, backend, _ => false); Throws(() => guard.Delete(root, true, DeleteMode.Recycle)); Throws(() => guard.Delete(root + "-outside", false, DeleteMode.Recycle));
        guard.Delete(file, false, DeleteMode.Recycle); Assert(backend.Calls == 1 && backend.Mode == DeleteMode.Recycle && File.Exists(file)); return Task.CompletedTask;
    });
    await Test("Caché: doble confirmación e índice actualizados solo sobre una fixture autorizada", async () =>
    {
        var root = NewDir("cache-delete"); var cache = Path.Combine(root, "Cache_Data"); Directory.CreateDirectory(cache); var file = Path.Combine(cache, "fixture"); File.WriteAllText(file, "fixture");
        var personal = Path.Combine(root, "Cookies"); File.WriteAllText(personal, "fixture fuera de caché"); var index = new IndexStore(Path.Combine(fixture, "cache-delete.db")); var volume = volumeService.ForPath(file); index.WriteBatch([Paths.ReadEntry(file, volume.Id)]);
        var service = new DeletionService(index, volumeService, new SafetyPolicy(tempRoots: [cache]), new BrowserCacheBackend([new("Prueba", cache, "fixture")], new FixtureBackend(root), _ => false));
        var review = await service.PrepareAsync([file], DeleteMode.Permanent, true); Throws(() => service.Confirm(review, true, "ELIMINAR DEFINITIVAMENTE")); service.CompleteReview(review, [file]); Throws(() => service.Confirm(review, true, ""));
        var results = await service.ExecuteAsync(service.Confirm(review, true, "ELIMINAR DEFINITIVAMENTE"), true, default);
        Assert(results.Single().Status == "Eliminado" && Directory.Exists(cache) && File.Exists(personal) && index.Search(new()).Total == 0);
    });
    await Test("Olvidados: índice más metadatos actuales, acceso reciente, exclusiones y datos desconocidos", async () =>
    {
        var root = NewDir("forgotten"); var index = new IndexStore(Path.Combine(fixture, "forgotten.db"));
        // Summary: Creates or ages the fixture file required to exercise conservative cleanup or old-file detection.
        string Old(string name) { var path = Path.Combine(root, name); File.WriteAllBytes(path, new byte[1024]); File.SetCreationTimeUtc(path, DateTime.UtcNow.AddDays(-400)); File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-400)); File.SetLastAccessTimeUtc(path, DateTime.UtcNow.AddDays(-400)); return path; }
        var old = Old("old.txt"); var accessed = Old("opened.txt"); File.SetLastAccessTimeUtc(accessed, DateTime.UtcNow.AddDays(-2)); var changed = Old("changed.txt"); var excluded = Old("excluded.txt"); var unknown = Old("unknown.txt"); File.SetLastAccessTimeUtc(unknown, new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        await new Scanner(index).ScanAsync(root, "v", true, [], null, default); File.SetLastWriteTimeUtc(changed, DateTime.UtcNow);
        var preview = await new ForgottenFileService(index).ReviewAsync(180, [excluded], default);
        Assert(preview.Files.Select(f => f.Path).ToHashSet().SetEquals([old, unknown])); Assert(preview.Files.Single(f => f.Path == unknown).RecordedAccess == null);
        Assert(preview.Skipped == 3 && File.Exists(old));
    });
    await Test("Olvidados: vista acotada a 200 candidatos y cancelación", async () =>
    {
        var root = NewDir("forgotten-cap"); var index = new IndexStore(Path.Combine(fixture, "forgotten-cap.db")); var entries = new List<Entry>();
        for (int i = 0; i < 230; i++) { var file = Path.Combine(root, $"{i}.txt"); File.WriteAllText(file, "fixture"); var old = DateTime.UtcNow.AddDays(-400); File.SetCreationTimeUtc(file, old); File.SetLastWriteTimeUtc(file, old); File.SetLastAccessTimeUtc(file, old); entries.Add(Paths.ReadEntry(file, "v")); }
        index.WriteBatch(entries); var service = new ForgottenFileService(index); var preview = await service.ReviewAsync(180, [], default); Assert(preview.Files.Count == 200 && preview.Checked <= 1000 && preview.Limited);
        using var cancel = new CancellationTokenSource(); cancel.Cancel(); bool canceled = false; try { await service.ReviewAsync(180, [], cancel.Token); } catch (OperationCanceledException) { canceled = true; } Assert(canceled);
    });
    await Test("Riesgo: ubicaciones críticas y datos offline no solicitan acceso ni parecen seguros", () =>
    {
        var root = NewDir("risk-paths"); var critical = Path.Combine(root, "Critical"); var inspector = new FileRiskInspector(new SafetyPolicy([critical]), _ => throw new Exception("No debe consultar permisos"));
        // Summary: Builds synthetic indexed entry metadata for the current bounded fixture scenario.
        Entry E(string path, int attributes = 0) => new(path, Path.GetDirectoryName(path)!, Path.GetFileName(path), "", false, 1, 1, 1, attributes, "offline");
        Assert(inspector.Inspect(E(Path.Combine(critical, "missing")), false).Label == "Protegido");
        Assert(inspector.Inspect(E(root), false).BlockDeletion); Assert(inspector.Inspect(E(Path.Combine(root, "other")), false).Label == "Sin verificar");
        Assert(inspector.Inspect(E(Path.Combine(root, "system"), (int)FileAttributes.System), false).Label == "Sistema"); return Task.CompletedTask;
    });
    await Test("Riesgo: diferencia denegación, bloqueo, solo lectura y acceso permitido sin borrar", () =>
    {
        var root = NewDir("risk-native"); var file = Path.Combine(root, "fixture.txt"); File.WriteAllText(file, "fixture"); var entry = Paths.ReadEntry(file, "v"); var policy = new SafetyPolicy();
        Assert(NativeFiles.ProbeDeleteAccess(file) == DeleteAccess.Allowed && File.Exists(file));
        Assert(new FileRiskInspector(policy, _ => DeleteAccess.Denied).Inspect(entry, true).Label == "Acceso restringido");
        Assert(new FileRiskInspector(policy, _ => DeleteAccess.InUse).Inspect(entry, true).Label == "En uso");
        using (File.Open(file, FileMode.Open, FileAccess.Read, FileShare.Read)) Assert(NativeFiles.ProbeDeleteAccess(file) == DeleteAccess.InUse);
        File.SetAttributes(file, FileAttributes.ReadOnly);
        try { Assert(new FileRiskInspector(policy).Inspect(entry, true).Label == "Solo lectura"); }
        finally { File.SetAttributes(file, FileAttributes.Normal); }
        Assert(!new FileRiskInspector(policy).Inspect(entry, true).BlockDeletion && File.Exists(file));
        Assert(!new FileRiskInspector(policy).Inspect(entry with { Attributes = (int)FileAttributes.System }, true).BlockDeletion, "Los metadatos actuales deben sustituir un atributo obsoleto del índice."); return Task.CompletedTask;
    });
    await Test("Protección de atributo sistema y de una carpeta contenedora antes de autorizar", async () =>
    {
        var root = NewDir("risk-system"); var file = Path.Combine(root, "system.fixture"); File.WriteAllText(file, "fixture"); var backend = new RecordingBackend();
        File.SetAttributes(file, FileAttributes.System);
        try
        {
            var service = new DeletionService(store, volumeService, new SafetyPolicy(), backend);
            foreach (var path in new[] { file, root })
            {
                bool rejected = false; try { await service.PrepareAsync([path], DeleteMode.Permanent); } catch (ProtectedDeletionException ex) { rejected = ex.TargetPath == file; }
                Assert(rejected && backend.Calls == 0 && File.Exists(file));
            }
        }
        finally { File.SetAttributes(file, FileAttributes.Normal); }
    });
    await Test("Permisos restringidos en descendiente y cambio después de la confirmación bloquean backend", async () =>
    {
        var root = NewDir("risk-access"); var file = Path.Combine(root, "fixture.txt"); File.WriteAllText(file, "fixture"); var index = new IndexStore(Path.Combine(fixture, "risk-access.db")); var volume = volumeService.ForPath(file); index.WriteBatch([Paths.ReadEntry(file, volume.Id)]);
        var backend = new RecordingBackend(); bool denied = true; var service = new DeletionService(index, volumeService, new SafetyPolicy(), backend, p => p == file && denied ? DeleteAccess.Denied : DeleteAccess.Allowed);
        bool rejected = false; try { await service.PrepareAsync([root], DeleteMode.Recycle); } catch (ProtectedDeletionException ex) { rejected = ex.TargetPath == file; } Assert(rejected && backend.Calls == 0);
        denied = false; var review = await service.PrepareAsync([file], DeleteMode.Permanent); service.CompleteReview(review, [file]); var permit = service.Confirm(review, true, "ELIMINAR DEFINITIVAMENTE"); denied = true;
        var result = (await service.ExecuteAsync(permit, false, default)).Single(); Assert(result.Status == "Error" && result.ProtectionPath == file && backend.Calls == 0 && File.Exists(file) && index.Search(new()).Total == 1);
    });
    await Test("ACL reales: denegación de DELETE y de borrado desde el padre, sin cambiar archivos ajenos", () =>
    {
        var root = NewDir("risk-acl"); var file = Path.Combine(root, "fixture.txt"); File.WriteAllText(file, "fixture");
        var info = new FileInfo(file); var folder = new DirectoryInfo(root);
        var section = System.Security.AccessControl.AccessControlSections.Access;
        var oldFile = System.IO.FileSystemAclExtensions.GetAccessControl(info).GetSecurityDescriptorSddlForm(section); var oldParent = System.IO.FileSystemAclExtensions.GetAccessControl(folder).GetSecurityDescriptorSddlForm(section);
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent(); var sid = identity.User!;
        try
        {
            var parentAcl = System.IO.FileSystemAclExtensions.GetAccessControl(folder); parentAcl.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(sid, System.Security.AccessControl.FileSystemRights.DeleteSubdirectoriesAndFiles, System.Security.AccessControl.AccessControlType.Deny)); System.IO.FileSystemAclExtensions.SetAccessControl(folder, parentAcl);
            var fileAcl = System.IO.FileSystemAclExtensions.GetAccessControl(info); fileAcl.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(sid, System.Security.AccessControl.FileSystemRights.Delete, System.Security.AccessControl.AccessControlType.Deny)); System.IO.FileSystemAclExtensions.SetAccessControl(info, fileAcl);
            Assert(NativeFiles.ProbeDeleteAccess(file) == DeleteAccess.Denied && new FileRiskInspector(new SafetyPolicy()).Inspect(Paths.ReadEntry(file, "v"), true).Label == "Acceso restringido" && File.Exists(file));
        }
        finally
        {
            var parentRestore = new System.Security.AccessControl.DirectorySecurity(); parentRestore.SetSecurityDescriptorSddlForm(oldParent, section); System.IO.FileSystemAclExtensions.SetAccessControl(folder, parentRestore);
            var fileRestore = new System.Security.AccessControl.FileSecurity(); fileRestore.SetSecurityDescriptorSddlForm(oldFile, section); System.IO.FileSystemAclExtensions.SetAccessControl(info, fileRestore);
        }
        Assert(NativeFiles.ProbeDeleteAccess(file) == DeleteAccess.Allowed, "La fixture debe recuperar sus permisos originales antes de finalizar.");
        return Task.CompletedTask;
    });
    await Test("Limpieza omite sistema y solo lectura sin convertirlos en candidatos de borrado", async () =>
    {
        var root = NewDir("risk-cleanup"); var paths = new[] { Path.Combine(root, "normal"), Path.Combine(root, "system"), Path.Combine(root, "readonly") };
        foreach (var file in paths) { File.WriteAllText(file, "fixture"); File.SetCreationTimeUtc(file, DateTime.UtcNow.AddDays(-40)); File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-40)); }
        File.SetAttributes(paths[1], FileAttributes.System); File.SetAttributes(paths[2], FileAttributes.ReadOnly);
        try { var preview = await CleanupProvider.AnalyzeRootsAsync([root], 14, default); Assert(preview.Paths.SequenceEqual([paths[0]]) && preview.Skipped == 2); }
        finally { foreach (var file in paths) File.SetAttributes(file, FileAttributes.Normal); }
    });
    await Test("Avanzadas: nombre Unicode, tamaño y SHA256 distinguen coincidencias de contenido", async () =>
    {
        var root = NewDir("advanced-modes"); foreach (var name in new[] { "A", "B", "C" }) Directory.CreateDirectory(Path.Combine(root, name));
        var a = Path.Combine(root, "A", "niño.txt"); var b = Path.Combine(root, "B", "NIÑO.txt"); var copy = Path.Combine(root, "C", "copia.txt");
        File.WriteAllText(a, "abc"); File.WriteAllText(b, "def"); File.WriteAllText(copy, "abc"); File.WriteAllText(Path.Combine(root, "unique.bin"), "tamaño distinto");
        var index = new IndexStore(Path.Combine(fixture, "advanced-modes.db")); var cache = new AdvancedResultStore(Path.Combine(fixture, "advanced-modes-results.db")); await new Scanner(index).ScanAsync(root, "v", true, [], null, default);
        var service = new AdvancedAnalysisService(index, cache, _ => true); var scope = new SearchFilter { Folder = root };
        var byName = await service.AnalyzeAsync(AdvancedKind.DuplicateName, scope, [], default); Assert(byName.Findings == 2 && byName.Groups == 1 && byName.BytesRead == 0);
        var bySize = await service.AnalyzeAsync(AdvancedKind.DuplicateSize, scope, [], default); Assert(bySize.Findings == 3 && bySize.BytesRead == 0);
        var byHash = await service.AnalyzeAsync(AdvancedKind.DuplicateHash, scope, [], default); var rows = cache.Page(0).Items;
        Assert(byHash.Findings == 2 && byHash.BytesRead == 9 && rows.Select(r => r.Path).ToHashSet().SetEquals([a, copy]));
        Assert(rows.All(r => r.Hash == Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("abc")))));
    });
    await Test("Avanzadas: ámbito, exclusiones, desconexión y archivos bloqueados", async () =>
    {
        var root = NewDir("advanced-scope"); var a = Path.Combine(root, "a.txt"); var b = Path.Combine(root, "b.txt"); File.WriteAllText(a, "abc"); File.WriteAllText(b, "abc");
        var index = new IndexStore(Path.Combine(fixture, "advanced-scope.db")); var cache = new AdvancedResultStore(Path.Combine(fixture, "advanced-scope-results.db")); await new Scanner(index).ScanAsync(root, "v", true, [], null, default);
        var service = new AdvancedAnalysisService(index, cache, _ => true);
        Assert((await service.AnalyzeAsync(AdvancedKind.DuplicateHash, new() { Volumes = ["other"] }, [], default)).Findings == 0);
        Assert((await service.AnalyzeAsync(AdvancedKind.DuplicateHash, new(), [a], default)).Findings == 0);
        var offline = await new AdvancedAnalysisService(index, cache, _ => false).AnalyzeAsync(AdvancedKind.DuplicateHash, new(), [], default); Assert(offline.Findings == 0 && offline.Skipped == 2);
        using (File.Open(b, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { var report = await service.AnalyzeAsync(AdvancedKind.DuplicateHash, new(), [], default); Assert(report.Findings == 0 && report.Skipped > 0); }
    });
    await Test("Duplicados: conservar reciente, antiguo y original manual sin deducirla por creación", async () =>
    {
        var root = NewDir("advanced-keep"); var a = Path.Combine(root, "a.txt"); var b = Path.Combine(root, "b.txt"); File.WriteAllText(a, "abc"); File.WriteAllText(b, "abc"); File.SetLastWriteTimeUtc(a, DateTime.UtcNow.AddDays(-20)); File.SetLastWriteTimeUtc(b, DateTime.UtcNow.AddDays(-10));
        var index = new IndexStore(Path.Combine(fixture, "advanced-keep.db")); var cache = new AdvancedResultStore(Path.Combine(fixture, "advanced-keep-results.db")); await new Scanner(index).ScanAsync(root, "v", true, [], null, default);
        await new AdvancedAnalysisService(index, cache, _ => true).AnalyzeAsync(AdvancedKind.DuplicateHash, new(), [], default);
        Throws(() => cache.ApplyRule(KeepRule.Original)); cache.ApplyRule(KeepRule.Newest); Assert(cache.Page(0).Items.Single(r => r.Keep).Path == b); Throws(() => cache.Plan(AdvancedKind.DuplicateHash, [b]));
        cache.ApplyRule(KeepRule.Oldest); Assert(cache.Page(0).Items.Single(r => r.Keep).Path == a);
        cache.MarkOriginal(b); cache.ApplyRule(KeepRule.Original); Assert(cache.Page(0).Items.Single(r => r.Keep && r.Original).Path == b && File.Exists(a) && File.Exists(b));
    });
    await Test("SHA256 vuelve a validar contenido con fechas conservadas y retiene la copia elegida", async () =>
    {
        var root = NewDir("advanced-hash-guard"); var a = Path.Combine(root, "a.txt"); var b = Path.Combine(root, "b.txt"); File.WriteAllText(a, "abc"); File.WriteAllText(b, "abc");
        var index = new IndexStore(Path.Combine(fixture, "advanced-hash-guard.db")); var cache = new AdvancedResultStore(Path.Combine(fixture, "advanced-hash-guard-results.db")); await new Scanner(index).ScanAsync(root, "v", true, [], null, default);
        await new AdvancedAnalysisService(index, cache, _ => true).AnalyzeAsync(AdvancedKind.DuplicateHash, new(), [], default); cache.MarkOriginal(a);
        var plan = cache.Plan(AdvancedKind.DuplicateHash, [b]); var timestamp = File.GetLastWriteTimeUtc(a); File.WriteAllText(a, "xyz"); File.SetLastWriteTimeUtc(a, timestamp);
        bool rejected = false; try { using var unexpected = await plan.AcquireAsync(default); } catch (IOException) { rejected = true; } Assert(rejected);
        File.WriteAllText(a, "abc"); File.SetLastWriteTimeUtc(a, timestamp); using var lease = await plan.AcquireAsync(default);
        Throws(() => File.Delete(a)); var oldB = File.GetLastWriteTimeUtc(b); File.WriteAllText(b, "xyz"); File.SetLastWriteTimeUtc(b, oldB);
        var backend = new RecordingBackend(); Throws(() => new AdvancedDeletionBackend(plan, backend).Delete(b, false, DeleteMode.Recycle)); Assert(backend.Calls == 0 && File.Exists(a) && File.Exists(b));
    });
    await Test("Duplicados: eliminación real de fixture con doble confirmación, copia conservada e índice", async () =>
    {
        var root = NewDir("advanced-delete"); var a = Path.Combine(root, "a.txt"); var b = Path.Combine(root, "b.txt"); File.WriteAllText(a, "abc"); File.WriteAllText(b, "abc");
        var index = new IndexStore(Path.Combine(fixture, "advanced-delete.db")); var cache = new AdvancedResultStore(Path.Combine(fixture, "advanced-delete-results.db")); var volume = volumeService.ForPath(root); await new Scanner(index).ScanAsync(root, volume.Id, true, [], null, default);
        await new AdvancedAnalysisService(index, cache, _ => true).AnalyzeAsync(AdvancedKind.DuplicateHash, new(), [], default); cache.MarkOriginal(a); var plan = cache.Plan(AdvancedKind.DuplicateHash, [b]);
        var service = new DeletionService(index, volumeService, new SafetyPolicy(), new AdvancedDeletionBackend(plan, new FixtureBackend(root))); var review = await service.PrepareAsync(plan.Paths, DeleteMode.Permanent);
        Throws(() => service.Confirm(review, true, "ELIMINAR DEFINITIVAMENTE")); service.CompleteReview(review, plan.Paths); Throws(() => service.Confirm(review, true, "")); var permit = service.Confirm(review, true, "ELIMINAR DEFINITIVAMENTE");
        using (await plan.AcquireAsync(default)) { var result = await service.ExecuteAsync(permit, false, default); Assert(result.Single().Status == "Eliminado"); cache.RemoveCompleted(result.Select(r => r.Path), true); }
        Assert(File.Exists(a) && !File.Exists(b) && index.Search(new() { IsDirectory = false }).Total == 1 && cache.Page(0).Total == 0);
    });
    await Test("Vacíos: 0 bytes, carpetas realmente vacías, ocultos y rechazo si aparece contenido", async () =>
    {
        var root = NewDir("advanced-empty"); var empty = Path.Combine(root, "empty"); var full = Path.Combine(root, "full"); Directory.CreateDirectory(empty); Directory.CreateDirectory(full);
        var zero = Path.Combine(root, "zero.txt"); File.WriteAllBytes(zero, []); var hidden = Path.Combine(full, "hidden.txt"); File.WriteAllText(hidden, "fixture"); File.SetAttributes(hidden, FileAttributes.Hidden);
        var index = new IndexStore(Path.Combine(fixture, "advanced-empty.db")); var cache = new AdvancedResultStore(Path.Combine(fixture, "advanced-empty-results.db")); await new Scanner(index).ScanAsync(root, "v", false, [], null, default);
        await new AdvancedAnalysisService(index, cache, _ => true).AnalyzeAsync(AdvancedKind.Empty, new(), [], default); Assert(cache.Page(0).Items.Select(r => r.Path).ToHashSet().SetEquals([empty, zero]));
        var plan = cache.Plan(AdvancedKind.Empty, [empty]); using var lease = await plan.AcquireAsync(default); var backend = new RecordingBackend(); var guard = new AdvancedDeletionBackend(plan, backend);
        Throws(() => guard.Delete(empty, true, DeleteMode.Recycle)); Assert(Directory.Exists(empty) && backend.Calls == 0);
        var timestamp = Directory.GetLastWriteTimeUtc(empty); var late = Path.Combine(empty, "late.txt"); File.WriteAllText(late, "fixture"); Directory.SetLastWriteTimeUtc(empty, timestamp);
        Throws(() => guard.Delete(empty, true, DeleteMode.Permanent)); Assert(File.Exists(late));
    });
    await Test("Carpeta vacía: eliminación permanente explícita sin recursión y actualización del índice", async () =>
    {
        var root = NewDir("advanced-empty-delete"); var empty = Path.Combine(root, "empty"); Directory.CreateDirectory(empty);
        var index = new IndexStore(Path.Combine(fixture, "advanced-empty-delete.db")); var cache = new AdvancedResultStore(Path.Combine(fixture, "advanced-empty-delete-results.db")); var volume = volumeService.ForPath(root); await new Scanner(index).ScanAsync(root, volume.Id, true, [], null, default);
        await new AdvancedAnalysisService(index, cache, _ => true).AnalyzeAsync(AdvancedKind.Empty, new(), [], default); var plan = cache.Plan(AdvancedKind.Empty, [empty]); Assert(plan.RequiresPermanentFolders);
        var service = new DeletionService(index, volumeService, new SafetyPolicy(), new AdvancedDeletionBackend(plan, new RecordingBackend())); var review = await service.PrepareAsync([empty], DeleteMode.Permanent); service.CompleteReview(review, [empty]); var permit = service.Confirm(review, true, "ELIMINAR DEFINITIVAMENTE");
        using (await plan.AcquireAsync(default)) { Assert((await service.ExecuteAsync(permit, false, default)).Single().Status == "Eliminado"); }
        Assert(!Directory.Exists(empty) && index.Search(new()).Total == 0 && Directory.Exists(root));
    });
    await Test("Top 100: tamaños de 64 bits, orden global, ámbitos, exclusiones y resultados offline", async () =>
    {
        var root = NewDir("advanced-largest"); var index = new IndexStore(Path.Combine(fixture, "advanced-largest.db")); var cache = new AdvancedResultStore(Path.Combine(fixture, "advanced-largest-results.db"));
        index.WriteBatch(Enumerable.Range(0, 130).Select(i => new Entry(Path.Combine(root, $"{i:D3}.iso"), root, $"{i:D3}.iso", ".ISO", false, 3L * 1024 * 1024 * 1024 + i, 1, 1, 0, "v")));
        var service = new AdvancedAnalysisService(index, cache, _ => false); var report = await service.AnalyzeAsync(AdvancedKind.Largest, new() { Volumes = ["v"] }, [Path.Combine(root, "129.iso")], default); var rows = cache.Page(0, true);
        Assert(report.Findings == 100 && report.BytesRead == 0 && rows.Items[0].Name == "128.iso" && rows.Items[^1].Name == "029.iso" && rows.Items[0].File.Size > int.MaxValue);
        Assert((await service.AnalyzeAsync(AdvancedKind.Largest, new() { Volumes = ["other"] }, [], default)).Findings == 0);
    });
    await Test("Resultados avanzados: páginas de 200, alias físicos excluidos y selección de sobrantes acotada", () =>
    {
        var root = NewDir("advanced-pages"); var cache = new AdvancedResultStore(Path.Combine(fixture, "advanced-pages-results.db"));
        var rows = Enumerable.Range(0, 1203).Select(i => new AdvancedFinding(new(Path.Combine(root, $"{i:D4}.txt"), root, "igual.txt", ".TXT", false, 1, 1, i, 0, "v"), "", "identity-" + i, null, false, false)).ToArray();
        Assert(cache.AddSources(rows, default) == 1203); Assert(cache.AddSources([rows[0] with { File = rows[0].File with { Path = Path.Combine(root, "alias.txt") } }], default) == 0);
        cache.Complete(AdvancedKind.DuplicateName, default); Assert(cache.Page(0).Total == 1203 && cache.Page(0).Items.Count == 200 && cache.Page(6).Items.Count == 3);
        cache.ApplyRule(KeepRule.Newest); Assert(cache.Surplus().Count == 500 && cache.Plan(AdvancedKind.DuplicateName, cache.Surplus()).Paths.Count == 500); Throws(() => cache.Plan(AdvancedKind.DuplicateName, rows.Take(501).Select(r => r.Path))); return Task.CompletedTask;
    });
    await Test("Cancelar SHA256 durante la lectura cierra archivos y no publica grupos parciales", async () =>
    {
        var root = NewDir("advanced-cancel"); foreach (var name in new[] { "a.bin", "b.bin" }) File.WriteAllBytes(Path.Combine(root, name), new byte[2 * 1024 * 1024]);
        var index = new IndexStore(Path.Combine(fixture, "advanced-cancel.db")); var cache = new AdvancedResultStore(Path.Combine(fixture, "advanced-cancel-results.db")); await new Scanner(index).ScanAsync(root, "v", true, [], null, default);
        using var cancel = new CancellationTokenSource(); bool stopped = false;
        try { await new AdvancedAnalysisService(index, cache, _ => true).AnalyzeAsync(AdvancedKind.DuplicateHash, new(), [], cancel.Token, new ImmediateProgress<AdvancedProgress>(p => { if (p.Bytes > 0) cancel.Cancel(); })); } catch (OperationCanceledException) { stopped = true; }
        Assert(stopped && cache.Page(0).Total == 0); using var probe = File.Open(Path.Combine(root, "a.bin"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    });
    await Test("Duplicados: retirar un grupo de la revisión no verifica ni autoriza sus destinos", async () =>
    {
        var root = NewDir("advanced-retained"); var a = Path.Combine(root, "a.txt"); var b = Path.Combine(root, "b.txt"); var c = Path.Combine(root, "c.txt"); var d = Path.Combine(root, "d.txt"); File.WriteAllText(a, "abc"); File.WriteAllText(b, "abc"); File.WriteAllText(c, "def"); File.WriteAllText(d, "def");
        var index = new IndexStore(Path.Combine(fixture, "advanced-retained.db")); var cache = new AdvancedResultStore(Path.Combine(fixture, "advanced-retained-results.db")); await new Scanner(index).ScanAsync(root, "v", true, [], null, default);
        await new AdvancedAnalysisService(index, cache, _ => true).AnalyzeAsync(AdvancedKind.DuplicateHash, new(), [], default); cache.MarkOriginal(a); cache.MarkOriginal(c); var plan = cache.Plan(AdvancedKind.DuplicateHash, [b, d]);
        File.WriteAllText(c, "cambió un grupo retirado"); using var lease = await plan.AcquireAsync(default, [b]); var backend = new RecordingBackend(); var guard = new AdvancedDeletionBackend(plan, backend);
        guard.Delete(b, false, DeleteMode.Recycle); Throws(() => guard.Delete(d, false, DeleteMode.Recycle)); Assert(backend.Calls == 1 && File.Exists(b) && File.Exists(d));
    });
    await Test("Avanzadas continúa ante Win32 de identidad en nombres, tamaños, SHA256 y vacíos", async () =>
    {
        var root = NewDir("advanced-win32"); foreach (var folder in new[] { "denied", "busy", "good-a", "good-b" }) { var dir = Path.Combine(root, folder); Directory.CreateDirectory(dir); File.WriteAllBytes(Path.Combine(dir, "same.txt"), []); }
        var index = new IndexStore(Path.Combine(fixture, "advanced-win32.db")); var cache = new AdvancedResultStore(Path.Combine(fixture, "advanced-win32-results.db")); await new Scanner(index).ScanAsync(root, "v", true, [], null, default);
        // Summary: Supplies native fixture identity data while counting calls or injecting the access failure required by this regression.
        string Identity(string path)
        {
            if (Paths.Within(path, Path.Combine(root, "denied"))) throw new System.ComponentModel.Win32Exception(5, "Acceso denegado de prueba");
            if (Paths.Within(path, Path.Combine(root, "busy"))) throw new System.ComponentModel.Win32Exception(32, "Archivo ocupado de prueba");
            return NativeFiles.Identity(path);
        }
        var service = new AdvancedAnalysisService(index, cache, _ => true, Identity);
        foreach (var kind in new[] { AdvancedKind.DuplicateName, AdvancedKind.DuplicateSize, AdvancedKind.DuplicateHash, AdvancedKind.Empty })
        {
            var report = await service.AnalyzeAsync(kind, new() { Folder = root }, [], default); Assert(report.Findings == 2 && report.Skipped == 2, $"El modo {kind} no continuó tras los errores nativos.");
            Assert(cache.Page(0).Items.All(r => r.Path.Contains("good-", StringComparison.Ordinal)));
        }
        Assert(Scanner.IsReadError(new System.ComponentModel.Win32Exception(5)) && !Scanner.IsReadError(new InvalidOperationException("Fallo de programación")));
    });
    await Test("Identidad y permisos nativos admiten rutas largas Unicode en análisis SHA256", async () =>
    {
        var root = NewDir("advanced-long-ñ"); var deep = root;
        for (int i = 0; i < 8; i++) deep = Path.Combine(deep, new string('a', 34)); Directory.CreateDirectory(deep);
        var first = Path.Combine(deep, "niño.txt"); var second = Path.Combine(deep, "copia.txt"); File.WriteAllText(first, "abc"); File.WriteAllText(second, "abc");
        Assert(first.Length > 260 && NativeFiles.Identity(first).Length > 0 && NativeFiles.ProbeDeleteAccess(first) == DeleteAccess.Allowed);
        var index = new IndexStore(Path.Combine(fixture, "advanced-long.db")); var cache = new AdvancedResultStore(Path.Combine(fixture, "advanced-long-results.db")); await new Scanner(index).ScanAsync(root, "v", true, [], null, default);
        var report = await new AdvancedAnalysisService(index, cache, _ => true).AnalyzeAsync(AdvancedKind.DuplicateHash, new(), [], default); Assert(report.Findings == 2 && report.Skipped == 0);
    });
    await Test("SHA256 por etapas descarta archivos grandes leyendo solo 8 KiB por archivo", async () =>
    {
        var root = NewDir("staged-distinct"); const int size = 2 * 1024 * 1024;
        for (int i = 0; i < 8; i++) { var data = new byte[size]; data[0] = (byte)i; File.WriteAllBytes(Path.Combine(root, $"{i}.bin"), data); }
        var index = new IndexStore(Path.Combine(fixture, "staged-distinct.db")); var cache = new AdvancedResultStore(Path.Combine(fixture, "staged-distinct-results.db")); await new Scanner(index).ScanAsync(root, "v", true, [], null, default);
        var report = await new AdvancedAnalysisService(index, cache, _ => true).AnalyzeAsync(AdvancedKind.DuplicateHash, new(), [], default);
        Assert(report.Findings == 0 && report.Sampled == 8 && report.FullReads == 0 && report.BytesRead == 8 * 8192 && report.Skipped == 0);
        Console.WriteLine($"  Lecturas: {8L * size:N0} bytes anteriores frente a {report.BytesRead:N0} bytes actuales (99,61 % menos).");
    });
    await Test("Muestras iguales y distinto contenido intermedio exigen SHA256 completo", async () =>
    {
        var root = NewDir("staged-collision"); var data = new byte[131072]; File.WriteAllBytes(Path.Combine(root, "a.bin"), data); data[65536] = 1; File.WriteAllBytes(Path.Combine(root, "b.bin"), data);
        var index = new IndexStore(Path.Combine(fixture, "staged-collision.db")); var cache = new AdvancedResultStore(Path.Combine(fixture, "staged-collision-results.db")); await new Scanner(index).ScanAsync(root, "v", true, [], null, default);
        var report = await new AdvancedAnalysisService(index, cache, _ => true).AnalyzeAsync(AdvancedKind.DuplicateHash, new(), [], default);
        Assert(report.Findings == 0 && report.FullReads == 2 && report.BytesRead == 2 * (8192 + data.Length));
    });
    await Test("Caché nativa reutiliza SHA256 e invalida cambios con tamaño y fecha restaurados", async () =>
    {
        var root = NewDir("staged-cache"); var data = new byte[131072]; var a = Path.Combine(root, "a.bin"); var b = Path.Combine(root, "b.bin"); File.WriteAllBytes(a, data); File.WriteAllBytes(b, data);
        Assert(NativeFiles.ChangeStamp(a).HasValue);
        var index = new IndexStore(Path.Combine(fixture, "staged-cache.db")); var cache = new AdvancedResultStore(Path.Combine(fixture, "staged-cache-results.db")); await new Scanner(index).ScanAsync(root, "v", true, [], null, default);
        var service = new AdvancedAnalysisService(index, cache, _ => true);
        var first = await service.AnalyzeAsync(AdvancedKind.DuplicateHash, new(), [], default); Assert(first.Findings == 2 && first.FullReads == 2);
        var second = await service.AnalyzeAsync(AdvancedKind.DuplicateHash, new(), [], default); Assert(second.Findings == 2 && second.Cached == 2 && second.FullReads == 0 && second.BytesRead == 16384);
        var time = File.GetLastWriteTimeUtc(b); data[65536] = 1; File.WriteAllBytes(b, data); File.SetLastWriteTimeUtc(b, time);
        var changed = await service.AnalyzeAsync(AdvancedKind.DuplicateHash, new(), [], default); Assert(changed.Findings == 0 && changed.Cached == 1 && changed.FullReads == 1);
        cache.Clear(true); var cleared = await service.AnalyzeAsync(AdvancedKind.DuplicateHash, new(), [], default); Assert(cleared.Cached == 0 && cleared.FullReads == 2);
    });
    await Test("Sin sello nativo fiable no reutiliza hashes completos", async () =>
    {
        var root = NewDir("staged-no-stamp"); File.WriteAllBytes(Path.Combine(root, "a.bin"), new byte[10000]); File.WriteAllBytes(Path.Combine(root, "b.bin"), new byte[10000]);
        var index = new IndexStore(Path.Combine(fixture, "staged-no-stamp.db")); var cache = new AdvancedResultStore(Path.Combine(fixture, "staged-no-stamp-results.db")); await new Scanner(index).ScanAsync(root, "v", true, [], null, default);
        var service = new AdvancedAnalysisService(index, cache, _ => true, changeStamp: _ => null);
        for (int i = 0; i < 2; i++) { var r = await service.AnalyzeAsync(AdvancedKind.DuplicateHash, new(), [], default); Assert(r.Findings == 2 && r.Cached == 0 && r.FullReads == 2); }
    });
    await Test("Prefiltro SQLite evita consultar metadatos de tamaños y nombres únicos", async () =>
    {
        var root = NewDir("staged-index"); var one = Path.Combine(root, "one.bin"); File.WriteAllBytes(one, new byte[21]); File.WriteAllBytes(Path.Combine(root, "two.bin"), new byte[21]); File.WriteAllBytes(Path.Combine(root, "unique.bin"), new byte[22]);
        var index = new IndexStore(Path.Combine(fixture, "staged-index.db")); var cache = new AdvancedResultStore(Path.Combine(fixture, "staged-index-results.db")); await new Scanner(index).ScanAsync(root, "v", true, [], null, default);
        int calls = 0; 
            // Summary: Supplies native fixture identity data while counting calls or injecting the access failure required by this regression.
            string Identity(string p) { Assert(!p.EndsWith("unique.bin")); calls++; return NativeFiles.Identity(p); }
        var service = new AdvancedAnalysisService(index, cache, _ => true, Identity);
        var sized = await service.AnalyzeAsync(AdvancedKind.DuplicateSize, new() { Volumes = ["v"] }, [], default); Assert(sized.Findings == 2 && calls == 2 && sized.Processed == 2);
        calls = 0; var named = await service.AnalyzeAsync(AdvancedKind.DuplicateName, new(), [], default); Assert(named.Findings == 0 && calls == 0);
        var other = await service.AnalyzeAsync(AdvancedKind.DuplicateSize, new() { Volumes = ["other"] }, [], default); Assert(other.Findings == 0 && calls == 0);
    });
    await Test("Vacíos: prefiltro descarta 20.000 archivos y 1.000 carpetas ocupadas sin consultar disco", async () =>
    {
        var root = NewDir("empty-prefilter-ñ"); var empty = Path.Combine(root, "vacía"); Directory.CreateDirectory(empty); var zero = Path.Combine(root, "cero.txt"); File.WriteAllBytes(zero, []);
        var index = new IndexStore(Path.Combine(fixture, "empty-prefilter.db")); var cache = new AdvancedResultStore(Path.Combine(fixture, "empty-prefilter-results.db")); await new Scanner(index).ScanAsync(root, "v", true, [], null, default);
        // Synthetic nonexistent rows: disk access would indicate a prefilter regression.
        for (int b = 0; b < 40; b++) index.WriteBatch(Enumerable.Range(b * 500, 500).Select(i => new Entry(Path.Combine(root, $"nonzero-{i}.bin"), root, $"nonzero-{i}.bin", ".BIN", false, 10, 1, 1, 0, "v")));
        for (int b = 0; b < 4; b++) index.WriteBatch(Enumerable.Range(b * 500, 500).Select(i => { int n = i / 2; var dir = Path.Combine(root, $"occupied-{n}"); return i % 2 == 0 ? new Entry(dir, root, $"occupied-{n}", "", true, 0, 1, 1, 16, "v") : new Entry(Path.Combine(dir, "child.bin"), dir, "child.bin", ".BIN", false, 1, 1, 1, 0, "v"); }));
        int identities = 0, enumerations = 0;
        // Summary: Supplies native fixture identity data while counting calls or injecting the access failure required by this regression.
        string Identity(string p) { Assert(p == empty || p == zero); identities++; return NativeFiles.Identity(p); }
        // Summary: Supplies a controlled fixture access or emptiness probe with the required failure/cancellation injection.
        bool Probe(string p) { Assert(p == empty); enumerations++; return Paths.IsDirectoryEmpty(p); }
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var r = await new AdvancedAnalysisService(index, cache, _ => true, Identity, directoryEmpty: Probe).AnalyzeAsync(AdvancedKind.Empty, new() { Folder = root, Volumes = ["v"] }, [], default);
        Assert(r.Findings == 2 && r.Processed == 2 && r.Skipped == 0 && identities == 2 && enumerations == 1 && r.BytesRead == 0);
        Console.WriteLine($"  Vacíos: 22.002 filas de alcance, solo 2 candidatos y 1 enumeración de carpeta; {watch.Elapsed} (fixture).");
        var wrong = await new AdvancedAnalysisService(index, cache, _ => true, Identity, directoryEmpty: Probe).AnalyzeAsync(AdvancedKind.Empty, new() { Volumes = ["other"] }, [], default); Assert(wrong.Processed == 0);
    });
    await Test("Vacíos: índice incompleto no oculta hijos de sistema; cambios requieren actualización", async () =>
    {
        var root = NewDir("empty-stale"); var hiddenDir = Path.Combine(root, "hidden"); var formerlyFull = Path.Combine(root, "formerly-full"); var nested = Path.Combine(root, "parent"); var leaf = Path.Combine(nested, "leaf");
        Directory.CreateDirectory(hiddenDir); Directory.CreateDirectory(formerlyFull); Directory.CreateDirectory(leaf);
        var hidden = Path.Combine(hiddenDir, "hidden.sys"); File.WriteAllBytes(hidden, []); File.SetAttributes(hidden, FileAttributes.Hidden | FileAttributes.System);
        var stale = Path.Combine(formerlyFull, "old.txt"); File.WriteAllText(stale, "fixture"); var zero = Path.Combine(root, "was-zero.txt"); File.WriteAllBytes(zero, []);
        var index = new IndexStore(Path.Combine(fixture, "empty-stale.db")); var cache = new AdvancedResultStore(Path.Combine(fixture, "empty-stale-results.db")); await new Scanner(index).ScanAsync(root, "v", false, [], null, default);
        File.WriteAllText(zero, "now-full"); File.Delete(stale); // Solo archivos de la fixture propia.
        var service = new AdvancedAnalysisService(index, cache, _ => true);
        await service.AnalyzeAsync(AdvancedKind.Empty, new() { Folder = root }, [], default);
        Assert(cache.Page(0).Items.Select(r => r.Path).ToHashSet().SetEquals([leaf]));
        await new Scanner(index).ScanAsync(root, "v", false, [], null, default);
        await service.AnalyzeAsync(AdvancedKind.Empty, new() { Folder = root }, [], default);
        Assert(cache.Page(0).Items.Select(r => r.Path).ToHashSet().SetEquals([leaf, formerlyFull]));
        // A folder containing an empty child is not empty; hidden and system entries also count.
        Assert(!Paths.IsDirectoryEmpty(hiddenDir) && !Paths.IsDirectoryEmpty(nested));
        File.SetAttributes(hidden, FileAttributes.Normal);
    });
    await Test("Vacíos: acceso denegado al enumerar nunca se interpreta como carpeta vacía", async () =>
    {
        var root = NewDir("empty-errors"); var denied = Path.Combine(root, "denied"); var broken = Path.Combine(root, "broken"); var good = Path.Combine(root, "good"); foreach (var d in new[] { denied, broken, good }) Directory.CreateDirectory(d);
        var index = new IndexStore(Path.Combine(fixture, "empty-errors.db")); var cache = new AdvancedResultStore(Path.Combine(fixture, "empty-errors-results.db")); await new Scanner(index).ScanAsync(root, "v", true, [], null, default);
        // Summary: Supplies a controlled fixture access or emptiness probe with the required failure/cancellation injection.
        bool Probe(string p) { if (p == denied) throw new UnauthorizedAccessException(); if (p == broken) throw new IOException(); return Paths.IsDirectoryEmpty(p); }
        var r = await new AdvancedAnalysisService(index, cache, _ => true, directoryEmpty: Probe).AnalyzeAsync(AdvancedKind.Empty, new() { Folder = root }, [], default);
        Assert(r.Processed == 3 && r.Skipped == 2 && r.Findings == 1 && cache.Page(0).Items.Single().Path == good);
        var offline = await new AdvancedAnalysisService(index, cache, _ => false, directoryEmpty: _ => throw new Exception("No debe acceder a la unidad desconectada")).AnalyzeAsync(AdvancedKind.Empty, new() { Folder = root }, [], default); Assert(offline.Findings == 0 && offline.Skipped == 3);
    });
    await Test("Vacíos: paginación, exclusiones y cancelación sin resultados parciales", async () =>
    {
        var root = NewDir("empty-pages"); for (int i = 0; i < 605; i++) Directory.CreateDirectory(Path.Combine(root, $"{i:D4}"));
        var index = new IndexStore(Path.Combine(fixture, "empty-pages.db")); var cache = new AdvancedResultStore(Path.Combine(fixture, "empty-pages-results.db")); await new Scanner(index).ScanAsync(root, "v", true, [], null, default);
        var service = new AdvancedAnalysisService(index, cache, _ => true); var excluded = Path.Combine(root, "0000");
        var r = await service.AnalyzeAsync(AdvancedKind.Empty, new() { Folder = root }, [excluded], default); Assert(r.Findings == 604 && r.Processed == 605 && r.Skipped == 1 && cache.Page(3).Items.Count == 4);
        using var cancel = new CancellationTokenSource(); int probes = 0;
        // Summary: Supplies a controlled fixture access or emptiness probe with the required failure/cancellation injection.
        bool Probe(string p) { if (++probes == 20) cancel.Cancel(); return Paths.IsDirectoryEmpty(p); }
        bool stopped = false; try { await new AdvancedAnalysisService(index, cache, _ => true, directoryEmpty: Probe).AnalyzeAsync(AdvancedKind.Empty, new() { Folder = root }, [], cancel.Token); } catch (OperationCanceledException) { stopped = true; }
        Assert(stopped && probes == 20 && cache.Page(0).Total == 0);
    });
    await Test("Top 100 sin advertencias repone 40 descartados y supera 200 exclusiones", async () =>
    {
        var root = NewDir("largest-eligible"); var index = new IndexStore(Path.Combine(fixture, "largest-eligible.db")); var cache = new AdvancedResultStore(Path.Combine(fixture, "largest-eligible-results.db"));
        var files = new List<string>();
        try
        {
            for (int i = 0; i < 140; i++) { var path = Path.Combine(root, $"{i:D3}.bin"); File.WriteAllBytes(path, new byte[1000 - i]); if (i < 40) File.SetAttributes(path, FileAttributes.ReadOnly); files.Add(path); }
            index.WriteBatch(files.Select(p => Paths.ReadEntry(p, "c")));
            var excluded = Path.Combine(root, "excluded"); index.WriteBatch(Enumerable.Range(0, 301).Select(i => new Entry(Path.Combine(excluded, i + ".bin"), excluded, i + ".bin", ".BIN", false, 10000, 1, 1, 0, "c")));
            var inspector = new FileRiskInspector(new SafetyPolicy(), _ => DeleteAccess.Allowed); var service = new AdvancedAnalysisService(index, cache, _ => true);
            var unfiltered = await service.AnalyzeAsync(AdvancedKind.Largest, new() { Volumes = ["c"] }, [excluded], default); Assert(unfiltered.Findings == 100 && cache.Page(0, true).Items[0].Name == "000.bin");
            var filtered = await service.AnalyzeAsync(AdvancedKind.Largest, new() { Volumes = ["c"] }, [excluded], default, include: e => !inspector.Inspect(e, true).BlockDeletion);
            var rows = cache.Page(0, true).Items; Assert(filtered.Findings == 100 && rows[0].Name == "040.bin" && rows[^1].Name == "139.bin" && filtered.Skipped == 341 && filtered.BytesRead == 0);
            Assert(rows.All(r => !inspector.Inspect(r.File, true).BlockDeletion));
            var fewer = await service.AnalyzeAsync(AdvancedKind.Largest, new() { Folder = root, Volumes = ["c"] }, [excluded], default, include: e => e.Size < 900); Assert(fewer.Findings == 39);
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel(); bool stopped = false;
            try { await service.AnalyzeAsync(AdvancedKind.Largest, new(), [], cancelled.Token); } catch (OperationCanceledException) { stopped = true; } Assert(stopped);
            Console.WriteLine("  Top 100: 40 advertencias sustituidas por 40 archivos siguientes; sin leer contenido.");
        }
        finally { foreach (var p in files) File.SetAttributes(p, FileAttributes.Normal); }
    });
    await Test("Top 100: cursor indexado por volumen/global, empates y ausencia de ordenación auxiliar", () =>
    {
        var root = NewDir("largest-order"); var db = Path.Combine(fixture, "largest-order.db"); var index = new IndexStore(db);
        index.WriteBatch(Enumerable.Range(0, 1200).Select(i => new Entry(Path.Combine(root, $"{i:D4}.bin"), root, $"{i:D4}.bin", ".BIN", false, i / 3, 1, 1, 0, i % 2 == 0 ? "c" : "f")));
        var all = index.LargestCandidates(new(), default).Take(100).ToArray(); var scoped = index.LargestCandidates(new() { Volumes = ["c"] }, default).Take(100).ToArray();
        Assert(all.Length == 100 && all[0].Size == 399 && all[0].Name == "1197.bin" && scoped.Length == 100 && scoped.All(e => e.VolumeId == "c"));
        Assert(index.LargestCandidates(new() { Folder = Path.Combine(root, "missing") }, default).Take(100).Count() == 0);
        using var c = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + db); c.Open(); c.CreateFunction<string, string>("fold", s => s.ToUpperInvariant(), isDeterministic: true);
        foreach (var sql in new[] { "SELECT * FROM entries INDEXED BY ix_largest WHERE isdir=0 ORDER BY size DESC,path", "SELECT * FROM entries INDEXED BY ix_volume_largest WHERE isdir=0 AND volume='c' ORDER BY size DESC,path" })
        {
            using var q = c.CreateCommand(); q.CommandText = "EXPLAIN QUERY PLAN " + sql; using var r = q.ExecuteReader(); var details = new List<string>(); while (r.Read()) details.Add(r.GetString(3)); Assert(details.All(d => !d.Contains("TEMP B-TREE")) && details.Any(d => d.Contains("ix_")));
        }
        return Task.CompletedTask;
    });
    await Test("Duplicados agrupados: páginas de grupos y 1.203 copias sin cargar el grupo completo", () =>
    {
        var root = NewDir("grouped-pages"); var cache = new AdvancedResultStore(Path.Combine(fixture, "grouped-pages.db"));
        cache.AddSources(Enumerable.Range(0, 1203).Select(i => new AdvancedFinding(new(Path.Combine(root, $"a{i:D4}.txt"), root, "big.txt", ".TXT", false, 10, 1, i, 0, "v"), "", "big-" + i, null, false, false)), default);
        cache.AddSources(Enumerable.Range(0, 22).Select(i => new AdvancedFinding(new(Path.Combine(root, $"z{i:D4}.txt"), root, $"small-{i / 2:D2}.txt", ".TXT", false, 3, 1, i, 0, "v"), "", "small-" + i, null, false, false)), default);
        cache.Complete(AdvancedKind.DuplicateName, default); var first = cache.DuplicateGroups(0); var last = cache.DuplicateGroups(1);
        Assert(first.Groups == 12 && first.Files == 1225 && first.Items.Count == 10 && last.Items.Count == 2 && first.Items.Select(g => g.Key).Intersect(last.Items.Select(g => g.Key)).Count() == 0);
        var big = first.Items.Single(g => g.Count == 1203); Assert(big.Size == 12030 && big.Kept == 0);
        var head = cache.DuplicateGroupItems(big.Key, 0); var next = cache.DuplicateGroupItems(big.Key, 1); var tail = cache.DuplicateGroupItems(big.Key, 24);
        Assert(head.Count == 50 && next.Count == 50 && tail.Count == 3 && head.Select(r => r.Path).Intersect(next.Select(r => r.Path)).Count() == 0 && tail[^1].Name == "big.txt");
        Assert(cache.DuplicateGroupItems("N:' OR 1=1 --", 0).Count == 0);
        cache.ApplyRule(KeepRule.Newest); Assert(cache.DuplicateGroups(0).Items.All(g => g.Kept == 1)); cache.MarkOriginal(head[0].Path); Assert(cache.Plan(AdvancedKind.DuplicateName, [tail[^1].Path]).Paths.Count == 1);
        Throws(() => cache.DuplicateGroups(-1)); Throws(() => cache.DuplicateGroupItems(big.Key, -1));
        return Task.CompletedTask;
    });
    await Test("100.000 entradas: paginación acotada y orden global", () =>
    {
        var root = NewDir("large"); var watch = System.Diagnostics.Stopwatch.StartNew();
        for (int batch = 0; batch < 200; batch++) store.WriteBatch(Enumerable.Range(batch * 500, 500).Select(i => new Entry(Path.Combine(root, $"{i:D6}.txt"), root, $"{i:D6}.txt", ".TXT", false, i, 1, 1, 0, "large")));
        var result = store.Search(new() { Volumes = ["large"] }, "size", true, 1); Assert(result.Total == 100000 && result.Items.Count == 200 && result.Items[0].Size == 99799); Console.WriteLine($"  Datos + consulta: {watch.Elapsed}. La fluidez visual requiere prueba manual."); return Task.CompletedTask;
    });
}
finally
{
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    // The harness cleanup deletion is restricted to its randomly named fixture.
    if (Path.GetFileName(fixture).StartsWith("CleanUpDesk-tests-", StringComparison.Ordinal) && Paths.Within(fixture, Paths.Normalize(fixtureBase))) Directory.Delete(fixture, true);
}
Console.WriteLine($"{passed} correctas; {failed} fallidas."); return failed == 0 ? 0 : 1;

/// <summary>Initializes the controlled fixture dependency used to reproduce this regression without operating on user files.</summary>
sealed class FixtureBackend(string root) : IDeleteBackend
{
    /// <summary>Deletes only an explicitly owned fixture file after validating the test backend boundary and mode.</summary>
    public void Delete(string path, bool directory, DeleteMode mode) { if (!Paths.Within(path, root) || mode != DeleteMode.Permanent || directory) throw new Exception("Fuera del alcance de prueba."); new WindowsDeleteBackend().Delete(path, directory, mode); }
}
sealed class RefusingBackend : IDeleteBackend
{
    public List<DeleteMode> Modes = []; 
        /// <summary>Simulates unsupported recycling and records the requested mode without deleting anything.</summary>
        public void Delete(string p, bool d, DeleteMode mode) { Modes.Add(mode); throw new IOException("Papelera no compatible (simulada)."); }
}
sealed class RecordingBackend : IDeleteBackend
{
    public int Calls; public DeleteMode Mode;
    /// <summary>Records a backend invocation without deleting a file.</summary>
    public void Delete(string path, bool directory, DeleteMode mode) { Calls++; Mode = mode; }
}
/// <summary>Initializes the controlled fixture dependency used to reproduce this regression without operating on user files.</summary>
sealed class ErrorTree(string root) : ITreeReader
{
    /// <summary>Enumerates owned fixture children while applying this test reader's controlled delay, interruption, or cancellation behavior.</summary>
    public IEnumerable<string> Children(string p) => [Path.Combine(root, "denied"), Path.Combine(root, "broken"), Path.Combine(root, "ok")];
    /// <summary>Returns fixture metadata or injects the isolated read failure required by this test reader.</summary>
    public Entry Read(string p, string v) { if (p.EndsWith("denied")) throw new UnauthorizedAccessException(); if (p.EndsWith("broken")) throw new IOException(); return new(p, root, "ok", "", false, 1, 1, 1, 0, v); }
}
/// <summary>Initializes the controlled fixture dependency used to reproduce this regression without operating on user files.</summary>
sealed class CancelTree(string root, CancellationTokenSource cts) : ITreeReader
{
    /// <summary>Enumerates owned fixture children while applying this test reader's controlled delay, interruption, or cancellation behavior.</summary>
    public IEnumerable<string> Children(string p) { for (int i = 0; i < 10000; i++) { if (i == 100) cts.Cancel(); yield return Path.Combine(root, i.ToString()); } }
    /// <summary>Returns fixture metadata or injects the isolated read failure required by this test reader.</summary>
    public Entry Read(string p, string v) => new(p, root, Path.GetFileName(p), "", false, 1, 1, 1, 0, v);
}
/// <summary>Initializes the controlled fixture dependency used to reproduce this regression without operating on user files.</summary>
sealed class ReparseTree(string root) : ITreeReader
{
    /// <summary>Enumerates owned fixture children while applying this test reader's controlled delay, interruption, or cancellation behavior.</summary>
    public IEnumerable<string> Children(string p) => [Path.Combine(root, "link")];
    /// <summary>Returns fixture metadata or injects the isolated read failure required by this test reader.</summary>
    public Entry Read(string p, string v) => new(p, root, "link", "", true, 0, 1, 1, (int)(FileAttributes.Directory | FileAttributes.ReparsePoint), v);
}
/// <summary>Initializes the controlled fixture dependency used to reproduce this regression without operating on user files.</summary>
sealed class InterruptedTree(string root) : ITreeReader
{
    /// <summary>Enumerates owned fixture children while applying this test reader's controlled delay, interruption, or cancellation behavior.</summary>
    public IEnumerable<string> Children(string p) { yield return Path.Combine(root, "nuevo.txt"); throw new IOException("Interrupción de lectura simulada."); }
    /// <summary>Returns fixture metadata or injects the isolated read failure required by this test reader.</summary>
    public Entry Read(string p, string v) => new(p, root, "nuevo.txt", ".TXT", false, 1, 1, 1, 0, v);
}
sealed class CountingTree : ITreeReader
{
    int reads;
    public int Reads => Volatile.Read(ref reads);
    /// <summary>Enumerates owned fixture children while applying this test reader's controlled delay, interruption, or cancellation behavior.</summary>
    public IEnumerable<string> Children(string p) { Interlocked.Increment(ref reads); return Directory.EnumerateFileSystemEntries(p); }
    /// <summary>Returns fixture metadata or injects the isolated read failure required by this test reader.</summary>
    public Entry Read(string p, string v) => Paths.ReadEntry(p, v);
}
sealed class FakeChangeFeed : ISearchChangeFeed
{
    public event Action<SearchChange>? Changed;
    public event Action? Interrupted;
    /// <summary>Emits a synthetic filesystem-change notification for the owned search-session fixture.</summary>
    public void Signal(SearchChange change) => Changed?.Invoke(change);
    /// <summary>Simulates a watcher interruption so the next search must reconcile its scope.</summary>
    public void Break() => Interrupted?.Invoke();
    /// <summary>Releases fixture synchronization or fake feed/audio resources without touching user data.</summary>
    public void Dispose() { Changed = null; Interrupted = null; }
}
/// <summary>Initializes the controlled fixture dependency used to reproduce this regression without operating on user files.</summary>
sealed class BlockingTree(string slow) : ITreeReader, IDisposable
{
    public ManualResetEventSlim Entered { get; } = new();
    public ManualResetEventSlim Release { get; } = new();
    /// <summary>Enumerates owned fixture children while applying this test reader's controlled delay, interruption, or cancellation behavior.</summary>
    public IEnumerable<string> Children(string p)
    {
        if (p.Equals(slow, StringComparison.OrdinalIgnoreCase)) { Entered.Set(); if (!Release.Wait(TimeSpan.FromSeconds(12))) throw new IOException("La prueba no liberó la carpeta lenta."); }
        return Directory.EnumerateFileSystemEntries(p);
    }
    /// <summary>Returns fixture metadata or injects the isolated read failure required by this test reader.</summary>
    public Entry Read(string p, string v) => Paths.ReadEntry(p, v);
    /// <summary>Releases fixture synchronization or fake feed/audio resources without touching user data.</summary>
    public void Dispose() { Entered.Dispose(); Release.Dispose(); }
}
/// <summary>Initializes the controlled fixture dependency used to reproduce this regression without operating on user files.</summary>
sealed class ImmediateProgress<T>(Action<T> action) : IProgress<T> { 
    /// <summary>Delivers test progress synchronously so cancellation timing is deterministic.</summary>
    public void Report(T value) => action(value); }
