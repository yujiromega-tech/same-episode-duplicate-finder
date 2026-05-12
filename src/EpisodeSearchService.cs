using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Xml;

namespace SameEpisodeDuplicateFinder
{
    internal sealed class EpisodeSearchService
    {
        private const string NyaaRssUrl = "https://nyaa.si/?page=rss&q={0}&c=0_0&f=0";

        public List<EpisodeSearchResult> Search(MissingEpisode episode, string releaseGroup, string resolution)
        {
            if (episode == null || string.IsNullOrWhiteSpace(episode.SearchQuery))
            {
                return new List<EpisodeSearchResult>();
            }

            var queryParts = new List<string> { episode.SearchQuery };
            if (!string.IsNullOrWhiteSpace(releaseGroup))
            {
                queryParts.Add(releaseGroup.Trim());
            }
            if (!string.IsNullOrWhiteSpace(resolution))
            {
                queryParts.Add(resolution.Trim());
            }

            return SearchNyaa(string.Join(" ", queryParts.ToArray()), 30);
        }

        private static List<EpisodeSearchResult> SearchNyaa(string query, int maxResults)
        {
            HttpNetworkSettings.Apply();
            var url = string.Format(NyaaRssUrl, Uri.EscapeDataString(query ?? ""));
            var document = new XmlDocument();
            using (var webClient = new WebClient())
            {
                webClient.Encoding = System.Text.Encoding.UTF8;
                webClient.Headers[HttpRequestHeader.UserAgent] = "SameEpisodeDuplicateFinder";
                document.LoadXml(webClient.DownloadString(url));
            }

            var manager = new XmlNamespaceManager(document.NameTable);
            manager.AddNamespace("nyaa", "https://nyaa.si/xmlns/nyaa");

            return document.SelectNodes("//item")
                           .Cast<XmlNode>()
                           .Take(maxResults)
                           .Select(item => new EpisodeSearchResult
                           {
                               Provider = "Nyaa",
                               Title = InnerText(item, "title"),
                               Link = InnerText(item, "link"),
                               Size = InnerText(item, "nyaa:size", manager),
                               Seeders = ParseInt(InnerText(item, "nyaa:seeders", manager)),
                               Leechers = ParseInt(InnerText(item, "nyaa:leechers", manager)),
                               Downloads = ParseInt(InnerText(item, "nyaa:downloads", manager)),
                               Trusted = InnerText(item, "nyaa:trusted", manager),
                               Published = FormatPublished(InnerText(item, "pubDate"))
                           })
                           .ToList();
        }

        private static string InnerText(XmlNode node, string path)
        {
            var selected = node == null ? null : node.SelectSingleNode(path);
            return selected == null ? "" : WebUtility.HtmlDecode(selected.InnerText.Trim());
        }

        private static string InnerText(XmlNode node, string path, XmlNamespaceManager manager)
        {
            var selected = node == null ? null : node.SelectSingleNode(path, manager);
            return selected == null ? "" : WebUtility.HtmlDecode(selected.InnerText.Trim());
        }

        private static int ParseInt(string value)
        {
            int parsed;
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) ? parsed : 0;
        }

        private static string FormatPublished(string value)
        {
            DateTime parsed;
            return DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out parsed)
                ? parsed.ToLocalTime().ToString("g")
                : value;
        }
    }
}
