# Code Review Notes

Review date: 2026-05-07

## Summary

The app has a sensible safety posture for a duplicate-cleanup tool: delete actions are explicit, files go to the Windows Recycle Bin, generated scan artifacts are ignored by Git, and AniDB credentials are protected with Windows user-level data protection.

The main publishing risk is repository hygiene rather than a single fatal code issue. The source repo contains generated CSVs, built executables, local settings, logs, and AniDB cache/state files. These should remain untracked and should not be included in a public GitHub repository.

## Findings

### High Priority

- Do not publish local generated artifacts or credentials. The existing `.gitignore` covers the important local files, including CSV reports, scan caches, logs, build outputs, and `SameEpisodeDuplicateFinder.anidb`.

### Medium Priority

- The codebase is one large WinForms source file. This is workable for local iteration, but it will be hard for outside contributors to review. Good first split targets are AniDB client/settings, scan parsing, recommendation scoring, file operations, and UI form code.
- There is no automated test suite. The riskiest areas to test first are filename parsing, duplicate grouping, recommendation scoring, safe target path generation, and delete/move preview logic.
- Optional external integrations can fail for reasons outside the app: AniDB UDP/network availability, AniDB rate limits, and FileBot installation/configuration.

### Low Priority

- The build environment is classic .NET Framework rather than SDK-style `dotnet build`. That is fine for a Windows WinForms app, but a documented build script helps future users reproduce local builds.
- Consider adding screenshots before a public launch so users understand the review flow before running a cleanup tool on their own library.

## Suggested Release Checklist

- Confirm the public repo only contains source, docs, project/build files, and intentionally included examples.
- Add a license before making the repository public.
- Build the app from a clean checkout.
- Run a scan against a small test folder.
- Confirm Delete sends files to Recycle Bin.
- Confirm generated local files remain untracked.
- Add screenshots or a short demo GIF.
