using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SameEpisodeDuplicateFinder.Tests
{
    internal static class UnitTestRunner
    {
        private static int passed;
        private static int failed;

        public static int Main()
        {
            Run("filename parsing handles anime release names", FilenameParsingHandlesAnimeReleaseNames);
            Run("filename parsing preserves file created year", FilenameParsingPreservesFileCreatedYear);
            Run("filename parsing handles season episode names", FilenameParsingHandlesSeasonEpisodeNames);
            Run("filename parsing ignores years and ambiguous ranges", FilenameParsingIgnoresYearsAndAmbiguousRanges);
            Run("filename parsing handles anime part titles", FilenameParsingHandlesAnimePartTitles);
            Run("grouping counts only repeated episode keys", GroupingCountsRepeatedEpisodeKeys);
            Run("file format filter supports ignore and allow-only modes", FileFormatFilterSupportsIgnoreAndAllowOnlyModes);
            Run("scoring prefers resolution, version, then size", ScoringPrefersResolutionVersionThenSize);
            Run("target path generation sanitizes folders and avoids collisions", TargetPathGenerationSanitizesAndAvoidsCollisions);
            Run("series cover filenames use mapped series title", SeriesCoverFilenamesUseMappedSeriesTitle);
            Run("action report writer escapes csv fields", ActionReportWriterEscapesCsvFields);
            Run("search matches only series title", SearchMatchesOnlySeriesTitle);
            Run("cover search titles handle anime part suffixes", CoverSearchTitlesHandleAnimePartSuffixes);
            Run("cover matching reference year uses file creation time", CoverMatchingReferenceYearUsesFileCreationTime);
            Run("automatic AniDB matches require high confidence", AutomaticAniDbMatchesRequireHighConfidence);
            Run("rejected AniDB near matches include review context", RejectedAniDbNearMatchesIncludeReviewContext);
            Run("AniDB cover safety rejects stale wrong metadata", AniDbCoverSafetyRejectsStaleWrongMetadata);
            Run("AniDB title scoring does not inflate repeated words", AniDbTitleScoringDoesNotInflateRepeatedWords);
            Run("AniDB title scoring ignores unrelated language aliases", AniDbTitleScoringIgnoresUnrelatedLanguageAliases);
            Run("missing episode finder reports local gaps", MissingEpisodeFinderReportsLocalGaps);
            Run("missing episode search query uses search key", MissingEpisodeSearchQueryUsesSearchKey);
            Run("episode search triggers full-season fallback after weak seeded results", EpisodeSearchTriggersFullSeasonFallbackAfterWeakSeededResults);
            Run("episode search builds magnet link", EpisodeSearchBuildsMagnetLink);
            Run("merged scan recomputes duplicate groups across roots", MergedScanRecomputesDuplicateGroupsAcrossRoots);
            Run("network paths are detected before deletion", NetworkPathsAreDetectedBeforeDeletion);
            Run("library action planner previews duplicate cover and gap work", LibraryActionPlannerPreviewsDuplicateCoverAndGapWork);

            Console.WriteLine();
            Console.WriteLine("{0} passed, {1} failed", passed, failed);
            return failed == 0 ? 0 : 1;
        }

        private static void FilenameParsingHandlesAnimeReleaseNames()
        {
            var root = Path.Combine(Path.GetTempPath(), "sedf-tests");
            var file = CreateScannedFile(root, "Library", "[SubsPlease] Frieren - Beyond Journey's End - 01 [1080p][v2].mkv", 734003200);

            EpisodeFile parsed;
            AssertTrue(EpisodeParser.TryParseFile(file, root, out parsed), "file should parse");
            AssertEqual("SubsPlease", parsed.SubtitleGroup, "subtitle group");
            AssertEqual("Frieren Beyond Journey's End", parsed.Title, "title");
            AssertEqual("E001", parsed.Episode, "episode");
            AssertEqual("v2", parsed.Version, "version");
            AssertEqual("frieren beyond journey's end|E001", parsed.Key, "group key");
            AssertEqual(file.FullName, parsed.Path, "path");
        }

        private static void FilenameParsingPreservesFileCreatedYear()
        {
            var root = Path.Combine(Path.GetTempPath(), "sedf-tests");
            var file = CreateScannedFile(root, "Library", "Series - 01.mkv", 1000);
            file.CreatedUtcTicks = new DateTime(2025, 7, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;

            EpisodeFile parsed;
            AssertTrue(EpisodeParser.TryParseFile(file, root, out parsed), "file should parse");
            AssertEqual(file.CreatedUtcTicks, parsed.CreatedUtcTicks, "created time should be preserved");
        }

        private static void FilenameParsingHandlesSeasonEpisodeNames()
        {
            var root = Path.Combine(Path.GetTempPath(), "sedf-tests");
            var file = CreateScannedFile(root, "Shows\\Delicious in Dungeon\\Season 1", "Delicious.in.Dungeon.S01E12.720p.mkv", 524288000);

            EpisodeFile parsed;
            AssertTrue(EpisodeParser.TryParseFile(file, root, out parsed), "file should parse");
            AssertEqual("Delicious in Dungeon", parsed.Title, "title");
            AssertEqual("S01E12", parsed.Episode, "episode");
            AssertEqual("delicious in dungeon|S01E12", parsed.Key, "group key");
        }

        private static void FilenameParsingIgnoresYearsAndAmbiguousRanges()
        {
            var root = Path.Combine(Path.GetTempPath(), "sedf-tests");
            var yearFile = CreateScannedFile(root, "Library", "[Group] Series (2006) - 04.mkv", 100);
            var remasterFile = CreateScannedFile(root, "Library", "Series - 2006 Remaster.mkv", 100);
            var rangeFile = CreateScannedFile(root, "Library", "Series - 01-02.mkv", 100);

            EpisodeFile parsed;
            AssertTrue(EpisodeParser.TryParseFile(yearFile, root, out parsed), "year file should parse");
            AssertEqual("Series", parsed.Title, "year should stay out of title");
            AssertEqual("E004", parsed.Episode, "episode should be 04, not the year");
            AssertTrue(!EpisodeParser.TryParseFile(remasterFile, root, out parsed), "remaster year should not parse as episode");
            AssertTrue(!EpisodeParser.TryParseFile(rangeFile, root, out parsed), "ambiguous ranges should be left for manual review");
        }

        private static void FilenameParsingHandlesAnimePartTitles()
        {
            var root = Path.Combine(Path.GetTempPath(), "sedf-tests");
            var file = CreateScannedFile(root, "Library", "[Erai-raws] Dr Stone - Science Future Part 3 - 06 [720p CR WEB-DL AVC AAC][MultiSub][F7D71D86].mkv", 100);

            EpisodeFile parsed;
            AssertTrue(EpisodeParser.TryParseFile(file, root, out parsed), "part title should parse");
            AssertEqual("Erai-raws", parsed.SubtitleGroup, "subtitle group");
            AssertEqual("Dr Stone Science Future Part 3", parsed.Title, "part title");
            AssertEqual("E006", parsed.Episode, "episode");
        }

        private static void GroupingCountsRepeatedEpisodeKeys()
        {
            var files = new List<EpisodeFile>
            {
                NewEpisode("show|E001", "Show", "Show - 01 [1080p].mkv", 100),
                NewEpisode("SHOW|E001", "Show", "Show - 01 [720p].mkv", 90),
                NewEpisode("show|E002", "Show", "Show - 02 [1080p].mkv", 100),
                NewEpisode("other|E001", "Other", "Other - 01 [1080p].mkv", 100),
                NewEpisode("other|E001", "Other", "Other - 01 [720p].mkv", 90)
            };

            AssertEqual(2, EpisodeParser.CountDuplicateEpisodeGroups(files), "duplicate group count");
        }

        private static void FileFormatFilterSupportsIgnoreAndAllowOnlyModes()
        {
            var root = Path.Combine(Path.GetTempPath(), "sedf-tests");
            var defaultFilter = FileFormatFilter.CreateDefault();

            AssertTrue(defaultFilter.ShouldIgnore(CreateScannedFile(root, "Library", "Show - 01.ass", 10)), "default filter should ignore subtitles");
            AssertTrue(!defaultFilter.ShouldIgnore(CreateScannedFile(root, "Library", "Show - 01.mkv", 10)), "default filter should scan video");
            AssertEqual(".mkv", FileFormatFilter.NormalizeExtension(" MKV "), "extension normalization");

            var allowOnly = new FileFormatFilter();
            allowOnly.AllowOnlyListed = true;
            allowOnly.Extensions.Add(".mkv");

            AssertTrue(!allowOnly.ShouldIgnore(CreateScannedFile(root, "Library", "Show - 01.mkv", 10)), "allow-only should scan listed extension");
            AssertTrue(allowOnly.ShouldIgnore(CreateScannedFile(root, "Library", "Show - 01.mp4", 10)), "allow-only should skip unlisted extension");
            AssertTrue(allowOnly.ShouldIgnore(CreateScannedFile(root, "Library", "README", 10)), "allow-only should skip extensionless files");
        }

        private static void ScoringPrefersResolutionVersionThenSize()
        {
            var keep = NewEpisode("show|E001", "Show", "Show - 01 [1080p][v2].mkv", 700L * 1024L * 1024L);
            keep.Version = "v2";
            var lowerResolution = NewEpisode("show|E001", "Show", "Show - 01 [720p][v9].mkv", 900L * 1024L * 1024L);
            lowerResolution.Version = "v9";
            var sameResolutionOlderVersion = NewEpisode("show|E001", "Show", "Show - 01 [1080p][v1].mkv", 800L * 1024L * 1024L);
            sameResolutionOlderVersion.Version = "v1";
            var sameResolutionSameVersionSmaller = NewEpisode("show|E001", "Show", "Show - 01 [1080p][v2].mkv", 500L * 1024L * 1024L);
            sameResolutionSameVersionSmaller.Version = "v2";

            AssertTrue(RecommendationScorer.GetAutoKeepScore(keep) > RecommendationScorer.GetAutoKeepScore(lowerResolution), "1080p should beat lower resolution even when lower file is newer/larger");
            AssertTrue(RecommendationScorer.GetAutoKeepScore(keep) > RecommendationScorer.GetAutoKeepScore(sameResolutionOlderVersion), "newer version should beat older version");
            AssertTrue(RecommendationScorer.GetAutoKeepScore(keep) > RecommendationScorer.GetAutoKeepScore(sameResolutionSameVersionSmaller), "larger file should win after resolution/version tie");
            AssertEqual("High", RecommendationScorer.GetDeleteConfidence(1000000), "high confidence threshold");
            AssertEqual("Medium", RecommendationScorer.GetDeleteConfidence(1), "medium confidence threshold");
            AssertEqual("Low", RecommendationScorer.GetDeleteConfidence(0), "low confidence threshold");
            AssertContains(RecommendationScorer.BuildRecommendationReason(lowerResolution, keep), "higher resolution", "recommendation reason");
        }

        private static void TargetPathGenerationSanitizesAndAvoidsCollisions()
        {
            var root = Path.Combine(Path.GetTempPath(), "sedf-target-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var targetRoot = Path.Combine(root, "Target");
                Directory.CreateDirectory(targetRoot);
                var safeFolder = MainForm.GetSafeFolderName("A:B<C?");
                var existingFolder = Path.Combine(targetRoot, safeFolder);
                Directory.CreateDirectory(existingFolder);
                File.WriteAllText(Path.Combine(existingFolder, "Episode.mkv"), "");

                var currentPath = Path.Combine(root, "Source", "Episode.mkv");
                Directory.CreateDirectory(Path.GetDirectoryName(currentPath));
                File.WriteAllText(currentPath, "");
                var row = NewEpisode("show|E001", "Show", "Episode.mkv", 10);
                row.Path = currentPath;
                row.FileName = "Episode.mkv";

                string targetPath;
                AssertTrue(MainForm.TryGetSeriesFolderPath(row, targetRoot, "A:B<C?", out targetPath), "target path should be generated");
                AssertEqual(Path.Combine(existingFolder, "Episode (2).mkv"), targetPath, "collision target path");
                AssertEqual("A_B_C_", safeFolder, "safe folder name");
                AssertTrue(MainForm.PathsEqual(currentPath.ToUpperInvariant(), currentPath.ToLowerInvariant()), "path comparison should be case-insensitive");
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
        }

        private static void SeriesCoverFilenamesUseMappedSeriesTitle()
        {
            AssertEqual("Air Gear.jpg", MainForm.GetSeriesCoverFileName("Air Gear"), "plain title cover name");
            AssertEqual("Dr Stone _ Science Future Part 3.jpg", MainForm.GetSeriesCoverFileName("Dr Stone : Science Future Part 3"), "invalid filename characters should be replaced");
            AssertEqual("series-cover.jpg", MainForm.GetSeriesCoverFileName(""), "blank title fallback");
        }

        private static void ActionReportWriterEscapesCsvFields()
        {
            var rows = new List<ActionPreviewRow>
            {
                new ActionPreviewRow
                {
                    Action = "Delete marked",
                    Confidence = "High",
                    Reason = "Quoted \"reason\", with comma",
                    CurrentPath = "C:\\Media\\Old.mkv",
                    TargetPath = "Recycle Bin"
                }
            };

            using (var writer = new StringWriter())
            {
                MainForm.WriteActionReport(writer, rows);
                var csv = writer.ToString();
                AssertContains(csv, "Action,Status,Reason,OldPath,NewPath", "csv header");
                AssertContains(csv, "\"Quoted \"\"reason\"\", with comma\"", "csv escaped reason");
            }
        }

        private static void SearchMatchesOnlySeriesTitle()
        {
            var row = NewEpisode("frieren|E001", "Frieren Beyond Journey's End", "[SubsPlease] Different File Name - 01.mkv", 100);
            row.FileLocation = "C:\\Media\\Other Folder";
            row.SubtitleGroup = "SubsPlease";
            row.RecommendationReason = "kept file has higher resolution";

            AssertTrue(MainForm.SeriesTitleMatchesSearch(row, "frieren"), "series title should match");
            AssertTrue(!MainForm.SeriesTitleMatchesSearch(row, "SubsPlease"), "subtitle group should not match");
            AssertTrue(!MainForm.SeriesTitleMatchesSearch(row, "Different File Name"), "file name should not match");
            AssertTrue(!MainForm.SeriesTitleMatchesSearch(row, "higher resolution"), "recommendation reason should not match");
        }

        private static void CoverSearchTitlesHandleAnimePartSuffixes()
        {
            var titles = MainForm.BuildCoverSearchTitles("Dr Stone - Science Future Part 3");

            AssertEqual("Dr. Stone: Science Future Part 3", titles[0], "specific provider-friendly title");
            AssertTrue(titles.Contains("Dr. Stone: Science Future"), "part suffix should be optional for cover lookup");
            AssertTrue(titles.Contains("Dr. Stone"), "base series should be a fallback");
            AssertTrue(titles.IndexOf("Dr. Stone: Science Future") < titles.IndexOf("Dr. Stone"), "specific title should be tried before base series");

            var seasonTitles = MainForm.BuildCoverSearchTitles("Yozakura san Chi no Daisakusen 2nd Season");
            AssertTrue(seasonTitles.Contains("Yozakura san Chi no Daisakusen Season 2"), "ordinal season should also search AniDB-style season number");
        }

        private static void CoverMatchingReferenceYearUsesFileCreationTime()
        {
            var files = new[]
            {
                new EpisodeFile { Title = "Show", CreatedUtcTicks = new DateTime(2024, 5, 1, 0, 0, 0, DateTimeKind.Utc).Ticks },
                new EpisodeFile { Title = "Show", CreatedUtcTicks = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc).Ticks },
                new EpisodeFile { Title = "Show", CreatedUtcTicks = new DateTime(2023, 6, 1, 0, 0, 0, DateTimeKind.Utc).Ticks }
            };

            AssertEqual(2024, MainForm.GetReferenceYearFromCreatedUtcForTest(files), "most common created year should be used as reference");
        }

        private static void AutomaticAniDbMatchesRequireHighConfidence()
        {
            AssertTrue(MainForm.IsAutomaticAniDbMatchConfident(new AniDbTitleCandidate { Title = "Bad Girl", Score = 100 }), "exact title should be safe for automatic metadata/cover use");
            AssertTrue(!MainForm.IsAutomaticAniDbMatchConfident(new AniDbTitleCandidate { Title = "Sousou no Frieren", Score = 85 }), "base-series fallback should require manual review before saving cover art");
            AssertTrue(!MainForm.IsAutomaticAniDbMatchConfident(new AniDbTitleCandidate { Title = "Unrelated low-confidence title", Score = 35 }), "weak AniDB title matches should require manual review");
            AssertTrue(MainForm.IsAutomaticAniDbMatchConfidentForTest(
                "Hell Mode",
                new AniDbTitleCandidate
                {
                    Title = "Hell Mode: The Hardcore Gamer Dominates in Another World with Garbage Balancing",
                    TitleType = "official",
                    Score = 85
                }),
                "official full-title expansions should be safe when the selected title is a clear prefix");
            AssertTrue(MainForm.IsAutomaticAniDbMatchConfidentForTest(
                "Jigokuraku 2nd Season",
                new AniDbTitleCandidate
                {
                    Title = "Jigokuraku (2026)",
                    TitleType = "main",
                    Score = 85
                }),
                "season titles represented by an explicit year should be safe when the base title is exact");
            AssertTrue(!MainForm.IsAutomaticAniDbMatchConfidentForTest(
                "Urusei Yatsura 2nd Season",
                new AniDbTitleCandidate
                {
                    Title = "Urusei Yatsura",
                    TitleType = "main",
                    Score = 85
                }),
                "base-series season fallbacks should still require manual review");
        }

        private static void RejectedAniDbNearMatchesIncludeReviewContext()
        {
            var text = MainForm.BuildRejectedAniDbCandidateDiagnosticForTest(
                "Urusei Yatsura 2nd Season",
                new AniDbTitleCandidate
                {
                    AniDbId = "123",
                    Title = "Urusei Yatsura",
                    TitleType = "syn",
                    Score = 85
                },
                "metadata lookup");

            AssertContains(text, "Urusei Yatsura 2nd Season", "diagnostic should include query");
            AssertContains(text, "Urusei Yatsura", "diagnostic should include rejected title");
            AssertContains(text, "score=85", "diagnostic should include score");
            AssertContains(text, "alternate title/language", "diagnostic should explain why it is logged");
        }

        private static void AniDbCoverSafetyRejectsStaleWrongMetadata()
        {
            var wrong = new AniDbAnimeResult
            {
                AniDbId = "999",
                Title = ".אוונגליון 2: (אי) אפשר להתקדם",
                Score = 0
            };
            var exact = new AniDbAnimeResult
            {
                AniDbId = "100",
                Title = "Urusei Yatsura 2nd Season",
                Score = 0
            };
            var yearSeason = new AniDbAnimeResult
            {
                AniDbId = "18090",
                Title = "Jigokuraku (2026)",
                Score = 85
            };

            AssertTrue(!MainForm.IsAniDbCoverMatchSafeForTest("Urusei Yatsura 2nd Season", wrong), "wrong stale metadata should not be accepted for cover art");
            AssertTrue(MainForm.IsAniDbCoverMatchSafeForTest("Urusei Yatsura 2nd Season", exact), "exact title metadata should be accepted even without a stored score");
            AssertTrue(MainForm.IsAniDbCoverMatchSafeForTest("Jigokuraku 2nd Season", yearSeason), "year-labeled sequel metadata should be accepted for cover art when the base title is exact");
        }

        private static void AniDbTitleScoringDoesNotInflateRepeatedWords()
        {
            AssertEqual(100, AniDbTitleIndex.ScoreTitleForTest("Bullet Bullet", "Bullet Bullet"), "exact repeated-word title should still be exact");
            AssertTrue(AniDbTitleIndex.ScoreTitleForTest("Bullet Bullet", "Black Bullet") < 85, "one shared repeated token should not be accepted as an automatic match");
            AssertEqual(85, AniDbTitleIndex.ScoreTitleForTest("Sousou no Frieren 2nd Season", "Sousou no Frieren"), "base series fallback should stay usable");
            AssertEqual(100, AniDbTitleIndex.ScoreTitleForTest("Yozakura san Chi no Daisakusen 2nd Season", "Yozakura-san Chi no Daisakusen Season 2"), "ordinal season variants should score as exact");
            AssertEqual(100, AniDbTitleIndex.ScoreTitleForTest("Kingdom 6th Season", "Kingdom Season 6"), "ordinal season numbers should normalize before scoring");
            AssertEqual(100, AniDbTitleIndex.ScoreTitleForTest("Kijin Gentoushou", "Kijin Gentou Shou"), "romaji spacing differences should not block exact matching");
            AssertEqual(100, AniDbTitleIndex.ScoreTitleForTest("Code Geass Rozé of the Recapture", "Code Geass: Roze of the Recapture"), "diacritics should not block exact title matching");
            AssertEqual(100, AniDbTitleIndex.ScoreTitleForTest("Dungeon ni Deai wo Motomeru no wa Machigatteiru Darou ka III", "Dungeon ni Deai o Motomeru no wa Machigatteiru Darou ka III"), "wo/o romaji variants should not block exact title matching");
            AssertEqual(100, AniDbTitleIndex.ScoreTitleForTest("Kimi ha Houkago Insomnia", "Kimi wa Houkago Insomnia"), "ha/wa particle variants should not block exact title matching");
            AssertEqual(100, AniDbTitleIndex.ScoreTitleForTest("Kanojo mo Kanojo", "Kanozyo mo Kanozyo"), "jo/jyo/zyo variants should not block exact title matching");
            AssertEqual(100, AniDbTitleIndex.ScoreTitleForTest("Shoujo Shuumatsu Ryokou", "Shojo Shumatsu Ryoko"), "long-vowel romaji variants should not block exact title matching");
            AssertEqual(100, AniDbTitleIndex.ScoreTitleForTest("Tsunlise", "Tunlise"), "tsu/tu variants should not block exact title matching");
            AssertEqual(100, AniDbTitleIndex.ScoreTitleForTest("Fuuka", "Huuka"), "fu/hu variants should not block exact title matching");
            AssertEqual(100, AniDbTitleIndex.ScoreTitleForTest("Honzuki no Gekokujou S3", "Honzuki no Gekokujou 3"), "S-number shorthand should normalize before scoring");
            AssertTrue(AniDbTitleIndex.ScoreTitleForTest("Hypnosis Mic Division Rap Battle Rhyme Anima +", "Hypnosis Mic: Division Rap Battle - Rhyme Anima") < 100, "plus-season titles should not collapse into base titles");
            AssertEqual(100, AniDbTitleIndex.ScoreTitleForTest("Hypnosis Mic Division Rap Battle Rhyme Anima +", "Hypnosis Mic: Division Rap Battle - Rhyme Anima +"), "plus-season titles should match plus-season candidates");
            AssertTrue(AniDbTitleIndex.ScoreTitleForTest("Tensei shitara Dragon no Tamago datta", "AG") < 85, "very short aliases should not match inside longer words");
            AssertTrue(AniDbTitleIndex.IsSafeSeasonYearRepresentationForTest("Jigokuraku 2nd Season", "Jigokuraku (2026)"), "year-labeled sequel entries should be allowed when the base title is exact");
            AssertTrue(AniDbTitleIndex.IsSafeSeasonYearRepresentationForTest("Genjitsu Shugi Yuusha no Oukoku Saikenki Part 2", "Genjitsu Shugi Yuusha no Oukoku Saikenki (2022)"), "part-number sequel labels should be accepted for year-labeled sequel entries when the base title is exact");
            AssertTrue(AniDbTitleIndex.ShouldSkipTitleCandidateForTest("Jigokuraku 2nd Season", "2×1"), "number-only titles should be skipped for Latin queries");
        }

        private static void AniDbTitleScoringIgnoresUnrelatedLanguageAliases()
        {
            AssertTrue(AniDbTitleIndex.ShouldSkipTitleCandidateForTest("Yozakura san Chi no Daisakusen 2nd Season", ".אוונגליון 2: (אי) אפשר להתקדם"), "Hebrew Evangelion title should be skipped for Latin queries");
        }

        private static void MissingEpisodeFinderReportsLocalGaps()
        {
            var rows = new List<EpisodeFile>
            {
                NewEpisode("show|E001", "Show", "Show - 01.mkv", 100),
                NewEpisode("show|E002", "Show", "Show - 02.mkv", 100),
                NewEpisode("show|E004", "Show", "Show - 04.mkv", 100),
                NewEpisode("show|E006", "Show", "Show - 06.mkv", 100),
                NewEpisode("seasonal|S01E01", "Seasonal", "Seasonal.S01E01.mkv", 100),
                NewEpisode("seasonal|S01E03", "Seasonal", "Seasonal.S01E03.mkv", 100),
                NewEpisode("complete|E001", "Complete", "Complete - 01.mkv", 100),
                NewEpisode("complete|E002", "Complete", "Complete - 02.mkv", 100)
            };

            var gaps = MainForm.BuildMissingEpisodeRows(rows);
            AssertEqual(3, gaps.Count, "gap row count");
            var show = gaps.Find(x => x.Title == "Show" && x.MissingEpisodes == "03");
            var showSecond = gaps.Find(x => x.Title == "Show" && x.MissingEpisodes == "05");
            var seasonal = gaps.Find(x => x.Title == "Seasonal");
            AssertTrue(show != null, "show gap should exist");
            AssertTrue(showSecond != null, "second show gap should exist");
            AssertTrue(seasonal != null, "seasonal gap should exist");
            AssertEqual("Main", show.Scope, "anime scope");
            AssertEqual("03", show.MissingEpisodes, "anime missing list");
            AssertEqual("Show 03", show.SearchKey, "anime search key");
            AssertEqual("01-06", show.PresentRange, "anime present range");
            AssertEqual(4, show.KnownEpisodes, "anime known count");
            AssertEqual("05", showSecond.MissingEpisodes, "second anime missing list");
            AssertEqual("Show 05", showSecond.SearchKey, "second anime search key");
            AssertEqual("S01", seasonal.Scope, "seasonal scope");
            AssertEqual("02", seasonal.MissingEpisodes, "seasonal missing list");
            AssertEqual("Seasonal S01E02", seasonal.SearchKey, "seasonal search key");
        }

        private static void MissingEpisodeSearchQueryUsesSearchKey()
        {
            var row = new MissingEpisodeRow
            {
                Title = "Air Gear",
                Scope = "Main",
                MissingEpisodes = "03",
                SearchKey = "Air Gear 03 1080p"
            };

            var missing = MissingEpisodeAnalyzer.ToSearchMissingEpisode(row);
            AssertTrue(missing != null, "missing episode should be created");
            AssertEqual("Air Gear 03 1080p", missing.SearchQuery, "anime query");

            row.Scope = "S01";
            row.MissingEpisodes = "02";
            row.SearchKey = "";
            missing = MissingEpisodeAnalyzer.ToSearchMissingEpisode(row);
            AssertEqual("Air Gear S01E02", missing.SearchQuery, "season query");
        }

        private static void EpisodeSearchBuildsMagnetLink()
        {
            var link = EpisodeSearchService.BuildMagnetLinkForTest("ABC123", "Air Gear 03");
            AssertContains(link, "magnet:?xt=urn:btih:ABC123", "magnet hash");
            AssertContains(link, "dn=Air%20Gear%2003", "magnet display name");
            AssertEqual("", EpisodeSearchService.BuildMagnetLinkForTest("", "Air Gear 03"), "empty hash");
        }

        private static void EpisodeSearchTriggersFullSeasonFallbackAfterWeakSeededResults()
        {
            var weak = new List<EpisodeSearchResult>
            {
                new EpisodeSearchResult { Seeders = 0 },
                new EpisodeSearchResult { Seeders = 0 },
                new EpisodeSearchResult { Seeders = 0 },
                new EpisodeSearchResult { Seeders = 0 },
                new EpisodeSearchResult { Seeders = 12 }
            };
            var notWeak = new List<EpisodeSearchResult>
            {
                new EpisodeSearchResult { Seeders = 0 },
                new EpisodeSearchResult { Seeders = 0 },
                new EpisodeSearchResult { Seeders = 0 },
                new EpisodeSearchResult { Seeders = 1 }
            };

            AssertTrue(EpisodeSearchService.ShouldSearchBatchFallback(weak), "more than three zero-seed results should trigger batch search");
            AssertTrue(!EpisodeSearchService.ShouldSearchBatchFallback(notWeak), "three zero-seed results should not trigger batch search");

            var queries = EpisodeSearchService.BuildFullSeasonSearchQueries(
                new MissingEpisode { SeriesTitle = "Bad Girl", Scope = "S01", SearchQuery = "Bad Girl 03" },
                "SubsPlease",
                "1080p");
            AssertTrue(queries.Count >= 4, "full-season search should try multiple query shapes");
            AssertContains(queries[0], "Bad Girl", "full-season query series title");
            AssertContains(queries[0], "S01", "full-season query scope");
            AssertContains(queries[0], "SubsPlease", "full-season query group");
            AssertContains(queries[0], "1080p", "full-season query resolution");
            AssertTrue(queries.Any(x => x.Contains("complete")), "full-season search should try complete keyword");
            AssertTrue(queries.Any(x => x.Contains("season")), "full-season search should try season keyword");
            AssertTrue(queries.Any(x => x.Contains("batch")), "full-season search may still try batch keyword");

            var query = EpisodeSearchService.BuildBatchSearchQuery(
                new MissingEpisode { SeriesTitle = "Bad Girl", Scope = "Main", SearchQuery = "Bad Girl 03" },
                "",
                "720p");
            AssertContains(query, "Bad Girl", "main full-season query series title");
            AssertTrue(!query.Contains("Main"), "main scope should be omitted from full-season query");
            AssertContains(query, "720p", "main full-season query resolution");
        }

        private static void MergedScanRecomputesDuplicateGroupsAcrossRoots()
        {
            var existing = new List<EpisodeFile>
            {
                NewEpisode("show|E001", "Show", "Show - 01 [1080p].mkv", 100),
                NewEpisode("show|E002", "Show", "Show - 02 [1080p].mkv", 100)
            };
            existing[0].Path = "D:\\Anime\\Show - 01 [1080p].mkv";
            existing[1].Path = "D:\\Anime\\Show - 02 [1080p].mkv";

            var added = new List<EpisodeFile>
            {
                NewEpisode("show|E001", "Show", "Show - 01 [720p].mkv", 90),
                NewEpisode("other|E001", "Other", "Other - 01.mkv", 50)
            };
            added[0].Path = "E:\\Anime\\Show - 01 [720p].mkv";
            added[1].Path = "E:\\Anime\\Other - 01.mkv";

            var merged = MainForm.BuildMergedScanResult(existing, added);
            AssertEqual(4, merged.ScannedRows.Count, "merged scanned count");
            AssertEqual(2, merged.DuplicateRows.Count, "merged duplicate candidate count");
            AssertEqual(1, merged.DuplicateGroups, "merged duplicate group count");
        }

        private static void NetworkPathsAreDetectedBeforeDeletion()
        {
            AssertTrue(MainForm.IsNetworkPath(@"\\server\share\Show\Show - 01.mkv"), "UNC path should be treated as network");
            AssertTrue(!MainForm.IsNetworkPath(@"C:\Media\Show\Show - 01.mkv"), "local path should not be treated as network");
        }

        private static void LibraryActionPlannerPreviewsDuplicateCoverAndGapWork()
        {
            var files = new List<EpisodeFile>
            {
                NewEpisode("show|E001", "Show", "Show - 01 [1080p].mkv", 700L * 1024L * 1024L),
                NewEpisode("show|E001", "Show", "Show - 01 [720p].mkv", 500L * 1024L * 1024L)
            };
            var gaps = new List<MissingEpisodeRow>
            {
                new MissingEpisodeRow
                {
                    Title = "Show",
                    MissingEpisodes = "02",
                    PresentRange = "01-03"
                }
            };

            var actions = LibraryActionPlanner.BuildPreview(files, gaps, title => false);
            AssertTrue(actions.Any(x => x.Category == LibraryActionCategory.FetchCover && x.SeriesTitle == "Show"), "cover fetch action should be planned");
            AssertTrue(actions.Any(x => x.Category == LibraryActionCategory.Delete && x.TargetPath.EndsWith("[720p].mkv", StringComparison.OrdinalIgnoreCase)), "lower quality duplicate should be planned for delete review");
            AssertTrue(actions.Any(x => x.Category == LibraryActionCategory.SearchMissing && x.Reason.Contains("02")), "missing episode search should be planned");
            AssertEqual(LibraryActionCategory.FetchCover, actions[0].Category, "cover should be planned before deleting when no cover exists");
        }

        private static ScannedFile CreateScannedFile(string root, string relativeFolder, string name, long sizeBytes)
        {
            var directory = Path.Combine(root, relativeFolder);
            return new ScannedFile
            {
                DirectoryName = directory,
                FullName = Path.Combine(directory, name),
                Name = name,
                BaseName = Path.GetFileNameWithoutExtension(name),
                Length = sizeBytes,
                CreatedUtcTicks = DateTime.UtcNow.Ticks,
                LastWriteUtcTicks = DateTime.UtcNow.Ticks
            };
        }

        private static EpisodeFile NewEpisode(string key, string title, string fileName, long sizeBytes)
        {
            return new EpisodeFile
            {
                Key = key,
                Title = title,
                Episode = key.Substring(key.IndexOf('|') + 1),
                FileName = fileName,
                Path = Path.Combine("C:\\Media", title, fileName),
                SizeBytes = sizeBytes
            };
        }

        private static void Run(string name, Action test)
        {
            try
            {
                test();
                passed++;
                Console.WriteLine("PASS " + name);
            }
            catch (Exception ex)
            {
                failed++;
                Console.WriteLine("FAIL " + name);
                Console.WriteLine("     " + ex.Message);
            }
        }

        private static void AssertTrue(bool value, string message)
        {
            if (!value)
            {
                throw new Exception(message);
            }
        }

        private static void AssertEqual<T>(T expected, T actual, string message)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
            {
                throw new Exception(string.Format("{0}: expected <{1}> but got <{2}>", message, expected, actual));
            }
        }

        private static void AssertContains(string actual, string expectedSubstring, string message)
        {
            if (actual == null || actual.IndexOf(expectedSubstring, StringComparison.OrdinalIgnoreCase) < 0)
            {
                throw new Exception(string.Format("{0}: expected <{1}> to contain <{2}>", message, actual, expectedSubstring));
            }
        }
    }
}
