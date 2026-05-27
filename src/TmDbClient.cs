using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace SameEpisodeDuplicateFinder
{
    internal sealed class TmDbSettings
    {
        public string ReadAccessToken { get; set; }

        public bool HasReadAccessToken
        {
            get { return !string.IsNullOrWhiteSpace(ReadAccessToken); }
        }
    }

    internal static class TmDbSettingsStore
    {
        public static string SettingsPath
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SameEpisodeDuplicateFinder.tmdb"); }
        }

        public static TmDbSettings Load()
        {
            var settings = new TmDbSettings();
            var envToken = Environment.GetEnvironmentVariable("SEDF_TMDB_READ_ACCESS_TOKEN");
            if (!string.IsNullOrWhiteSpace(envToken))
            {
                settings.ReadAccessToken = envToken.Trim();
            }

            if (!File.Exists(SettingsPath))
            {
                return settings;
            }

            foreach (var line in File.ReadAllLines(SettingsPath, Encoding.UTF8))
            {
                var split = line.IndexOf('=');
                if (split < 0)
                {
                    continue;
                }

                var key = line.Substring(0, split);
                var value = line.Substring(split + 1);
                if (string.Equals(key, "ReadAccessToken", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(settings.ReadAccessToken))
                {
                    settings.ReadAccessToken = value;
                }
                else if (string.Equals(key, "ReadAccessTokenProtected", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(settings.ReadAccessToken))
                {
                    settings.ReadAccessToken = Unprotect(value);
                }
            }

            return settings;
        }

        public static void Save(TmDbSettings settings)
        {
            using (var writer = new StreamWriter(SettingsPath, false, new UTF8Encoding(false)))
            {
                writer.WriteLine("ReadAccessTokenProtected=" + Protect(settings.ReadAccessToken ?? ""));
            }
        }

        private static string Protect(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value ?? "");
            var protectedBytes = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(protectedBytes);
        }

        private static string Unprotect(string value)
        {
            try
            {
                var protectedBytes = Convert.FromBase64String(value ?? "");
                var bytes = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(bytes);
            }
            catch
            {
                return "";
            }
        }
    }

    internal sealed class TmDbSeriesResult
    {
        public TmDbSeriesResult()
        {
            Diagnostics = new List<string>();
        }

        public string QueryTitle { get; set; }
        public string TmDbId { get; set; }
        public string Title { get; set; }
        public string Year { get; set; }
        public string PosterUrl { get; set; }
        public string BackdropUrl { get; set; }
        public string Error { get; set; }
        public List<string> Diagnostics { get; private set; }

        public bool Found
        {
            get { return !string.IsNullOrWhiteSpace(TmDbId); }
        }

        public AniDbAnimeResult ToMetadataResult()
        {
            return new AniDbAnimeResult
            {
                QueryTitle = QueryTitle,
                AniDbId = string.IsNullOrWhiteSpace(TmDbId) ? "" : "tmdb:" + TmDbId,
                Title = Title,
                Year = Year,
                PictureFile = PosterUrl,
                Error = Error
            };
        }
    }

    internal sealed class TmDbClient
    {
        private const string BaseUrl = "https://api.themoviedb.org/3";
        private const string ImageBaseUrl = "https://image.tmdb.org/t/p/original";
        private readonly TmDbSettings settings;
        private readonly JavaScriptSerializer serializer = new JavaScriptSerializer();

        public TmDbClient(TmDbSettings settings)
        {
            this.settings = settings ?? new TmDbSettings();
        }

        public bool IsConfigured
        {
            get { return settings.HasReadAccessToken; }
        }

        public TmDbSeriesResult LookupSeries(string title)
        {
            var result = new TmDbSeriesResult { QueryTitle = title };
            if (!settings.HasReadAccessToken)
            {
                result.Error = "TMDB read access token is not configured";
                result.Diagnostics.Add("TMDB search for \"" + Display(title) + "\": read access token is not configured.");
                return result;
            }

            var url = BaseUrl + "/search/tv?query=" + Uri.EscapeDataString(title ?? "") + "&include_adult=false&language=en-US&page=1";
            var root = ReadJsonObject(url);
            var data = GetArray(root, "results");
            if (data == null || data.Count == 0)
            {
                result.Error = "No TMDB match";
                result.Diagnostics.Add("TMDB search for \"" + Display(title) + "\": returned zero candidates.");
                return result;
            }

            var candidates = data.Cast<object>()
                                 .OfType<IDictionary>()
                                 .Select(x => new ProviderMatchCandidate
                                 {
                                     Id = FirstString(x, "id"),
                                     Title = FirstString(x, "name", "original_name"),
                                     Year = ExtractYear(FirstString(x, "first_air_date")),
                                     ImageUrl = NormalizePosterUrl(FirstString(x, "poster_path")),
                                     BackdropUrl = NormalizePosterUrl(FirstString(x, "backdrop_path")),
                                     AlternateTitles = new List<string>
                                     {
                                         FirstString(x, "name"),
                                         FirstString(x, "original_name")
                                     }
                                 })
                                 .ToList();
            var selected = ProviderMatchEvaluator.SelectBest(title, candidates);
            result.Diagnostics.AddRange(ProviderMatchEvaluator.BuildCandidateDiagnostics("TMDB", title, candidates, selected));
            if (!selected.Found)
            {
                result.Error = ProviderMatchEvaluator.BuildRejectedMatchMessage("TMDB", selected);
                return result;
            }

            result.TmDbId = selected.Candidate.Id;
            result.Title = selected.Candidate.Title;
            result.Year = selected.Candidate.Year;
            result.PosterUrl = selected.Candidate.ImageUrl;
            result.BackdropUrl = selected.Candidate.BackdropUrl;
            if (string.IsNullOrWhiteSpace(result.Title))
            {
                result.Title = title;
            }

            if (string.IsNullOrWhiteSpace(result.TmDbId))
            {
                result.Error = "TMDB search result did not include an id";
            }
            else if (string.IsNullOrWhiteSpace(result.PosterUrl))
            {
                result.PosterUrl = NormalizePosterUrl(LookupPosterPathFromImages("tv", result.TmDbId));
            }

            return result;
        }

        public TmDbSeriesResult LookupSeriesById(string tmDbId)
        {
            var result = new TmDbSeriesResult { QueryTitle = "tmdb:" + (tmDbId ?? "") };
            if (!settings.HasReadAccessToken)
            {
                result.Error = "TMDB read access token is not configured";
                result.Diagnostics.Add("TMDB direct id " + Display(tmDbId) + ": read access token is not configured.");
                return result;
            }

            if (!Regex.IsMatch(tmDbId ?? "", @"^\d+$"))
            {
                result.Error = "Invalid TMDB id";
                result.Diagnostics.Add("TMDB direct id " + Display(tmDbId) + ": invalid id.");
                return result;
            }

            string lookupError;
            var matchedMediaType = "tv";
            var root = ReadDetailsJsonById(matchedMediaType, tmDbId, out lookupError);
            if (root == null)
            {
                matchedMediaType = "movie";
                root = ReadDetailsJsonById(matchedMediaType, tmDbId, out lookupError);
            }

            result.Error = lookupError;

            if (root == null || root.Count == 0)
            {
                if (string.IsNullOrWhiteSpace(result.Error))
                {
                    result.Error = "No TMDB TV or movie match for id " + tmDbId;
                }

                result.Diagnostics.Add("TMDB direct id " + Display(tmDbId) + ": returned zero details.");
                return result;
            }

            result.TmDbId = FirstString(root, "id");
            result.Title = FirstString(root, "name", "title", "original_name", "original_title");
            result.Year = ExtractYear(FirstString(root, "first_air_date", "release_date"));
            result.PosterUrl = NormalizePosterUrl(FirstString(root, "poster_path"));
            result.BackdropUrl = NormalizePosterUrl(FirstString(root, "backdrop_path"));
            result.Diagnostics.Add("TMDB direct id " + tmDbId + ": title=\"" + Display(result.Title) + "\"; year=" + Display(result.Year) + "; artwork=" + (string.IsNullOrWhiteSpace(result.PosterUrl) ? "no" : "yes"));
            if (string.IsNullOrWhiteSpace(result.PosterUrl))
            {
                result.PosterUrl = NormalizePosterUrl(LookupPosterPathFromImages(matchedMediaType, result.TmDbId));
                if (!string.IsNullOrWhiteSpace(result.PosterUrl))
                {
                    result.Diagnostics.Add("TMDB direct id " + tmDbId + ": poster found from images endpoint.");
                }
            }

            if (string.IsNullOrWhiteSpace(result.TmDbId))
            {
                result.Error = "TMDB series id was not found";
            }

            return result;
        }

        public bool TryDownloadSeriesBackdrop(string title, string targetPath, out string message)
        {
            List<string> diagnostics;
            return TryDownloadSeriesBackdrop(title, targetPath, out message, out diagnostics);
        }

        public bool TryDownloadSeriesBackdrop(string title, string targetPath, out string message, out List<string> diagnostics)
        {
            message = "";
            diagnostics = new List<string>();
            if (File.Exists(targetPath))
            {
                return true;
            }

            var result = LookupSeries(title);
            diagnostics = result.Diagnostics;
            if (!result.Found)
            {
                message = result.Error;
                return false;
            }

            if (string.IsNullOrWhiteSpace(result.BackdropUrl) && !string.IsNullOrWhiteSpace(result.TmDbId))
            {
                result.BackdropUrl = NormalizePosterUrl(LookupBackdropPathFromImages("tv", result.TmDbId));
            }

            if (string.IsNullOrWhiteSpace(result.BackdropUrl))
            {
                message = "TMDB match did not include backdrop art";
                return false;
            }

            using (var webClient = new HttpTimeoutWebClient())
            {
                HttpNetworkSettings.Apply();
                webClient.Headers[HttpRequestHeader.UserAgent] = "SameEpisodeDuplicateFinder";
                webClient.DownloadFile(result.BackdropUrl, targetPath);
            }

            message = result.Title;
            return true;
        }

        public bool TryDownloadSeriesBackdropById(string tmDbId, string targetPath, out string message)
        {
            List<string> diagnostics;
            return TryDownloadSeriesBackdropById(tmDbId, targetPath, out message, out diagnostics);
        }

        public bool TryDownloadSeriesBackdropById(string tmDbId, string targetPath, out string message, out List<string> diagnostics)
        {
            message = "";
            diagnostics = new List<string>();
            if (File.Exists(targetPath))
            {
                return true;
            }

            var result = LookupSeriesById(tmDbId);
            diagnostics = result.Diagnostics;
            if (!result.Found)
            {
                message = result.Error;
                return false;
            }

            if (string.IsNullOrWhiteSpace(result.BackdropUrl) && !string.IsNullOrWhiteSpace(result.TmDbId))
            {
                result.BackdropUrl = NormalizePosterUrl(LookupBackdropPathFromImages("tv", result.TmDbId));
                if (string.IsNullOrWhiteSpace(result.BackdropUrl))
                {
                    result.BackdropUrl = NormalizePosterUrl(LookupBackdropPathFromImages("movie", result.TmDbId));
                }
            }

            if (string.IsNullOrWhiteSpace(result.BackdropUrl))
            {
                message = "TMDB match did not include backdrop art";
                return false;
            }

            using (var webClient = new HttpTimeoutWebClient())
            {
                HttpNetworkSettings.Apply();
                webClient.Headers[HttpRequestHeader.UserAgent] = "SameEpisodeDuplicateFinder";
                webClient.DownloadFile(result.BackdropUrl, targetPath);
            }

            message = string.IsNullOrWhiteSpace(result.Title) ? "tmdb:" + tmDbId : result.Title;
            return true;
        }

        public bool TryDownloadSeriesCover(string title, string targetPath, out string message)
        {
            List<string> diagnostics;
            return TryDownloadSeriesCover(title, targetPath, out message, out diagnostics);
        }

        public bool TryDownloadSeriesCover(string title, string targetPath, out string message, out List<string> diagnostics)
        {
            message = "";
            diagnostics = new List<string>();
            if (File.Exists(targetPath))
            {
                return true;
            }

            var result = LookupSeries(title);
            diagnostics = result.Diagnostics;
            if (!result.Found)
            {
                message = result.Error;
                return false;
            }

            if (string.IsNullOrWhiteSpace(result.PosterUrl))
            {
                message = "TMDB match did not include poster art";
                return false;
            }

            using (var webClient = new HttpTimeoutWebClient())
            {
                HttpNetworkSettings.Apply();
                webClient.Headers[HttpRequestHeader.UserAgent] = "SameEpisodeDuplicateFinder";
                webClient.DownloadFile(result.PosterUrl, targetPath);
            }

            message = result.Title;
            return true;
        }

        public bool TryDownloadSeriesCoverById(string tmDbId, string targetPath, out string message)
        {
            List<string> diagnostics;
            return TryDownloadSeriesCoverById(tmDbId, targetPath, out message, out diagnostics);
        }

        public bool TryDownloadSeriesCoverById(string tmDbId, string targetPath, out string message, out List<string> diagnostics)
        {
            message = "";
            diagnostics = new List<string>();
            if (File.Exists(targetPath))
            {
                return true;
            }

            var result = LookupSeriesById(tmDbId);
            diagnostics = result.Diagnostics;
            if (!result.Found)
            {
                message = result.Error;
                return false;
            }

            if (string.IsNullOrWhiteSpace(result.PosterUrl))
            {
                message = "TMDB match did not include poster art";
                return false;
            }

            using (var webClient = new HttpTimeoutWebClient())
            {
                HttpNetworkSettings.Apply();
                webClient.Headers[HttpRequestHeader.UserAgent] = "SameEpisodeDuplicateFinder";
                webClient.DownloadFile(result.PosterUrl, targetPath);
            }

            message = string.IsNullOrWhiteSpace(result.Title) ? "tmdb:" + tmDbId : result.Title;
            return true;
        }

        private static string Display(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "-" : value.Trim();
        }

        private IDictionary ReadDetailsJsonById(string mediaType, string tmDbId, out string error)
        {
            error = "";
            try
            {
                var url = BaseUrl + "/" + mediaType + "/" + Uri.EscapeDataString(tmDbId) + "?language=en-US";
                return ReadJsonObject(url);
            }
            catch (Exception ex)
            {
                error = "TMDB " + mediaType + " id " + tmDbId + ": " + ex.Message;
                return null;
            }
        }

        private string LookupPosterPathFromImages(string mediaType, string tmDbId)
        {
            if (string.IsNullOrWhiteSpace(mediaType) || string.IsNullOrWhiteSpace(tmDbId))
            {
                return "";
            }

            try
            {
                var url = BaseUrl + "/" + mediaType + "/" + Uri.EscapeDataString(tmDbId) + "/images?include_image_language=en,null,ja";
                var root = ReadJsonObject(url);
                var posters = GetArray(root, "posters");
                if (posters == null || posters.Count == 0)
                {
                    return "";
                }

                var preferred = posters.Cast<object>()
                                       .OfType<IDictionary>()
                                       .OrderByDescending(x => PreferredPosterLanguageRank(FirstString(x, "iso_639_1")))
                                       .ThenByDescending(x => FirstNumber(x, "vote_average"))
                                       .ThenByDescending(x => FirstNumber(x, "vote_count"))
                                       .FirstOrDefault();
                return preferred == null ? "" : FirstString(preferred, "file_path");
            }
            catch
            {
                return "";
            }
        }

        private string LookupBackdropPathFromImages(string mediaType, string tmDbId)
        {
            if (string.IsNullOrWhiteSpace(mediaType) || string.IsNullOrWhiteSpace(tmDbId))
            {
                return "";
            }

            try
            {
                var url = BaseUrl + "/" + mediaType + "/" + Uri.EscapeDataString(tmDbId) + "/images?include_image_language=en,null,ja";
                var root = ReadJsonObject(url);
                var backdrops = GetArray(root, "backdrops");
                if (backdrops == null || backdrops.Count == 0)
                {
                    return "";
                }

                var preferred = backdrops.Cast<object>()
                                         .OfType<IDictionary>()
                                         .OrderByDescending(x => PreferredPosterLanguageRank(FirstString(x, "iso_639_1")))
                                         .ThenByDescending(x => FirstNumber(x, "vote_average"))
                                         .ThenByDescending(x => FirstNumber(x, "vote_count"))
                                         .FirstOrDefault();
                return preferred == null ? "" : FirstString(preferred, "file_path");
            }
            catch
            {
                return "";
            }
        }

        private IDictionary ReadJsonObject(string url)
        {
            HttpNetworkSettings.Apply();
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "GET";
            request.Accept = "application/json";
            request.UserAgent = "SameEpisodeDuplicateFinder";
            request.Timeout = 30000;
            request.ReadWriteTimeout = 30000;
            request.Headers[HttpRequestHeader.Authorization] = "Bearer " + settings.ReadAccessToken;

            try
            {
                using (var response = (HttpWebResponse)request.GetResponse())
                using (var stream = response.GetResponseStream())
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    return serializer.DeserializeObject(reader.ReadToEnd()) as IDictionary;
                }
            }
            catch (WebException ex)
            {
                var response = ex.Response as HttpWebResponse;
                if (response != null)
                {
                    throw new InvalidOperationException("TMDB " + ClassifyHttpFailure(response.StatusCode) + ": " + (int)response.StatusCode + " " + response.StatusDescription, ex);
                }

                throw new InvalidOperationException("TMDB Network Error: " + ex.Message, ex);
            }
        }

        private static ArrayList GetArray(IDictionary source, string key)
        {
            if (source == null || !source.Contains(key) || source[key] == null)
            {
                return null;
            }

            var arrayList = source[key] as ArrayList;
            if (arrayList != null)
            {
                return arrayList;
            }

            var objectArray = source[key] as object[];
            if (objectArray != null)
            {
                return new ArrayList(objectArray);
            }

            return null;
        }

        private static string FirstString(IDictionary source, params string[] keys)
        {
            foreach (var key in keys)
            {
                if (source.Contains(key) && source[key] != null)
                {
                    return Convert.ToString(source[key]);
                }
            }

            return "";
        }

        private static double FirstNumber(IDictionary source, string key)
        {
            if (source == null || !source.Contains(key) || source[key] == null)
            {
                return 0;
            }

            double value;
            return double.TryParse(Convert.ToString(source[key]), out value) ? value : 0;
        }

        private static int PreferredPosterLanguageRank(string language)
        {
            if (string.Equals(language, "en", StringComparison.OrdinalIgnoreCase))
            {
                return 3;
            }

            if (string.IsNullOrWhiteSpace(language))
            {
                return 2;
            }

            if (string.Equals(language, "ja", StringComparison.OrdinalIgnoreCase))
            {
                return 1;
            }

            return 0;
        }

        private static string NormalizePosterUrl(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "";
            }

            if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            {
                return "https://" + value.Substring("http://".Length);
            }

            if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }

            return ImageBaseUrl.TrimEnd('/') + "/" + value.TrimStart('/');
        }

        private static string ClassifyHttpFailure(HttpStatusCode statusCode)
        {
            if (statusCode == HttpStatusCode.Unauthorized)
            {
                return "Auth Error";
            }

            if (statusCode == HttpStatusCode.Forbidden)
            {
                return "Forbidden";
            }

            if ((int)statusCode == 429)
            {
                return "Rate Limited";
            }

            return "Network Error";
        }

        private static string ExtractYear(string date)
        {
            if (string.IsNullOrWhiteSpace(date) || date.Length < 4)
            {
                return "";
            }

            return date.Substring(0, 4);
        }
    }
}
