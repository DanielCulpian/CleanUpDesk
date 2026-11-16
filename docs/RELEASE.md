# Preparing release 1.0.0

The intended first public release is **1.0.0**. This document is a preparation checklist, not evidence that a tag or GitHub release already exists. Do not publish local indexes, logs, fixtures, or build caches.

## Preflight

1. Run all commands in [DEVELOPMENT.md](DEVELOPMENT.md), inspect failures and NuGet security warnings, and record actual results in [VALIDATION.md](VALIDATION.md).
2. Complete the manual Windows, accessibility, removable-media, and package checks. Use disposable test files only.
3. Review staged files against `.gitignore`; ensure MIT and the original Octicons license are present. Review transitive dependency notices for the distributed package.
4. Choose and consistently apply release metadata when preparing the actual release; this documentation pass does not bump assembly/package versions.

## Packaging

Self-contained Windows x64 (runtime packages must be available during restore/publish):

```powershell
.\scripts\build.ps1 -Publish
```

This runs build and both test harnesses before producing `artifacts/CleanUpDesk-win-x64.zip`. No installed .NET runtime should be needed, but verify that on a clean machine before making that claim for a release asset.

Framework-dependent alternative:

```powershell
dotnet publish src/CleanUpDesk.App -c Release --self-contained false -o artifacts/portable
Copy-Item LICENSE artifacts/portable/LICENSE
Copy-Item README.md artifacts/portable/README.md
Compress-Archive -Path artifacts/portable/* -DestinationPath artifacts/CleanUpDesk-framework-dependent.zip -Force
```

The framework-dependent package requires **.NET Desktop Runtime 10 x64**. Extract the entire archive and run `CleanUpDesk.App.exe`; copying only the executable is insufficient. Neither package is an installer or digitally signed by this workflow.

## Publication

Review archive contents and integrity, include the project license and applicable dependency notices, test startup offline, and document known limitations. Only then create the repository/tag/release and attach the tested archive. Keep source code in Git; generated binaries belong in release assets, not normal source commits. Future ARM64 support requires publishing and testing on that hardware rather than assuming x64 validation applies.
