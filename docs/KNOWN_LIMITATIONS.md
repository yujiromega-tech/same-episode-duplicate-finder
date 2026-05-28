# Known Limitations

These notes describe the Phase 2.10 WinForms release-candidate baseline.

## Platform

- The active app is Windows-only and targets Windows 10 or newer with .NET Framework 4.8.
- Linux and macOS validation are deferred until the shared-core and Avalonia phases.

## UI

- The WinForms shell is the stable testing UI. Avalonia work has not started in this baseline.
- Layout has been polished for normal desktop sizes, but very small windows may still require scrolling or resizing.
- The right inspector and selected-series header prioritize review workflow density over a fully responsive design.

## Providers and Artwork

- AniDB remains the first source for cover art.
- TVDB and TMDB are fallback providers and require local API settings.
- Provider matches are heuristic. Review match diagnostics when artwork or metadata looks suspicious.
- Backdrop artwork is stored separately from cover artwork using `Series Name.backdrop.jpg`.

## File Operations

- Delete actions require review and send files to the Windows Recycle Bin.
- Network-path deletes are warned separately because Recycle Bin behavior may differ by share.
- Move/FileBot operations should be previewed before use on large libraries.

## Search and RSS

- Episode search depends on public RSS/search provider availability and reported seed counts.
- Full-season fallback is a review aid, not an automatic replacement workflow.
- The selected RSS feed is local to the running app session.

## Codebase

- A large amount of UI and workflow orchestration still lives in the WinForms source file.
- Phase 3.0 should extract scanner, parser, duplicate analysis, provider clients, artwork logic, action planner, RSS/feed logic, and diagnostics into shared core services before any Avalonia replacement work.
