using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Xml;

namespace SameEpisodeDuplicateFinder
{
    internal sealed class AniDbEpisodeService
    {
        public List<OfficialEpisode> GetOfficialEpisodes(string aniDbId, string seriesTitle)
        {
            var xml = AniDbClient.DownloadAnimeXml(aniDbId);
            var document = new XmlDocument();
            document.LoadXml(xml);
            return document.SelectNodes("//episodes/episode")
                           .Cast<XmlNode>()
                           .Select(node => ParseEpisode(node, aniDbId, seriesTitle))
                           .Where(x => x != null)
                           .OrderBy(x => x.Scope, StringComparer.OrdinalIgnoreCase)
                           .ThenBy(x => x.EpisodeNumber)
                           .ToList();
        }

        private static OfficialEpisode ParseEpisode(XmlNode node, string aniDbId, string seriesTitle)
        {
            var epNo = node.SelectSingleNode("epno");
            if (epNo == null)
            {
                return null;
            }

            var type = GetAttribute(epNo, "type");
            if (!string.IsNullOrWhiteSpace(type) && type != "1")
            {
                return null;
            }

            int episodeNumber;
            if (!int.TryParse((epNo.InnerText ?? "").Trim(), out episodeNumber) || episodeNumber <= 0)
            {
                return null;
            }

            return new OfficialEpisode
            {
                SeriesTitle = seriesTitle,
                AniDbId = aniDbId,
                Scope = "Main",
                EpisodeNumber = episodeNumber,
                EpisodeCode = episodeNumber < 100 ? episodeNumber.ToString("D2") : episodeNumber.ToString(),
                Title = WebUtility.HtmlDecode(FirstTitle(node)),
                AirDate = InnerText(node, "airdate")
            };
        }

        private static string FirstTitle(XmlNode node)
        {
            foreach (XmlNode title in node.SelectNodes("title"))
            {
                if (GetAttribute(title, "xml:lang") == "en" || GetAttribute(title, "lang") == "en")
                {
                    return title.InnerText.Trim();
                }
            }

            var first = node.SelectSingleNode("title");
            return first == null ? "" : first.InnerText.Trim();
        }

        private static string InnerText(XmlNode node, string path)
        {
            var selected = node == null ? null : node.SelectSingleNode(path);
            return selected == null ? "" : WebUtility.HtmlDecode(selected.InnerText.Trim());
        }

        private static string GetAttribute(XmlNode node, string name)
        {
            return node != null && node.Attributes != null && node.Attributes[name] != null
                ? node.Attributes[name].Value
                : "";
        }
    }
}
