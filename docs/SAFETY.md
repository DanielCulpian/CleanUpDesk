# Safety and privacy

## Deletion workflow

All entry points request deletion through a common workflow. First, a review window shows the complete proposed selection, paths, count, estimated logical size, and mode; entries can be removed. A separate final confirmation requires another explicit action. Permanent deletion additionally requires `ELIMINAR DEFINITIVAMENTE`. Destructive buttons do not receive default focus.

The plan removes duplicates and descendant overlaps, checks availability, and records filesystem identity where supported. Immediately before execution, the application revalidates targets and unexpected reparse paths. Drive roots, critical Windows/boot/recovery/application locations and their containing folders are blocked. Permission restrictions are respected: no ownership change, elevation, or ACL modification is attempted.

Recycle Bin operations use Windows APIs. If recycling is unsupported or fails, the operation stops; it never silently becomes permanent. A permanent attempt requires a new specific review/confirmation. Cancelling between items leaves completed deletions completed. Each item receives an outcome and the local index/history is updated. The application does not provide automatic restoration; use the Windows Recycle Bin for recoverable items.

Checks reduce mistakes but are not a transactional filesystem lock against every external change. Logical bytes do not equal guaranteed reclaimed physical space: hard links, compression, sparse files, and the Recycle Bin affect the result.

## Cleanup boundaries

User temporary files come from the known temporary location. Windows temporary files require explicit category selection. A configurable conservative age threshold applies; locked, protected, and unsafe entries are skipped. Roots are preserved and links are not followed outside authorized locations. No folder is classified as disposable merely because its name is `temp`.

Browser cleanup is optional and limited to known local HTTP cache locations. It excludes passwords, cookies, history, and sessions, and checks relevant browser processes. Portable/custom profiles are not universally discovered. Downloads, personal documents, registry entries, drivers, restore points, and update components are not default cleanup categories.

Recycle Bin preview and emptying use Windows APIs for the displayed drive scope. Emptying is permanent and also requires both verification steps and the phrase. Do not interpret the preview as individual file-level restoration support.

Old-file recommendations are advisory metadata candidates. Last-access timestamps may be disabled, delayed, or changed by software. No candidate is automatically deleted; startup review only prepares information.

## Local data

The application has no accounts, remote services, telemetry, or background uploads. It stores personal filenames and paths under `%LOCALAPPDATA%\CleanUpDesk`, including SQLite indexes, snapshots, configuration, and bounded diagnostics. These files are not encrypted. Use settings to clear indexes/history and configure retention. Deleting an index does not delete indexed user files.

The .NET SDK's own CLI telemetry is separate from application behavior; the build script opts out for its process. Do not publish local databases or unsanitized diagnostics when reporting issues. Repository ignore rules are a safeguard, not a substitute for reviewing staged files.
