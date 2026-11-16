# CleanUpDesk

CleanUpDesk is an offline file analysis and cleanup desktop application for **Windows 10 and 11 x64**, built with C#, .NET 10 LTS, WPF, and SQLite. The application interface is in Spanish; project documentation and source summaries are in English.

**Programmed by OpenAI Codex, directed and manually tested by a human: [DanielCulpian](https://github.com/DanielCulpian).** Codex also runs automated regression tests. Human feedback has driven the implementation and fixes; it does not imply certification on every Windows installation.

Release **1.0.0** has explicit assembly/package metadata and prepared [release notes](docs/releases/1.0.0.md). Publication and clean-machine package checks are separate steps; this text does not assert that a GitHub release has already been published. The project is licensed under [MIT](LICENSE).

## Features

- Discover local and removable volumes; browse folders independently of recursive indexing.
- Store metadata in SQLite and combine name, extension, size, date, type, attributes, path, and volume filters. Save favorite searches and retain results for disconnected volumes.
- Refresh indexes explicitly, cancel searches, copy paths, inspect details, and navigate from a folder search result without losing the search.
- Find duplicate candidates by name, size, or SHA-256; inspect expandable groups and choose copies to retain. Find empty files/folders and the largest 100 eligible files.
- Preview user/system temporary files, the Recycle Bin, optional known browser HTTP caches, and advisory old-file candidates.
- Review every deletion in two separate steps. Permanent deletion requires the exact phrase `ELIMINAR DEFINITIVAMENTE`. Protected locations are blocked rather than elevated.
- Light/dark themes, paginated and virtualized lists, progress/cancellation, optional completion sound, background-section notifications, and safe shutdown of active work.

No account, external service, or application telemetry is required. Builds initially need NuGet access; the installed application works offline. Names and paths in the local index are personal data: see [safety and privacy](docs/SAFETY.md).

## Build and run

Install the [**.NET 10 SDK for Windows x64**](https://dotnet.microsoft.com/download/dotnet/10.0). Visual Studio is optional. From the repository root in PowerShell:

```powershell
dotnet restore CleanUpDesk.slnx
dotnet build CleanUpDesk.slnx -c Release --no-restore
dotnet run --project tests/CleanUpDesk.Tests -c Release --no-build
dotnet run --project tests/CleanUpDesk.UiTests -c Release --no-build
dotnet run --project src/CleanUpDesk.App -c Release --no-build
```

The tests are console executables, not `dotnet test` projects. UI tests require an interactive Windows desktop and use a silent audio backend. They operate on owned fixtures, never on personal files. Optional static checks require Python 3:

```powershell
python scripts/verify-source.py
dotnet run --project scripts/DocumentationAudit -- .
```

`scripts/build.ps1` restores, builds, and runs both suites. For release assets, use `scripts/package-release.ps1` (self-contained) or add `-FrameworkDependent` (requires the desktop runtime). Both validate first and produce versioned ZIPs with licenses, release notes, and SHA-256 checksums. See [release preparation](docs/RELEASE.md) for the remaining publication checks.

## Quick start

1. Open **Discos** to browse a volume or select folders/volumes for analysis. Review skipped entries and the actual scan scope.
2. Open **Búsqueda global** to query indexed metadata. Use explicit disk/folder refresh actions for new or moved files; an index is not a perfect live mirror.
3. Combine filters: categories use AND, multiple extensions or volumes use OR. Remove active filters or reset them if a result is unexpectedly absent.
4. Select results to copy paths, open their containing folder, or request deletion. Double-clicking a folder navigates to browsing; it never deletes.
5. In **Limpieza** or **Opciones avanzadas**, analyze first and inspect candidates. Nothing is automatically cleaned on startup. Retention rules only select candidates; they do not execute deletion.

## Architecture and development

`CleanUpDesk.Core` separates traversal, query/index persistence, native file operations, safety policy, and cleanup providers. `CleanUpDesk.App` supplies XAML presentation, observable view models, dialogs, and operation coordination. The two test projects cover core behavior and actual WPF integration.

The project uses a pragmatic MVVM style: view models expose state, while window partial classes handle WPF events, clipboard, native dialogs, and UI coordination. It does not claim a strict code-behind-free MVVM architecture. Changes were developed iteratively by Codex, verified with automated fixtures, and refined through human testing and feedback.

- [Architecture and algorithms](docs/ARCHITECTURE.md)
- [Development workflow and source conventions](docs/DEVELOPMENT.md)
- [Deletion, cleanup, and privacy model](docs/SAFETY.md)
- [Executed validation and remaining manual checks](docs/VALIDATION.md)
- [Preparing release 1.0.0](docs/RELEASE.md)

## Limitations

Index completeness depends on permissions, connected volumes, selected scopes, exclusions, and the last refresh. Access errors are reported; the application does not take ownership or request administrator access. Estimated time and reclaimable space are approximate. Last-access timestamps do not reliably establish whether a file is unused. There is no automatic restoration, installer, or digital signature.

The application avoids following reparse points by default. Safety validation reduces risk but cannot make filesystem operations atomic against unrelated external changes. Hardware unplug/reconnect, screen-reader behavior, mixed-DPI setups, and the full Windows 10/11 compatibility matrix require manual validation before release.

## Credits

Project direction and human testing: [DanielCulpian](https://github.com/DanielCulpian). Implementation: OpenAI Codex. GitHub symbol: [Octicons](https://github.com/primer/octicons), MIT license preserved in [Octicons-LICENSE.txt](src/CleanUpDesk.App/Assets/Octicons-LICENSE.txt). The application logo is supplied with this project. Dependencies retain their own licenses; release packages must preserve their notices.
