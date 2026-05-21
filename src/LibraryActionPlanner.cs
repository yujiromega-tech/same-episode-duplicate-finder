using System;
using System.Collections.Generic;
using System.Linq;

namespace SameEpisodeDuplicateFinder
{
    internal static class LibraryActionPlanner
    {
        public static List<LibraryAction> BuildPreview(
            IEnumerable<EpisodeFile> files,
            IEnumerable<MissingEpisodeRow> gaps,
            Func<string, bool> seriesHasCover = null)
        {
            var actions = new List<LibraryAction>();
            var rows = (files ?? Enumerable.Empty<EpisodeFile>())
                .Where(x => x != null)
                .ToList();

            AddDuplicateActions(actions, rows, seriesHasCover);
            AddCoverActions(actions, rows, seriesHasCover);
            AddMissingEpisodeActions(actions, gaps);

            return actions
                .OrderByDescending(x => x.Priority)
                .ThenBy(x => x.Category.ToString(), StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.SeriesTitle ?? "", StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static void AddDuplicateActions(List<LibraryAction> actions, IList<EpisodeFile> rows, Func<string, bool> seriesHasCover)
        {
            var groups = rows
                .Where(x => !string.IsNullOrWhiteSpace(x.Key))
                .GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                .Where(x => x.Count() > 1);

            foreach (var group in groups)
            {
                var candidates = group.ToList();
                var keep = candidates.FirstOrDefault(x => string.Equals(x.Recommendation, "Keep", StringComparison.OrdinalIgnoreCase))
                           ?? candidates.OrderByDescending(RecommendationScorer.GetAutoKeepScore).FirstOrDefault();
                if (keep == null)
                {
                    continue;
                }

                var keepScore = RecommendationScorer.GetAutoKeepScore(keep);
                var keepHasCover = HasCover(keep.Title, seriesHasCover);
                foreach (var candidate in candidates.Where(x => !ReferenceEquals(x, keep)))
                {
                    if (!ShouldPlanDelete(candidate))
                    {
                        continue;
                    }

                    var scoreGap = keepScore - RecommendationScorer.GetAutoKeepScore(candidate);
                    var confidence = string.IsNullOrWhiteSpace(candidate.Confidence)
                        ? RecommendationScorer.GetDeleteConfidence(scoreGap)
                        : candidate.Confidence;
                    var isDelete = RecommendationScorer.GetConfidenceRank(confidence) >= 2;
                    actions.Add(new LibraryAction
                    {
                        Priority = GetDeletePriority(confidence, keepHasCover),
                        Category = isDelete ? LibraryActionCategory.Delete : LibraryActionCategory.ManualReview,
                        SeriesTitle = candidate.Title,
                        TargetPath = candidate.Path,
                        Confidence = confidence,
                        Reason = string.IsNullOrWhiteSpace(candidate.RecommendationReason)
                            ? RecommendationScorer.BuildRecommendationReason(candidate, keep)
                            : candidate.RecommendationReason,
                        DerivedBy = keepHasCover ? "DuplicateWithCover" : "DuplicateBeforeCover"
                    });
                }
            }
        }

        private static bool ShouldPlanDelete(EpisodeFile candidate)
        {
            return candidate != null &&
                   (candidate.Delete ||
                    string.Equals(candidate.Recommendation, "Delete", StringComparison.OrdinalIgnoreCase) ||
                    string.IsNullOrWhiteSpace(candidate.Recommendation));
        }

        private static void AddCoverActions(List<LibraryAction> actions, IList<EpisodeFile> rows, Func<string, bool> seriesHasCover)
        {
            if (seriesHasCover == null)
            {
                return;
            }

            foreach (var title in rows.Select(x => x.Title)
                                      .Where(x => !string.IsNullOrWhiteSpace(x))
                                      .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (seriesHasCover(title))
                {
                    continue;
                }

                actions.Add(new LibraryAction
                {
                    Priority = 80,
                    Category = LibraryActionCategory.FetchCover,
                    SeriesTitle = title,
                    Confidence = "Medium",
                    Reason = "No local series cover is available.",
                    DerivedBy = "SeriesNeedsCover"
                });
            }
        }

        private static void AddMissingEpisodeActions(List<LibraryAction> actions, IEnumerable<MissingEpisodeRow> gaps)
        {
            foreach (var gap in (gaps ?? Enumerable.Empty<MissingEpisodeRow>()).Where(x => x != null))
            {
                actions.Add(new LibraryAction
                {
                    Priority = 60,
                    Category = LibraryActionCategory.SearchMissing,
                    SeriesTitle = gap.Title,
                    Confidence = "Medium",
                    Reason = string.Format("Missing episode {0}; present range {1}.", gap.MissingEpisodes, gap.PresentRange),
                    DerivedBy = "MissingEpisodeGap"
                });
            }
        }

        private static bool HasCover(string title, Func<string, bool> seriesHasCover)
        {
            return seriesHasCover != null &&
                   !string.IsNullOrWhiteSpace(title) &&
                   seriesHasCover(title);
        }

        private static int GetDeletePriority(string confidence, bool keepHasCover)
        {
            if (string.Equals(confidence, "High", StringComparison.OrdinalIgnoreCase))
            {
                return keepHasCover ? 95 : 75;
            }

            if (string.Equals(confidence, "Medium", StringComparison.OrdinalIgnoreCase))
            {
                return keepHasCover ? 60 : 45;
            }

            return 20;
        }
    }
}
