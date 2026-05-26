using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace SameEpisodeDuplicateFinder
{
    internal sealed class ProviderMatchCandidate
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string Year { get; set; }
        public string ImageUrl { get; set; }
        public bool IsFranchiseParent { get; set; }
        public List<string> AlternateTitles { get; set; }

        public ProviderMatchCandidate()
        {
            AlternateTitles = new List<string>();
        }
    }

    internal sealed class ProviderMatchSelection
    {
        public ProviderMatchCandidate Candidate { get; set; }
        public int Score { get; set; }
        public string MatchedTitle { get; set; }
        public ProviderMatchCandidate BestRejected { get; set; }
        public int BestRejectedScore { get; set; }
        public string BestRejectedTitle { get; set; }

        public bool Found
        {
            get { return Candidate != null; }
        }
    }

    internal static class ProviderMatchEvaluator
    {
        public const int DefaultMinimumScore = 90;

        public static ProviderMatchSelection SelectBest(string queryTitle, IEnumerable<ProviderMatchCandidate> candidates)
        {
            return SelectBest(queryTitle, candidates, DefaultMinimumScore);
        }

        public static ProviderMatchSelection SelectBest(string queryTitle, IEnumerable<ProviderMatchCandidate> candidates, int minimumScore)
        {
            var selection = new ProviderMatchSelection();
            if (string.IsNullOrWhiteSpace(queryTitle) || candidates == null)
            {
                return selection;
            }

            foreach (var candidate in candidates.Where(x => x != null))
            {
                string matchedTitle;
                var score = ScoreCandidate(queryTitle, candidate, out matchedTitle);
                if (score >= minimumScore && IsUnsafeFranchiseParentAliasMatch(queryTitle, candidate, matchedTitle))
                {
                    if (score > selection.BestRejectedScore)
                    {
                        selection.BestRejected = candidate;
                        selection.BestRejectedScore = score;
                        selection.BestRejectedTitle = matchedTitle;
                    }

                    continue;
                }

                if (score >= minimumScore)
                {
                    if (selection.Candidate == null ||
                        score > selection.Score ||
                        (score == selection.Score && IsBetterCandidate(candidate, selection.Candidate)))
                    {
                        selection.Candidate = candidate;
                        selection.Score = score;
                        selection.MatchedTitle = matchedTitle;
                    }
                }
                else if (score > selection.BestRejectedScore)
                {
                    selection.BestRejected = candidate;
                    selection.BestRejectedScore = score;
                    selection.BestRejectedTitle = matchedTitle;
                }
            }

            return selection;
        }

        public static int ScoreForTest(string queryTitle, string candidateTitle)
        {
            return ScoreTitle(queryTitle, candidateTitle);
        }

        public static string BuildRejectedMatchMessage(string provider, ProviderMatchSelection selected)
        {
            if (selected != null && selected.BestRejected != null)
            {
                var title = string.IsNullOrWhiteSpace(selected.BestRejectedTitle)
                    ? selected.BestRejected.Title
                    : selected.BestRejectedTitle;
                return "No confident " + provider + " title match. Best rejected: " + title + " (score " + selected.BestRejectedScore.ToString("N0") + ")";
            }

            return "No confident " + provider + " title match";
        }

        public static List<string> BuildCandidateDiagnostics(string provider, string queryTitle, IEnumerable<ProviderMatchCandidate> candidates, ProviderMatchSelection selected)
        {
            var diagnostics = new List<string>();
            if (candidates == null)
            {
                return diagnostics;
            }

            foreach (var candidate in candidates.Where(x => x != null).Take(5))
            {
                string matchedTitle;
                var score = ScoreCandidate(queryTitle, candidate, out matchedTitle);
                var decision = IsSelectedCandidate(candidate, selected) ? "selected" : "rejected";
                diagnostics.Add(
                    provider + " candidate for \"" + Display(queryTitle) + "\": id=" + Display(candidate.Id) +
                    "; title=\"" + Display(candidate.Title) +
                    "\"; matched=\"" + Display(matchedTitle) +
                    "\"; year=" + Display(candidate.Year) +
                    "; artwork=" + (string.IsNullOrWhiteSpace(candidate.ImageUrl) ? "no" : "yes") +
                    "; score=" + score.ToString("N0") +
                    "; decision=" + decision);
            }

            return diagnostics;
        }

        private static bool IsSelectedCandidate(ProviderMatchCandidate candidate, ProviderMatchSelection selected)
        {
            return selected != null &&
                   selected.Candidate != null &&
                   (object.ReferenceEquals(candidate, selected.Candidate) ||
                    (!string.IsNullOrWhiteSpace(candidate.Id) &&
                     string.Equals(candidate.Id, selected.Candidate.Id, StringComparison.OrdinalIgnoreCase)));
        }

        private static bool IsBetterCandidate(ProviderMatchCandidate candidate, ProviderMatchCandidate current)
        {
            if (candidate == null)
            {
                return false;
            }

            if (current == null)
            {
                return true;
            }

            if (current.IsFranchiseParent && !candidate.IsFranchiseParent)
            {
                return true;
            }

            if (!current.IsFranchiseParent && candidate.IsFranchiseParent)
            {
                return false;
            }

            var candidateYear = ParseYear(candidate.Year);
            var currentYear = ParseYear(current.Year);
            if (candidateYear.HasValue && currentYear.HasValue && candidateYear.Value != currentYear.Value)
            {
                return candidateYear.Value > currentYear.Value;
            }

            return false;
        }

        private static bool IsUnsafeFranchiseParentAliasMatch(string queryTitle, ProviderMatchCandidate candidate, string matchedTitle)
        {
            if (candidate == null || !candidate.IsFranchiseParent)
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(candidate.Title) || string.IsNullOrWhiteSpace(matchedTitle))
            {
                return true;
            }

            var query = NormalizeLookupTitle(queryTitle);
            var parentTitle = NormalizeLookupTitle(candidate.Title);
            var matched = NormalizeLookupTitle(matchedTitle);
            if (string.IsNullOrWhiteSpace(query) || string.IsNullOrWhiteSpace(parentTitle))
            {
                return true;
            }

            if (string.Equals(query, parentTitle, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(RemoveTitleSpaces(query), RemoveTitleSpaces(parentTitle), StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return !string.Equals(matched, parentTitle, StringComparison.OrdinalIgnoreCase) &&
                   !string.Equals(RemoveTitleSpaces(matched), RemoveTitleSpaces(parentTitle), StringComparison.OrdinalIgnoreCase);
        }

        private static int? ParseYear(string value)
        {
            int year;
            if (int.TryParse(value, out year))
            {
                return year;
            }

            return null;
        }

        private static string Display(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "-" : value.Trim();
        }

        private static int ScoreCandidate(string queryTitle, ProviderMatchCandidate candidate, out string matchedTitle)
        {
            matchedTitle = "";
            var score = 0;
            foreach (var title in GetCandidateTitles(candidate))
            {
                var titleScore = ScoreTitle(queryTitle, title);
                if (titleScore > score)
                {
                    score = titleScore;
                    matchedTitle = title;
                }
            }

            return score;
        }

        private static IEnumerable<string> GetCandidateTitles(ProviderMatchCandidate candidate)
        {
            if (!string.IsNullOrWhiteSpace(candidate.Title))
            {
                yield return candidate.Title;
            }

            if (candidate.AlternateTitles == null)
            {
                yield break;
            }

            foreach (var title in candidate.AlternateTitles.Where(x => !string.IsNullOrWhiteSpace(x)))
            {
                yield return title;
            }
        }

        private static int ScoreTitle(string queryTitle, string candidateTitle)
        {
            if (ShouldSkipIncompatibleScript(queryTitle, candidateTitle))
            {
                return 0;
            }

            var query = NormalizeLookupTitle(queryTitle);
            var candidate = NormalizeLookupTitle(candidateTitle);
            if (string.IsNullOrWhiteSpace(query) || string.IsNullOrWhiteSpace(candidate))
            {
                return 0;
            }

            if (string.Equals(query, candidate, StringComparison.OrdinalIgnoreCase))
            {
                return 100;
            }

            if (string.Equals(RemoveTitleSpaces(query), RemoveTitleSpaces(candidate), StringComparison.OrdinalIgnoreCase))
            {
                return 100;
            }

            if ((candidate.Contains(query) && IsSafeContainedTitle(query)) ||
                (query.Contains(candidate) && IsSafeContainedTitle(candidate)))
            {
                return 85;
            }

            var queryTokens = new HashSet<string>(query.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);
            var candidateTokens = new HashSet<string>(candidate.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);
            if (queryTokens.Count == 0 || candidateTokens.Count == 0)
            {
                return 0;
            }

            var matches = queryTokens.Count(x => candidateTokens.Contains(x));
            var score = (int)Math.Round((decimal)matches * 100M / Math.Max(queryTokens.Count, candidateTokens.Count));
            return score >= 35 ? score : 0;
        }

        private static string NormalizeLookupTitle(string title)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                return "";
            }

            title = RemoveDiacritics(title).ToLowerInvariant();
            title = title.Replace("+", " plus ");
            title = Regex.Replace(title, @"\bs\s*(\d{1,2})\b", "$1", RegexOptions.IgnoreCase);
            title = Regex.Replace(title, @"\[[^\]]+\]|\([^\)]*\)", " ");
            title = Regex.Replace(title, @"\b(\d{1,2})(st|nd|rd|th)\b", "$1", RegexOptions.IgnoreCase);
            title = Regex.Replace(title, @"[^a-z0-9]+", " ");
            title = NormalizeRomanizedJapanese(title);
            title = Regex.Replace(title, @"\b(the|a|an|tv|ova|movie|season|part|series)\b", " ");
            return Regex.Replace(title, @"\s+", " ").Trim();
        }

        private static string NormalizeRomanizedJapanese(string title)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                return "";
            }

            var normalized = title;
            normalized = Regex.Replace(normalized, @"\bwo\b", "o", RegexOptions.IgnoreCase);
            normalized = Regex.Replace(normalized, @"\bha\b", "wa", RegexOptions.IgnoreCase);
            normalized = Regex.Replace(normalized, @"\bhe\b", "e", RegexOptions.IgnoreCase);
            normalized = Regex.Replace(normalized, "jyo", "zyo", RegexOptions.IgnoreCase);
            normalized = Regex.Replace(normalized, "jyu", "zyu", RegexOptions.IgnoreCase);
            normalized = Regex.Replace(normalized, "jya", "zya", RegexOptions.IgnoreCase);
            normalized = Regex.Replace(normalized, "jo", "zyo", RegexOptions.IgnoreCase);
            normalized = Regex.Replace(normalized, "ju", "zyu", RegexOptions.IgnoreCase);
            normalized = Regex.Replace(normalized, "ja", "zya", RegexOptions.IgnoreCase);
            normalized = Regex.Replace(normalized, "sho", "syo", RegexOptions.IgnoreCase);
            normalized = Regex.Replace(normalized, "shu", "syu", RegexOptions.IgnoreCase);
            normalized = Regex.Replace(normalized, "sha", "sya", RegexOptions.IgnoreCase);
            normalized = Regex.Replace(normalized, "cho", "tyo", RegexOptions.IgnoreCase);
            normalized = Regex.Replace(normalized, "chu", "tyu", RegexOptions.IgnoreCase);
            normalized = Regex.Replace(normalized, "cha", "tya", RegexOptions.IgnoreCase);
            normalized = Regex.Replace(normalized, "shi", "si", RegexOptions.IgnoreCase);
            normalized = Regex.Replace(normalized, "chi", "ti", RegexOptions.IgnoreCase);
            normalized = Regex.Replace(normalized, "tsu", "tu", RegexOptions.IgnoreCase);
            normalized = Regex.Replace(normalized, "fu", "hu", RegexOptions.IgnoreCase);
            normalized = Regex.Replace(normalized, "ji", "zi", RegexOptions.IgnoreCase);
            normalized = Regex.Replace(normalized, "ou", "o", RegexOptions.IgnoreCase);
            normalized = Regex.Replace(normalized, "oo", "o", RegexOptions.IgnoreCase);
            normalized = Regex.Replace(normalized, "uu", "u", RegexOptions.IgnoreCase);
            return normalized;
        }

        private static string RemoveDiacritics(string value)
        {
            var normalized = (value ?? "").Normalize(NormalizationForm.FormD);
            var builder = new StringBuilder(normalized.Length);
            foreach (var ch in normalized)
            {
                if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch) != System.Globalization.UnicodeCategory.NonSpacingMark)
                {
                    builder.Append(ch);
                }
            }

            return builder.ToString().Normalize(NormalizationForm.FormC);
        }

        private static string RemoveTitleSpaces(string title)
        {
            return Regex.Replace(title ?? "", @"\s+", "");
        }

        private static bool ShouldSkipIncompatibleScript(string queryTitle, string candidateTitle)
        {
            if (string.IsNullOrWhiteSpace(queryTitle) || string.IsNullOrWhiteSpace(candidateTitle))
            {
                return false;
            }

            return HasLatinLetter(queryTitle) &&
                   !HasLatinLetter(candidateTitle) &&
                   HasNonLatinLetter(candidateTitle);
        }

        private static bool HasLatinLetter(string value)
        {
            return !string.IsNullOrWhiteSpace(value) &&
                   value.Any(ch => (ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z'));
        }

        private static bool HasNonLatinLetter(string value)
        {
            return !string.IsNullOrWhiteSpace(value) &&
                   value.Any(ch => char.IsLetter(ch) && !((ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z')));
        }

        private static bool IsSafeContainedTitle(string normalizedTitle)
        {
            return !string.IsNullOrWhiteSpace(normalizedTitle) &&
                   normalizedTitle.Length >= 4 &&
                   normalizedTitle.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Length >= 2;
        }
    }
}
