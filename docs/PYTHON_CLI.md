# Python CLI Prototype

`python/sedf_cli.py` is a standard-library-only prototype of the core scanner, parser, grouping, scoring, and CSV report flow.

It does not replace the Windows desktop app yet. Its purpose is to validate the future portable core while the C# WinForms app remains the working application.

## Usage

```powershell
python python\sedf_cli.py "D:\Anime" --output same-episode-duplicates.csv
```

Write every parsed episodic file instead of duplicate candidates only:

```powershell
python python\sedf_cli.py "D:\Anime" --all --output all-episodes.csv
```

## Current Scope

- Parses anime-style names such as `[Group] Series - 01 [1080p][v2].mkv`.
- Parses season/episode names such as `Series.S01E12.1080p.mkv`.
- Groups rows by normalized title and episode key.
- Scores duplicates by resolution, version tag, then size.
- Writes CSV reports.

## Known Gaps

- No GUI.
- No Recycle Bin or move workflow.
- No AniDB or FileBot integration.
- No scan cache.
- Long-path behavior has not been tuned for Windows yet.
