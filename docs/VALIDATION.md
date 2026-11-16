# Validation

## Evidence and scope

The application has been manually tested by DanielCulpian during iterative development. Codex runs automated core and WPF regression harnesses. Automated success does not certify all hardware or prove that deletion is risk-free.

The latest functional baseline contains **66 core regression cases** and an isolated WPF integration suite. The documentation pass reruns compilation, these harnesses, source validation, and the named-routine documentation audit; its final run results are recorded below.

## Automated coverage

Core coverage includes combined AND/OR filters, inclusive size/date bounds, SQL parameters, Unicode and long paths, injected access/read errors, cancellation and partial coverage, disconnected volume models, reparse avoidance, duplicate/overlapping selections, critical path/container protection, two-step confirmation and single-use permits, identity changes, no recycle-to-permanent fallback, authorized temporary scopes, and index updates after fixture deletion.

Advanced checks cover sample-before-full SHA-256, cache invalidation, explicit keepers, actual emptiness validation, bounded grouping, warning filtering before the largest-file limit, and paginated SQL over 100,000 synthetic entries. Synthetic rows do not prove physical disk throughput.

WPF checks exercise actual views in both themes, concurrent search/browsing, cancellation, folder navigation preserving search, protected-item dialogs, advanced results, notifications, tracked shutdown, warning filtering, and duplicate-group layout at multiple widths. Tests inject silent audio and assert zero native playback attempts, preventing the regression where test windows emitted delayed completion sounds.

Destructive checks operate only on owned fixtures. Disconnects, some access failures, and reparse behavior are injected rather than tested by unplugging a user's device. The real Recycle Bin is not emptied by the suite.

## Measured fixture examples

- Eight distinct 2 MiB files: preliminary samples read 65,536 logical bytes instead of 16,777,216 full-file bytes (99.61% less). This is algorithmic read volume, not a universal speed guarantee.
- A 22,002-entry empty-file/folder fixture narrows to two filesystem candidates rather than opening every indexed path.
- A top-files fixture with 40 warning entries and 100 eligible replacements returns 100 eligible entries without reading file contents.
- Large-result tests check bounded pages and global ordering. Visual responsiveness on a user's storage still needs interactive testing.

## Remaining manual release checks

- Windows 10 and 11 on clean x64 installations; removable media, disconnect/reconnect, and changed drive letters.
- Keyboard navigation, Narrator, high contrast, mixed DPI, and small/large displays.
- Recycle Bin compatibility on supported media, using disposable files only.
- Physical audio timing and shutdown behavior under real device load; no automated claim of exact speaker latency.
- Clean-machine offline startup of each published package, dependency notices, archive integrity, and antivirus/signing behavior.

A self-contained package has not yet been validated in the current environment. There is no signed installer. A prior test-fixture cleanup incident has a local recovery helper; that machine-specific helper is excluded from publication and must never be used as a general cleanup utility.

## Documentation-pass results

Verified on November 16, 2026 with .NET SDK 10.0.404 on Windows:

| Check | Result |
| --- | --- |
| Release solution build, isolated artifacts directory | Passed, zero errors; five NU1900 warnings because the NuGet vulnerability service was unreachable |
| Core harness with `--workspace-fixtures` | 66 passed, zero failed |
| Isolated WPF harness | Passed; zero native audio playback attempts |
| Roslyn documentation audit | 538 named routines/primary constructors, zero missing summaries |
| Python structural/encoding check | Passed: 10 XML files, 77 event handlers, local references, safety guards, and Spanish UTF-8 UI text |
| Summary XML syntax | 504 XML summaries parsed successfully; local functions use plain summary comments |
| Publication checks | Nine generated/private path examples ignored, six source examples retained, 13 local documentation links resolved |
| Packaging script | PowerShell syntax parsed successfully; packaging itself was not rerun for this documentation pass |

The NU1900 warnings mean this run did **not** complete an online dependency vulnerability audit. Repeat restore/audit with network access before release. Generated verification logs stay in ignored local tooling directories rather than entering the public repository.
