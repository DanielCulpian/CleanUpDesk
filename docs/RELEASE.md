# Release 1.0.0 preparation

Release metadata is defined centrally in `Directory.Build.props`: package version 1.0.0 and assembly/file version 1.0.0.0. The application header remains uncluttered. No script creates a Git tag or publishes to GitHub.

## Build assets

From the repository root in Windows PowerShell, using the .NET 10 x64 SDK:

```powershell
# Recommended: includes the desktop runtime.
.\scripts\package-release.ps1

# Alternative: requires .NET Desktop Runtime 10 x64.
.\scripts\package-release.ps1 -FrameworkDependent
```

Both commands restore, build, audit source summaries, run both regression harnesses, publish into a fresh staging directory, copy licenses/notices and release notes, and create versioned ZIPs under `artifacts/release/1.0.0`. They also produce `SHA256SUMS.txt`. Upload the archives, not the staging directories or older unversioned packages.

The recommended asset is `CleanUpDesk-1.0.0-win-x64.zip`. The alternative is `CleanUpDesk-1.0.0-win-x64-framework-dependent.zip`. Extract all files and open `CleanUpDesk.App.exe`. Neither package is an installer or digitally signed.

## Actual preparation results

The current environment built the framework-dependent package from cached dependencies. Self-contained publishing failed with NU1301: NuGet network access is blocked and runtime packs are not cached. Generate the self-contained package from a normal terminal with NuGet access. Do not rename a framework-dependent ZIP to imply that it includes the runtime.

Regression results are recorded in [VALIDATION.md](VALIDATION.md). NU1900 warnings mean this environment could not perform the online vulnerability audit. Repeat with network access before publication:

```powershell
dotnet restore CleanUpDesk.slnx
dotnet list CleanUpDesk.slnx package --vulnerable --include-transitive
```

## Review before publication

Test the exact extracted ZIP with disposable files: startup, search, cancellation, themes, full paths, and both deletion confirmation steps. Validate on a clean Windows installation; test without an installed runtime for the self-contained package. Hardware, accessibility, physical audio timing, and removable-media checks remain manual; see [VALIDATION.md](VALIDATION.md). Preserve licenses and runtime notices.

## Commit and upload

Review the pending source changes, then commit release preparation:

```powershell
git add Directory.Build.props README.md THIRD-PARTY-NOTICES.md licenses/ scripts/package-release.ps1 docs/
git diff --cached --stat
git commit -m "chore: prepare release 1.0.0"
git push origin main
```

Open GitHub **Releases → Draft a new release**. Create tag **v1.0.0** targeting the newly pushed main commit. Title: **CleanUpDesk 1.0.0**. Paste [the release notes](releases/1.0.0.md), attach the tested executable ZIP and `SHA256SUMS.txt`, and save as a draft while reviewing. Publish as a stable release after the package checks. Leave the prerelease checkbox unchecked for the final 1.0.0.

GitHub automatically attaches source archives; those are not ready-to-run application packages. Upload the application ZIP separately. Keep generated binaries out of source commits. Use a new version for corrections after publication instead of moving the tag.
