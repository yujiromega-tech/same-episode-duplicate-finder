using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace SameEpisodeDuplicateFinder
{
    internal static class MissingEpisodeAnalyzer
    {
        public static List<MissingEpisodeRow> BuildLocalGapRows(IEnumerable<EpisodeFile> files)
        {
            return (files ?? Enumerable.Empty<EpisodeFile>())
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.Title))
                .Select(x =>
                {
                    string scope;
                    int episodeNumber;
                    return TryParseEpisodeNumber(x.Episode, out scope, out episodeNumber)
                        ? new { File = x, Scope = scope, EpisodeNumber = episodeNumber }
                        : null;
                })
                .Where(x => x != null)
                .GroupBy(x => x.File.Title + "|" + x.Scope, StringComparer.OrdinalIgnoreCase)
                .SelectMany(g => BuildLocalGapRows(g.Select(x => x.File), g.First().File.Title, g.First().Scope, g.Select(x => x.EpisodeNumber)))
                .OrderBy(x => x.Title, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.Scope, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.SearchKey, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public static MissingEpisode ToSearchMissingEpisode(MissingEpisodeRow row)
        {
            if (row == null || string.IsNullOrWhiteSpace(row.MissingEpisodes))
            {
                return null;
            }

            int episodeNumber;
            if (!TryGetFirstEpisodeNumber(row.MissingEpisodes, out episodeNumber))
            {
                return null;
            }

            return new MissingEpisode
            {
                SeriesTitle = row.Title,
                Scope = row.Scope,
                EpisodeNumber = episodeNumber,
                EpisodeCode = FormatEpisodeNumber(episodeNumber),
                SearchQuery = string.IsNullOrWhiteSpace(row.SearchKey)
                    ? BuildSearchQuery(row.Title, row.Scope, episodeNumber)
                    : row.SearchKey
            };
        }

        public static string BuildSearchQuery(string title, string scope, int episodeNumber)
        {
            var episode = FormatEpisodeNumber(episodeNumber);
            if (!string.IsNullOrWhiteSpace(scope) && Regex.IsMatch(scope, @"^S\d+$", RegexOptions.IgnoreCase))
            {
                return string.Format("{0} {1}E{2}", title, scope.ToUpperInvariant(), episode);
            }

            return string.Format("{0} {1}", title, episode);
        }

        private static List<MissingEpisodeRow> BuildLocalGapRows(IEnumerable<EpisodeFile> files, string title, string scope, IEnumerable<int> episodeNumbers)
        {
            var result = new List<MissingEpisodeRow>();
            var present = episodeNumbers.Distinct().OrderBy(x => x).ToList();
            if (present.Count < 2)
            {
                return result;
            }

            var first = present.First();
            var last = present.Last();
            var presentSet = new HashSet<int>(present);
            var missing = Enumerable.Range(first, last - first + 1).Where(x => !presentSet.Contains(x)).ToList();
            if (missing.Count == 0)
            {
                return result;
            }

            var presentRange = FormatEpisodeNumber(first) + "-" + FormatEpisodeNumber(last);
            var locationCount = CountDistinctLocations(files);
            foreach (var missingEpisode in missing)
            {
                var searchKey = BuildSearchQuery(title, scope, missingEpisode);
                result.Add(new MissingEpisodeRow
                {
                    Title = title,
                    Scope = scope,
                    SearchKey = searchKey,
                    MissingEpisodes = FormatEpisodeNumber(missingEpisode),
                    PresentRange = presentRange,
                    KnownEpisodes = present.Count,
                    MissingCount = 1,
                    LocationCount = locationCount
                });
            }

            return result;
        }

        private static bool TryParseEpisodeNumber(string episode, out string scope, out int episodeNumber)
        {
            scope = "";
            episodeNumber = 0;
            var sxe = Regex.Match(episode ?? "", @"^S(?<season>\d+)E(?<episode>\d+)$", RegexOptions.IgnoreCase);
            if (sxe.Success && int.TryParse(sxe.Groups["episode"].Value, out episodeNumber))
            {
                int season;
                scope = int.TryParse(sxe.Groups["season"].Value, out season)
                    ? "S" + season.ToString("D2")
                    : sxe.Groups["season"].Value.ToUpperInvariant();
                return episodeNumber > 0;
            }

            var anime = Regex.Match(episode ?? "", @"^E(?<episode>\d+)$", RegexOptions.IgnoreCase);
            if (anime.Success && int.TryParse(anime.Groups["episode"].Value, out episodeNumber))
            {
                scope = "Main";
                return episodeNumber > 0;
            }

            return false;
        }

        private static bool TryGetFirstEpisodeNumber(string missingEpisodes, out int episodeNumber)
        {
            episodeNumber = 0;
            var match = Regex.Match(missingEpisodes ?? "", @"\d+");
            return match.Success && int.TryParse(match.Value, out episodeNumber);
        }

        private static string FormatEpisodeNumber(int episodeNumber)
        {
            return episodeNumber < 100 ? episodeNumber.ToString("D2") : episodeNumber.ToString();
        }

        private static int CountDistinctLocations(IEnumerable<EpisodeFile> files)
        {
            return files.Where(x => x != null)
                        .Select(x => string.IsNullOrWhiteSpace(x.FileLocation) ? "" : x.FileLocation.Trim())
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Count();
        }
    }
}
