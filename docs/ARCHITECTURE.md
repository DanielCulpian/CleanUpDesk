# Architecture

## Project boundaries

| Project | Responsibility |
| --- | --- |
| `src/CleanUpDesk.Core` | Metadata models, Windows volume discovery, traversal, SQLite queries, indexing, risk checks, deletion, cleanup providers, advanced analysis |
| `src/CleanUpDesk.App` | WPF XAML, themes, observable state, dialogs, native UI integration, operation coordination |
| `tests/CleanUpDesk.Tests` | Framework-free core regression executable using isolated fixtures |
| `tests/CleanUpDesk.UiTests` | STA WPF integration executable with isolated settings, databases, and silent audio |

Core does not depend on WPF. The application references Core. Runtime dependencies are intentionally small: Microsoft.Data.Sqlite and its transitive native SQLite packages. No remote backend, account system, telemetry pipeline, dependency-injection container, or external message bus is required.

### Main implementation areas

| Files | Role |
| --- | --- |
| `Models.cs`, `VolumeService.cs` | Metadata, settings, query contracts, and volume identities |
| `Scanner.cs`, `DirectoryBrowser.cs` | Recursive indexing and independent single-folder browsing |
| `IndexStore.cs`, `AdvancedResultStore.cs` | SQLite schema, bounded queries, and advanced result persistence |
| `SearchIndexRefresher.cs`, `SearchIndexSession.cs`, `SearchScopePlanner.cs` | Scope preparation, metadata refresh, and reconciliation |
| `Deletion.cs`, `NativeFiles.cs`, `FileRisk.cs`, `Paths.cs` | Shared safety policy, identity checks, and native filesystem operations |
| `Cleanup.cs`, `BrowserCache.cs`, `ForgottenFiles.cs` | Explicit cleanup scopes and advisory old-file review |
| `AdvancedAnalysis.cs` | Duplicate, empty-entry, and largest-file analysis and review plans |
| App `MainViewModel.cs`, `MainWindow` partials and XAML | Observable presentation, commands/events, dialogs, and operation coordination |
| App `CompletionSound.cs`, `ProcessNotifications.cs`, `SafeClosing.cs` | Completion signals, section attention indicators, and cooperative shutdown |

## Presentation and concurrency

XAML defines layouts and theme resources. `MainViewModel` exposes observable state; `MainWindow` partial classes coordinate search, advanced features, warning filters, notifications, and shutdown. This is pragmatic MVVM, with deliberate code-behind for desktop-specific orchestration.

Disk work runs off the Dispatcher with bounded concurrency and cancellation tokens. Browsing and folder-size calculations can continue while searches or advanced analysis run. Shared mutation/index operations use guards; destructive operations cannot overlap on the same destinations. Cancellation is cooperative: an active Windows call may finish before the next cancellation boundary. Closing the window warns about tracked work, then cancels and awaits it before exit if the user chooses to close.

## Indexing and queries

`VolumeService` uses stable volume identity where Windows supplies it, with a fallback when necessary. `Scanner` reads cached enumeration metadata, skips reparse points, isolates read failures, and writes batches through SQLite transactions. A cancelled scan records partial coverage rather than inventing completion. No content hashing is required for normal search.

`IndexStore` uses parameterized SQL, WAL, normalized names, suitable indexes, and bounded pages. Filter categories combine with AND; selections within a category combine with OR. Creation/modification and size bounds are inclusive. Offline index rows remain searchable with availability indicators. Browsing snapshots are separate from recursive scan coverage.

`SearchIndexRefresher`, `SearchScopePlanner`, and `SearchIndexSession` reconcile explicit search scopes. Watcher interruptions or overflow require reconciliation; external changes are not promised to appear perfectly in real time. Operations performed by the application update relevant indexed state.

## Advanced algorithms

Duplicate name/size modes compare metadata only and identify candidates, not proven identical content. SHA-256 mode narrows by size, samples the first/last 4 KiB, and hashes surviving candidates fully. Small files are hashed once. A bounded cache is reused only with identity/timestamp validation; unavailable native change stamps disable reuse. Selected duplicate deletions revalidate content and retain a protected keeper. The original copy is chosen explicitly, not inferred from a filename.

Empty-file/folder detection first uses indexed zero sizes and child-existence SQL. Filesystem validation then checks surviving candidates, including hidden/system children. A folder containing an empty child is not itself empty. Empty-folder deletion rechecks emptiness and uses a nonrecursive operation.

Largest-file analysis streams an ordered SQLite cursor through size/path indexes, without reading file contents or loading the full corpus. Index migration runs on a worker. Warning exclusion is applied before completing the top 100, so excluded rows are replaced by later eligible rows when available. Fewer than 100 can legitimately remain if the eligible scope is smaller.

Duplicate groups are paginated, initially collapsed, and expose wrapped full paths independently of metadata. Group/item pagination bounds view-model memory. Search grids use bounded pages and visual virtualization.

## Operations and persistence

`Deletion`, `FileRisk`, `NativeFiles`, and `Paths` implement plan validation, identity checks, protected locations, overlap reduction, and native execution. Cleanup providers enumerate authorized locations; they do not bypass the deletion workflow. See [SAFETY.md](SAFETY.md).

Local data lives under `%LOCALAPPDATA%\CleanUpDesk`: SQLite indexes/snapshots, JSON configuration, operation history, and bounded diagnostic logs. Indexes contain names and paths, not file contents. They are not encrypted. Settings expose index/history removal and log retention.

Completion notifications originate at worker completion instead of waiting for the Dispatcher. Native sound is preloaded, short, nonqueued, and discarded when busy or stale. Shutdown stops playback and prevents late notifications. Automated UI tests inject silent audio and assert zero native playback attempts.
