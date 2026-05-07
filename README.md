# Same Episode Duplicate Finder

Same Episode Duplicate Finder is a Windows desktop tool for reviewing duplicate anime or episodic media files. It scans a folder, groups files that look like the same series episode, recommends likely keep/delete choices, and helps move or recycle files after manual review.

Current version: `0.0.1`.

The app is designed for large, messy libraries where duplicate episodes may differ by release group, size, path, naming style, version tag, or AniDB metadata.

## Features

- Scans local or network folders for same-episode duplicate candidates.
- Parses series title, episode number, subtitle/release group, version, size, filename, and location.
- Shows duplicate groups in a sortable WinForms grid.
- Provides a series panel for quickly jumping between groups.
- Offers recommendation labels such as keep/delete, confidence, review status, and reason.
- Supports manual delete marking plus configurable auto-mark thresholds.
- Sends deleted files to the Windows Recycle Bin rather than permanent deletion.
- Exports review data to CSV.
- Saves and reloads the last scan cache.
- Supports configurable file extension filters.
- Includes optional AniDB lookup, AniDB title matching, and missing cover workflow.
- Includes optional FileBot integration for external rename/move workflows.
- Handles long paths in selected delete operations.

## Use Cases

- Review duplicate anime episodes before deleting anything.
- Compare duplicate files split across multiple library folders or drives.
- Find all files for a selected series and move them into one clean folder.
- Identify likely lower-quality or redundant releases while keeping a manual review step.
- Export duplicate candidates for spreadsheet review.
- Use AniDB metadata to improve series identification and cover art organization.
- Prepare a cleanup batch but preview it before applying changes.

## Safety Model

This tool is review assistance, not an automatic deletion authority.

- Delete actions require files to be marked first.
- The final delete action sends files to the Windows Recycle Bin.
- Auto Mark can be limited to high-confidence recommendations.
- Suggested actions can be previewed before applying marks or moves.
- Move operations write a move report next to the executable.
- Start with a small folder before scanning an entire media library.

## Requirements

- Windows.
- .NET Framework 4.x runtime.
- Optional: AniDB account and registered client name for AniDB lookup.
- Optional: FileBot for FileBot-assisted rename/move workflows.

## Build

This repository includes a simple PowerShell build script that uses the classic .NET Framework compiler included with Windows:

```powershell
.\build.ps1
```

The compiled executable is written to:

```text
dist\SameEpisodeDuplicateFinder.exe
```

If you prefer Visual Studio, open `SameEpisodeDuplicateFinder.csproj` and build it as a Windows Forms application.

## Tests

Run the focused unit test harness with:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build-tests.ps1
```

The tests cover filename parsing, duplicate-group counting, recommendation scoring helpers, and safe target path generation.

## Usage

1. Launch `SameEpisodeDuplicateFinder.exe`.
2. Choose **File > Browse and Scan...**.
3. Select a media folder.
4. Review duplicate candidates in the grid.
5. Use the series panel, search, sorting, and recommendation fields to inspect groups.
6. Optionally run **Tools > Suggest Best Actions**.
7. Mark only files you are comfortable removing.
8. Review **Deletion Ready** before pressing Delete.

## Generated Files

The app may create local files next to the executable:

- `SameEpisodeDuplicateFinder.lastscan.*` for cached scan results.
- `SameEpisodeDuplicateFinder.fileformats` for scan extension filters.
- `SameEpisodeDuplicateFinder.columns` for visible column layout.
- `SameEpisodeDuplicateFinder.automark` for auto-mark threshold.
- `SameEpisodeDuplicateFinder.errors.log` for local error details.
- `SameEpisodeDuplicateFinder.anidb` for AniDB settings.
- `SameEpisodeDuplicateFinder.anidb-titles.xml` for the AniDB title cache.

These files are ignored by Git because they may contain local library data, credentials, or machine-specific state.

## AniDB Notes

AniDB lookup uses AniDB's UDP API and title/cover endpoints. Saved AniDB passwords are protected with Windows user-level data protection for the current Windows account, but the settings file is still local private state and should not be committed.

If UDP lookup times out, wait before retrying and confirm that UDP port 9000 is allowed.

## Limitations

- The app is Windows-only.
- The current codebase is a single large WinForms source file.
- There is no automated test suite yet.
- Duplicate recommendations are heuristic and should be manually reviewed.
- External services and tools, including AniDB and FileBot, may fail or be rate-limited independently of this app.

## Roadmap

- Split the single source file into smaller classes.
- Add unit tests for filename parsing, grouping, scoring, and target path generation.
- Add sample screenshots and example CSV output.
- Add a signed release build workflow.
- Consider a safer dry-run report for all destructive or move operations.

## License

No open-source license has been selected yet. Add a license before publishing publicly if others should be allowed to use, modify, or redistribute the code.

## Changelog

### 0.0.1

- Initial beta-ready source package.
- Added WinForms duplicate review app source.
- Added reproducible local build script.
- Added GitHub-facing README, publishing checklist, and code review notes.
