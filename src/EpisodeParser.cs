using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace SameEpisodeDuplicateFinder
{
    internal static class EpisodeParser
    {
        public static int CountDuplicateEpisodeGroups(IEnumerable<EpisodeFile> files)
        {
            return files.GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                        .Count(g => g.Count() > 1);
        }

        public static bool TryParseFile(ScannedFile file, string rootFull, out EpisodeFile result)
        {
            result = null;
            var baseName = file.BaseName;
            string title = null;
            string episodeKey = null;
            string subtitleGroup = "";

            subtitleGroup = GetSubtitleGroup(baseName);

            var sxe = Regex.Match(baseName, @"^(?<title>.*?)[ ._\-\[\(]*s(?<season>\d{1,2})e(?<episode>\d{1,3})(?:\D|$)", RegexOptions.IgnoreCase);
            if (sxe.Success)
            {
                var season = int.Parse(sxe.Groups["season"].Value);
                var episode = int.Parse(sxe.Groups["episode"].Value);
                title = sxe.Groups["title"].Value;
                episodeKey = string.Format("S{0:D2}E{1:D2}", season, episode);
            }
            else
            {
                if (Regex.IsMatch(baseName, @"\s+-\s+(?:19|20)\d{2}\s+\p{L}", RegexOptions.IgnoreCase))
                {
                    return false;
                }

                var anime = Regex.Match(baseName, @"^(?:\[[^\]]+\]\s*)?(?<title>.+)\s+-\s+(?<episode>\d{1,4})(?:\s|\[|\(|$)");
                if (anime.Success)
                {
                    title = anime.Groups["title"].Value;
                    episodeKey = string.Format("E{0:D3}", int.Parse(anime.Groups["episode"].Value));
                }
            }

            if (episodeKey == null)
            {
                return false;
            }

            title = NormalizeTitle(title);
            if (string.IsNullOrWhiteSpace(title))
            {
                title = GetShowFolderTitle(file, rootFull);
            }

            var version = "";
            var versionMatch = Regex.Match(baseName, @"\[(v\d+)\]", RegexOptions.IgnoreCase);
            if (versionMatch.Success)
            {
                version = versionMatch.Groups[1].Value.ToLowerInvariant();
            }

            var key = string.Format("{0}|{1}", title.ToLowerInvariant(), episodeKey);

            result = new EpisodeFile
            {
                Delete = false,
                Key = key,
                Title = title,
                Episode = episodeKey,
                SubtitleGroup = subtitleGroup,
                Version = version,
                SizeBytes = file.Length,
                SizeMB = Math.Round((decimal)file.Length / (decimal)(1024 * 1024), 2),
                FileName = file.Name,
                FileLocation = file.DirectoryName,
                Path = file.FullName,
                LastWriteUtcTicks = file.LastWriteUtcTicks
            };

            return true;
        }

        public static string NormalizeTitle(string title)
        {
            if (title == null)
            {
                return "";
            }

            var normalized = Regex.Replace(title, @"[._-]+", " ");
            normalized = Regex.Replace(normalized, @"\(\s*(?:19|20)\d{2}\s*\)", " ");
            normalized = Regex.Replace(normalized, @"\s+", " ");
            return normalized.Trim();
        }

        public static string GetShowFolderTitle(ScannedFile file, string rootFull)
        {
            var relativeDirectory = file.DirectoryName.Substring(rootFull.Length).TrimStart('\\');
            var parts = relativeDirectory.Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries)
                                         .Where(p => !StringComparer.OrdinalIgnoreCase.Equals(p, "Uncen"))
                                         .Where(p => !StringComparer.OrdinalIgnoreCase.Equals(p, "{complete}"))
                                         .Where(p => !Regex.IsMatch(p, @"^Season\s*\d+$", RegexOptions.IgnoreCase))
                                         .ToArray();

            if (parts.Length > 0)
            {
                return NormalizeTitle(parts[0]);
            }

            return NormalizeTitle(new DirectoryInfo(file.DirectoryName).Name);
        }

        public static string GetSubtitleGroup(string fileNameOrBaseName)
        {
            var match = Regex.Match(fileNameOrBaseName ?? "", @"^\[(?<group>[^\]]+)\]\s*");
            return match.Success ? match.Groups["group"].Value.Trim() : "";
        }
    }
}
