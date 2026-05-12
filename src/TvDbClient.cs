using System;
using System.Collections;
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
        public string QueryTitle { get; set; }
        public string TvDbId { get; set; }
        public string Title { get; set; }
        public string Year { get; set; }
        public string ImageUrl { get; set; }
        public string Error { get; set; }

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
                return result;
            }

            var token = EnsureToken();
            var url = BaseUrl + "/search?query=" + Uri.EscapeDataString(title ?? "") + "&type=series&limit=5";
            var root = ReadJsonObject(url, "GET", null, token);
            var data = GetArray(root, "data");
            if (data == null || data.Count == 0)
            {
                result.Error = "No TVDB match";
                return result;
            }

            var first = data.Cast<object>().OfType<IDictionary>().FirstOrDefault();
            if (first == null)
            {
                result.Error = "TVDB returned no usable search result";
                return result;
            }

            result.TvDbId = FirstString(first, "tvdb_id", "id");
            result.Title = FirstString(first, "name", "title");
            result.Year = FirstString(first, "year");
            result.ImageUrl = NormalizeImageUrl(FirstString(first, "image_url", "image"));
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

        public bool TryDownloadSeriesCover(string title, string targetPath, out string message)
        {
            message = "";
            if (File.Exists(targetPath))
            {
                return true;
            }

            var result = LookupSeries(title);
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
                    throw new InvalidOperationException("TVDB request failed: " + (int)response.StatusCode + " " + response.StatusDescription, ex);
                }

                throw new InvalidOperationException("TVDB request failed: " + ex.Message, ex);
            }
        }

        private static IDictionary GetObject(IDictionary source, string key)
        {
            return source != null && source.Contains(key) ? source[key] as IDictionary : null;
        }

        private static ArrayList GetArray(IDictionary source, string key)
        {
            return source != null && source.Contains(key) ? source[key] as ArrayList : null;
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

        private static string NormalizeImageUrl(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "";
            }

            if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }

            return ArtworkBaseUrl.TrimEnd('/') + "/" + value.TrimStart('/');
        }

        private static string EscapeJson(string value)
        {
            return (value ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }
}
