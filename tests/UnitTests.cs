using System;
using System.Collections.Generic;
using System.IO;

namespace SameEpisodeDuplicateFinder.Tests
{
    internal static class UnitTestRunner
    {
        private static int passed;
        private static int failed;

        public static int Main()
        {
            Run("filename parsing handles anime release names", FilenameParsingHandlesAnimeReleaseNames);
            Run("filename parsing handles season episode names", FilenameParsingHandlesSeasonEpisodeNames);
            Run("grouping counts only repeated episode keys", GroupingCountsRepeatedEpisodeKeys);
            Run("file format filter supports ignore and allow-only modes", FileFormatFilterSupportsIgnoreAndAllowOnlyModes);
            Run("scoring prefers resolution, version, then size", ScoringPrefersResolutionVersionThenSize);
            Run("target path generation sanitizes folders and avoids collisions", TargetPathGenerationSanitizesAndAvoidsCollisions);
            Run("action report writer escapes csv fields", ActionReportWriterEscapesCsvFields);
            Run("search matches only series title", SearchMatchesOnlySeriesTitle);
            Run("missing episode finder reports local gaps", MissingEpisodeFinderReportsLocalGaps);

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
            AssertEqual(2, gaps.Count, "gap row count");
            var show = gaps.Find(x => x.Title == "Show");
            var seasonal = gaps.Find(x => x.Title == "Seasonal");
            AssertTrue(show != null, "show gap should exist");
            AssertTrue(seasonal != null, "seasonal gap should exist");
            AssertEqual("Main", show.Scope, "anime scope");
            AssertEqual("03, 05", show.MissingEpisodes, "anime missing list");
            AssertEqual("01-06", show.PresentRange, "anime present range");
            AssertEqual(4, show.KnownEpisodes, "anime known count");
            AssertEqual("S01", seasonal.Scope, "seasonal scope");
            AssertEqual("02", seasonal.MissingEpisodes, "seasonal missing list");
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
