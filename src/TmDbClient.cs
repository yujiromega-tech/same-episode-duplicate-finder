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
        public string QueryTitle { get; set; }
        public string TmDbId { get; set; }
        public string Title { get; set; }
        public string Year { get; set; }
        public string PosterUrl { get; set; }
        public string Error { get; set; }

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
                return result;
            }

            var url = BaseUrl + "/search/tv?query=" + Uri.EscapeDataString(title ?? "") + "&include_adult=false&language=en-US&page=1";
            var root = ReadJsonObject(url);
            var data = GetArray(root, "results");
            if (data == null || data.Count == 0)
            {
                result.Error = "No TMDB match";
                return result;
            }

            var first = data.Cast<object>().OfType<IDictionary>().FirstOrDefault();
            if (first == null)
            {
                result.Error = "TMDB returned no usable search result";
                return result;
            }

            result.TmDbId = FirstString(first, "id");
            result.Title = FirstString(first, "name", "original_name");
            result.Year = ExtractYear(FirstString(first, "first_air_date"));
            result.PosterUrl = NormalizePosterUrl(FirstString(first, "poster_path"));
            if (string.IsNullOrWhiteSpace(result.Title))
            {
                result.Title = title;
            }

            if (string.IsNullOrWhiteSpace(result.TmDbId))
            {
                result.Error = "TMDB search result did not include an id";
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
