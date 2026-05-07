using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace SameEpisodeDuplicateFinder
{
    internal static class RecommendationScorer
    {
        public static string GetDeleteConfidence(long scoreGap)
        {
            if (scoreGap >= 1000000L)
            {
                return "High";
            }

            return scoreGap > 0 ? "Medium" : "Low";
        }

        public static bool IsAutoMarkCandidate(EpisodeFile row, AutoMarkThreshold threshold)
        {
            return row != null &&
                   string.Equals(row.Recommendation, "Delete", StringComparison.OrdinalIgnoreCase) &&
                   ConfidenceMeetsThreshold(row.Confidence, threshold);
        }

        public static bool ConfidenceMeetsThreshold(string confidence, AutoMarkThreshold threshold)
        {
            return GetConfidenceRank(confidence) >= GetConfidenceRank(threshold);
        }

        public static int GetConfidenceRank(string confidence)
        {
            if (string.Equals(confidence, "High", StringComparison.OrdinalIgnoreCase))
            {
                return 3;
            }

            if (string.Equals(confidence, "Medium", StringComparison.OrdinalIgnoreCase))
            {
                return 2;
            }

            if (string.Equals(confidence, "Low", StringComparison.OrdinalIgnoreCase))
            {
                return 1;
            }

            return 0;
        }

        public static int GetConfidenceRank(AutoMarkThreshold threshold)
        {
            if (threshold == AutoMarkThreshold.High)
            {
                return 3;
            }

            if (threshold == AutoMarkThreshold.Medium)
            {
                return 2;
            }

            return 1;
        }

        public static string BuildRecommendationReason(EpisodeFile candidate, EpisodeFile keep)
        {
            var reasons = new List<string>();
            var candidateName = ((candidate == null ? "" : candidate.FileName) + " " + (candidate == null ? "" : candidate.Path)).ToLowerInvariant();
            var keepName = ((keep == null ? "" : keep.FileName) + " " + (keep == null ? "" : keep.Path)).ToLowerInvariant();
            var candidateResolution = ExtractResolution(candidateName);
            var keepResolution = ExtractResolution(keepName);
            if (keepResolution > candidateResolution)
            {
                reasons.Add("kept file has higher resolution");
            }

            var candidateVersion = ExtractVersion(candidate == null ? "" : candidate.Version, candidateName);
            var keepVersion = ExtractVersion(keep == null ? "" : keep.Version, keepName);
            if (keepVersion > candidateVersion)
            {
                reasons.Add("kept file has newer version tag");
            }

            if (keep != null && candidate != null && keep.SizeBytes > candidate.SizeBytes)
            {
                reasons.Add("kept file is larger");
            }

            return reasons.Count == 0 ? "Lower quality score than recommended keep." : string.Join("; ", reasons.ToArray()) + ".";
        }

        public static long GetAutoKeepScore(EpisodeFile file)
        {
            var name = ((file == null ? "" : file.FileName) + " " + (file == null ? "" : file.Path)).ToLowerInvariant();
            var resolution = ExtractResolution(name);
            var version = ExtractVersion(file == null ? "" : file.Version, name);
            var sizeScore = file == null ? 0 : Math.Min(file.SizeBytes / (1024 * 1024), 999999);
            return (long)resolution * 100000000L + (long)version * 1000000L + sizeScore;
        }

        public static int ExtractResolution(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return 0;
            }

            if (Regex.IsMatch(text, @"(?:^|[^0-9])4k(?:[^0-9]|$)", RegexOptions.IgnoreCase))
            {
                return 2160;
            }

            var matches = Regex.Matches(text, @"(?:^|[^0-9])(?<resolution>2160|1440|1080|720|576|480|360)\s*p?(?:[^0-9]|$)", RegexOptions.IgnoreCase);
            var best = 0;
            foreach (Match match in matches)
            {
                int resolution;
                if (int.TryParse(match.Groups["resolution"].Value, out resolution) && resolution > best)
                {
                    best = resolution;
                }
            }

            return best;
        }

        public static int ExtractVersion(string versionText, string fileText)
        {
            var best = 0;
            foreach (var text in new[] { versionText ?? "", fileText ?? "" })
            {
                var matches = Regex.Matches(text, @"(?:^|[^a-z0-9])v(?<version>\d{1,2})(?:[^a-z0-9]|$)", RegexOptions.IgnoreCase);
                foreach (Match match in matches)
                {
                    int version;
                    if (int.TryParse(match.Groups["version"].Value, out version) && version > best)
                    {
                        best = version;
                    }
                }
            }

            return best;
        }
    }
}
