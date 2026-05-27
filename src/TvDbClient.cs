using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace SameEpisodeDuplicateFinder
{
    internal sealed class TvDbSettings
    {
        public string ApiKey { get; set; }
        public string Token { get; set; }
        public DateTime TokenExpiresUtc { get; set; }

        public bool HasApiKey
        {
            get { return !string.IsNullOrWhiteSpace(ApiKey); }
        }
    }

    internal static class TvDbSettingsStore
    {
        public static string SettingsPath
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SameEpisodeDuplicateFinder.tvdb"); }
        }

        public static TvDbSettings Load()
        {
            var settings = new TvDbSettings();
            var envKey = Environment.GetEnvironmentVariable("SEDF_TVDB_API_KEY");
            if (!string.IsNullOrWhiteSpace(envKey))
            {
                settings.ApiKey = envKey.Trim();
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
                if (string.Equals(key, "ApiKey", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(settings.ApiKey))
                {
                    settings.ApiKey = value;
                }
                else if (string.Equals(key, "ApiKeyProtected", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(settings.ApiKey))
                {
                    settings.ApiKey = Unprotect(value);
                }
                else if (string.Equals(key, "Token", StringComparison.OrdinalIgnoreCase))
                {
                    settings.Token = Unprotect(value);
                }
                else if (string.Equals(key, "TokenExpiresUtc", StringComparison.OrdinalIgnoreCase))
                {
                    DateTime parsed;
                    if (DateTime.TryParse(value, null, System.Globalization.DateTimeStyles.AdjustToUniversal, out parsed))
                    {
                        settings.TokenExpiresUtc = parsed.ToUniversalTime();
                    }
                }
            }

            return settings;
        }

        public static void Save(TvDbSettings settings)
        {
            using (var writer = new StreamWriter(SettingsPath, false, new UTF8Encoding(false)))
            {
                writer.WriteLine("ApiKeyProtected=" + Protect(settings.ApiKey ?? ""));
                writer.WriteLine("Token=" + Protect(settings.Token ?? ""));
                writer.WriteLine("TokenExpiresUtc=" + settings.TokenExpiresUtc.ToUniversalTime().ToString("o"));
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

    internal sealed class TvDbSeriesResult
    {
        public TvDbSeriesResult()
        {
            Diagnostics = new List<string>();
        }

        public string QueryTitle { get; set; }
        public string TvDbId { get; set; }
        public string Title { get; set; }
        public string Year { get; set; }
        public string ImageUrl { get; set; }
        public string BackdropUrl { get; set; }
        public string Error { get; set; }
        public List<string> Diagnostics { get; private set; }

        public bool Found
        {
            get { return !string.IsNullOrWhiteSpace(TvDbId); }
        }

        public AniDbAnimeResult ToMetadataResult()
        {
            return new AniDbAnimeResult
            {
                QueryTitle = QueryTitle,
                AniDbId = string.IsNullOrWhiteSpace(TvDbId) ? "" : "tvdb:" + TvDbId,
                Title = Title,
                Year = Year,
                PictureFile = ImageUrl,
                Error = Error
            };
        }
    }

    internal sealed class TvDbClient
    {
        private const string BaseUrl = "https://api4.thetvdb.com/v4";
        private const string ArtworkBaseUrl = "https://artworks.thetvdb.com";
        private readonly TvDbSettings settings;
        private readonly JavaScriptSerializer serializer = new JavaScriptSerializer();

        public TvDbClient(TvDbSettings settings)
        {
            this.settings = settings ?? new TvDbSettings();
        }

        public bool IsConfigured
        {
            get { return settings.HasApiKey; }
        }

        public TvDbSeriesResult LookupSeries(string title)
        {
            var result = new TvDbSeriesResult { QueryTitle = title };
            if (!settings.HasApiKey)
            {
                result.Error = "TVDB API key is not configured";
                result.Diagnostics.Add("TVDB search for \"" + Display(title) + "\": API key is not configured.");
                return result;
            }

            var token = EnsureToken();
            var url = BaseUrl + "/search?query=" + Uri.EscapeDataString(title ?? "") + "&type=series&limit=5";
            var root = ReadJsonObject(url, "GET", null, token);
            var data = GetArray(root, "data");
            if (data == null || data.Count == 0)
            {
                result.Error = "No TVDB match";
                result.Diagnostics.Add("TVDB search for \"" + Display(title) + "\": returned zero candidates.");
                return result;
            }

            var candidates = data.Cast<object>()
                                 .OfType<IDictionary>()
                                 .Select(x => new ProviderMatchCandidate
                                 {
                                     Id = FirstString(x, "tvdb_id", "id"),
                                     Title = FirstString(x, "name", "title"),
                                     Year = FirstString(x, "year"),
                                     ImageUrl = NormalizeImageUrl(FirstString(x, "image_url", "image")),
                                     IsFranchiseParent = IsKnownFranchiseParent(FirstString(x, "tvdb_id", "id")),
                                     AlternateTitles = BuildSearchCandidateTitles(x)
                                 })
                                 .ToList();
            var selected = ProviderMatchEvaluator.SelectBest(title, candidates);
            result.Diagnostics.AddRange(ProviderMatchEvaluator.BuildCandidateDiagnostics("TVDB", title, candidates, selected));
            if (!selected.Found)
            {
                result.Error = ProviderMatchEvaluator.BuildRejectedMatchMessage("TVDB", selected);
                return result;
            }

            result.TvDbId = selected.Candidate.Id;
            result.Title = selected.Candidate.Title;
            result.Year = selected.Candidate.Year;
            result.ImageUrl = selected.Candidate.ImageUrl;
            if (string.IsNullOrWhiteSpace(result.Title))
            {
                result.Title = title;
            }

            if (string.IsNullOrWhiteSpace(result.TvDbId))
            {
                result.Error = "TVDB search result did not include an id";
            }

            return result;
        }

        public TvDbSeriesResult LookupSeriesById(string tvDbId)
        {
            var result = new TvDbSeriesResult { QueryTitle = "tvdb:" + (tvDbId ?? "") };
            if (!settings.HasApiKey)
            {
                result.Error = "TVDB API key is not configured";
                result.Diagnostics.Add("TVDB direct id " + Display(tvDbId) + ": API key is not configured.");
                return result;
            }

            if (!System.Text.RegularExpressions.Regex.IsMatch(tvDbId ?? "", @"^\d+$"))
            {
                result.Error = "Invalid TVDB id";
                result.Diagnostics.Add("TVDB direct id " + Display(tvDbId) + ": invalid id.");
                return result;
            }

            var token = EnsureToken();
            var root = ReadJsonObject(BaseUrl + "/series/" + Uri.EscapeDataString(tvDbId) + "/extended", "GET", null, token);
            var data = GetObject(root, "data");
            if (data == null || data.Count == 0)
            {
                result.Error = "No TVDB series for id " + tvDbId;
                result.Diagnostics.Add("TVDB direct id " + Display(tvDbId) + ": returned zero details.");
                return result;
            }

            result.TvDbId = FirstString(data, "id", "tvdb_id");
            result.Title = FirstString(data, "name", "title", "slug");
            result.Year = FirstString(data, "year");
            result.ImageUrl = NormalizeImageUrl(FirstString(data, "image", "image_url"));
            result.BackdropUrl = NormalizeImageUrl(FirstBackdropImage(data, result.Diagnostics));
            result.Diagnostics.Add("TVDB direct id " + tvDbId + ": title=\"" + Display(result.Title) + "\"; year=" + Display(result.Year) + "; artwork=" + (string.IsNullOrWhiteSpace(result.ImageUrl) ? "no" : "yes"));
            if (string.IsNullOrWhiteSpace(result.ImageUrl))
            {
                result.ImageUrl = NormalizeImageUrl(FirstArtworkImage(data));
                if (!string.IsNullOrWhiteSpace(result.ImageUrl))
                {
                    result.Diagnostics.Add("TVDB direct id " + tvDbId + ": artwork found from extended artwork list.");
                }
            }

            if (string.IsNullOrWhiteSpace(result.TvDbId))
            {
                result.Error = "TVDB series id was not found";
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

            if (string.IsNullOrWhiteSpace(result.BackdropUrl) && !string.IsNullOrWhiteSpace(result.TvDbId))
            {
                result = LookupSeriesById(result.TvDbId);
                diagnostics.AddRange(result.Diagnostics);
            }

            if (string.IsNullOrWhiteSpace(result.BackdropUrl))
            {
                message = "TVDB match did not include backdrop art";
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

        public bool TryDownloadSeriesBackdropById(string tvDbId, string targetPath, out string message)
        {
            List<string> diagnostics;
            return TryDownloadSeriesBackdropById(tvDbId, targetPath, out message, out diagnostics);
        }

        public bool TryDownloadSeriesBackdropById(string tvDbId, string targetPath, out string message, out List<string> diagnostics)
        {
            message = "";
            diagnostics = new List<string>();
            if (File.Exists(targetPath))
            {
                return true;
            }

            var result = LookupSeriesById(tvDbId);
            diagnostics = result.Diagnostics;
            if (!result.Found)
            {
                message = result.Error;
                return false;
            }

            if (string.IsNullOrWhiteSpace(result.BackdropUrl))
            {
                message = "TVDB match did not include backdrop art";
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

            if (string.IsNullOrWhiteSpace(result.ImageUrl))
            {
                message = "TVDB match did not include cover art";
                return false;
            }

            using (var webClient = new HttpTimeoutWebClient())
            {
                HttpNetworkSettings.Apply();
                webClient.Headers[HttpRequestHeader.UserAgent] = "SameEpisodeDuplicateFinder";
                webClient.DownloadFile(result.ImageUrl, targetPath);
            }

            message = result.Title;
            return true;
        }

        public bool TryDownloadSeriesCoverById(string tvDbId, string targetPath, out string message)
        {
            List<string> diagnostics;
            return TryDownloadSeriesCoverById(tvDbId, targetPath, out message, out diagnostics);
        }

        public bool TryDownloadSeriesCoverById(string tvDbId, string targetPath, out string message, out List<string> diagnostics)
        {
            message = "";
            diagnostics = new List<string>();
            if (File.Exists(targetPath))
            {
                return true;
            }

            var result = LookupSeriesById(tvDbId);
            diagnostics = result.Diagnostics;
            if (!result.Found)
            {
                message = result.Error;
                return false;
            }

            if (string.IsNullOrWhiteSpace(result.ImageUrl))
            {
                message = "TVDB match did not include cover art";
                return false;
            }

            using (var webClient = new HttpTimeoutWebClient())
            {
                HttpNetworkSettings.Apply();
                webClient.Headers[HttpRequestHeader.UserAgent] = "SameEpisodeDuplicateFinder";
                webClient.DownloadFile(result.ImageUrl, targetPath);
            }

            message = result.Title;
            return true;
        }

        private static string Display(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "-" : value.Trim();
        }

        private string EnsureToken()
        {
            if (!string.IsNullOrWhiteSpace(settings.Token) && settings.TokenExpiresUtc > DateTime.UtcNow.AddDays(1))
            {
                return settings.Token;
            }

            var body = "{\"apikey\":\"" + EscapeJson(settings.ApiKey) + "\"}";
            var root = ReadJsonObject(BaseUrl + "/login", "POST", body, null);
            var data = GetObject(root, "data");
            var token = data == null ? "" : FirstString(data, "token");
            if (string.IsNullOrWhiteSpace(token))
            {
                throw new InvalidOperationException("TVDB login did not return a bearer token.");
            }

            settings.Token = token;
            settings.TokenExpiresUtc = DateTime.UtcNow.AddDays(29);
            TvDbSettingsStore.Save(settings);
            return token;
        }

        private IDictionary ReadJsonObject(string url, string method, string body, string token)
        {
            HttpNetworkSettings.Apply();
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = method;
            request.Accept = "application/json";
            request.ContentType = "application/json";
            request.UserAgent = "SameEpisodeDuplicateFinder";
            request.Timeout = 30000;
            request.ReadWriteTimeout = 30000;
            if (!string.IsNullOrWhiteSpace(token))
            {
                request.Headers[HttpRequestHeader.Authorization] = "Bearer " + token;
            }

            if (!string.IsNullOrEmpty(body))
            {
                var bytes = Encoding.UTF8.GetBytes(body);
                request.ContentLength = bytes.Length;
                using (var stream = request.GetRequestStream())
                {
                    stream.Write(bytes, 0, bytes.Length);
                }
            }

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
                    throw new InvalidOperationException("TVDB " + ClassifyHttpFailure(response.StatusCode) + ": " + (int)response.StatusCode + " " + response.StatusDescription, ex);
                }

                throw new InvalidOperationException("TVDB Network Error: " + ex.Message, ex);
            }
        }

        private static IDictionary GetObject(IDictionary source, string key)
        {
            return source != null && source.Contains(key) ? source[key] as IDictionary : null;
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
            if (source == null)
            {
                return "";
            }

            foreach (var key in keys)
            {
                if (source.Contains(key) && source[key] != null)
                {
                    return Convert.ToString(source[key]);
                }
            }

            return "";
        }

        private static string FirstArtworkImage(IDictionary source)
        {
            var artworks = GetArray(source, "artworks");
            if (artworks == null || artworks.Count == 0)
            {
                return "";
            }

            var first = artworks.Cast<object>()
                                .OfType<IDictionary>()
                                .OrderByDescending(x => FirstString(x, "type", "typeName").IndexOf("poster", StringComparison.OrdinalIgnoreCase) >= 0)
                                .ThenByDescending(x => FirstString(x, "language").IndexOf("eng", StringComparison.OrdinalIgnoreCase) >= 0)
                                .FirstOrDefault();
            return FirstString(first, "image", "thumbnail", "image_url");
        }

        private static string FirstBackdropImage(IDictionary source)
        {
            return FirstBackdropImage(source, null);
        }

        internal static string SelectBackdropImageForTest(IDictionary source)
        {
            return FirstBackdropImage(source, null);
        }

        private static string FirstBackdropImage(IDictionary source, IList<string> diagnostics)
        {
            var artworks = GetArray(source, "artworks");
            if (artworks == null || artworks.Count == 0)
            {
                if (diagnostics != null)
                {
                    diagnostics.Add("TVDB extended artwork list did not include any artwork entries.");
                }

                return "";
            }

            var candidates = artworks.Cast<object>()
                                .OfType<IDictionary>()
                                .Select(x => new
                                {
                                    Artwork = x,
                                    Rank = ArtworkTypeRank(x),
                                    Type = ArtworkTypeText(x),
                                    Image = FirstString(x, "image", "thumbnail", "image_url")
                                })
                                .Where(x => x.Rank > 0 && !string.IsNullOrWhiteSpace(x.Image))
                                .ToList();
            var first = candidates
                                .OrderByDescending(x => x.Rank)
                                .ThenByDescending(x => FirstString(x.Artwork, "language").IndexOf("eng", StringComparison.OrdinalIgnoreCase) >= 0)
                                .FirstOrDefault();
            if (first == null)
            {
                if (diagnostics != null)
                {
                    diagnostics.Add("TVDB extended artwork list did not include usable landscape backdrop art; poster/cover artwork was ignored.");
                }

                return "";
            }

            if (diagnostics != null)
            {
                diagnostics.Add("TVDB selected backdrop artwork type " + Display(first.Type) + ".");
            }

            return first.Image;
        }

        private static int ArtworkTypeRank(IDictionary artwork)
        {
            var value = ArtworkTypeText(artwork);
            var rank = ArtworkTypeRank(value);
            if (rank > 0)
            {
                return rank;
            }

            return IsLandscapeArtwork(artwork) ? 1 : 0;
        }

        private static int ArtworkTypeRank(string value)
        {
            value = value ?? "";
            if (value.IndexOf("background", StringComparison.OrdinalIgnoreCase) >= 0 ||
                value.IndexOf("backdrop", StringComparison.OrdinalIgnoreCase) >= 0 ||
                value.IndexOf("fanart", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return 4;
            }

            if (value.IndexOf("banner", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return 3;
            }

            return 0;
        }

        private static string ArtworkTypeText(IDictionary artwork)
        {
            if (artwork == null)
            {
                return "";
            }

            foreach (var key in new[] { "type", "typeName", "artworkType", "artworkTypeName", "artwork_type" })
            {
                if (!artwork.Contains(key) || artwork[key] == null)
                {
                    continue;
                }

                var nested = artwork[key] as IDictionary;
                if (nested != null)
                {
                    var nestedValue = FirstString(nested, "name", "type", "slug");
                    if (!string.IsNullOrWhiteSpace(nestedValue))
                    {
                        return nestedValue;
                    }
                }

                var value = Convert.ToString(artwork[key]);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }

            return "";
        }

        private static bool IsLandscapeArtwork(IDictionary artwork)
        {
            int width;
            int height;
            if (!TryGetInt(artwork, out width, "width", "imageWidth", "thumbnailWidth") ||
                !TryGetInt(artwork, out height, "height", "imageHeight", "thumbnailHeight") ||
                width <= 0 ||
                height <= 0)
            {
                return false;
            }

            return width >= height * 1.4;
        }

        private static bool TryGetInt(IDictionary source, out int value, params string[] keys)
        {
            value = 0;
            if (source == null)
            {
                return false;
            }

            foreach (var key in keys)
            {
                if (!source.Contains(key) || source[key] == null)
                {
                    continue;
                }

                if (int.TryParse(Convert.ToString(source[key]), out value))
                {
                    return true;
                }
            }

            return false;
        }

        private static List<string> BuildSearchCandidateTitles(IDictionary source)
        {
            var titles = new List<string>
            {
                FirstString(source, "name"),
                FirstString(source, "title"),
                FirstString(source, "slug")
            };

            var aliases = GetArray(source, "aliases");
            if (aliases != null)
            {
                foreach (var alias in aliases)
                {
                    var aliasObject = alias as IDictionary;
                    if (aliasObject != null)
                    {
                        titles.Add(FirstString(aliasObject, "name", "title"));
                    }
                    else if (alias != null)
                    {
                        titles.Add(Convert.ToString(alias));
                    }
                }
            }

            return titles.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static bool IsKnownFranchiseParent(string tvDbId)
        {
            return string.Equals(tvDbId, "73694", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(tvDbId, "74096", StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeImageUrl(string value)
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

            return ArtworkBaseUrl.TrimEnd('/') + "/" + value.TrimStart('/');
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

        private static string EscapeJson(string value)
        {
            return (value ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }
}
