# Development workflow

## Authorship and method

CleanUpDesk was programmed by **OpenAI Codex**, under the direction of **DanielCulpian**, who manually tested the application and supplied behavioral and visual feedback. Codex also executes automated checks. Neither AI generation nor passing tests replaces human review of filesystem safety or release validation.

Development followed small iterations: implement a real behavior, exercise it in isolated fixtures, check the actual WPF integration, then refine it from human feedback. Safety and bounded resource use take priority over secondary features. Architecture is pragmatic MVVM with separated Core services and explicit desktop orchestration; it is not an assertion of strict textbook MVVM or formal verification.

## Source conventions

- Keep application text in Spanish and documentation/comments in English. Save source as UTF-8; preserve Unicode fixture names.
- Document each named method, constructor, native declaration, and primary constructor with a meaningful XML summary. Local functions use `// Summary:` comments. Explain intent, constraints, or side effects rather than merely repeating the identifier.
- Keep SQL parameterized, I/O cancellable, and result sets bounded. Do not start implicit searches from binding refresh events.
- Route every destructive entry point through the shared two-step review and final confirmation. Never introduce silent permanent-delete fallback or administrator elevation.
- Do not commit indexes, personal paths, diagnostic logs, generated outputs, credentials, or local recovery scripts.

`scripts/DocumentationAudit` checks named C# routines and primary constructors in source/tests using the Roslyn assemblies shipped with the SDK, without adding a runtime dependency. It excludes generated build directories. Lambdas, accessors, compiler-generated members, and top-level script entry points are outside this named-routine check.

## Verification

```powershell
dotnet restore CleanUpDesk.slnx
dotnet build CleanUpDesk.slnx -c Release --no-restore
dotnet run --project tests/CleanUpDesk.Tests -c Release --no-build
dotnet run --project tests/CleanUpDesk.UiTests -c Release --no-build
dotnet run --project scripts/DocumentationAudit -- .
python scripts/verify-source.py
```

Tests are executable harnesses with failure exit codes. Core tests use random owned directories. UI tests run an isolated WPF application with temporary databases, controlled fixtures, and silent audio. Never exercise deletion against personal files, protected system folders, or the real Recycle Bin during development. Permission failures and disconnects can be injected; simulated conditions must be identified as such in reports.

Build into a separate artifacts path if a running application locks normal output files. Keep an existing build and the published folder separate: publishing does not update the executable under `bin/Release`. The optional Python source checker validates structure and encoding; it does not replace compilation or runtime tests.

## Contribution review

Describe the concrete behavior changed, safety implications, checks actually run, and remaining limitations. For reports include the executed build path, scope, reproducible steps, and sanitized diagnostics. Do not upload a personal index or unsanitized path listing. No release tag or version bump is implied by a documentation change.
