# Publishing Checklist

Use this checklist when preparing the GitHub repository.

## Before First Push

1. Choose a repository name, for example `same-episode-duplicate-finder`.
2. Choose visibility: private while testing, public after license and safety notes are ready.
3. Choose a license if other people should be allowed to use or modify the code.
4. Confirm `git status --short` does not include generated CSVs, EXEs, logs, or AniDB settings.
5. Run `.\build.ps1` from a clean checkout.

## Suggested Repository Description

Windows duplicate-review tool for anime and episodic media libraries, with scan grouping, review recommendations, Recycle Bin deletes, CSV export, and optional AniDB/FileBot workflows.

## Suggested Topics

- windows
- winforms
- duplicate-files
- anime
- media-library
- anidb
- filebot
- csharp

## First Release Notes

Initial beta release with:

- Same-episode duplicate candidate scanning.
- Review grid and series navigation.
- Manual delete marks and recommendation-assisted auto mark.
- Recycle Bin delete behavior.
- CSV export and last-scan cache.
- Optional AniDB lookup and missing cover tools.
- Optional FileBot integration.

## Commands

After a GitHub repo exists:

```powershell
git init
git add README.md SameEpisodeDuplicateFinder.csproj build.ps1 .gitignore docs src
git commit -m "Prepare Same Episode Duplicate Finder for publishing"
git branch -M main
git remote add origin https://github.com/<owner>/<repo>.git
git push -u origin main
```
