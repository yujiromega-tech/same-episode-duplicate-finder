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

            var results = SearchNyaa(string.Join(" ", queryParts.ToArray()), 30);
            if (ShouldSearchFullSeasonFallback(results))
            {
                var fallbackResults = new List<EpisodeSearchResult>();
                foreach (var query in BuildFullSeasonSearchQueries(episode, releaseGroup, resolution))
                {
                    foreach (var result in SearchNyaa(query, 10))
                    {
                        result.Provider = "Nyaa Full Season";
                        result.IsBatchResult = true;
                        fallbackResults.Add(result);
                    }
                }

                results.AddRange(
                    fallbackResults
                        .GroupBy(x => string.IsNullOrWhiteSpace(x.MagnetLink) ? x.Link : x.MagnetLink, StringComparer.OrdinalIgnoreCase)
                        .Select(g => g.First()));
            }

            return results;
        }

        internal static bool ShouldSearchBatchFallback(IEnumerable<EpisodeSearchResult> results)
        {
            return ShouldSearchFullSeasonFallback(results);
        }

        internal static bool ShouldSearchFullSeasonFallback(IEnumerable<EpisodeSearchResult> results)
        {
            return (results ?? Enumerable.Empty<EpisodeSearchResult>()).Count(x => x != null && x.Seeders == 0) > 3;
        }

        internal static string BuildBatchSearchQuery(MissingEpisode episode, string releaseGroup, string resolution)
        {
            return BuildFullSeasonSearchQueries(episode, releaseGroup, resolution).FirstOrDefault() ?? "";
        }

        internal static List<string> BuildFullSeasonSearchQueries(MissingEpisode episode, string releaseGroup, string resolution)
        {
            var baseParts = BuildFullSeasonBaseQueryParts(episode, releaseGroup, resolution);
            var queries = new List<string>();
            AddFullSeasonQuery(queries, baseParts, "");
            AddFullSeasonQuery(queries, baseParts, "complete");
            AddFullSeasonQuery(queries, baseParts, "season");
            AddFullSeasonQuery(queries, baseParts, "batch");
            return queries;
        }

        private static List<string> BuildFullSeasonBaseQueryParts(MissingEpisode episode, string releaseGroup, string resolution)
        {
            var queryParts = new List<string>();
            if (episode != null && !string.IsNullOrWhiteSpace(episode.SeriesTitle))
            {
                queryParts.Add(episode.SeriesTitle.Trim());
            }
            if (ShouldIncludeBatchScope(episode == null ? "" : episode.Scope))
            {
                queryParts.Add(episode.Scope.Trim());
            }

            if (!string.IsNullOrWhiteSpace(releaseGroup))
            {
                queryParts.Add(releaseGroup.Trim());
            }
            if (!string.IsNullOrWhiteSpace(resolution))
            {
                queryParts.Add(resolution.Trim());
            }

            return queryParts;
        }

        private static void AddFullSeasonQuery(List<string> queries, List<string> baseParts, string keyword)
        {
            var parts = new List<string>(baseParts ?? new List<string>());
            if (!string.IsNullOrWhiteSpace(keyword))
            {
                parts.Add(keyword.Trim());
            }

            var query = string.Join(" ", parts.Where(x => !string.IsNullOrWhiteSpace(x)).ToArray());
            if (!string.IsNullOrWhiteSpace(query) && !queries.Contains(query, StringComparer.OrdinalIgnoreCase))
            {
                queries.Add(query);
            }
        }

        internal static bool ShouldIncludeBatchScope(string scope)
        {
            return !string.IsNullOrWhiteSpace(scope) &&
                   !string.Equals(scope, "Series", StringComparison.OrdinalIgnoreCase) &&
                   !string.Equals(scope, "Main", StringComparison.OrdinalIgnoreCase);
        }

        private static List<EpisodeSearchResult> SearchNyaa(string query, int maxResults)
        {
            HttpNetworkSettings.Apply();
            var url = string.Format(NyaaRssUrl, Uri.EscapeDataString(query ?? ""));
            var document = new XmlDocument();
            using (var webClient = new HttpTimeoutWebClient())
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
                               SearchQuery = query,
                               Title = InnerText(item, "title"),
                               Link = InnerText(item, "link"),
                               MagnetLink = BuildMagnetLink(InnerText(item, "nyaa:infoHash", manager), InnerText(item, "title")),
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

        private static string BuildMagnetLink(string infoHash, string title)
        {
            if (string.IsNullOrWhiteSpace(infoHash))
            {
                return "";
            }

            return "magnet:?xt=urn:btih:" + Uri.EscapeDataString(infoHash.Trim()) +
                   "&dn=" + Uri.EscapeDataString(title ?? "");
        }

        internal static string BuildMagnetLinkForTest(string infoHash, string title)
        {
            return BuildMagnetLink(infoHash, title);
        }
    }
}
