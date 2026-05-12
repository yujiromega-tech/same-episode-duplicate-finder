using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace SameEpisodeDuplicateFinder
{
    internal sealed class EpisodeFile
    {
        public bool Delete { get; set; }
        public string Key { get; set; }
        public string Title { get; set; }
        public string Episode { get; set; }
        public string SubtitleGroup { get; set; }
        public string Version { get; set; }
        public long SizeBytes { get; set; }
        public decimal SizeMB { get; set; }
        public string FileName { get; set; }
        public string FileLocation { get; set; }
        public string Path { get; set; }
        public long LastWriteUtcTicks { get; set; }
        public string AniDbId { get; set; }
        public string AniDbTitle { get; set; }
        public string AniDbYear { get; set; }
        public string Recommendation { get; set; }
        public string Confidence { get; set; }
        public string ReviewStatus { get; set; }
        public string ArtworkStatus { get; set; }
        public string RecommendationReason { get; set; }

        public string AniDbDisplay
        {
            get
            {
                if (string.IsNullOrWhiteSpace(AniDbId) && string.IsNullOrWhiteSpace(AniDbTitle))
                {
                    return "";
                }

                var title = string.IsNullOrWhiteSpace(AniDbTitle) ? "AniDB" : AniDbTitle;
                return string.IsNullOrWhiteSpace(AniDbYear)
                    ? string.Format("{0} [{1}]", title, AniDbId)
                    : string.Format("{0} ({1}) [{2}]", title, AniDbYear, AniDbId);
            }
        }

        public string SimplifiedFileName
        {
            get
            {
                var title = string.IsNullOrWhiteSpace(Title) ? FileName : Title;
                return string.Format("{0} - {1}", title, GetDisplayEpisode());
            }
        }

        private string GetDisplayEpisode()
        {
            if (string.IsNullOrWhiteSpace(Episode))
            {
                return "";
            }

            var animeEpisode = Regex.Match(Episode, @"^E(?<episode>\d+)$", RegexOptions.IgnoreCase);
            if (animeEpisode.Success)
            {
                int episodeNumber;
                if (int.TryParse(animeEpisode.Groups["episode"].Value, out episodeNumber))
                {
                    return episodeNumber < 100
                        ? episodeNumber.ToString("D2")
                        : episodeNumber.ToString();
                }
            }

            return Episode.ToUpperInvariant();
        }
    }

    internal sealed class ScannedFile
    {
        public string FullName { get; set; }
        public string DirectoryName { get; set; }
        public string Name { get; set; }
        public string BaseName { get; set; }
        public long Length { get; set; }
        public long LastWriteUtcTicks { get; set; }
    }

    internal sealed class ScanResult
    {
        public List<EpisodeFile> DuplicateRows { get; set; }
        public List<EpisodeFile> ScannedRows { get; set; }
        public int VisitedFiles { get; set; }
        public int IgnoredFiles { get; set; }
        public int CacheHits { get; set; }
        public int DuplicateGroups { get; set; }

        public string Summary
        {
            get
            {
                return string.Format(
                    "Scan complete: {0:N0} scanned | {1:N0} accepted | {2:N0} cached | {3:N0} ignored | {4:N0} duplicate groups.",
                    VisitedFiles,
                    ScannedRows == null ? 0 : ScannedRows.Count,
                    CacheHits,
                    IgnoredFiles,
                    DuplicateGroups);
            }
        }
    }

    internal sealed class ActionPreviewRow
    {
        public string Action { get; set; }
        public string Confidence { get; set; }
        public string Reason { get; set; }
        public string CurrentPath { get; set; }
        public string TargetPath { get; set; }
    }

    internal sealed class MissingEpisodeRow
    {
        public string Title { get; set; }
        public string Scope { get; set; }
        public string MissingEpisodes { get; set; }
        public string PresentRange { get; set; }
        public int KnownEpisodes { get; set; }
        public int MissingCount { get; set; }
        public int LocationCount { get; set; }
    }

    internal sealed class OfficialEpisode
    {
        public string SeriesTitle { get; set; }
        public string AniDbId { get; set; }
        public string Scope { get; set; }
        public int EpisodeNumber { get; set; }
        public string EpisodeCode { get; set; }
        public string Title { get; set; }
        public string AirDate { get; set; }
    }

    internal sealed class MissingEpisode
    {
        public string SeriesTitle { get; set; }
        public string Scope { get; set; }
        public int EpisodeNumber { get; set; }
        public string EpisodeCode { get; set; }
        public string OfficialTitle { get; set; }
        public string SearchQuery { get; set; }
    }

    internal sealed class EpisodeSearchResult
    {
        public string Provider { get; set; }
        public string Title { get; set; }
        public string Size { get; set; }
        public int Seeders { get; set; }
        public int Leechers { get; set; }
        public int Downloads { get; set; }
        public string Trusted { get; set; }
        public string Published { get; set; }
        public string Link { get; set; }
        public string MagnetLink { get; set; }
    }

    internal sealed class AniDbTitleCandidate
    {
        public bool Use { get; set; }
        public string QueryTitle { get; set; }
        public string AniDbId { get; set; }
        public string Title { get; set; }
        public string TitleType { get; set; }
        public int Score { get; set; }
        public string TargetFolder { get; set; }
    }
}
