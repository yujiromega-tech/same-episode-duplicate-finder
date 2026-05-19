using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using System.Xml;
using Microsoft.VisualBasic.FileIO;

namespace SameEpisodeDuplicateFinder
{
    internal static class AniDbApiClient
    {
        public const string Name = "duplikates";
        public const int Version = 1;
    }

    internal static class HttpNetworkSettings
    {
        public static void Apply()
        {
            ServicePointManager.SecurityProtocol = ServicePointManager.SecurityProtocol | SecurityProtocolType.Tls12;
        }
    }

    internal static class AniDbTitleIndex
    {
        private static readonly TimeSpan XmlRegexTimeout = TimeSpan.FromSeconds(5);
        private static readonly string[] TitleUrls =
        {
            "https://anidb.net/api/anime-titles.xml.gz"
        };

        public static string CachePath
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SameEpisodeDuplicateFinder.anidb-titles.xml"); }
        }

        public static List<AniDbTitleCandidate> FindCandidates(string queryTitle, string targetFolder, int maxResults)
        {
            var xml = LoadTitleXml();
            var normalizedQuery = NormalizeTitle(queryTitle);
            if (string.IsNullOrWhiteSpace(normalizedQuery))
            {
                return new List<AniDbTitleCandidate>();
            }

            var results = new Dictionary<string, AniDbTitleCandidate>(StringComparer.OrdinalIgnoreCase);

            foreach (Match animeMatch in Regex.Matches(xml, @"<anime\s+aid=""(?<aid>\d+)""[^>]*>(?<body>.*?)</anime>", RegexOptions.Singleline | RegexOptions.IgnoreCase, XmlRegexTimeout))
            {
                var aid = animeMatch.Groups["aid"].Value;
                foreach (Match titleMatch in Regex.Matches(animeMatch.Groups["body"].Value, @"<title(?<attrs>[^>]*)>(?<title>.*?)</title>", RegexOptions.Singleline | RegexOptions.IgnoreCase, XmlRegexTimeout))
                {
                    var title = WebUtility.HtmlDecode(titleMatch.Groups["title"].Value.Trim());
                    var score = ScoreTitle(normalizedQuery, NormalizeTitle(title));
                    if (score <= 0)
                    {
                        continue;
                    }

                    AniDbTitleCandidate existing;
                    if (!results.TryGetValue(aid, out existing) || score > existing.Score)
                    {
                        results[aid] = new AniDbTitleCandidate
                        {
                            Use = false,
                            QueryTitle = queryTitle,
                            AniDbId = aid,
                            Title = title,
                            TitleType = ExtractXmlAttribute(titleMatch.Groups["attrs"].Value, "type"),
                            Score = score,
                            TargetFolder = targetFolder
                        };
                    }
                }
            }

            return results.Values
                          .OrderByDescending(x => x.Score)
                          .ThenBy(x => x.Title, StringComparer.OrdinalIgnoreCase)
                          .Take(maxResults)
                          .ToList();
        }

        private static string LoadTitleXml()
        {
            if (File.Exists(CachePath) && File.GetLastWriteTimeUtc(CachePath) > DateTime.UtcNow.AddDays(-1))
            {
                return File.ReadAllText(CachePath, Encoding.UTF8);
            }

            Exception lastError = null;
            HttpNetworkSettings.Apply();
            foreach (var url in TitleUrls)
            {
                try
                {
                    using (var webClient = new HttpTimeoutWebClient())
                    {
                        webClient.Headers[HttpRequestHeader.UserAgent] = "SameEpisodeDuplicateFinder";
                        var compressed = webClient.DownloadData(url);
                        using (var compressedStream = new MemoryStream(compressed))
                        using (var gzip = new GZipStream(compressedStream, CompressionMode.Decompress))
                        using (var reader = new StreamReader(gzip, Encoding.UTF8))
                        {
                            var xml = reader.ReadToEnd();
                            File.WriteAllText(CachePath, xml, new UTF8Encoding(false));
                            return xml;
                        }
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex;
                }
            }

            if (File.Exists(CachePath))
            {
                return File.ReadAllText(CachePath, Encoding.UTF8);
            }

            var detail = lastError == null || string.IsNullOrWhiteSpace(lastError.Message) ? "No error detail was returned." : lastError.Message;
            throw new InvalidOperationException("AniDB title index could not be downloaded: " + detail, lastError);
        }

        private static string ExtractXmlAttribute(string attributes, string name)
        {
            var match = Regex.Match(attributes ?? "", @"\b" + Regex.Escape(name) + @"\s*=\s*[""'](?<value>[^""']+)[""']", RegexOptions.IgnoreCase, XmlRegexTimeout);
            return match.Success ? WebUtility.HtmlDecode(match.Groups["value"].Value.Trim()) : "";
        }

        private static string NormalizeTitle(string title)
        {
            title = (title ?? "").ToLowerInvariant();
            title = Regex.Replace(title, @"\[[^\]]+\]|\([^\)]*\)", " ");
            title = Regex.Replace(title, @"[^a-z0-9]+", " ");
            title = Regex.Replace(title, @"\b(the|a|an|tv|ova|movie|season|part)\b", " ");
            return Regex.Replace(title, @"\s+", " ").Trim();
        }

        private static int ScoreTitle(string query, string candidate)
        {
            if (string.IsNullOrWhiteSpace(query) || string.IsNullOrWhiteSpace(candidate))
            {
                return 0;
            }

            if (string.Equals(query, candidate, StringComparison.OrdinalIgnoreCase))
            {
                return 100;
            }

            if (candidate.Contains(query) || query.Contains(candidate))
            {
                return 85;
            }

            var queryTokens = query.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var candidateTokens = new HashSet<string>(candidate.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);
            if (queryTokens.Length == 0 || candidateTokens.Count == 0)
            {
                return 0;
            }

            var matches = queryTokens.Count(x => candidateTokens.Contains(x));
            var score = (int)Math.Round((decimal)matches * 100M / Math.Max(queryTokens.Length, candidateTokens.Count));
            return score >= 35 ? score : 0;
        }
    }

    internal sealed class CachedParsedFile
    {
        public long SizeBytes { get; set; }
        public long LastWriteUtcTicks { get; set; }
        public EpisodeFile File { get; set; }
    }

    internal sealed class GridColumnLayoutItem
    {
        public string PropertyName { get; set; }
        public int DisplayIndex { get; set; }
        public int Width { get; set; }
        public bool Visible { get; set; }
    }

    internal static class GridColumnLayoutStore
    {
        public static string SettingsPath
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SameEpisodeDuplicateFinder.columns"); }
        }

        public static List<GridColumnLayoutItem> Load()
        {
            var result = new List<GridColumnLayoutItem>();
            if (!File.Exists(SettingsPath))
            {
                return result;
            }

            foreach (var line in File.ReadAllLines(SettingsPath, Encoding.UTF8))
            {
                var parts = line.Split('\t');
                if (parts.Length < 4 || string.IsNullOrWhiteSpace(parts[0]))
                {
                    continue;
                }

                int displayIndex;
                int width;
                bool visible;
                if (!int.TryParse(parts[1], out displayIndex))
                {
                    displayIndex = result.Count;
                }
                if (!int.TryParse(parts[2], out width))
                {
                    width = 100;
                }
                if (!bool.TryParse(parts[3], out visible))
                {
                    visible = true;
                }

                result.Add(new GridColumnLayoutItem
                {
                    PropertyName = parts[0],
                    DisplayIndex = displayIndex,
                    Width = width,
                    Visible = visible
                });
            }

            return result;
        }

        public static void Save(IEnumerable<DataGridViewColumn> columns)
        {
            using (var writer = new StreamWriter(SettingsPath, false, new UTF8Encoding(false)))
            {
                foreach (var column in columns.OrderBy(x => x.DisplayIndex))
                {
                    if (string.IsNullOrWhiteSpace(column.DataPropertyName))
                    {
                        continue;
                    }

                    writer.WriteLine(string.Join("\t", new[]
                    {
                        column.DataPropertyName,
                        column.DisplayIndex.ToString(),
                        column.Width.ToString(),
                        column.Visible.ToString()
                    }));
                }
            }
        }
    }

    internal static class BetaNoticeStore
    {
        public static string SeenPath
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SameEpisodeDuplicateFinder.beta-seen"); }
        }

        public static bool HasSeen
        {
            get { return File.Exists(SeenPath); }
        }

        public static void MarkSeen()
        {
            File.WriteAllText(SeenPath, DateTime.UtcNow.ToString("o"), new UTF8Encoding(false));
        }
    }

    internal enum AutoMarkThreshold
    {
        Low,
        Medium,
        High
    }

    internal static class AutoMarkThresholdStore
    {
        private static string SettingsPath
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SameEpisodeDuplicateFinder.automark"); }
        }

        public static AutoMarkThreshold Load()
        {
            try
            {
                if (!File.Exists(SettingsPath))
                {
                    return AutoMarkThreshold.High;
                }

                var value = File.ReadAllText(SettingsPath).Trim();
                if (string.Equals(value, "Low", StringComparison.OrdinalIgnoreCase))
                {
                    return AutoMarkThreshold.Low;
                }

                if (string.Equals(value, "Medium", StringComparison.OrdinalIgnoreCase))
                {
                    return AutoMarkThreshold.Medium;
                }
            }
            catch (Exception)
            {
            }

            return AutoMarkThreshold.High;
        }

        public static void Save(AutoMarkThreshold threshold)
        {
            File.WriteAllText(SettingsPath, threshold.ToString(), new UTF8Encoding(false));
        }
    }

    internal sealed class UiLayoutSettings
    {
        public bool DarkMode { get; set; }
        public bool ShowSeriesCovers { get; set; }
        public bool CandidatesPanelCollapsed { get; set; }
        public bool DeletionPanelCollapsed { get; set; }
        public bool MissingEpisodesPanelCollapsed { get; set; }
        public bool EpisodeSearchPanelCollapsed { get; set; }
        public bool SelectedFeedPanelCollapsed { get; set; }

        public static UiLayoutSettings CreateDefault()
        {
            return new UiLayoutSettings
            {
                DarkMode = true,
                ShowSeriesCovers = false,
                CandidatesPanelCollapsed = false,
                DeletionPanelCollapsed = false,
                MissingEpisodesPanelCollapsed = true,
                EpisodeSearchPanelCollapsed = true,
                SelectedFeedPanelCollapsed = true
            };
        }
    }

    internal static class UiLayoutSettingsStore
    {
        private static string SettingsPath
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SameEpisodeDuplicateFinder.ui"); }
        }

        public static UiLayoutSettings Load()
        {
            var settings = UiLayoutSettings.CreateDefault();
            try
            {
                if (!File.Exists(SettingsPath))
                {
                    return settings;
                }

                foreach (var line in File.ReadAllLines(SettingsPath, Encoding.UTF8))
                {
                    var parts = line.Split(new[] { '=' }, 2);
                    if (parts.Length != 2)
                    {
                        continue;
                    }

                    var key = parts[0].Trim();
                    bool value;
                    if (!bool.TryParse(parts[1].Trim(), out value))
                    {
                        continue;
                    }

                    ApplyValue(settings, key, value);
                }
            }
            catch
            {
            }

            return settings;
        }

        public static void Save(UiLayoutSettings settings)
        {
            if (settings == null)
            {
                return;
            }

            using (var writer = new StreamWriter(SettingsPath, false, new UTF8Encoding(false)))
            {
                writer.WriteLine("DarkMode=" + settings.DarkMode);
                writer.WriteLine("ShowSeriesCovers=" + settings.ShowSeriesCovers);
                writer.WriteLine("CandidatesPanelCollapsed=" + settings.CandidatesPanelCollapsed);
                writer.WriteLine("DeletionPanelCollapsed=" + settings.DeletionPanelCollapsed);
                writer.WriteLine("MissingEpisodesPanelCollapsed=" + settings.MissingEpisodesPanelCollapsed);
                writer.WriteLine("EpisodeSearchPanelCollapsed=" + settings.EpisodeSearchPanelCollapsed);
                writer.WriteLine("SelectedFeedPanelCollapsed=" + settings.SelectedFeedPanelCollapsed);
            }
        }

        private static void ApplyValue(UiLayoutSettings settings, string key, bool value)
        {
            if (string.Equals(key, "DarkMode", StringComparison.OrdinalIgnoreCase))
            {
                settings.DarkMode = value;
            }
            else if (string.Equals(key, "ShowSeriesCovers", StringComparison.OrdinalIgnoreCase))
            {
                settings.ShowSeriesCovers = value;
            }
            else if (string.Equals(key, "CandidatesPanelCollapsed", StringComparison.OrdinalIgnoreCase))
            {
                settings.CandidatesPanelCollapsed = value;
            }
            else if (string.Equals(key, "DeletionPanelCollapsed", StringComparison.OrdinalIgnoreCase))
            {
                settings.DeletionPanelCollapsed = value;
            }
            else if (string.Equals(key, "MissingEpisodesPanelCollapsed", StringComparison.OrdinalIgnoreCase))
            {
                settings.MissingEpisodesPanelCollapsed = value;
            }
            else if (string.Equals(key, "EpisodeSearchPanelCollapsed", StringComparison.OrdinalIgnoreCase))
            {
                settings.EpisodeSearchPanelCollapsed = value;
            }
            else if (string.Equals(key, "SelectedFeedPanelCollapsed", StringComparison.OrdinalIgnoreCase))
            {
                settings.SelectedFeedPanelCollapsed = value;
            }
        }
    }

    internal static class LongPath
    {
        private const int MaxPath = 260;
        private const int FindFirstExLargeFetch = 2;
        private static readonly IntPtr InvalidHandleValue = new IntPtr(-1);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr FindFirstFileEx(
            string lpFileName,
            FINDEX_INFO_LEVELS fInfoLevelId,
            out WIN32_FIND_DATA lpFindFileData,
            FINDEX_SEARCH_OPS fSearchOp,
            IntPtr lpSearchFilter,
            int dwAdditionalFlags);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool FindNextFile(IntPtr hFindFile, out WIN32_FIND_DATA lpFindFileData);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FindClose(IntPtr hFindFile);

        private enum FINDEX_INFO_LEVELS
        {
            FindExInfoStandard = 0,
            FindExInfoBasic = 1
        }

        private enum FINDEX_SEARCH_OPS
        {
            FindExSearchNameMatch = 0
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WIN32_FIND_DATA
        {
            public uint dwFileAttributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME ftCreationTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME ftLastAccessTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME ftLastWriteTime;
            public uint nFileSizeHigh;
            public uint nFileSizeLow;
            public uint dwReserved0;
            public uint dwReserved1;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MaxPath)]
            public string cFileName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)]
            public string cAlternateFileName;
        }

        public static IEnumerable<ScannedFile> EnumerateFiles(string root)
        {
            return EnumerateFiles(root, null);
        }

        public static IEnumerable<ScannedFile> EnumerateFiles(string root, Action<string> directoryProgress)
        {
            var rootFull = Path.GetFullPath(root).TrimEnd('\\');
            var pending = new Stack<string>();
            pending.Push(rootFull);

            while (pending.Count > 0)
            {
                var directory = pending.Pop();
                if (directoryProgress != null)
                {
                    directoryProgress(directory);
                }

                WIN32_FIND_DATA data;
                var handle = FindFirstFileEx(
                    ToExtendedSearchPattern(directory),
                    FINDEX_INFO_LEVELS.FindExInfoBasic,
                    out data,
                    FINDEX_SEARCH_OPS.FindExSearchNameMatch,
                    IntPtr.Zero,
                    FindFirstExLargeFetch);

                if (handle == InvalidHandleValue)
                {
                    continue;
                }

                try
                {
                    do
                    {
                        var name = data.cFileName;
                        if (name == "." || name == "..")
                        {
                            continue;
                        }

                        var fullPath = Path.Combine(directory, name);
                        var isDirectory = (data.dwFileAttributes & 0x10) != 0;
                        if (isDirectory)
                        {
                            pending.Push(fullPath);
                        }
                        else
                        {
                            var size = ((long)data.nFileSizeHigh << 32) + data.nFileSizeLow;
                            yield return new ScannedFile
                            {
                                FullName = fullPath,
                                DirectoryName = directory,
                                Name = name,
                                BaseName = Path.GetFileNameWithoutExtension(name),
                                Length = size,
                                LastWriteUtcTicks = ToDateTimeUtcTicks(data.ftLastWriteTime)
                            };
                        }
                    }
                    while (FindNextFile(handle, out data));
                }
                finally
                {
                    FindClose(handle);
                }
            }
        }

        public static string ToExtendedPath(string path)
        {
            if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
            {
                return path;
            }

            var full = Path.GetFullPath(path);
            if (full.StartsWith(@"\\", StringComparison.Ordinal))
            {
                return @"\\?\UNC\" + full.Substring(2);
            }

            return @"\\?\" + full;
        }

        private static string ToExtendedSearchPattern(string directory)
        {
            return ToExtendedPath(directory).TrimEnd('\\') + @"\*";
        }

        public static bool IsLongPath(string path)
        {
            return path.Length >= MaxPath;
        }

        private static long ToDateTimeUtcTicks(System.Runtime.InteropServices.ComTypes.FILETIME fileTime)
        {
            var high = ((long)fileTime.dwHighDateTime) << 32;
            var fileTimeValue = high + (uint)fileTime.dwLowDateTime;
            return DateTime.FromFileTimeUtc(fileTimeValue).Ticks;
        }
    }

    internal sealed class AniDbAnimeResult
    {
        public string QueryTitle { get; set; }
        public string AniDbId { get; set; }
        public string Title { get; set; }
        public string Year { get; set; }
        public string PictureFile { get; set; }
        public string Error { get; set; }
        public int Score { get; set; }

        public bool Found
        {
            get { return !string.IsNullOrWhiteSpace(AniDbId); }
        }
    }

    internal static class AniDbClient
    {
        public static AniDbAnimeResult LookupAnimeById(string aniDbId, string queryTitle, string fallbackTitle)
        {
            var result = new AniDbAnimeResult
            {
                AniDbId = aniDbId,
                QueryTitle = queryTitle,
                Title = fallbackTitle
            };

            if (string.IsNullOrWhiteSpace(aniDbId))
            {
                result.Error = "No AniDB match";
                return result;
            }

            var xml = DownloadAnimeXml(aniDbId);
            result.PictureFile = ExtractElement(xml, "picture");
            result.Year = ExtractYear(xml);
            var title = ExtractTitle(xml);
            if (!string.IsNullOrWhiteSpace(title))
            {
                result.Title = title;
            }
            if (string.IsNullOrWhiteSpace(result.Title))
            {
                result.Title = queryTitle;
            }

            return result;
        }

        public static string GetAnimePictureFile(string aniDbId)
        {
            if (string.IsNullOrWhiteSpace(aniDbId))
            {
                return "";
            }

            var xml = DownloadAnimeXml(aniDbId);
            return ExtractElement(xml, "picture");
        }

        internal static string DownloadAnimeXml(string aniDbId)
        {
            HttpNetworkSettings.Apply();
            var url = string.Format(
                "http://api.anidb.net:9001/httpapi?request=anime&client={0}&clientver={1}&protover=1&aid={2}",
                Encode(AniDbApiClient.Name),
                AniDbApiClient.Version,
                Encode(aniDbId));
            using (var webClient = new HttpTimeoutWebClient())
            {
                webClient.Encoding = Encoding.UTF8;
                webClient.Headers[HttpRequestHeader.UserAgent] = "SameEpisodeDuplicateFinder";
                var bytes = webClient.DownloadData(url);
                if (bytes.Length >= 2 && bytes[0] == 0x1f && bytes[1] == 0x8b)
                {
                    using (var compressedStream = new MemoryStream(bytes))
                    using (var gzip = new GZipStream(compressedStream, CompressionMode.Decompress))
                    using (var reader = new StreamReader(gzip, Encoding.UTF8))
                    {
                        return reader.ReadToEnd();
                    }
                }

                return Encoding.UTF8.GetString(bytes);
            }
        }

        private static string ExtractTitle(string xml)
        {
            var matches = Regex.Matches(xml ?? "", @"<title\b(?<attrs>[^>]*)>(?<title>[^<]+)</title>", RegexOptions.IgnoreCase);
            foreach (Match match in matches)
            {
                if (match.Groups["attrs"].Value.IndexOf("type=\"main\"", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return WebUtility.HtmlDecode(match.Groups["title"].Value.Trim());
                }
            }
            foreach (Match match in matches)
            {
                if (match.Groups["attrs"].Value.IndexOf("type=\"official\"", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return WebUtility.HtmlDecode(match.Groups["title"].Value.Trim());
                }
            }

            return matches.Count > 0 ? WebUtility.HtmlDecode(matches[0].Groups["title"].Value.Trim()) : "";
        }

        private static string ExtractYear(string xml)
        {
            var startDate = ExtractElement(xml, "startdate");
            if (!string.IsNullOrWhiteSpace(startDate) && startDate.Length >= 4)
            {
                return startDate.Substring(0, 4);
            }

            return ExtractElement(xml, "year");
        }

        private static string ExtractElement(string xml, string elementName)
        {
            var match = Regex.Match(xml ?? "", @"<" + Regex.Escape(elementName) + @">\s*(?<value>[^<]+)\s*</" + Regex.Escape(elementName) + @">", RegexOptions.IgnoreCase);
            return match.Success ? WebUtility.HtmlDecode(match.Groups["value"].Value.Trim()) : "";
        }

        private static string Encode(string value)
        {
            return Uri.EscapeDataString(value ?? "").Replace("%20", "+");
        }
    }

    internal sealed class FileFormatFilterDialog : Form
    {
        private static readonly string[] CommonExtensions =
        {
            ".mkv", ".mp4", ".avi", ".mov", ".wmv", ".m4v", ".webm", ".ts", ".m2ts",
            ".ass", ".srt", ".ssa", ".vtt",
            ".flac", ".mp3", ".m4a", ".aac", ".ogg", ".opus", ".wav", ".wma", ".alac", ".ape"
        };

        private readonly RadioButton disallowRadio;
        private readonly RadioButton allowOnlyRadio;
        private readonly CheckedListBox extensionList;
        private readonly TextBox customBox;

        public FileFormatFilterDialog(FileFormatFilter currentFilter)
        {
            Text = "File Formats";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = false;
            MaximizeBox = false;
            ClientSize = new Size(440, 500);

            var filter = currentFilter == null ? FileFormatFilter.CreateDefault() : currentFilter.Clone();

            var layout = new TableLayoutPanel();
            layout.Dock = DockStyle.Fill;
            layout.Padding = new Padding(12);
            layout.ColumnCount = 1;
            layout.RowCount = 5;
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));

            disallowRadio = new RadioButton();
            disallowRadio.Text = "Disallow selected formats";
            disallowRadio.Dock = DockStyle.Fill;
            disallowRadio.Checked = !filter.AllowOnlyListed;

            allowOnlyRadio = new RadioButton();
            allowOnlyRadio.Text = "Only allow selected formats";
            allowOnlyRadio.Dock = DockStyle.Fill;
            allowOnlyRadio.Checked = filter.AllowOnlyListed;

            extensionList = new CheckedListBox();
            extensionList.CheckOnClick = true;
            extensionList.Dock = DockStyle.Fill;
            extensionList.IntegralHeight = false;

            var allExtensions = CommonExtensions.Concat(filter.Extensions)
                                                .Select(FileFormatFilter.NormalizeExtension)
                                                .Where(x => !string.IsNullOrWhiteSpace(x))
                                                .Distinct(StringComparer.OrdinalIgnoreCase)
                                                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                                                .ToList();
            foreach (var extension in allExtensions)
            {
                extensionList.Items.Add(extension, filter.Extensions.Any(x => string.Equals(x, extension, StringComparison.OrdinalIgnoreCase)));
            }

            var customPanel = new TableLayoutPanel();
            customPanel.Dock = DockStyle.Fill;
            customPanel.ColumnCount = 1;
            customPanel.RowCount = 2;
            customPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
            customPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            var customLabel = new Label();
            customLabel.Text = "Extra extensions, separated by comma or space";
            customLabel.Dock = DockStyle.Fill;
            customLabel.TextAlign = ContentAlignment.MiddleLeft;

            customBox = new TextBox();
            customBox.Dock = DockStyle.Fill;

            customPanel.Controls.Add(customLabel, 0, 0);
            customPanel.Controls.Add(customBox, 0, 1);

            var buttonPanel = new FlowLayoutPanel();
            buttonPanel.Dock = DockStyle.Fill;
            buttonPanel.FlowDirection = FlowDirection.RightToLeft;
            buttonPanel.Padding = new Padding(0, 8, 0, 0);

            var okButton = new Button();
            okButton.Text = "Apply";
            okButton.Width = 90;
            okButton.Click += delegate
            {
                if (GetSelectedExtensions().Count == 0)
                {
                    MessageBox.Show(this, "Select at least one extension.", "File Formats", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                DialogResult = DialogResult.OK;
            };

            var cancelButton = new Button();
            cancelButton.Text = "Cancel";
            cancelButton.Width = 90;
            cancelButton.DialogResult = DialogResult.Cancel;

            var defaultsButton = new Button();
            defaultsButton.Text = "Defaults";
            defaultsButton.Width = 90;
            defaultsButton.Click += delegate
            {
                var defaults = FileFormatFilter.CreateDefault();
                disallowRadio.Checked = true;
                for (var i = 0; i < extensionList.Items.Count; i++)
                {
                    var extension = extensionList.Items[i].ToString();
                    extensionList.SetItemChecked(i, defaults.Extensions.Any(x => string.Equals(x, extension, StringComparison.OrdinalIgnoreCase)));
                }
                customBox.Text = "";
            };

            buttonPanel.Controls.Add(okButton);
            buttonPanel.Controls.Add(cancelButton);
            buttonPanel.Controls.Add(defaultsButton);

            layout.Controls.Add(disallowRadio, 0, 0);
            layout.Controls.Add(allowOnlyRadio, 0, 1);
            layout.Controls.Add(extensionList, 0, 2);
            layout.Controls.Add(customPanel, 0, 3);
            layout.Controls.Add(buttonPanel, 0, 4);

            Controls.Add(layout);
            AcceptButton = okButton;
            CancelButton = cancelButton;
        }

        public FileFormatFilter Filter
        {
            get
            {
                var filter = new FileFormatFilter();
                filter.AllowOnlyListed = allowOnlyRadio.Checked;
                filter.Extensions.AddRange(GetSelectedExtensions());
                return filter;
            }
        }

        private List<string> GetSelectedExtensions()
        {
            var extensions = new List<string>();
            foreach (var item in extensionList.CheckedItems)
            {
                AddExtension(extensions, item.ToString());
            }

            foreach (var part in customBox.Text.Split(new[] { ';', ',', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                AddExtension(extensions, part);
            }

            return extensions.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static void AddExtension(List<string> extensions, string value)
        {
            var extension = FileFormatFilter.NormalizeExtension(value);
            if (!string.IsNullOrWhiteSpace(extension) &&
                !extensions.Any(x => string.Equals(x, extension, StringComparison.OrdinalIgnoreCase)))
            {
                extensions.Add(extension);
            }
        }
    }

    internal sealed class AniDbCoverMatchDialog : Form
    {
        private readonly DataGridView grid;
        private readonly BindingList<AniDbTitleCandidate> rows;

        public AniDbCoverMatchDialog(IEnumerable<AniDbTitleCandidate> candidates)
        {
            Text = "Pick AniDB Cover Matches";
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(920, 560);
            MinimizeBox = false;
            MaximizeBox = false;

            rows = new BindingList<AniDbTitleCandidate>((candidates ?? Enumerable.Empty<AniDbTitleCandidate>()).ToList());

            grid = new DataGridView();
            grid.Dock = DockStyle.Fill;
            grid.AutoGenerateColumns = false;
            grid.AllowUserToAddRows = false;
            grid.AllowUserToDeleteRows = false;
            grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            grid.MultiSelect = true;
            grid.DataSource = rows;
            AddCheckColumn("Use", "Use", 48);
            AddTextColumn("QueryTitle", "Missing series", 200);
            AddTextColumn("Title", "AniDB match", 260);
            AddTextColumn("AniDbId", "AID", 70);
            AddTextColumn("TitleType", "Type", 80);
            AddTextColumn("Score", "Score", 60);
            AddTextColumn("TargetFolder", "Save folder", 280);

            var buttonPanel = new FlowLayoutPanel();
            buttonPanel.Dock = DockStyle.Bottom;
            buttonPanel.Height = 48;
            buttonPanel.FlowDirection = FlowDirection.RightToLeft;
            buttonPanel.Padding = new Padding(8);

            var okButton = new Button();
            okButton.Text = "Fetch Selected";
            okButton.Width = 112;
            okButton.Click += delegate
            {
                DialogResult = DialogResult.OK;
            };

            var cancelButton = new Button();
            cancelButton.Text = "Cancel";
            cancelButton.Width = 92;
            cancelButton.DialogResult = DialogResult.Cancel;

            var selectBestButton = new Button();
            selectBestButton.Text = "Select Best";
            selectBestButton.Width = 92;
            selectBestButton.Click += delegate
            {
                foreach (var group in rows.GroupBy(x => x.QueryTitle, StringComparer.OrdinalIgnoreCase))
                {
                    var best = group.OrderByDescending(x => x.Score).FirstOrDefault();
                    foreach (var row in group)
                    {
                        row.Use = ReferenceEquals(row, best);
                    }
                }

                grid.Refresh();
            };

            buttonPanel.Controls.Add(okButton);
            buttonPanel.Controls.Add(cancelButton);
            buttonPanel.Controls.Add(selectBestButton);
            Controls.Add(grid);
            Controls.Add(buttonPanel);
            AcceptButton = okButton;
            CancelButton = cancelButton;
        }

        public List<AniDbTitleCandidate> SelectedCandidates
        {
            get { return rows.Where(x => x.Use).ToList(); }
        }

        private void AddCheckColumn(string propertyName, string headerText, int width)
        {
            var column = new DataGridViewCheckBoxColumn();
            column.DataPropertyName = propertyName;
            column.HeaderText = headerText;
            column.Width = width;
            grid.Columns.Add(column);
        }

        private void AddTextColumn(string propertyName, string headerText, int width)
        {
            var column = new DataGridViewTextBoxColumn();
            column.DataPropertyName = propertyName;
            column.HeaderText = headerText;
            column.Width = width;
            column.ReadOnly = propertyName != "AniDbId";
            grid.Columns.Add(column);
        }
    }

    internal sealed class FileBotCommandSettings
    {
        public string FileBotPath { get; set; }
        public string Database { get; set; }
        public string Action { get; set; }
        public string Conflict { get; set; }
        public string Format { get; set; }
        public string OutputFolder { get; set; }
        public string Query { get; set; }
        public bool NonStrict { get; set; }
        public bool Recursive { get; set; }

        public static FileBotCommandSettings CreateDefault()
        {
            return new FileBotCommandSettings
            {
                FileBotPath = "filebot",
                Database = "AniDB",
                Action = "test",
                Conflict = "skip",
                Format = "{plex.id}",
                OutputFolder = "",
                Query = "",
                NonStrict = true,
                Recursive = false
            };
        }
    }

    internal sealed class FileBotCommandDialog : Form
    {
        private readonly TextBox pathBox;
        private readonly ComboBox databaseBox;
        private readonly ComboBox actionBox;
        private readonly ComboBox conflictBox;
        private readonly TextBox formatBox;
        private readonly TextBox outputBox;
        private readonly TextBox queryBox;
        private readonly CheckBox nonStrictBox;
        private readonly CheckBox recursiveBox;
        private readonly TextBox commandBox;
        private readonly List<string> selectedPaths;

        public FileBotCommandDialog(IEnumerable<string> paths)
        {
            selectedPaths = paths == null ? new List<string>() : paths.ToList();
            var settings = FileBotCommandSettings.CreateDefault();

            Text = "FileBot";
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ClientSize = new Size(720, 560);

            var layout = new TableLayoutPanel();
            layout.Dock = DockStyle.Fill;
            layout.Padding = new Padding(12);
            layout.ColumnCount = 3;
            layout.RowCount = 11;
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
            for (var i = 0; i < 8; i++)
            {
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            }
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));

            pathBox = new TextBox();
            pathBox.Text = settings.FileBotPath;
            pathBox.Dock = DockStyle.Fill;
            pathBox.TextChanged += delegate { UpdateCommandText(); };
            var browseButton = new Button();
            browseButton.Text = "Browse";
            browseButton.Dock = DockStyle.Fill;
            browseButton.Click += BrowseFileBot_Click;

            databaseBox = CreateCombo(new[] { "AniDB", "TheMovieDB::TV", "TheTVDB", "TheMovieDB", "OMDb", "file" }, settings.Database);
            actionBox = CreateCombo(new[] { "test", "move", "copy", "hardlink", "symlink" }, settings.Action);
            conflictBox = CreateCombo(new[] { "skip", "auto", "index", "fail", "replace" }, settings.Conflict);
            databaseBox.TextChanged += delegate { UpdateCommandText(); };
            actionBox.TextChanged += delegate { UpdateCommandText(); };
            conflictBox.TextChanged += delegate { UpdateCommandText(); };
            formatBox = CreateTextBox(settings.Format);
            outputBox = CreateTextBox(settings.OutputFolder);
            queryBox = CreateTextBox(settings.Query);

            nonStrictBox = new CheckBox();
            nonStrictBox.Text = "Use -non-strict matching";
            nonStrictBox.Checked = settings.NonStrict;
            nonStrictBox.Dock = DockStyle.Fill;
            nonStrictBox.CheckedChanged += delegate { UpdateCommandText(); };

            recursiveBox = new CheckBox();
            recursiveBox.Text = "Recursive";
            recursiveBox.Checked = settings.Recursive;
            recursiveBox.Dock = DockStyle.Fill;
            recursiveBox.CheckedChanged += delegate { UpdateCommandText(); };

            commandBox = new TextBox();
            commandBox.Multiline = true;
            commandBox.ReadOnly = true;
            commandBox.ScrollBars = ScrollBars.Vertical;
            commandBox.Dock = DockStyle.Fill;

            var buttonPanel = new FlowLayoutPanel();
            buttonPanel.FlowDirection = FlowDirection.RightToLeft;
            buttonPanel.Dock = DockStyle.Fill;
            buttonPanel.Padding = new Padding(0, 8, 0, 0);

            var runButton = new Button();
            runButton.Text = "Run";
            runButton.Width = 92;
            runButton.Click += delegate
            {
                DialogResult = DialogResult.OK;
            };

            var cancelButton = new Button();
            cancelButton.Text = "Cancel";
            cancelButton.Width = 92;
            cancelButton.DialogResult = DialogResult.Cancel;

            buttonPanel.Controls.Add(runButton);
            buttonPanel.Controls.Add(cancelButton);

            AddRow(layout, 0, "FileBot", pathBox, browseButton);
            AddRow(layout, 1, "Database", databaseBox, null);
            AddRow(layout, 2, "Action", actionBox, null);
            AddRow(layout, 3, "Conflict", conflictBox, null);
            AddRow(layout, 4, "Format", formatBox, null);
            AddRow(layout, 5, "Output", outputBox, null);
            AddRow(layout, 6, "Query", queryBox, null);
            layout.Controls.Add(nonStrictBox, 1, 7);
            layout.Controls.Add(recursiveBox, 2, 7);
            layout.Controls.Add(CreateLabel("Command"), 0, 8);
            layout.Controls.Add(commandBox, 1, 8);
            layout.SetColumnSpan(commandBox, 2);
            layout.Controls.Add(CreateLabel("Files"), 0, 9);
            var filesBox = new TextBox();
            filesBox.Multiline = true;
            filesBox.ReadOnly = true;
            filesBox.ScrollBars = ScrollBars.Vertical;
            filesBox.Dock = DockStyle.Fill;
            filesBox.Text = string.Join(Environment.NewLine, selectedPaths.ToArray());
            layout.Controls.Add(filesBox, 1, 9);
            layout.SetColumnSpan(filesBox, 2);
            layout.Controls.Add(buttonPanel, 0, 10);
            layout.SetColumnSpan(buttonPanel, 3);

            Controls.Add(layout);
            AcceptButton = runButton;
            CancelButton = cancelButton;
            UpdateCommandText();
        }

        public FileBotCommandSettings Settings
        {
            get
            {
                return new FileBotCommandSettings
                {
                    FileBotPath = pathBox.Text.Trim(),
                    Database = databaseBox.Text.Trim(),
                    Action = actionBox.Text.Trim(),
                    Conflict = conflictBox.Text.Trim(),
                    Format = formatBox.Text,
                    OutputFolder = outputBox.Text.Trim(),
                    Query = queryBox.Text.Trim(),
                    NonStrict = nonStrictBox.Checked,
                    Recursive = recursiveBox.Checked
                };
            }
        }

        public string CommandPreview
        {
            get { return commandBox.Text; }
        }

        private static ComboBox CreateCombo(IEnumerable<string> values, string selected)
        {
            var combo = new ComboBox();
            combo.DropDownStyle = ComboBoxStyle.DropDown;
            combo.Dock = DockStyle.Fill;
            foreach (var value in values)
            {
                combo.Items.Add(value);
            }
            combo.Text = selected;
            combo.TextChanged += delegate { };
            combo.SelectedIndexChanged += delegate { };
            return combo;
        }

        private TextBox CreateTextBox(string text)
        {
            var box = new TextBox();
            box.Text = text;
            box.Dock = DockStyle.Fill;
            box.TextChanged += delegate { UpdateCommandText(); };
            return box;
        }

        private static Label CreateLabel(string text)
        {
            var label = new Label();
            label.Text = text;
            label.Dock = DockStyle.Fill;
            label.TextAlign = ContentAlignment.MiddleLeft;
            return label;
        }

        private static void AddRow(TableLayoutPanel layout, int row, string label, Control input, Control extra)
        {
            layout.Controls.Add(CreateLabel(label), 0, row);
            layout.Controls.Add(input, 1, row);
            if (extra != null)
            {
                layout.Controls.Add(extra, 2, row);
            }
            else
            {
                layout.SetColumnSpan(input, 2);
            }
        }

        private void BrowseFileBot_Click(object sender, EventArgs e)
        {
            using (var dialog = new OpenFileDialog())
            {
                dialog.Title = "Select filebot.exe";
                dialog.Filter = "FileBot|filebot.exe|Applications|*.exe|All files|*.*";
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    pathBox.Text = dialog.FileName;
                }
            }
        }

        private void UpdateCommandText()
        {
            if (commandBox == null)
            {
                return;
            }

            commandBox.Text = MainForm.BuildFileBotCommandPreview(Settings, selectedPaths);
        }
    }

    internal static class ExplorerFolderDialog
    {
        private const uint FosPickFolders = 0x00000020;
        private const uint FosForceFileSystem = 0x00000040;
        private const uint FosNoChangeDir = 0x00000008;
        private const uint FosPathMustExist = 0x00000800;
        private const uint SigDnFileSysPath = 0x80058000;
        private const int HResultCanceled = unchecked((int)0x800704C7);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        private static extern void SHCreateItemFromParsingName(
            string pszPath,
            IntPtr pbc,
            [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
            out IShellItem ppv);

        public static bool TryShow(IWin32Window owner, string title, string selectedPath, out string folderPath)
        {
            folderPath = "";
            IFileDialog dialog = null;
            IShellItem result = null;
            try
            {
                dialog = (IFileDialog)new FileOpenDialog();
                uint options;
                dialog.GetOptions(out options);
                dialog.SetOptions(options | FosPickFolders | FosForceFileSystem | FosPathMustExist | FosNoChangeDir);
                if (!string.IsNullOrWhiteSpace(title))
                {
                    dialog.SetTitle(title);
                }

                if (!string.IsNullOrWhiteSpace(selectedPath) && Directory.Exists(selectedPath))
                {
                    IShellItem folder;
                    SHCreateItemFromParsingName(selectedPath, IntPtr.Zero, typeof(IShellItem).GUID, out folder);
                    dialog.SetFolder(folder);
                    Marshal.ReleaseComObject(folder);
                }

                var ownerHandle = owner == null ? IntPtr.Zero : owner.Handle;
                var hr = dialog.Show(ownerHandle);
                if (hr == HResultCanceled)
                {
                    return false;
                }
                if (hr < 0)
                {
                    Marshal.ThrowExceptionForHR(hr);
                }

                dialog.GetResult(out result);
                IntPtr pathPointer;
                result.GetDisplayName(SigDnFileSysPath, out pathPointer);
                folderPath = Marshal.PtrToStringUni(pathPointer);
                Marshal.FreeCoTaskMem(pathPointer);
                return !string.IsNullOrWhiteSpace(folderPath);
            }
            catch
            {
                return false;
            }
            finally
            {
                if (result != null)
                {
                    Marshal.ReleaseComObject(result);
                }
                if (dialog != null)
                {
                    Marshal.ReleaseComObject(dialog);
                }
            }
        }

        [ComImport]
        [Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7")]
        private class FileOpenDialog
        {
        }

        [ComImport]
        [Guid("42F85136-DB7E-439C-85F1-E4075D135FC8")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFileDialog
        {
            [PreserveSig]
            int Show(IntPtr parent);
            void SetFileTypes(uint cFileTypes, IntPtr rgFilterSpec);
            void SetFileTypeIndex(uint iFileType);
            void GetFileTypeIndex(out uint piFileType);
            void Advise(IntPtr pfde, out uint pdwCookie);
            void Unadvise(uint dwCookie);
            void SetOptions(uint fos);
            void GetOptions(out uint pfos);
            void SetDefaultFolder(IShellItem psi);
            void SetFolder(IShellItem psi);
            void GetFolder(out IShellItem ppsi);
            void GetCurrentSelection(out IShellItem ppsi);
            void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
            void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
            void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
            void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
            void GetResult(out IShellItem ppsi);
            void AddPlace(IShellItem psi, int fdap);
            void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
            void Close(int hr);
            void SetClientGuid(ref Guid guid);
            void ClearClientData();
            void SetFilter(IntPtr pFilter);
        }

        [ComImport]
        [Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem
        {
            void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
            void GetParent(out IShellItem ppsi);
            void GetDisplayName(uint sigdnName, out IntPtr ppszName);
            void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
            void Compare(IShellItem psi, uint hint, out int piOrder);
        }
    }

    internal sealed class ModernGroupBox : GroupBox
    {
        public Color BorderColor { get; set; }
        public Color HeaderBackColor { get; set; }
        public Color TitleColor { get; set; }

        public ModernGroupBox()
        {
            BorderColor = Color.FromArgb(64, 76, 88);
            HeaderBackColor = Color.FromArgb(20, 28, 33);
            TitleColor = Color.White;
            DoubleBuffered = true;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);
            var headerHeight = Math.Max(22, Font.Height + 8);
            var borderTop = headerHeight / 2;
            using (var borderPen = new Pen(BorderColor, 1F))
            {
                var border = new Rectangle(0, borderTop, Width - 1, Height - borderTop - 1);
                e.Graphics.DrawRectangle(borderPen, border);
            }

            var title = Text ?? string.Empty;
            if (title.Length == 0)
            {
                return;
            }

            var titleSize = TextRenderer.MeasureText(title, Font);
            var titleRect = new Rectangle(10, 0, Math.Min(Width - 20, titleSize.Width + 16), headerHeight);
            using (var backBrush = new SolidBrush(HeaderBackColor))
            {
                e.Graphics.FillRectangle(backBrush, titleRect);
            }

            var textRect = new Rectangle(titleRect.Left + 8, 0, titleRect.Width - 16, headerHeight);
            TextRenderer.DrawText(
                e.Graphics,
                title,
                Font,
                textRect,
                TitleColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
    }

    internal sealed class MainForm : Form
    {
        private const string AllSeriesTag = "__ALL_SERIES__";
        private const int MinimumAutomaticAniDbMatchScore = 85;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        private readonly Label rootBox;
        private readonly TextBox searchBox;
        private readonly Button topScanButton;
        private readonly Button deleteButton;
        private readonly ToolTip toolTip;
        private readonly MenuStrip mainMenu;
        private readonly ToolStripMenuItem fileBrowseMenuItem;
        private readonly ToolStripMenuItem fileLoadSavedMenuItem;
        private readonly ToolStripMenuItem fileExportMenuItem;
        private readonly ToolStripMenuItem viewColumnsMenuItem;
        private readonly ToolStripMenuItem viewCandidatesMenuItem;
        private readonly ToolStripMenuItem viewReadyMenuItem;
        private readonly ToolStripMenuItem viewMissingEpisodesMenuItem;
        private readonly ToolStripMenuItem viewEpisodeSearchMenuItem;
        private readonly ToolStripMenuItem viewSelectedFeedMenuItem;
        private readonly ToolStripMenuItem viewRestoreWorkspaceMenuItem;
        private readonly ToolStripMenuItem viewSeriesCoversMenuItem;
        private readonly ToolStripMenuItem viewDarkModeMenuItem;
        private readonly ToolStripMenuItem toolsClearMarksMenuItem;
        private readonly ToolStripMenuItem toolsAniDbMenuItem;
        private readonly ToolStripMenuItem toolsPreviewActionsMenuItem;
        private readonly ToolStripMenuItem toolsAutoMarkMenuItem;
        private readonly ToolStripMenuItem toolsAutoMarkLevelMenuItem;
        private readonly ToolStripMenuItem toolsAutoMarkHighMenuItem;
        private readonly ToolStripMenuItem toolsAutoMarkMediumMenuItem;
        private readonly ToolStripMenuItem toolsAutoMarkLowMenuItem;
        private readonly ToolStripMenuItem toolsMoveToNameFoldersMenuItem;
        private readonly ToolStripMenuItem toolsFileBotMenuItem;
        private readonly ToolStripMenuItem toolsFileFormatsMenuItem;
        private readonly ToolStripMenuItem toolsOpenMoveReportMenuItem;
        private readonly ToolStripMenuItem toolsMonitorFoldersMenuItem;
        private readonly ToolStripMenuItem toolsOpenDiagnosticLogMenuItem;
        private readonly ToolStripMenuItem toolsCopyDiagnosticLogMenuItem;
        private readonly ToolStripMenuItem helpGuideMenuItem;
        private readonly ToolStripMenuItem helpCredentialMenuItem;
        private readonly Label statusLabel;
        private readonly TextBox activityLogBox;
        private readonly ProgressBar progressBar;
        private readonly Panel busyNoticePanel;
        private readonly Label busyNoticeTitleLabel;
        private readonly Label busyNoticeStatusLabel;
        private readonly Button busyNoticeCancelButton;
        private readonly System.Windows.Forms.Timer shellSeriesFetchDebounceTimer;
        private readonly System.Windows.Forms.Timer monitorTimer;
        private readonly Label scannedChipLabel;
        private readonly Label candidatesChipLabel;
        private readonly Label visibleChipLabel;
        private readonly Label deletionChipLabel;
        private readonly Label duplicateChipLabel;
        private readonly Label locationChipLabel;
        private readonly Label filterChipLabel;
        private readonly Label cacheChipLabel;
        private readonly Label providerChipLabel;
        private readonly TabControl reviewTabs;
        private readonly ListView seriesListView;
        private readonly ListView seriesCoverView;
        private readonly ImageList seriesCoverImages;
        private readonly DataGridView grid;
        private readonly DataGridView deletionGrid;
        private readonly DataGridView missingEpisodesGrid;
        private readonly DataGridView episodeSearchGrid;
        private readonly DataGridView selectedFeedGrid;
        private readonly ComboBox episodeSearchGroupBox;
        private readonly ComboBox episodeSearchResolutionBox;
        private readonly Button episodeSearchButton;
        private readonly Button episodeSearchAllButton;
        private readonly Button episodeSearchAddButton;
        private readonly Button selectedFeedOpenButton;
        private readonly Button selectedFeedCopyButton;
        private readonly Button selectedFeedRemoveButton;
        private readonly Button selectedFeedClearButton;
        private readonly LinkLabel detailsBox;
        private readonly GroupBox metadataGroup;
        private readonly Label metadataBox;
        private readonly ContextMenuStrip candidateContextMenu;
        private readonly ToolStripMenuItem openCandidateFileItem;
        private readonly ToolStripMenuItem openCandidateFolderItem;
        private readonly ToolStripMenuItem moveCandidateToNameFoldersItem;
        private readonly ToolStripMenuItem fileBotCandidateItem;
        private readonly ToolStripMenuItem previewCandidateActionsItem;
        private readonly Label candidateTotalLabel;
        private readonly Label deletionTotalLabel;
        private readonly Label missingEpisodesTotalLabel;
        private readonly Label episodeSearchTotalLabel;
        private readonly Label selectedFeedTotalLabel;
        private readonly PictureBox shellSeriesCoverBox;
        private readonly Label shellSeriesTitleLabel;
        private readonly Label shellSeriesMetaLabel;
        private readonly Label shellScannedStatLabel;
        private readonly Label shellDuplicateStatLabel;
        private readonly Label shellMissingStatLabel;
        private readonly Label shellAniDbBadgeLabel;
        private readonly Label shellTvDbBadgeLabel;
        private readonly Label shellTmDbBadgeLabel;
        private readonly Label shellInspectorTitleLabel;
        private readonly Label inspectorPreviewLabel;
        private readonly Label inspectorActionsLabel;
        private readonly PictureBox inspectorPreviewBox;
        private readonly Button inspectorKeepButton;
        private readonly Button inspectorDeleteButton;
        private readonly Button inspectorIgnoreButton;
        private readonly Button inspectorOpenFolderButton;
        private readonly Button navLibraryButton;
        private readonly Button navDuplicatesButton;
        private readonly Button navMissingEpisodesButton;
        private readonly Button navEpisodeSearchButton;
        private readonly Button navSelectedRssButton;
        private readonly Button navActivityButton;
        private readonly Button navSettingsButton;
        private readonly GroupBox settingsGroup;
        private readonly Label settingsSummaryLabel;
        private readonly Button settingsMetadataButton;
        private readonly Button settingsFileFormatsButton;
        private readonly Button settingsMonitorButton;
        private readonly Button settingsThemeButton;
        private readonly Button settingsFileBotButton;
        private readonly TableLayoutPanel workspacePanel;
        private readonly TableLayoutPanel workflowPanel;
        private readonly SplitContainer duplicateWorkflowSplit;
        private readonly GroupBox seriesGroup;
        private readonly GroupBox activityGroup;
        private readonly GroupBox detailsGroup;
        private readonly GroupBox candidatesGroup;
        private readonly GroupBox deletionGroup;
        private readonly GroupBox missingEpisodesGroup;
        private readonly GroupBox episodeSearchGroup;
        private readonly GroupBox selectedFeedGroup;
        private readonly Button candidatesCloseButton;
        private readonly Button deletionCloseButton;
        private readonly Button missingEpisodesCloseButton;
        private readonly Button episodeSearchCloseButton;
        private readonly Button selectedFeedCloseButton;
        private readonly List<EpisodeFile> allRows;
        private readonly List<EpisodeFile> allScannedRows;
        private readonly BindingList<EpisodeFile> rows;
        private readonly BindingList<EpisodeFile> deletionRows;
        private readonly BindingList<MissingEpisodeRow> missingEpisodeRows;
        private readonly BindingList<EpisodeSearchResult> episodeSearchRows;
        private readonly BindingList<SelectedSearchFeedItem> selectedFeedRows;
        private readonly BindingSource source;
        private readonly BindingSource deletionSource;
        private readonly BindingSource missingEpisodesSource;
        private readonly BindingSource episodeSearchSource;
        private readonly BindingSource selectedFeedSource;
        private FileFormatFilter fileFormatFilter;
        private AutoMarkThreshold autoMarkThreshold;
        private DataGridView activeGrid;
        private object activeSeriesTag;
        private string activeReviewFilter;
        private string activeSearchText;
        private bool busyState;
        private volatile bool cancelRequested;
        private bool showSeriesCovers;
        private bool darkMode;
        private bool candidatesPanelCollapsed;
        private bool deletionPanelCollapsed;
        private bool missingEpisodesPanelCollapsed;
        private bool episodeSearchPanelCollapsed;
        private bool selectedFeedPanelCollapsed;
        private string activeShellSection;
        private bool restoringColumnLayout;
        private readonly object shellCoverFetchLock;
        private readonly HashSet<string> shellCoverFetchAttempted;
        private readonly HashSet<string> shellCoverFetchInProgress;
        private readonly Dictionary<string, string> seriesCoverPathCache;
        private DateTime lastShellCoverFetchUtc;
        private string pendingShellFetchTitle;
        private List<EpisodeFile> pendingShellFetchRows;
        private DateTime nextMonitorCheckUtc;
        private TcpListener selectedFeedServer;
        private Thread selectedFeedServerThread;
        private volatile bool selectedFeedServerRunning;
        private string selectedFeedUrl;

        public MainForm()
        {
            Text = "Duplikates - Same Episode Duplicate Finder";
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(1100, 700);
            Size = Screen.PrimaryScreen.WorkingArea.Size;
            WindowState = FormWindowState.Maximized;
            Font = new Font("Segoe UI", 9F);
            Icon = CreateAppIcon();

            allRows = new List<EpisodeFile>();
            allScannedRows = new List<EpisodeFile>();
            rows = new BindingList<EpisodeFile>();
            deletionRows = new BindingList<EpisodeFile>();
            missingEpisodeRows = new BindingList<MissingEpisodeRow>();
            episodeSearchRows = new BindingList<EpisodeSearchResult>();
            selectedFeedRows = new BindingList<SelectedSearchFeedItem>();
            source = new BindingSource();
            source.DataSource = rows;
            deletionSource = new BindingSource();
            deletionSource.DataSource = deletionRows;
            missingEpisodesSource = new BindingSource();
            missingEpisodesSource.DataSource = missingEpisodeRows;
            episodeSearchSource = new BindingSource();
            episodeSearchSource.DataSource = episodeSearchRows;
            selectedFeedSource = new BindingSource();
            selectedFeedSource.DataSource = selectedFeedRows;
            shellCoverFetchLock = new object();
            shellCoverFetchAttempted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            shellCoverFetchInProgress = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            seriesCoverPathCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            lastShellCoverFetchUtc = DateTime.MinValue;
            pendingShellFetchTitle = "";
            pendingShellFetchRows = new List<EpisodeFile>();
            shellSeriesFetchDebounceTimer = new System.Windows.Forms.Timer();
            shellSeriesFetchDebounceTimer.Interval = 900;
            shellSeriesFetchDebounceTimer.Tick += ShellSeriesFetchDebounceTimer_Tick;
            monitorTimer = new System.Windows.Forms.Timer();
            monitorTimer.Interval = 60000;
            monitorTimer.Tick += MonitorTimer_Tick;
            fileFormatFilter = FileFormatFilterStore.Load();
            autoMarkThreshold = AutoMarkThresholdStore.Load();
            var uiSettings = UiLayoutSettingsStore.Load();
            activeReviewFilter = "All";
            activeSearchText = "";
            activeShellSection = "Duplicates";
            darkMode = uiSettings.DarkMode;
            showSeriesCovers = false;
            candidatesPanelCollapsed = uiSettings.CandidatesPanelCollapsed;
            deletionPanelCollapsed = uiSettings.DeletionPanelCollapsed;
            missingEpisodesPanelCollapsed = uiSettings.MissingEpisodesPanelCollapsed;
            episodeSearchPanelCollapsed = uiSettings.EpisodeSearchPanelCollapsed;
            selectedFeedPanelCollapsed = uiSettings.SelectedFeedPanelCollapsed;
            BackColor = AppBackColor;
            toolTip = new ToolTip();
            toolTip.AutoPopDelay = 9000;
            toolTip.InitialDelay = 450;
            toolTip.ReshowDelay = 100;
            toolTip.ShowAlways = true;

            mainMenu = new MenuStrip();
            mainMenu.Dock = DockStyle.Top;
            mainMenu.BackColor = PanelBackColor;
            mainMenu.ForeColor = PrimaryTextColor;
            mainMenu.Padding = new Padding(8, 4, 0, 4);

            var fileMenu = new ToolStripMenuItem("File");
            fileBrowseMenuItem = new ToolStripMenuItem("Scan...");
            fileBrowseMenuItem.ToolTipText = "Choose one or more folders and scan them as one combined session.";
            fileBrowseMenuItem.Click += BrowseButton_Click;
            fileLoadSavedMenuItem = new ToolStripMenuItem("Open Last Session");
            fileLoadSavedMenuItem.ToolTipText = "Open the cached results from the last scanned session.";
            fileLoadSavedMenuItem.Enabled = File.Exists(GetCachePath());
            fileLoadSavedMenuItem.Click += LoadSavedButton_Click;
            fileExportMenuItem = new ToolStripMenuItem("Export CSV...");
            fileExportMenuItem.ToolTipText = "Export the current candidate list to a CSV file.";
            fileExportMenuItem.Enabled = false;
            fileExportMenuItem.Click += ExportButton_Click;
            fileMenu.DropDownItems.Add(fileBrowseMenuItem);
            fileMenu.DropDownItems.Add(fileLoadSavedMenuItem);
            fileMenu.DropDownItems.Add(new ToolStripSeparator());
            fileMenu.DropDownItems.Add(fileExportMenuItem);

            var viewMenu = new ToolStripMenuItem("View");
            viewColumnsMenuItem = new ToolStripMenuItem("Columns...");
            viewColumnsMenuItem.ToolTipText = "Choose which candidate columns are visible.";
            viewColumnsMenuItem.Click += ColumnsButton_Click;
            viewCandidatesMenuItem = new ToolStripMenuItem("Hide Candidates");
            viewCandidatesMenuItem.ToolTipText = "Show or hide the Candidates panel.";
            viewCandidatesMenuItem.Click += ToggleCandidatesButton_Click;
            viewReadyMenuItem = new ToolStripMenuItem("Hide Ready to Remove");
            viewReadyMenuItem.ToolTipText = "Show or hide the Ready to Remove panel.";
            viewReadyMenuItem.Click += ToggleReadyButton_Click;
            viewMissingEpisodesMenuItem = new ToolStripMenuItem("Hide Missing Episodes");
            viewMissingEpisodesMenuItem.ToolTipText = "Show or hide the Missing Episodes panel.";
            viewMissingEpisodesMenuItem.Click += ToggleMissingEpisodesButton_Click;
            viewEpisodeSearchMenuItem = new ToolStripMenuItem("Hide Episode Search");
            viewEpisodeSearchMenuItem.ToolTipText = "Show or hide the Episode Search panel.";
            viewEpisodeSearchMenuItem.Click += ToggleEpisodeSearchButton_Click;
            viewSelectedFeedMenuItem = new ToolStripMenuItem("Hide Selected Feed");
            viewSelectedFeedMenuItem.ToolTipText = "Show or hide the selected RSS feed panel.";
            viewSelectedFeedMenuItem.Click += ToggleSelectedFeedButton_Click;
            viewRestoreWorkspaceMenuItem = new ToolStripMenuItem("Restore Workspace");
            viewRestoreWorkspaceMenuItem.ToolTipText = "Show the default review panels again.";
            viewRestoreWorkspaceMenuItem.Click += RestoreWorkspaceMenuItem_Click;
            viewSeriesCoversMenuItem = new ToolStripMenuItem("Series Covers");
            viewSeriesCoversMenuItem.ToolTipText = "Series rail cover tiles are disabled so selection stays fast. Covers load in the main workflow.";
            viewSeriesCoversMenuItem.CheckOnClick = true;
            viewSeriesCoversMenuItem.Checked = showSeriesCovers;
            viewSeriesCoversMenuItem.Enabled = false;
            viewSeriesCoversMenuItem.Click += ToggleSeriesCoversMenuItem_Click;
            viewDarkModeMenuItem = new ToolStripMenuItem("Dark Mode");
            viewDarkModeMenuItem.ToolTipText = "Toggle the application between dark and light mode.";
            viewDarkModeMenuItem.CheckOnClick = true;
            viewDarkModeMenuItem.Checked = darkMode;
            viewDarkModeMenuItem.Click += ToggleDarkModeMenuItem_Click;
            viewMenu.DropDownItems.Add(viewColumnsMenuItem);
            viewMenu.DropDownItems.Add(viewCandidatesMenuItem);
            viewMenu.DropDownItems.Add(viewReadyMenuItem);
            viewMenu.DropDownItems.Add(viewMissingEpisodesMenuItem);
            viewMenu.DropDownItems.Add(viewEpisodeSearchMenuItem);
            viewMenu.DropDownItems.Add(viewSelectedFeedMenuItem);
            viewMenu.DropDownItems.Add(viewRestoreWorkspaceMenuItem);
            viewMenu.DropDownItems.Add(viewSeriesCoversMenuItem);
            viewMenu.DropDownItems.Add(new ToolStripSeparator());
            viewMenu.DropDownItems.Add(viewDarkModeMenuItem);
            
            var toolsMenu = new ToolStripMenuItem("Tools");
            toolsClearMarksMenuItem = new ToolStripMenuItem("Clear Marks");
            toolsClearMarksMenuItem.ToolTipText = "Remove all current deletion marks without changing files on disk.";
            toolsClearMarksMenuItem.Enabled = false;
            toolsClearMarksMenuItem.Click += ClearMarksButton_Click;
            toolsAniDbMenuItem = new ToolStripMenuItem("Update Metadata && Covers...");
            toolsAniDbMenuItem.ToolTipText = "Choose metadata lookup, missing cover fetch, or both. AniDB uses the HTTP XML client; TVDB and TMDB are fallbacks where configured.";
            toolsAniDbMenuItem.Click += AniDbButton_Click;
            toolsPreviewActionsMenuItem = new ToolStripMenuItem("Review Suggested Actions...");
            toolsPreviewActionsMenuItem.ToolTipText = "Preview recommended actions before you mark or move anything.";
            toolsPreviewActionsMenuItem.Enabled = false;
            toolsPreviewActionsMenuItem.Click += PreviewBatchActionsMenuItem_Click;
            toolsAutoMarkMenuItem = new ToolStripMenuItem("Auto Mark");
            toolsAutoMarkMenuItem.ToolTipText = "Mark the app's recommended delete candidates for later review.";
            toolsAutoMarkMenuItem.Enabled = false;
            toolsAutoMarkMenuItem.Click += AutoMarkButton_Click;
            toolsAutoMarkLevelMenuItem = new ToolStripMenuItem("Auto Mark Level");
            toolsAutoMarkLevelMenuItem.ToolTipText = "Choose how aggressive Auto Mark and Review Suggested Actions are.";
            toolsAutoMarkHighMenuItem = new ToolStripMenuItem("High only");
            toolsAutoMarkHighMenuItem.ToolTipText = "Mark only high-confidence delete recommendations.";
            toolsAutoMarkHighMenuItem.Tag = AutoMarkThreshold.High;
            toolsAutoMarkHighMenuItem.Click += AutoMarkThresholdMenuItem_Click;
            toolsAutoMarkMediumMenuItem = new ToolStripMenuItem("Medium and high");
            toolsAutoMarkMediumMenuItem.ToolTipText = "Mark medium- and high-confidence delete recommendations.";
            toolsAutoMarkMediumMenuItem.Tag = AutoMarkThreshold.Medium;
            toolsAutoMarkMediumMenuItem.Click += AutoMarkThresholdMenuItem_Click;
            toolsAutoMarkLowMenuItem = new ToolStripMenuItem("Low, medium, and high");
            toolsAutoMarkLowMenuItem.ToolTipText = "Mark every delete recommendation, including low-confidence ties.";
            toolsAutoMarkLowMenuItem.Tag = AutoMarkThreshold.Low;
            toolsAutoMarkLowMenuItem.Click += AutoMarkThresholdMenuItem_Click;
            toolsAutoMarkLevelMenuItem.DropDownItems.Add(toolsAutoMarkHighMenuItem);
            toolsAutoMarkLevelMenuItem.DropDownItems.Add(toolsAutoMarkMediumMenuItem);
            toolsAutoMarkLevelMenuItem.DropDownItems.Add(toolsAutoMarkLowMenuItem);
            toolsMoveToNameFoldersMenuItem = new ToolStripMenuItem("Move Selected Series to Folder...");
            toolsMoveToNameFoldersMenuItem.ToolTipText = "Move every scanned file from the selected series into one series-named folder.";
            toolsMoveToNameFoldersMenuItem.Enabled = false;
            toolsMoveToNameFoldersMenuItem.Click += MoveSelectedToNameFoldersMenuItem_Click;
            toolsFileBotMenuItem = new ToolStripMenuItem("FileBot...");
            toolsFileBotMenuItem.ToolTipText = "Send selected files to FileBot with a preview of the command.";
            toolsFileBotMenuItem.Enabled = false;
            toolsFileBotMenuItem.Click += FileBotMenuItem_Click;
            toolsFileFormatsMenuItem = new ToolStripMenuItem("File Formats...");
            toolsFileFormatsMenuItem.ToolTipText = "Choose which file extensions are included or ignored during scans.";
            toolsFileFormatsMenuItem.Click += FileFormatsMenuItem_Click;
            toolsOpenMoveReportMenuItem = new ToolStripMenuItem("Open Last Move Report");
            toolsOpenMoveReportMenuItem.ToolTipText = "Open the CSV report from the last Move Selected Series run.";
            toolsOpenMoveReportMenuItem.Enabled = File.Exists(GetMoveReportPath());
            toolsOpenMoveReportMenuItem.Click += OpenMoveReportMenuItem_Click;
            toolsMonitorFoldersMenuItem = new ToolStripMenuItem("Monitor Scan Roots Every 5 Days");
            toolsMonitorFoldersMenuItem.ToolTipText = "While the app is running, periodically re-scan current roots and refresh missing episodes.";
            toolsMonitorFoldersMenuItem.CheckOnClick = true;
            toolsMonitorFoldersMenuItem.Click += MonitorFoldersMenuItem_Click;
            toolsOpenDiagnosticLogMenuItem = new ToolStripMenuItem("Open Diagnostic Log");
            toolsOpenDiagnosticLogMenuItem.ToolTipText = "Open the live tester log with scans, button actions, provider requests, cover pulls, and errors.";
            toolsOpenDiagnosticLogMenuItem.Click += OpenDiagnosticLogMenuItem_Click;
            toolsCopyDiagnosticLogMenuItem = new ToolStripMenuItem("Copy Diagnostic Log Path");
            toolsCopyDiagnosticLogMenuItem.ToolTipText = "Copy the live tester log path so it can be shared for troubleshooting.";
            toolsCopyDiagnosticLogMenuItem.Click += CopyDiagnosticLogMenuItem_Click;
            toolsMenu.DropDownItems.Add(toolsClearMarksMenuItem);
            toolsMenu.DropDownItems.Add(toolsAniDbMenuItem);
            toolsMenu.DropDownItems.Add(new ToolStripSeparator());
            toolsMenu.DropDownItems.Add(toolsPreviewActionsMenuItem);
            toolsMenu.DropDownItems.Add(new ToolStripSeparator());
            toolsMenu.DropDownItems.Add(toolsAutoMarkMenuItem);
            toolsMenu.DropDownItems.Add(toolsAutoMarkLevelMenuItem);
            toolsMenu.DropDownItems.Add(toolsMoveToNameFoldersMenuItem);
            toolsMenu.DropDownItems.Add(toolsFileBotMenuItem);
            toolsMenu.DropDownItems.Add(new ToolStripSeparator());
            toolsMenu.DropDownItems.Add(toolsFileFormatsMenuItem);
            toolsMenu.DropDownItems.Add(toolsMonitorFoldersMenuItem);
            toolsMenu.DropDownItems.Add(toolsOpenMoveReportMenuItem);
            toolsMenu.DropDownItems.Add(new ToolStripSeparator());
            toolsMenu.DropDownItems.Add(toolsOpenDiagnosticLogMenuItem);
            toolsMenu.DropDownItems.Add(toolsCopyDiagnosticLogMenuItem);
            UpdateAutoMarkThresholdUi();

            var helpMenu = new ToolStripMenuItem("Help");
            helpGuideMenuItem = new ToolStripMenuItem("Beta Guide");
            helpGuideMenuItem.ToolTipText = "Show the safety notes and recommended first-run workflow.";
            helpGuideMenuItem.Click += HelpGuideMenuItem_Click;
            helpCredentialMenuItem = new ToolStripMenuItem("Metadata Providers");
            helpCredentialMenuItem.ToolTipText = "Show provider storage, client, and attribution details.";
            helpCredentialMenuItem.Click += HelpCredentialMenuItem_Click;
            helpMenu.DropDownItems.Add(helpGuideMenuItem);
            helpMenu.DropDownItems.Add(helpCredentialMenuItem);

            mainMenu.Items.Add(fileMenu);
            mainMenu.Items.Add(viewMenu);
            mainMenu.Items.Add(toolsMenu);
            mainMenu.Items.Add(helpMenu);
            MainMenuStrip = mainMenu;

            var topPanel = new TableLayoutPanel();
            topPanel.Dock = DockStyle.Top;
            topPanel.Height = 96;
            topPanel.Padding = new Padding(14, 10, 14, 8);
            topPanel.BackColor = PanelBackColor;
            topPanel.Tag = "CommandBar";
            topPanel.ColumnCount = 1;
            topPanel.RowCount = 3;
            topPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            topPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            topPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            topPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 6));

            var rootLabel = new Label();
            rootLabel.Text = "Location";
            rootLabel.TextAlign = ContentAlignment.MiddleLeft;
            rootLabel.Dock = DockStyle.Fill;

            rootBox = new Label();
            rootBox.Text = "No folder scanned";
            rootBox.Dock = DockStyle.Fill;
            rootBox.AutoEllipsis = true;
            rootBox.TextAlign = ContentAlignment.MiddleLeft;
            rootBox.BorderStyle = BorderStyle.None;
            rootBox.Padding = new Padding(10, 0, 10, 0);
            rootBox.Margin = new Padding(0, 4, 8, 4);
            rootBox.TextChanged += RootBox_TextChanged;
            toolTip.SetToolTip(rootBox, "Scanned folder list. Use File > Scan to choose one or more folders.");
            rootBox.Tag = new List<string>();

            searchBox = new TextBox();
            searchBox.Dock = DockStyle.Fill;
            searchBox.BorderStyle = BorderStyle.FixedSingle;
            searchBox.Margin = new Padding(0, 3, 0, 3);
            searchBox.TextChanged += SearchBox_TextChanged;
            toolTip.SetToolTip(searchBox, "Filter the Series panel and visible rows by series name.");

            deleteButton = new Button();
            deleteButton.Text = "Delete";
            deleteButton.Dock = DockStyle.Fill;
            deleteButton.MinimumSize = new Size(0, 28);
            deleteButton.Enabled = false;
            deleteButton.Click += DeleteButton_Click;
            StyleDeleteButton(deleteButton);
            toolTip.SetToolTip(deleteButton, "Move Ready to Remove files to the Recycle Bin.");

            statusLabel = new Label();
            statusLabel.Text = "Choose a folder, then scan. Deletion sends files to the Recycle Bin.";
            statusLabel.TextAlign = ContentAlignment.MiddleLeft;
            statusLabel.AutoSize = false;
            statusLabel.AutoEllipsis = true;
            statusLabel.Dock = DockStyle.Fill;
            statusLabel.Padding = new Padding(6, 2, 6, 2);
            statusLabel.ForeColor = SecondaryTextColor;

            progressBar = new ProgressBar();
            progressBar.Dock = DockStyle.Fill;
            progressBar.Style = ProgressBarStyle.Blocks;
            progressBar.Visible = false;

            scannedChipLabel = CreateChipLabel();
            candidatesChipLabel = CreateChipLabel();
            visibleChipLabel = CreateChipLabel();
            deletionChipLabel = CreateChipLabel();
            duplicateChipLabel = CreateChipLabel();
            locationChipLabel = CreateChipLabel();
            filterChipLabel = CreateChipLabel();
            cacheChipLabel = CreateChipLabel();
            providerChipLabel = CreateChipLabel();

            activityLogBox = new TextBox();
            activityLogBox.Dock = DockStyle.Fill;
            activityLogBox.Multiline = true;
            activityLogBox.ReadOnly = true;
            activityLogBox.BorderStyle = BorderStyle.None;
            activityLogBox.ScrollBars = ScrollBars.Vertical;
            activityLogBox.WordWrap = true;

            var dashboardInputs = new TableLayoutPanel();
            dashboardInputs.Dock = DockStyle.Fill;
            dashboardInputs.ColumnCount = 3;
            dashboardInputs.RowCount = 1;
            dashboardInputs.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72));
            dashboardInputs.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            dashboardInputs.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 116));
            dashboardInputs.Controls.Add(rootLabel, 0, 0);
            dashboardInputs.Controls.Add(rootBox, 1, 0);

            topScanButton = new Button();
            topScanButton.Text = "Scan";
            topScanButton.Dock = DockStyle.Fill;
            topScanButton.Margin = new Padding(8, 3, 0, 3);
            topScanButton.Click += BrowseButton_Click;
            StyleButton(topScanButton, true);
            toolTip.SetToolTip(topScanButton, "Choose one or more folders and scan them as one combined session.");
            dashboardInputs.Controls.Add(topScanButton, 2, 0);

            topPanel.Controls.Add(dashboardInputs, 0, 0);
            topPanel.Controls.Add(statusLabel, 0, 1);
            topPanel.Controls.Add(progressBar, 0, 2);

            busyNoticePanel = new Panel();
            busyNoticePanel.Dock = DockStyle.Top;
            busyNoticePanel.Height = 74;
            busyNoticePanel.Padding = new Padding(12, 8, 12, 8);
            busyNoticePanel.Visible = false;

            var busyNoticeLayout = new TableLayoutPanel();
            busyNoticeLayout.Dock = DockStyle.Fill;
            busyNoticeLayout.ColumnCount = 2;
            busyNoticeLayout.RowCount = 2;
            busyNoticeLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            busyNoticeLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150F));
            busyNoticeLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24F));
            busyNoticeLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            busyNoticeTitleLabel = new Label();
            busyNoticeTitleLabel.Dock = DockStyle.Fill;
            busyNoticeTitleLabel.AutoEllipsis = true;
            busyNoticeTitleLabel.Font = new Font(Font, FontStyle.Bold);
            busyNoticeTitleLabel.Text = "Application busy";
            busyNoticeTitleLabel.TextAlign = ContentAlignment.MiddleLeft;

            busyNoticeStatusLabel = new Label();
            busyNoticeStatusLabel.Dock = DockStyle.Fill;
            busyNoticeStatusLabel.AutoEllipsis = true;
            busyNoticeStatusLabel.Text = "Working in the background. The window may refresh slowly during large folders or slow provider connections.";
            busyNoticeStatusLabel.TextAlign = ContentAlignment.MiddleLeft;

            busyNoticeCancelButton = new Button();
            busyNoticeCancelButton.Text = "Stop Current Task";
            busyNoticeCancelButton.Dock = DockStyle.Fill;
            busyNoticeCancelButton.Click += CancelButton_Click;
            StyleButton(busyNoticeCancelButton, false);
            toolTip.SetToolTip(busyNoticeCancelButton, "Stop the scan, lookup, move, or other long-running task at the next safe step.");

            busyNoticeLayout.Controls.Add(busyNoticeTitleLabel, 0, 0);
            busyNoticeLayout.SetColumnSpan(busyNoticeTitleLabel, 2);
            busyNoticeLayout.Controls.Add(busyNoticeStatusLabel, 0, 1);
            busyNoticeLayout.Controls.Add(busyNoticeCancelButton, 1, 1);
            busyNoticePanel.Controls.Add(busyNoticeLayout);

            seriesListView = new ListView();
            seriesListView.Dock = DockStyle.Fill;
            seriesListView.View = View.Details;
            seriesListView.FullRowSelect = true;
            seriesListView.HideSelection = false;
            seriesListView.MultiSelect = false;
            seriesListView.AllowColumnReorder = true;
            seriesListView.BorderStyle = BorderStyle.None;
            seriesListView.HeaderStyle = ColumnHeaderStyle.Nonclickable;
            seriesListView.ShowItemToolTips = true;
            seriesListView.Columns.Add("Series", 240);
            seriesListView.Columns.Add("Files", 210);
            seriesListView.Columns.Add("Size", 72);
            seriesListView.ItemSelectionChanged += SeriesListView_ItemSelectionChanged;

            seriesCoverImages = new ImageList();
            seriesCoverImages.ColorDepth = ColorDepth.Depth32Bit;
            seriesCoverImages.ImageSize = new Size(112, 160);

            seriesCoverView = new ListView();
            seriesCoverView.Dock = DockStyle.Fill;
            seriesCoverView.View = View.LargeIcon;
            seriesCoverView.LargeImageList = seriesCoverImages;
            seriesCoverView.HideSelection = false;
            seriesCoverView.MultiSelect = false;
            seriesCoverView.BorderStyle = BorderStyle.None;
            seriesCoverView.Alignment = ListViewAlignment.Top;
            seriesCoverView.LabelWrap = true;
            seriesCoverView.Activation = ItemActivation.OneClick;
            seriesCoverView.ItemSelectionChanged += SeriesCoverView_ItemSelectionChanged;

            grid = new DataGridView();
            grid.Dock = DockStyle.Fill;
            grid.AutoGenerateColumns = false;
            grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.AllCells;
            grid.AllowUserToAddRows = false;
            grid.AllowUserToDeleteRows = false;
            grid.AllowUserToOrderColumns = true;
            grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            grid.MultiSelect = true;
            grid.DataSource = source;
            grid.CellFormatting += Grid_CellFormatting;
            grid.CellValueChanged += Grid_CellValueChanged;
            grid.CurrentCellDirtyStateChanged += Grid_CurrentCellDirtyStateChanged;
            grid.KeyDown += Grid_KeyDown;
            grid.SelectionChanged += Grid_SelectionChanged;
            grid.CellMouseDown += Grid_CellMouseDown;
            StyleGrid(grid);
            activeGrid = grid;

            reviewTabs = new TabControl();
            reviewTabs.Dock = DockStyle.Fill;
            reviewTabs.Appearance = TabAppearance.Normal;
            AddReviewTab("All", "All");
            AddReviewTab("Delete Recs", "Delete");
            AddReviewTab("Auto High", "AutoHigh");
            AddReviewTab("Auto Medium", "AutoMedium");
            AddReviewTab("Auto Low", "AutoLow");
            AddReviewTab("Needs Review", "NeedsReview");
            AddReviewTab("No Cover", "MissingCover");
            AddReviewTab("Marked", "Marked");
            reviewTabs.SelectedIndexChanged += ReviewTabs_SelectedIndexChanged;

            candidateContextMenu = new ContextMenuStrip();
            openCandidateFileItem = new ToolStripMenuItem("Open file");
            openCandidateFileItem.ToolTipText = "Open the selected media file with the default Windows app.";
            openCandidateFileItem.Click += OpenCandidateFileItem_Click;
            openCandidateFolderItem = new ToolStripMenuItem("Open local folder");
            openCandidateFolderItem.ToolTipText = "Open the selected file's folder in File Explorer.";
            openCandidateFolderItem.Click += OpenCandidateFolderItem_Click;
            moveCandidateToNameFoldersItem = new ToolStripMenuItem("Move selected series to folder...");
            moveCandidateToNameFoldersItem.ToolTipText = "Move every scanned file from the selected series into one series-named folder.";
            moveCandidateToNameFoldersItem.Click += MoveSelectedToNameFoldersMenuItem_Click;
            fileBotCandidateItem = new ToolStripMenuItem("FileBot...");
            fileBotCandidateItem.ToolTipText = "Run FileBot on the selected files.";
            fileBotCandidateItem.Click += FileBotMenuItem_Click;
            previewCandidateActionsItem = new ToolStripMenuItem("Review suggested actions...");
            previewCandidateActionsItem.ToolTipText = "Preview recommendations for the current candidate set.";
            previewCandidateActionsItem.Click += PreviewBatchActionsMenuItem_Click;
            candidateContextMenu.Items.Add(openCandidateFileItem);
            candidateContextMenu.Items.Add(openCandidateFolderItem);
            candidateContextMenu.Items.Add(new ToolStripSeparator());
            candidateContextMenu.Items.Add(previewCandidateActionsItem);
            candidateContextMenu.Items.Add(moveCandidateToNameFoldersItem);
            candidateContextMenu.Items.Add(fileBotCandidateItem);
            grid.ContextMenuStrip = candidateContextMenu;

            candidateTotalLabel = new Label();
            candidateTotalLabel.Dock = DockStyle.Fill;
            candidateTotalLabel.TextAlign = ContentAlignment.MiddleLeft;
            candidateTotalLabel.Padding = new Padding(4, 0, 0, 0);
            candidateTotalLabel.Text = "No duplicate candidates yet.";

            deletionGrid = new DataGridView();
            deletionGrid.Dock = DockStyle.Fill;
            deletionGrid.AutoGenerateColumns = false;
            deletionGrid.AllowUserToAddRows = false;
            deletionGrid.AllowUserToDeleteRows = false;
            deletionGrid.AllowUserToOrderColumns = true;
            deletionGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            deletionGrid.MultiSelect = true;
            deletionGrid.DataSource = deletionSource;
            deletionGrid.CellFormatting += Grid_CellFormatting;
            deletionGrid.CellValueChanged += Grid_CellValueChanged;
            deletionGrid.CurrentCellDirtyStateChanged += Grid_CurrentCellDirtyStateChanged;
            deletionGrid.KeyDown += Grid_KeyDown;
            deletionGrid.SelectionChanged += Grid_SelectionChanged;
            deletionGrid.CellMouseDown += Grid_CellMouseDown;
            deletionGrid.ContextMenuStrip = candidateContextMenu;
            StyleGrid(deletionGrid);

            deletionTotalLabel = new Label();
            deletionTotalLabel.Dock = DockStyle.Fill;
            deletionTotalLabel.TextAlign = ContentAlignment.MiddleLeft;
            deletionTotalLabel.Padding = new Padding(4, 0, 0, 0);
            deletionTotalLabel.Text = "No files marked for removal.";

            missingEpisodesGrid = new DataGridView();
            missingEpisodesGrid.Dock = DockStyle.Fill;
            missingEpisodesGrid.AutoGenerateColumns = false;
            missingEpisodesGrid.AllowUserToAddRows = false;
            missingEpisodesGrid.AllowUserToDeleteRows = false;
            missingEpisodesGrid.AllowUserToOrderColumns = true;
            missingEpisodesGrid.ReadOnly = true;
            missingEpisodesGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            missingEpisodesGrid.MultiSelect = false;
            missingEpisodesGrid.DataSource = missingEpisodesSource;
            missingEpisodesGrid.SelectionChanged += MissingEpisodesGrid_SelectionChanged;
            missingEpisodesGrid.CellDoubleClick += MissingEpisodesGrid_CellDoubleClick;
            StyleGrid(missingEpisodesGrid);
            AddMissingEpisodeColumn("Title", "Series", 180);
            AddMissingEpisodeColumn("Scope", "Scope", 72);
            AddMissingEpisodeColumn("MissingEpisodes", "Missing", 70);
            AddMissingEpisodeColumn("SearchKey", "Search Key", 220);
            AddMissingEpisodeColumn("PresentRange", "Present Range", 110);
            AddMissingEpisodeColumn("KnownEpisodes", "Known", 70);
            AddMissingEpisodeColumn("MissingCount", "Missing Count", 92);
            AddMissingEpisodeColumn("LocationCount", "Locations", 78);

            missingEpisodesTotalLabel = new Label();
            missingEpisodesTotalLabel.Dock = DockStyle.Fill;
            missingEpisodesTotalLabel.TextAlign = ContentAlignment.MiddleLeft;
            missingEpisodesTotalLabel.Padding = new Padding(4, 0, 0, 0);
            missingEpisodesTotalLabel.Text = "No local episode gaps found.";

            episodeSearchGrid = new DataGridView();
            episodeSearchGrid.Dock = DockStyle.Fill;
            episodeSearchGrid.AutoGenerateColumns = false;
            episodeSearchGrid.AllowUserToAddRows = false;
            episodeSearchGrid.AllowUserToDeleteRows = false;
            episodeSearchGrid.AllowUserToOrderColumns = true;
            episodeSearchGrid.ReadOnly = true;
            episodeSearchGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            episodeSearchGrid.MultiSelect = false;
            episodeSearchGrid.DataSource = episodeSearchSource;
            episodeSearchGrid.SelectionChanged += EpisodeSearchGrid_SelectionChanged;
            episodeSearchGrid.CellDoubleClick += EpisodeSearchGrid_CellDoubleClick;
            StyleGrid(episodeSearchGrid);
            AddEpisodeSearchColumn("Provider", "Provider", 72);
            AddEpisodeSearchColumn("SeriesTitle", "Series", 140);
            AddEpisodeSearchColumn("MissingEpisode", "Ep", 52);
            AddEpisodeSearchColumn("SearchQuery", "Search Query", 190);
            AddEpisodeSearchColumn("IsBatchResult", "Full Season", 82);
            AddEpisodeSearchColumn("Title", "Result", 300);
            AddEpisodeSearchColumn("Size", "Size", 80);
            AddEpisodeSearchColumn("Seeders", "Seed", 58);
            AddEpisodeSearchColumn("Leechers", "Leech", 58);
            AddEpisodeSearchColumn("Downloads", "Done", 62);
            AddEpisodeSearchColumn("Trusted", "Trusted", 70);
            AddEpisodeSearchColumn("Published", "Published", 120);

            selectedFeedGrid = new DataGridView();
            selectedFeedGrid.Dock = DockStyle.Fill;
            selectedFeedGrid.AutoGenerateColumns = false;
            selectedFeedGrid.AllowUserToAddRows = false;
            selectedFeedGrid.AllowUserToDeleteRows = false;
            selectedFeedGrid.AllowUserToOrderColumns = true;
            selectedFeedGrid.ReadOnly = true;
            selectedFeedGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            selectedFeedGrid.MultiSelect = false;
            selectedFeedGrid.DataSource = selectedFeedSource;
            selectedFeedGrid.SelectionChanged += SelectedFeedGrid_SelectionChanged;
            selectedFeedGrid.CellDoubleClick += SelectedFeedGrid_CellDoubleClick;
            StyleGrid(selectedFeedGrid);
            AddSelectedFeedColumn("SeriesTitle", "Series", 150);
            AddSelectedFeedColumn("MissingEpisode", "Ep", 52);
            AddSelectedFeedColumn("Provider", "Provider", 68);
            AddSelectedFeedColumn("IsBatchResult", "Full Season", 82);
            AddSelectedFeedColumn("Title", "Selected Result", 260);
            AddSelectedFeedColumn("Size", "Size", 78);
            AddSelectedFeedColumn("Seeders", "Seed", 56);
            AddSelectedFeedColumn("Published", "Published", 110);
            AddSelectedFeedColumn("Added", "Added", 110);

            episodeSearchGroupBox = new ComboBox();
            episodeSearchGroupBox.Dock = DockStyle.Fill;
            episodeSearchGroupBox.DropDownStyle = ComboBoxStyle.DropDownList;
            episodeSearchGroupBox.Items.Add("Any group");
            episodeSearchGroupBox.SelectedIndex = 0;
            toolTip.SetToolTip(episodeSearchGroupBox, "Release group filter for episode search.");

            episodeSearchResolutionBox = new ComboBox();
            episodeSearchResolutionBox.Dock = DockStyle.Fill;
            episodeSearchResolutionBox.DropDownStyle = ComboBoxStyle.DropDownList;
            episodeSearchResolutionBox.Items.Add("Any resolution");
            episodeSearchResolutionBox.Items.Add("2160p");
            episodeSearchResolutionBox.Items.Add("1080p");
            episodeSearchResolutionBox.Items.Add("720p");
            episodeSearchResolutionBox.Items.Add("480p");
            episodeSearchResolutionBox.SelectedIndex = 0;
            toolTip.SetToolTip(episodeSearchResolutionBox, "Resolution filter for episode search.");

            episodeSearchButton = new Button();
            episodeSearchButton.Text = "Search";
            episodeSearchButton.Dock = DockStyle.Fill;
            episodeSearchButton.Enabled = false;
            episodeSearchButton.Click += EpisodeSearchButton_Click;
            StyleButton(episodeSearchButton, false);

            episodeSearchAllButton = new Button();
            episodeSearchAllButton.Text = "Search All";
            episodeSearchAllButton.Dock = DockStyle.Fill;
            episodeSearchAllButton.Enabled = false;
            episodeSearchAllButton.Click += EpisodeSearchAllButton_Click;
            StyleButton(episodeSearchAllButton, false);
            toolTip.SetToolTip(episodeSearchAllButton, "Search every missing episode for the selected series/scope using the current filters.");

            episodeSearchAddButton = new Button();
            episodeSearchAddButton.Text = "Add";
            episodeSearchAddButton.Dock = DockStyle.Fill;
            episodeSearchAddButton.Enabled = false;
            episodeSearchAddButton.Click += EpisodeSearchAddButton_Click;
            StyleButton(episodeSearchAddButton, false);
            toolTip.SetToolTip(episodeSearchAddButton, "Add the highlighted search result to the selected RSS feed.");

            episodeSearchTotalLabel = new Label();
            episodeSearchTotalLabel.Dock = DockStyle.Fill;
            episodeSearchTotalLabel.TextAlign = ContentAlignment.MiddleLeft;
            episodeSearchTotalLabel.Padding = new Padding(4, 0, 0, 0);
            episodeSearchTotalLabel.Text = "Select a missing episode to search.";

            selectedFeedTotalLabel = new Label();
            selectedFeedTotalLabel.Dock = DockStyle.Fill;
            selectedFeedTotalLabel.TextAlign = ContentAlignment.MiddleLeft;
            selectedFeedTotalLabel.Padding = new Padding(4, 0, 0, 0);
            selectedFeedTotalLabel.Text = "No selected RSS results yet.";

            selectedFeedOpenButton = new Button();
            selectedFeedOpenButton.Text = "Open RSS";
            selectedFeedOpenButton.Dock = DockStyle.Fill;
            selectedFeedOpenButton.Enabled = false;
            selectedFeedOpenButton.Click += SelectedFeedOpenButton_Click;
            StyleButton(selectedFeedOpenButton, false);

            selectedFeedCopyButton = new Button();
            selectedFeedCopyButton.Text = "Copy URL";
            selectedFeedCopyButton.Dock = DockStyle.Fill;
            selectedFeedCopyButton.Enabled = false;
            selectedFeedCopyButton.Click += SelectedFeedCopyButton_Click;
            StyleButton(selectedFeedCopyButton, false);

            selectedFeedRemoveButton = new Button();
            selectedFeedRemoveButton.Text = "Remove";
            selectedFeedRemoveButton.Dock = DockStyle.Fill;
            selectedFeedRemoveButton.Enabled = false;
            selectedFeedRemoveButton.Click += SelectedFeedRemoveButton_Click;
            StyleButton(selectedFeedRemoveButton, false);

            selectedFeedClearButton = new Button();
            selectedFeedClearButton.Text = "Clear";
            selectedFeedClearButton.Dock = DockStyle.Fill;
            selectedFeedClearButton.Enabled = false;
            selectedFeedClearButton.Click += SelectedFeedClearButton_Click;
            StyleButton(selectedFeedClearButton, false);

            AddCheckColumn("Delete", "Delete", 58);
            AddTextColumn("Recommendation", "Recommended", 120);
            AddTextColumn("Confidence", "Confidence", 92);
            AddTextColumn("ReviewStatus", "Status", 120);
            AddTextColumn("ArtworkStatus", "Artwork", 100);
            AddTextColumn("Episode", "Episode", 75);
            AddTextColumn("SimplifiedFileName", "Episode File", 260);
            AddTextColumn("SubtitleGroup", "Group", 120);
            AddTextColumn("SizeMB", "MB", 80);
            AddTextColumn("Version", "Version", 70);
            AddTextColumn("FileLocation", "Location", 420);
            AddTextColumn("AniDbDisplay", "Metadata", 220);
            AddTextColumn("Key", "Group Key", 260);
            AddTextColumn("Title", "Title", 240);
            AddTextColumn("AniDbId", "Metadata ID", 92);
            AddTextColumn("AniDbTitle", "Metadata Title", 220);
            AddTextColumn("AniDbYear", "Metadata Year", 96);
            AddTextColumn("SizeBytes", "Size Bytes", 105);
            SetColumnVisibility("Version", false);
            SetColumnVisibility("FileLocation", false);
            SetColumnVisibility("AniDbDisplay", false);
            SetColumnVisibility("Key", false);
            SetColumnVisibility("Title", false);
            SetColumnVisibility("AniDbId", false);
            SetColumnVisibility("AniDbTitle", false);
            SetColumnVisibility("AniDbYear", false);
            SetColumnVisibility("SizeBytes", false);
            ApplySavedColumnLayout();
            grid.ColumnDisplayIndexChanged += Grid_ColumnLayoutChanged;
            grid.ColumnWidthChanged += Grid_ColumnLayoutChanged;
            deletionGrid.ColumnDisplayIndexChanged += Grid_ColumnLayoutChanged;
            deletionGrid.ColumnWidthChanged += Grid_ColumnLayoutChanged;

            detailsBox = new LinkLabel();
            detailsBox.Dock = DockStyle.Fill;
            detailsBox.BorderStyle = BorderStyle.None;
            detailsBox.BackColor = PanelBackColor;
            detailsBox.ForeColor = PrimaryTextColor;
            detailsBox.LinkColor = Color.FromArgb(42, 91, 215);
            detailsBox.ActiveLinkColor = Color.FromArgb(29, 78, 216);
            detailsBox.VisitedLinkColor = Color.FromArgb(88, 80, 160);
            detailsBox.AutoEllipsis = true;
            detailsBox.TextAlign = ContentAlignment.TopLeft;
            detailsBox.Padding = new Padding(2);
            detailsBox.Text = "Select a file to see details.";
            detailsBox.LinkClicked += DetailsBox_LinkClicked;

            detailsGroup = CreateSectionGroup("Details", new Padding(8));
            detailsGroup.Controls.Add(detailsBox);

            metadataBox = new Label();
            metadataBox.Dock = DockStyle.Fill;
            metadataBox.AutoEllipsis = true;
            metadataBox.TextAlign = ContentAlignment.TopLeft;
            metadataBox.Padding = new Padding(2);
            metadataBox.Text = "Select a row to see provider match details.";

            metadataGroup = CreateSectionGroup("Metadata Match", new Padding(8));
            metadataGroup.Controls.Add(metadataBox);

            shellInspectorTitleLabel = new Label();
            shellInspectorTitleLabel.Text = "File Details";
            shellInspectorTitleLabel.Dock = DockStyle.Fill;
            shellInspectorTitleLabel.Font = new Font(Font.FontFamily, 10F, FontStyle.Bold);
            shellInspectorTitleLabel.TextAlign = ContentAlignment.MiddleLeft;

            inspectorPreviewLabel = new Label();
            inspectorPreviewLabel.Text = "Artwork Preview";
            inspectorPreviewLabel.Dock = DockStyle.Fill;
            inspectorPreviewLabel.Font = new Font(Font.FontFamily, 9F, FontStyle.Bold);
            inspectorPreviewLabel.TextAlign = ContentAlignment.MiddleLeft;

            inspectorActionsLabel = new Label();
            inspectorActionsLabel.Text = "Actions";
            inspectorActionsLabel.Dock = DockStyle.Fill;
            inspectorActionsLabel.Font = new Font(Font.FontFamily, 9F, FontStyle.Bold);
            inspectorActionsLabel.TextAlign = ContentAlignment.MiddleLeft;

            inspectorPreviewBox = new PictureBox();
            inspectorPreviewBox.Dock = DockStyle.Fill;
            inspectorPreviewBox.SizeMode = PictureBoxSizeMode.Zoom;
            inspectorPreviewBox.BorderStyle = BorderStyle.FixedSingle;

            inspectorKeepButton = new Button();
            inspectorKeepButton.Text = "Keep This File";
            inspectorKeepButton.Dock = DockStyle.Fill;
            inspectorKeepButton.Click += InspectorKeepButton_Click;
            StyleButton(inspectorKeepButton, true);

            inspectorDeleteButton = new Button();
            inspectorDeleteButton.Text = "Mark for Removal";
            inspectorDeleteButton.Dock = DockStyle.Fill;
            inspectorDeleteButton.Click += InspectorDeleteButton_Click;
            StyleDeleteButton(inspectorDeleteButton);

            inspectorIgnoreButton = new Button();
            inspectorIgnoreButton.Text = "Ignore Episode Group";
            inspectorIgnoreButton.Dock = DockStyle.Fill;
            inspectorIgnoreButton.Click += InspectorIgnoreButton_Click;
            StyleButton(inspectorIgnoreButton, false);

            inspectorOpenFolderButton = new Button();
            inspectorOpenFolderButton.Text = "Open File Location";
            inspectorOpenFolderButton.Dock = DockStyle.Fill;
            inspectorOpenFolderButton.Click += InspectorOpenFolderButton_Click;
            StyleButton(inspectorOpenFolderButton, false);

            seriesGroup = CreateSectionGroup("Series", new Padding(8));

            var seriesPanel = new TableLayoutPanel();
            seriesPanel.Dock = DockStyle.Fill;
            seriesPanel.ColumnCount = 1;
            seriesPanel.RowCount = 2;
            seriesPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            seriesPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            var seriesSearchPanel = new TableLayoutPanel();
            seriesSearchPanel.Dock = DockStyle.Fill;
            seriesSearchPanel.ColumnCount = 2;
            seriesSearchPanel.RowCount = 1;
            seriesSearchPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 52));
            seriesSearchPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));

            var seriesSearchLabel = new Label();
            seriesSearchLabel.Text = "Search";
            seriesSearchLabel.Dock = DockStyle.Fill;
            seriesSearchLabel.TextAlign = ContentAlignment.MiddleLeft;

            seriesSearchPanel.Controls.Add(seriesSearchLabel, 0, 0);
            seriesSearchPanel.Controls.Add(searchBox, 1, 0);

            var seriesViewPanel = new Panel();
            seriesViewPanel.Dock = DockStyle.Fill;
            seriesViewPanel.Controls.Add(seriesListView);
            seriesViewPanel.Controls.Add(seriesCoverView);

            seriesPanel.Controls.Add(seriesSearchPanel, 0, 0);
            seriesPanel.Controls.Add(seriesViewPanel, 0, 1);
            seriesGroup.Controls.Add(seriesPanel);
            StyleSeriesListView();
            StyleSeriesCoverView();
            UpdateSeriesPanelMode();

            candidatesGroup = CreateSectionGroup("Candidates", new Padding(8));

            var candidatesPanel = new TableLayoutPanel();
            candidatesPanel.Dock = DockStyle.Fill;
            candidatesPanel.ColumnCount = 1;
            candidatesPanel.RowCount = 3;
            candidatesPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
            candidatesPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            candidatesPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            candidatesPanel.Controls.Add(candidateTotalLabel, 0, 0);
            candidatesPanel.Controls.Add(reviewTabs, 0, 1);
            candidatesPanel.Controls.Add(grid, 0, 2);
            candidatesGroup.Controls.Add(candidatesPanel);
            candidatesCloseButton = CreatePanelCloseButton("Hide the Candidates panel.", ToggleCandidatesButton_Click);
            AttachPanelCloseButton(candidatesGroup, candidatesCloseButton);

            deletionGroup = CreateSectionGroup("Ready to Remove", new Padding(8));

            var deletionPanel = new TableLayoutPanel();
            deletionPanel.Dock = DockStyle.Fill;
            deletionPanel.ColumnCount = 2;
            deletionPanel.RowCount = 2;
            deletionPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            deletionPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
            deletionPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            deletionPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            deletionPanel.Controls.Add(deletionTotalLabel, 0, 0);
            deletionPanel.Controls.Add(deleteButton, 1, 0);
            deletionPanel.Controls.Add(deletionGrid, 0, 1);
            deletionPanel.SetColumnSpan(deletionGrid, 2);
            deletionGroup.Controls.Add(deletionPanel);
            deletionCloseButton = CreatePanelCloseButton("Hide the Ready to Remove panel.", ToggleReadyButton_Click);
            AttachPanelCloseButton(deletionGroup, deletionCloseButton);

            missingEpisodesGroup = CreateSectionGroup("Missing Episodes", new Padding(8));

            var missingEpisodesPanel = new TableLayoutPanel();
            missingEpisodesPanel.Dock = DockStyle.Fill;
            missingEpisodesPanel.ColumnCount = 1;
            missingEpisodesPanel.RowCount = 2;
            missingEpisodesPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
            missingEpisodesPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            missingEpisodesPanel.Controls.Add(missingEpisodesTotalLabel, 0, 0);
            missingEpisodesPanel.Controls.Add(missingEpisodesGrid, 0, 1);
            missingEpisodesGroup.Controls.Add(missingEpisodesPanel);
            missingEpisodesCloseButton = CreatePanelCloseButton("Hide the Missing Episodes panel.", ToggleMissingEpisodesButton_Click);
            AttachPanelCloseButton(missingEpisodesGroup, missingEpisodesCloseButton);

            episodeSearchGroup = CreateSectionGroup("Episode Search", new Padding(8));

            var episodeSearchPanel = new TableLayoutPanel();
            episodeSearchPanel.Dock = DockStyle.Fill;
            episodeSearchPanel.ColumnCount = 2;
            episodeSearchPanel.RowCount = 4;
            episodeSearchPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            episodeSearchPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            episodeSearchPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
            episodeSearchPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            episodeSearchPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            episodeSearchPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            episodeSearchPanel.Controls.Add(episodeSearchTotalLabel, 0, 0);
            episodeSearchPanel.SetColumnSpan(episodeSearchTotalLabel, 2);
            episodeSearchPanel.Controls.Add(episodeSearchGroupBox, 0, 1);
            episodeSearchPanel.Controls.Add(episodeSearchResolutionBox, 1, 1);

            var episodeSearchButtonPanel = new TableLayoutPanel();
            episodeSearchButtonPanel.Dock = DockStyle.Fill;
            episodeSearchButtonPanel.ColumnCount = 3;
            episodeSearchButtonPanel.RowCount = 1;
            episodeSearchButtonPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33F));
            episodeSearchButtonPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33F));
            episodeSearchButtonPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.34F));
            episodeSearchButtonPanel.Controls.Add(episodeSearchButton, 0, 0);
            episodeSearchButtonPanel.Controls.Add(episodeSearchAllButton, 1, 0);
            episodeSearchButtonPanel.Controls.Add(episodeSearchAddButton, 2, 0);

            episodeSearchPanel.Controls.Add(episodeSearchButtonPanel, 0, 2);
            episodeSearchPanel.SetColumnSpan(episodeSearchButtonPanel, 2);
            episodeSearchPanel.Controls.Add(episodeSearchGrid, 0, 3);
            episodeSearchPanel.SetColumnSpan(episodeSearchGrid, 2);
            episodeSearchGroup.Controls.Add(episodeSearchPanel);
            episodeSearchCloseButton = CreatePanelCloseButton("Hide the Episode Search panel.", ToggleEpisodeSearchButton_Click);
            AttachPanelCloseButton(episodeSearchGroup, episodeSearchCloseButton);

            selectedFeedGroup = CreateSectionGroup("Selected RSS Feed", new Padding(8));

            var selectedFeedPanel = new TableLayoutPanel();
            selectedFeedPanel.Dock = DockStyle.Fill;
            selectedFeedPanel.ColumnCount = 1;
            selectedFeedPanel.RowCount = 3;
            selectedFeedPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            selectedFeedPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            selectedFeedPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            selectedFeedPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            var selectedFeedButtonPanel = new TableLayoutPanel();
            selectedFeedButtonPanel.Dock = DockStyle.Fill;
            selectedFeedButtonPanel.ColumnCount = 4;
            selectedFeedButtonPanel.RowCount = 1;
            selectedFeedButtonPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25F));
            selectedFeedButtonPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25F));
            selectedFeedButtonPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25F));
            selectedFeedButtonPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25F));
            selectedFeedButtonPanel.Controls.Add(selectedFeedOpenButton, 0, 0);
            selectedFeedButtonPanel.Controls.Add(selectedFeedCopyButton, 1, 0);
            selectedFeedButtonPanel.Controls.Add(selectedFeedRemoveButton, 2, 0);
            selectedFeedButtonPanel.Controls.Add(selectedFeedClearButton, 3, 0);

            selectedFeedPanel.Controls.Add(selectedFeedTotalLabel, 0, 0);
            selectedFeedPanel.Controls.Add(selectedFeedButtonPanel, 0, 1);
            selectedFeedPanel.Controls.Add(selectedFeedGrid, 0, 2);
            selectedFeedGroup.Controls.Add(selectedFeedPanel);
            selectedFeedCloseButton = CreatePanelCloseButton("Hide the Selected RSS Feed panel.", ToggleSelectedFeedButton_Click);
            AttachPanelCloseButton(selectedFeedGroup, selectedFeedCloseButton);

            activityGroup = CreateSectionGroup("History / Alerts", new Padding(8));
            activityGroup.Controls.Add(activityLogBox);

            settingsGroup = CreateSectionGroup("Settings", new Padding(10));

            var settingsPanel = new TableLayoutPanel();
            settingsPanel.Dock = DockStyle.Fill;
            settingsPanel.ColumnCount = 1;
            settingsPanel.RowCount = 7;
            settingsPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 70));
            settingsPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            settingsPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            settingsPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            settingsPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            settingsPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            settingsPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            settingsSummaryLabel = new Label();
            settingsSummaryLabel.Dock = DockStyle.Fill;
            settingsSummaryLabel.TextAlign = ContentAlignment.MiddleLeft;
            settingsSummaryLabel.AutoEllipsis = true;
            settingsSummaryLabel.Text = "Quick access to common setup and workflow options. Full dialogs remain available from the menu.";

            settingsMetadataButton = CreateSettingsButton("Metadata Providers", SettingsMetadataButton_Click);
            settingsFileFormatsButton = CreateSettingsButton("File Format Filter", SettingsFileFormatsButton_Click);
            settingsMonitorButton = CreateSettingsButton("Monitoring", SettingsMonitorButton_Click);
            settingsThemeButton = CreateSettingsButton("Toggle Theme", SettingsThemeButton_Click);
            settingsFileBotButton = CreateSettingsButton("FileBot", SettingsFileBotButton_Click);

            settingsPanel.Controls.Add(settingsSummaryLabel, 0, 0);
            settingsPanel.Controls.Add(settingsMetadataButton, 0, 1);
            settingsPanel.Controls.Add(settingsFileFormatsButton, 0, 2);
            settingsPanel.Controls.Add(settingsMonitorButton, 0, 3);
            settingsPanel.Controls.Add(settingsThemeButton, 0, 4);
            settingsPanel.Controls.Add(settingsFileBotButton, 0, 5);
            settingsGroup.Controls.Add(settingsPanel);

            shellSeriesCoverBox = new PictureBox();
            shellSeriesCoverBox.Dock = DockStyle.Fill;
            shellSeriesCoverBox.SizeMode = PictureBoxSizeMode.Zoom;
            shellSeriesCoverBox.Margin = new Padding(0, 0, 16, 0);

            shellSeriesTitleLabel = new Label();
            shellSeriesTitleLabel.Text = "No series selected";
            shellSeriesTitleLabel.Dock = DockStyle.Fill;
            shellSeriesTitleLabel.Font = new Font(Font.FontFamily, 18F, FontStyle.Bold);
            shellSeriesTitleLabel.TextAlign = ContentAlignment.BottomLeft;
            shellSeriesTitleLabel.AutoEllipsis = true;

            shellSeriesMetaLabel = new Label();
            shellSeriesMetaLabel.Text = "Scan one or more folders to populate the workspace.";
            shellSeriesMetaLabel.Dock = DockStyle.Fill;
            shellSeriesMetaLabel.TextAlign = ContentAlignment.TopLeft;
            shellSeriesMetaLabel.AutoEllipsis = true;
            shellSeriesMetaLabel.ForeColor = SecondaryTextColor;

            shellScannedStatLabel = CreateShellStatLabel("Scanned\r\n0 files");
            shellDuplicateStatLabel = CreateShellStatLabel("Duplicates\r\n0 groups");
            shellMissingStatLabel = CreateShellStatLabel("Missing\r\n0 episodes");
            shellAniDbBadgeLabel = CreateProviderBadgeLabel("AniDB", "Ready");
            shellTvDbBadgeLabel = CreateProviderBadgeLabel("TVDB", "Setup");
            shellTmDbBadgeLabel = CreateProviderBadgeLabel("TMDB", "Setup");

            navLibraryButton = CreateNavButton("Library", "Show the scanned series selector.", ShellNavButton_Click);
            navDuplicatesButton = CreateNavButton("Duplicates", "Show duplicate candidates.", ShellNavButton_Click);
            navMissingEpisodesButton = CreateNavButton("Missing Episodes", "Show the existing Missing Episodes workflow.", ShellNavButton_Click);
            navEpisodeSearchButton = CreateNavButton("Episode Search", "Show the existing Episode Search workflow.", ShellNavButton_Click);
            navSelectedRssButton = CreateNavButton("Selected RSS", "Show the selected RSS feed workflow.", ShellNavButton_Click);
            navActivityButton = CreateNavButton("Activity", "Show history and alert messages.", ShellNavButton_Click);
            navSettingsButton = CreateNavButton("Settings", "Open common setup shortcuts.", ShellNavButton_Click);
            navLibraryButton.Tag = "Library";
            navDuplicatesButton.Tag = "Duplicates";
            navMissingEpisodesButton.Tag = "MissingEpisodes";
            navEpisodeSearchButton.Tag = "EpisodeSearch";
            navSelectedRssButton.Tag = "SelectedRss";
            navActivityButton.Tag = "Activity";
            navSettingsButton.Tag = "Settings";

            var navigationPanel = new TableLayoutPanel();
            navigationPanel.Dock = DockStyle.Fill;
            navigationPanel.ColumnCount = 1;
            navigationPanel.RowCount = 10;
            navigationPanel.Padding = new Padding(10, 8, 10, 8);
            navigationPanel.Tag = "Sidebar";
            navigationPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            navigationPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            navigationPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            navigationPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            navigationPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            navigationPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            navigationPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            navigationPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 12));
            navigationPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            navigationPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 70));
            navigationPanel.Controls.Add(navLibraryButton, 0, 0);
            navigationPanel.Controls.Add(navDuplicatesButton, 0, 1);
            navigationPanel.Controls.Add(navMissingEpisodesButton, 0, 2);
            navigationPanel.Controls.Add(navEpisodeSearchButton, 0, 3);
            navigationPanel.Controls.Add(navSelectedRssButton, 0, 4);
            navigationPanel.Controls.Add(navActivityButton, 0, 5);
            navigationPanel.Controls.Add(navSettingsButton, 0, 6);
            navigationPanel.Controls.Add(seriesGroup, 0, 8);

            var sidebarStatsLabel = new Label();
            sidebarStatsLabel.Dock = DockStyle.Fill;
            sidebarStatsLabel.TextAlign = ContentAlignment.BottomLeft;
            sidebarStatsLabel.ForeColor = SecondaryTextColor;
            sidebarStatsLabel.Text = "Select a series to focus the workspace.";
            navigationPanel.Controls.Add(sidebarStatsLabel, 0, 9);

            var seriesHeader = new TableLayoutPanel();
            seriesHeader.Dock = DockStyle.Fill;
            seriesHeader.Padding = new Padding(14, 12, 14, 12);
            seriesHeader.Tag = "Section";
            seriesHeader.ColumnCount = 5;
            seriesHeader.RowCount = 3;
            seriesHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 136));
            seriesHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 23F));
            seriesHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 22F));
            seriesHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 22F));
            seriesHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33F));
            seriesHeader.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
            seriesHeader.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            seriesHeader.RowStyles.Add(new RowStyle(SizeType.Absolute, 84));
            seriesHeader.Controls.Add(shellSeriesCoverBox, 0, 0);
            seriesHeader.SetRowSpan(shellSeriesCoverBox, 3);
            seriesHeader.Controls.Add(shellSeriesTitleLabel, 1, 0);
            seriesHeader.SetColumnSpan(shellSeriesTitleLabel, 4);
            seriesHeader.Controls.Add(shellSeriesMetaLabel, 1, 1);
            seriesHeader.SetColumnSpan(shellSeriesMetaLabel, 4);
            seriesHeader.Controls.Add(shellScannedStatLabel, 1, 2);
            seriesHeader.Controls.Add(shellDuplicateStatLabel, 2, 2);
            seriesHeader.Controls.Add(shellMissingStatLabel, 3, 2);

            var providerPanel = new TableLayoutPanel();
            providerPanel.Dock = DockStyle.Fill;
            providerPanel.ColumnCount = 1;
            providerPanel.RowCount = 3;
            providerPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 33.33F));
            providerPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 33.33F));
            providerPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 33.34F));
            providerPanel.Controls.Add(shellAniDbBadgeLabel, 0, 0);
            providerPanel.Controls.Add(shellTvDbBadgeLabel, 0, 1);
            providerPanel.Controls.Add(shellTmDbBadgeLabel, 0, 2);
            seriesHeader.Controls.Add(providerPanel, 4, 2);

            workflowPanel = new TableLayoutPanel();
            workflowPanel.Dock = DockStyle.Fill;
            workflowPanel.AutoScroll = false;
            workflowPanel.ColumnCount = 1;
            workflowPanel.RowCount = 2;
            workflowPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 68F));
            workflowPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 32F));

            duplicateWorkflowSplit = new SplitContainer();
            duplicateWorkflowSplit.Dock = DockStyle.Fill;
            duplicateWorkflowSplit.Orientation = Orientation.Horizontal;
            duplicateWorkflowSplit.SplitterWidth = 7;
            duplicateWorkflowSplit.Panel1MinSize = 220;
            duplicateWorkflowSplit.Panel2MinSize = 180;
            duplicateWorkflowSplit.FixedPanel = FixedPanel.None;
            duplicateWorkflowSplit.Panel1.Controls.Add(candidatesGroup);
            duplicateWorkflowSplit.Panel2.Controls.Add(deletionGroup);
            duplicateWorkflowSplit.SplitterMoved += DuplicateWorkflowSplit_SplitterMoved;
            workflowPanel.Controls.Add(duplicateWorkflowSplit, 0, 0);

            var mainContentPanel = new TableLayoutPanel();
            mainContentPanel.Dock = DockStyle.Fill;
            mainContentPanel.Padding = new Padding(8, 8, 8, 8);
            mainContentPanel.Tag = "Section";
            mainContentPanel.ColumnCount = 1;
            mainContentPanel.RowCount = 2;
            mainContentPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 200));
            mainContentPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            mainContentPanel.Controls.Add(seriesHeader, 0, 0);
            mainContentPanel.Controls.Add(workflowPanel, 0, 1);

            var inspectorPanel = new TableLayoutPanel();
            inspectorPanel.Dock = DockStyle.Fill;
            inspectorPanel.Padding = new Padding(10, 8, 10, 8);
            inspectorPanel.Tag = "Inspector";
            inspectorPanel.ColumnCount = 1;
            inspectorPanel.RowCount = 10;
            inspectorPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            inspectorPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            inspectorPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 92));
            inspectorPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
            inspectorPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 128));
            inspectorPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
            inspectorPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            inspectorPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            inspectorPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            inspectorPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            inspectorPanel.Controls.Add(shellInspectorTitleLabel, 0, 0);
            inspectorPanel.Controls.Add(detailsGroup, 0, 1);
            inspectorPanel.Controls.Add(metadataGroup, 0, 2);
            inspectorPanel.Controls.Add(inspectorPreviewLabel, 0, 3);
            inspectorPanel.Controls.Add(inspectorPreviewBox, 0, 4);
            inspectorPanel.Controls.Add(inspectorActionsLabel, 0, 5);
            inspectorPanel.Controls.Add(inspectorKeepButton, 0, 6);
            inspectorPanel.Controls.Add(inspectorDeleteButton, 0, 7);
            inspectorPanel.Controls.Add(inspectorIgnoreButton, 0, 8);
            inspectorPanel.Controls.Add(inspectorOpenFolderButton, 0, 9);

            workspacePanel = new TableLayoutPanel();
            workspacePanel.Dock = DockStyle.Fill;
            workspacePanel.Padding = new Padding(0);
            workspacePanel.BackColor = AppBackColor;
            workspacePanel.ColumnCount = 3;
            workspacePanel.RowCount = 1;
            workspacePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 270));
            workspacePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            workspacePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 360));
            workspacePanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            workspacePanel.Controls.Add(navigationPanel, 0, 0);
            workspacePanel.Controls.Add(mainContentPanel, 1, 0);
            workspacePanel.Controls.Add(inspectorPanel, 2, 0);
            ApplyWorkspacePanelVisibility();

            Controls.Add(workspacePanel);
            Controls.Add(busyNoticePanel);
            Controls.Add(topPanel);
            Controls.Add(mainMenu);
            ApplyTheme();
            UpdateDashboard();
            StartSelectedFeedServer();
            SaveAndRefreshSelectedFeed();
            Shown += MainForm_Shown;
        }

        private Label CreateChipLabel()
        {
            var label = new Label();
            label.AutoSize = false;
            label.AutoEllipsis = true;
            label.Dock = DockStyle.Fill;
            label.Padding = new Padding(4, 2, 4, 2);
            label.Margin = new Padding(2);
            label.BorderStyle = BorderStyle.None;
            label.TextAlign = ContentAlignment.MiddleCenter;
            return label;
        }

        private Label CreateShellStatLabel(string text)
        {
            var label = new Label();
            label.Text = text;
            label.Dock = DockStyle.Fill;
            label.AutoEllipsis = true;
            label.TextAlign = ContentAlignment.MiddleLeft;
            label.Padding = new Padding(10, 0, 6, 0);
            label.Margin = new Padding(0, 0, 8, 0);
            label.BorderStyle = BorderStyle.FixedSingle;
            return label;
        }

        private Label CreateProviderBadgeLabel(string provider, string status)
        {
            var label = new Label();
            label.Text = FormatProviderBadgeText(provider, status);
            label.Dock = DockStyle.Fill;
            label.AutoEllipsis = true;
            label.TextAlign = ContentAlignment.MiddleCenter;
            label.Padding = new Padding(4, 0, 4, 0);
            label.Margin = new Padding(0, 1, 0, 1);
            label.BorderStyle = BorderStyle.FixedSingle;
            return label;
        }

        private Button CreateNavButton(string text, string tooltip, EventHandler clickHandler)
        {
            var button = new Button();
            button.Text = text;
            button.Dock = DockStyle.Fill;
            button.TextAlign = ContentAlignment.MiddleLeft;
            button.Padding = new Padding(14, 0, 8, 0);
            button.Margin = new Padding(0, 3, 0, 3);
            button.Click += clickHandler;
            StyleButton(button, false);
            toolTip.SetToolTip(button, tooltip);
            return button;
        }

        private Button CreateSettingsButton(string text, EventHandler clickHandler)
        {
            var button = new Button();
            button.Text = text;
            button.Dock = DockStyle.Fill;
            button.TextAlign = ContentAlignment.MiddleLeft;
            button.Padding = new Padding(10, 0, 6, 0);
            button.Margin = new Padding(0, 3, 0, 3);
            button.Click += clickHandler;
            StyleButton(button, false);
            return button;
        }

        private GroupBox CreateSectionGroup(string text, Padding padding)
        {
            var group = new ModernGroupBox();
            group.Text = text;
            group.Dock = DockStyle.Fill;
            group.Padding = padding;
            StyleGroupBox(group);
            return group;
        }

        private static Icon CreateAppIcon()
        {
            using (var bitmap = new Bitmap(32, 32))
            using (var graphics = Graphics.FromImage(bitmap))
            using (var back = new SolidBrush(Color.FromArgb(26, 31, 38)))
            using (var card = new SolidBrush(Color.FromArgb(79, 156, 249)))
            using (var cardTwo = new SolidBrush(Color.FromArgb(46, 204, 113)))
            using (var pen = new Pen(Color.FromArgb(235, 245, 255), 2F))
            {
                graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                graphics.Clear(Color.Transparent);
                graphics.FillEllipse(back, 1, 1, 30, 30);
                graphics.FillRectangle(cardTwo, 9, 8, 12, 15);
                graphics.DrawRectangle(pen, 9, 8, 12, 15);
                graphics.FillRectangle(card, 13, 11, 12, 15);
                graphics.DrawRectangle(pen, 13, 11, 12, 15);
                graphics.FillPolygon(Brushes.White, new[]
                {
                    new Point(17, 15),
                    new Point(17, 22),
                    new Point(23, 18)
                });

                var handle = bitmap.GetHicon();
                try
                {
                    return (Icon)Icon.FromHandle(handle).Clone();
                }
                finally
                {
                    DestroyIcon(handle);
                }
            }
        }

        private void AddTextColumn(string propertyName, string headerText, int width)
        {
            AddTextColumn(grid, propertyName, headerText, width);
            AddTextColumn(deletionGrid, propertyName, headerText, width);
        }

        private void AddMissingEpisodeColumn(string propertyName, string headerText, int width)
        {
            var column = new DataGridViewTextBoxColumn();
            column.DataPropertyName = propertyName;
            column.HeaderText = headerText;
            column.Width = width;
            column.SortMode = DataGridViewColumnSortMode.Automatic;
            missingEpisodesGrid.Columns.Add(column);
        }

        private void AddEpisodeSearchColumn(string propertyName, string headerText, int width)
        {
            var column = new DataGridViewTextBoxColumn();
            column.DataPropertyName = propertyName;
            column.HeaderText = headerText;
            column.Width = width;
            column.SortMode = DataGridViewColumnSortMode.Automatic;
            episodeSearchGrid.Columns.Add(column);
        }

        private void AddSelectedFeedColumn(string propertyName, string headerText, int width)
        {
            var column = new DataGridViewTextBoxColumn();
            column.DataPropertyName = propertyName;
            column.HeaderText = headerText;
            column.Name = propertyName;
            column.Width = width;
            column.SortMode = DataGridViewColumnSortMode.Automatic;
            selectedFeedGrid.Columns.Add(column);
        }

        private void AddReviewTab(string text, string tag)
        {
            var page = new TabPage(text);
            page.Tag = tag;
            reviewTabs.TabPages.Add(page);
        }

        private static void AddTextColumn(DataGridView targetGrid, string propertyName, string headerText, int width)
        {
            var column = new DataGridViewTextBoxColumn();
            column.DataPropertyName = propertyName;
            column.HeaderText = headerText;
            column.Width = width;
            column.ReadOnly = true;
            targetGrid.Columns.Add(column);
        }

        private void AddCheckColumn(string propertyName, string headerText, int width)
        {
            AddCheckColumn(grid, propertyName, headerText, width);
            AddCheckColumn(deletionGrid, propertyName, headerText, width);
        }

        private static void AddCheckColumn(DataGridView targetGrid, string propertyName, string headerText, int width)
        {
            var column = new DataGridViewCheckBoxColumn();
            column.DataPropertyName = propertyName;
            column.HeaderText = headerText;
            column.Width = width;
            targetGrid.Columns.Add(column);
        }

        private void SetColumnVisibility(string propertyName, bool visible)
        {
            SetColumnVisibility(grid, propertyName, visible);
            SetColumnVisibility(deletionGrid, propertyName, visible);
        }

        private Color AppBackColor
        {
            get { return darkMode ? Color.FromArgb(12, 18, 22) : Color.FromArgb(242, 245, 248); }
        }

        private Color PanelBackColor
        {
            get { return darkMode ? Color.FromArgb(20, 28, 33) : Color.White; }
        }

        private Color HeaderBackColor
        {
            get { return darkMode ? Color.FromArgb(27, 37, 44) : Color.FromArgb(239, 244, 250); }
        }

        private Color PrimaryTextColor
        {
            get { return darkMode ? Color.FromArgb(241, 241, 241) : Color.FromArgb(39, 49, 64); }
        }

        private Color SecondaryTextColor
        {
            get { return darkMode ? Color.FromArgb(176, 190, 199) : Color.FromArgb(78, 88, 102); }
        }

        private Color BorderColor
        {
            get { return darkMode ? Color.FromArgb(45, 58, 67) : Color.FromArgb(191, 202, 216); }
        }

        private Color GridLineColor
        {
            get { return darkMode ? Color.FromArgb(42, 55, 64) : Color.FromArgb(226, 232, 240); }
        }

        private Color AlternateRowColor
        {
            get { return darkMode ? Color.FromArgb(18, 26, 31) : Color.FromArgb(248, 250, 252); }
        }

        private Color SelectionBackColor
        {
            get { return darkMode ? Color.FromArgb(26, 88, 150) : Color.FromArgb(219, 234, 254); }
        }

        private Color SelectionTextColor
        {
            get { return darkMode ? Color.White : Color.FromArgb(30, 41, 59); }
        }

        private Color DeleteMarkColor
        {
            get { return darkMode ? Color.FromArgb(88, 28, 35) : Color.MistyRose; }
        }

        private Color DeleteButtonBackColor
        {
            get { return darkMode ? Color.FromArgb(45, 45, 48) : Color.FromArgb(248, 250, 252); }
        }

        private Color DeleteButtonBorderColor
        {
            get { return darkMode ? Color.FromArgb(148, 80, 88) : Color.FromArgb(220, 160, 160); }
        }

        private Color DeleteButtonTextColor
        {
            get { return darkMode ? Color.FromArgb(254, 202, 202) : Color.FromArgb(127, 29, 29); }
        }

        private Color DuplicateSeriesBackColor
        {
            get { return darkMode ? Color.FromArgb(69, 26, 34) : Color.FromArgb(254, 242, 242); }
        }

        private Color DuplicateSeriesForeColor
        {
            get { return darkMode ? Color.FromArgb(254, 202, 202) : Color.FromArgb(127, 29, 29); }
        }

        private Color AccentColor
        {
            get { return darkMode ? Color.FromArgb(49, 132, 255) : Color.FromArgb(0, 95, 184); }
        }

        private Color SidebarBackColor
        {
            get { return darkMode ? Color.FromArgb(15, 23, 28) : Color.FromArgb(248, 250, 252); }
        }

        private Color InspectorBackColor
        {
            get { return darkMode ? Color.FromArgb(18, 25, 30) : Color.FromArgb(248, 250, 252); }
        }

        private Color SectionTitleColor
        {
            get { return darkMode ? Color.FromArgb(226, 232, 240) : Color.FromArgb(15, 23, 42); }
        }

        private void StyleButton(Button button, bool emphasis)
        {
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 1;
            button.FlatAppearance.BorderColor = emphasis ? AccentColor : BorderColor;
            button.BackColor = emphasis ? AccentColor : HeaderBackColor;
            button.ForeColor = emphasis ? Color.White : PrimaryTextColor;
            button.Margin = new Padding(3);
            button.Cursor = Cursors.Hand;
        }

        private void StyleDeleteButton(Button button)
        {
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 1;
            button.FlatAppearance.BorderColor = DeleteButtonBorderColor;
            button.BackColor = DeleteButtonBackColor;
            button.ForeColor = DeleteButtonTextColor;
            button.Margin = new Padding(3);
        }

        private void StyleChipLabel(Label label)
        {
            if (label == null)
            {
                return;
            }

            label.BackColor = HeaderBackColor;
            label.ForeColor = SecondaryTextColor;
            label.BorderStyle = BorderStyle.FixedSingle;
        }

        private void StyleDashboard()
        {
            StyleChipLabel(statusLabel);
            StyleChipLabel(scannedChipLabel);
            StyleChipLabel(candidatesChipLabel);
            StyleChipLabel(visibleChipLabel);
            StyleChipLabel(deletionChipLabel);
            StyleChipLabel(duplicateChipLabel);
            StyleChipLabel(locationChipLabel);
            StyleChipLabel(filterChipLabel);
            StyleChipLabel(cacheChipLabel);
            StyleChipLabel(providerChipLabel);
        }

        private void StyleBusyNotice()
        {
            if (busyNoticePanel == null)
            {
                return;
            }

            busyNoticePanel.BorderStyle = BorderStyle.FixedSingle;
            busyNoticePanel.BackColor = darkMode ? Color.FromArgb(24, 50, 72) : Color.FromArgb(232, 244, 255);
            busyNoticeTitleLabel.BackColor = busyNoticePanel.BackColor;
            busyNoticeTitleLabel.ForeColor = darkMode ? Color.FromArgb(255, 255, 255) : Color.FromArgb(20, 52, 83);
            busyNoticeStatusLabel.BackColor = busyNoticePanel.BackColor;
            busyNoticeStatusLabel.ForeColor = darkMode ? Color.FromArgb(224, 235, 245) : Color.FromArgb(38, 74, 106);
            StyleButton(busyNoticeCancelButton, false);
        }

        private void StyleGrid(DataGridView targetGrid)
        {
            targetGrid.BorderStyle = BorderStyle.None;
            targetGrid.BackgroundColor = PanelBackColor;
            targetGrid.GridColor = GridLineColor;
            targetGrid.RowHeadersVisible = false;
            targetGrid.EnableHeadersVisualStyles = false;
            targetGrid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;
            targetGrid.ColumnHeadersDefaultCellStyle.BackColor = HeaderBackColor;
            targetGrid.ColumnHeadersDefaultCellStyle.ForeColor = PrimaryTextColor;
            targetGrid.ColumnHeadersDefaultCellStyle.SelectionBackColor = HeaderBackColor;
            targetGrid.ColumnHeadersDefaultCellStyle.SelectionForeColor = PrimaryTextColor;
            targetGrid.DefaultCellStyle.BackColor = PanelBackColor;
            targetGrid.DefaultCellStyle.ForeColor = PrimaryTextColor;
            targetGrid.DefaultCellStyle.SelectionBackColor = SelectionBackColor;
            targetGrid.DefaultCellStyle.SelectionForeColor = SelectionTextColor;
            targetGrid.AlternatingRowsDefaultCellStyle.BackColor = AlternateRowColor;
            targetGrid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
            targetGrid.ColumnHeadersHeight = 32;
            targetGrid.RowTemplate.Height = 30;
            targetGrid.DefaultCellStyle.Padding = new Padding(4, 0, 4, 0);
            targetGrid.Refresh();
        }

        private void StyleSeriesListView()
        {
            if (seriesListView == null)
            {
                return;
            }

            seriesListView.BackColor = PanelBackColor;
            seriesListView.ForeColor = PrimaryTextColor;
        }

        private void StyleSeriesCoverView()
        {
            if (seriesCoverView == null)
            {
                return;
            }

            seriesCoverView.BackColor = PanelBackColor;
            seriesCoverView.ForeColor = PrimaryTextColor;
        }

        private void StyleGroupBox(GroupBox groupBox)
        {
            groupBox.BackColor = PanelBackColor;
            groupBox.ForeColor = SectionTitleColor;
            var modern = groupBox as ModernGroupBox;
            if (modern != null)
            {
                modern.BorderColor = BorderColor;
                modern.HeaderBackColor = PanelBackColor;
                modern.TitleColor = SectionTitleColor;
                modern.Invalidate();
            }
        }

        private Button CreatePanelCloseButton(string tooltip, EventHandler clickHandler)
        {
            var button = new Button();
            button.Text = "X";
            button.Size = new Size(24, 22);
            button.TabStop = false;
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 0;
            button.BackColor = PanelBackColor;
            button.ForeColor = SecondaryTextColor;
            button.Font = new Font(Font.FontFamily, 8F, FontStyle.Bold);
            button.Tag = "CloseButton";
            button.Click += clickHandler;
            toolTip.SetToolTip(button, tooltip);
            return button;
        }

        private void AttachPanelCloseButton(GroupBox groupBox, Button closeButton)
        {
            groupBox.Controls.Add(closeButton);
            groupBox.Resize += delegate { PositionPanelCloseButton(groupBox, closeButton); };
            PositionPanelCloseButton(groupBox, closeButton);
            closeButton.BringToFront();
        }

        private void PositionPanelCloseButton(GroupBox groupBox, Button closeButton)
        {
            closeButton.Location = new Point(Math.Max(0, groupBox.ClientSize.Width - closeButton.Width - 8), 0);
            closeButton.BringToFront();
        }
        private void UpdateCandidatesToggleText()
        {
            viewCandidatesMenuItem.Text = candidatesPanelCollapsed ? "Show Candidates" : "Hide Candidates";
        }

        private void UpdateReadyToggleText()
        {
            viewReadyMenuItem.Text = deletionPanelCollapsed ? "Show Ready" : "Hide Ready";
        }

        private void UpdateMissingEpisodesToggleText()
        {
            viewMissingEpisodesMenuItem.Text = missingEpisodesPanelCollapsed ? "Show Missing Episodes" : "Hide Missing Episodes";
        }

        private void UpdateEpisodeSearchToggleText()
        {
            viewEpisodeSearchMenuItem.Text = episodeSearchPanelCollapsed ? "Show Episode Search" : "Hide Episode Search";
        }

        private void UpdateSelectedFeedToggleText()
        {
            viewSelectedFeedMenuItem.Text = selectedFeedPanelCollapsed ? "Show Selected Feed" : "Hide Selected Feed";
        }

        private void ApplyWorkspacePanelVisibility()
        {
            var candidatesVisible = !candidatesPanelCollapsed;
            var deletionVisible = candidatesVisible && !deletionPanelCollapsed;
            var missingEpisodesVisible = !missingEpisodesPanelCollapsed;
            var episodeSearchVisible = !episodeSearchPanelCollapsed;
            var selectedFeedVisible = !selectedFeedPanelCollapsed;
            if (workflowPanel != null)
            {
                workflowPanel.SuspendLayout();
                workflowPanel.Controls.Clear();
                workflowPanel.RowStyles.Clear();
                workflowPanel.RowCount = 1;
                workflowPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

                var section = activeShellSection ?? "Duplicates";
                if (string.Equals(section, "MissingEpisodes", StringComparison.OrdinalIgnoreCase))
                {
                    FocusMissingEpisodesForActiveSeries();
                    workflowPanel.Controls.Add(missingEpisodesGroup, 0, 0);
                    missingEpisodesGroup.Visible = missingEpisodesVisible;
                }
                else if (string.Equals(section, "EpisodeSearch", StringComparison.OrdinalIgnoreCase))
                {
                    workflowPanel.Controls.Add(episodeSearchGroup, 0, 0);
                    episodeSearchGroup.Visible = episodeSearchVisible;
                }
                else if (string.Equals(section, "SelectedRss", StringComparison.OrdinalIgnoreCase))
                {
                    workflowPanel.Controls.Add(selectedFeedGroup, 0, 0);
                    selectedFeedGroup.Visible = selectedFeedVisible;
                }
                else if (string.Equals(section, "Activity", StringComparison.OrdinalIgnoreCase))
                {
                    activityGroup.Text = "History / Alerts";
                    workflowPanel.Controls.Add(activityGroup, 0, 0);
                    activityGroup.Visible = true;
                }
                else if (string.Equals(section, "Library", StringComparison.OrdinalIgnoreCase))
                {
                    workflowPanel.Controls.Add(activityGroup, 0, 0);
                    activityGroup.Text = "Library";
                    activityGroup.Visible = true;
                    UpdateActivity("Library navigation selected. Choose a series from the left rail to focus the workspace.", true);
                }
                else if (string.Equals(section, "Settings", StringComparison.OrdinalIgnoreCase))
                {
                    workflowPanel.Controls.Add(settingsGroup, 0, 0);
                    settingsGroup.Visible = true;
                    UpdateSettingsSummary();
                }
                else
                {
                    activityGroup.Text = "History / Alerts";
                    workflowPanel.RowCount = 1;
                    workflowPanel.RowStyles.Clear();
                    workflowPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
                    if (candidatesVisible)
                    {
                        if (deletionVisible)
                        {
                            duplicateWorkflowSplit.Panel1.Controls.Add(candidatesGroup);
                            duplicateWorkflowSplit.Panel2.Controls.Add(deletionGroup);
                            workflowPanel.Controls.Add(duplicateWorkflowSplit, 0, 0);
                            ApplyDuplicateWorkflowSplitterDistance();
                        }
                        else
                        {
                            workflowPanel.Controls.Add(candidatesGroup, 0, 0);
                        }
                    }
                    candidatesGroup.Visible = candidatesVisible;
                    deletionGroup.Visible = deletionVisible;
                    duplicateWorkflowSplit.Visible = candidatesVisible && deletionVisible;
                }

                workflowPanel.ResumeLayout();
            }

            UpdateCandidatesToggleText();
            UpdateReadyToggleText();
            UpdateMissingEpisodesToggleText();
            UpdateEpisodeSearchToggleText();
            UpdateSelectedFeedToggleText();
            UpdateWorkspaceMenuState();
            UpdateShellNavigationState();
            UpdateShellSeriesHeader();
        }

        private void ApplyDuplicateWorkflowSplitterDistance()
        {
            if (duplicateWorkflowSplit == null || !duplicateWorkflowSplit.Visible)
            {
                return;
            }

            var height = duplicateWorkflowSplit.ClientSize.Height;
            if (height <= 0)
            {
                duplicateWorkflowSplit.BeginInvoke(new Action(ApplyDuplicateWorkflowSplitterDistance));
                return;
            }

            var minimumTop = duplicateWorkflowSplit.Panel1MinSize;
            var minimumBottom = duplicateWorkflowSplit.Panel2MinSize;
            var available = height - duplicateWorkflowSplit.SplitterWidth;
            if (available <= minimumTop + minimumBottom)
            {
                duplicateWorkflowSplit.Panel1MinSize = Math.Max(120, available / 2);
                duplicateWorkflowSplit.Panel2MinSize = Math.Max(120, available - duplicateWorkflowSplit.Panel1MinSize);
                return;
            }

            var target = Math.Max(minimumTop, Math.Min(available - minimumBottom, (int)(available * 0.56)));
            if (Math.Abs(duplicateWorkflowSplit.SplitterDistance - target) > 8)
            {
                duplicateWorkflowSplit.SplitterDistance = target;
            }
        }

        private void DuplicateWorkflowSplit_SplitterMoved(object sender, SplitterEventArgs e)
        {
            AppendDiagnosticLog("UI", "Duplicate workflow splitter moved. Candidates height " + duplicateWorkflowSplit.SplitterDistance.ToString("N0") + " px.");
        }

        private void UpdateWorkspaceMenuState()
        {
            viewCandidatesMenuItem.Checked = !candidatesPanelCollapsed;
            viewReadyMenuItem.Checked = !deletionPanelCollapsed && !candidatesPanelCollapsed;
            viewMissingEpisodesMenuItem.Checked = !missingEpisodesPanelCollapsed;
            viewEpisodeSearchMenuItem.Checked = !episodeSearchPanelCollapsed;
            viewSelectedFeedMenuItem.Checked = !selectedFeedPanelCollapsed;
            viewReadyMenuItem.Enabled = !candidatesPanelCollapsed;
            viewMissingEpisodesMenuItem.Enabled = true;
            viewEpisodeSearchMenuItem.Enabled = true;
            viewSelectedFeedMenuItem.Enabled = true;
            viewRestoreWorkspaceMenuItem.Enabled = candidatesPanelCollapsed || deletionPanelCollapsed || missingEpisodesPanelCollapsed || episodeSearchPanelCollapsed || selectedFeedPanelCollapsed;
        }

        private void RestoreWorkspaceMenuItem_Click(object sender, EventArgs e)
        {
            activeShellSection = "Duplicates";
            candidatesPanelCollapsed = false;
            deletionPanelCollapsed = false;
            missingEpisodesPanelCollapsed = false;
            episodeSearchPanelCollapsed = false;
            selectedFeedPanelCollapsed = false;
            ApplyWorkspacePanelVisibility();
        }
        private void ToggleDarkModeMenuItem_Click(object sender, EventArgs e)
        {
            darkMode = viewDarkModeMenuItem.Checked;
            ApplyTheme();
        }

        private void ToggleSeriesCoversMenuItem_Click(object sender, EventArgs e)
        {
            showSeriesCovers = viewSeriesCoversMenuItem.Checked;
            UpdateSeriesPanelMode();
            PopulateSeriesPanel();
        }

        private void UpdateSeriesPanelMode()
        {
            if (seriesListView == null || seriesCoverView == null)
            {
                return;
            }

            seriesCoverView.Visible = showSeriesCovers;
            seriesListView.Visible = !showSeriesCovers;
            if (showSeriesCovers)
            {
                seriesCoverView.BringToFront();
            }
            else
            {
                seriesListView.BringToFront();
            }
        }

        private void ApplyTheme()
        {
            BackColor = AppBackColor;
            mainMenu.BackColor = PanelBackColor;
            mainMenu.ForeColor = PrimaryTextColor;
            ApplyMenuTheme(mainMenu.Items);
            ApplyControlTheme(this);
            StyleDashboard();
            StyleBusyNotice();
            StyleGrid(grid);
            StyleGrid(deletionGrid);
            StyleGrid(missingEpisodesGrid);
            StyleGrid(episodeSearchGrid);
            StyleGrid(selectedFeedGrid);
            StyleSeriesListView();
            StyleSeriesCoverView();
            candidateContextMenu.BackColor = PanelBackColor;
            candidateContextMenu.ForeColor = PrimaryTextColor;
            foreach (ToolStripItem item in candidateContextMenu.Items)
            {
                item.BackColor = PanelBackColor;
                item.ForeColor = PrimaryTextColor;
            }
            detailsBox.BackColor = PanelBackColor;
            detailsBox.ForeColor = PrimaryTextColor;
            detailsBox.LinkColor = darkMode ? Color.FromArgb(147, 197, 253) : Color.FromArgb(42, 91, 215);
            detailsBox.ActiveLinkColor = darkMode ? Color.FromArgb(191, 219, 254) : Color.FromArgb(29, 78, 216);
            detailsBox.VisitedLinkColor = darkMode ? Color.FromArgb(196, 181, 253) : Color.FromArgb(88, 80, 160);
            activityLogBox.BackColor = PanelBackColor;
            activityLogBox.ForeColor = PrimaryTextColor;
            reviewTabs.BackColor = PanelBackColor;
            reviewTabs.ForeColor = PrimaryTextColor;
            grid.Refresh();
            deletionGrid.Refresh();
            missingEpisodesGrid.Refresh();
            episodeSearchGrid.Refresh();
            selectedFeedGrid.Refresh();
            UpdateShellNavigationState();
            UpdateShellSeriesHeader();
        }

        private void ApplyMenuTheme(ToolStripItemCollection items)
        {
            foreach (ToolStripItem item in items)
            {
                item.BackColor = PanelBackColor;
                item.ForeColor = PrimaryTextColor;
                var menuItem = item as ToolStripMenuItem;
                if (menuItem != null)
                {
                    ApplyMenuTheme(menuItem.DropDownItems);
                }
            }
        }

        private void ApplyControlTheme(Control control)
        {
            if (control == null)
            {
                return;
            }

            if (control == this || control is TableLayoutPanel || control is Panel)
            {
                var tag = Convert.ToString(control.Tag);
                if (control == this || control == workspacePanel)
                {
                    control.BackColor = AppBackColor;
                }
                else if (string.Equals(tag, "Sidebar", StringComparison.OrdinalIgnoreCase))
                {
                    control.BackColor = SidebarBackColor;
                }
                else if (string.Equals(tag, "Inspector", StringComparison.OrdinalIgnoreCase))
                {
                    control.BackColor = InspectorBackColor;
                }
                else if (string.Equals(tag, "CommandBar", StringComparison.OrdinalIgnoreCase))
                {
                    control.BackColor = darkMode ? Color.FromArgb(13, 19, 24) : PanelBackColor;
                }
                else
                {
                    control.BackColor = PanelBackColor;
                }
            }
            else if (control is GroupBox)
            {
                StyleGroupBox((GroupBox)control);
            }
            else if (control is TextBox)
            {
                control.BackColor = PanelBackColor;
                control.ForeColor = PrimaryTextColor;
            }
            else if (control is Label)
            {
                control.BackColor = PanelBackColor;
                control.ForeColor = control == statusLabel ? SecondaryTextColor : PrimaryTextColor;
            }
            else if (control is Button)
            {
                var button = (Button)control;
                if (button == deleteButton || button == inspectorDeleteButton)
                {
                    StyleDeleteButton(button);
                }
                else if (button == topScanButton || button == inspectorKeepButton)
                {
                    StyleButton(button, true);
                }
                else if (string.Equals(Convert.ToString(button.Tag), "CloseButton", StringComparison.OrdinalIgnoreCase))
                {
                    button.FlatStyle = FlatStyle.Flat;
                    button.FlatAppearance.BorderSize = 0;
                    button.BackColor = PanelBackColor;
                    button.ForeColor = SecondaryTextColor;
                    button.Cursor = Cursors.Hand;
                }
                else
                {
                    StyleButton(button, false);
                }
            }
            else if (control is ListView)
            {
                ((ListView)control).BackColor = PanelBackColor;
                ((ListView)control).ForeColor = PrimaryTextColor;
            }
            else if (control is PictureBox)
            {
                control.BackColor = PanelBackColor;
            }
            else if (control is DataGridView)
            {
                StyleGrid((DataGridView)control);
            }

            foreach (Control child in control.Controls)
            {
                ApplyControlTheme(child);
            }
        }

        private static void SetColumnVisibility(DataGridView targetGrid, string propertyName, bool visible)
        {
            foreach (DataGridViewColumn column in targetGrid.Columns)
            {
                if (column.DataPropertyName == propertyName)
                {
                    column.Visible = visible;
                    return;
                }
            }
        }

        private void ApplySavedColumnLayout()
        {
            var layout = GridColumnLayoutStore.Load();
            if (layout.Count == 0)
            {
                return;
            }

            restoringColumnLayout = true;
            try
            {
                ApplyColumnLayout(grid, layout);
                ApplyColumnLayout(deletionGrid, layout);
            }
            finally
            {
                restoringColumnLayout = false;
            }
        }

        private static void ApplyColumnLayout(DataGridView targetGrid, List<GridColumnLayoutItem> layout)
        {
            var byProperty = layout
                .Where(x => !string.IsNullOrWhiteSpace(x.PropertyName))
                .GroupBy(x => x.PropertyName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            var visibleCount = layout.Count(x => x.Visible);

            foreach (DataGridViewColumn column in targetGrid.Columns)
            {
                GridColumnLayoutItem item;
                if (!byProperty.TryGetValue(column.DataPropertyName, out item))
                {
                    continue;
                }

                if (item.Width >= 40)
                {
                    column.Width = item.Width;
                }
                if (visibleCount > 0)
                {
                    column.Visible = item.Visible;
                }
            }

            var ordered = targetGrid.Columns.Cast<DataGridViewColumn>()
                .Where(x => byProperty.ContainsKey(x.DataPropertyName))
                .OrderBy(x => byProperty[x.DataPropertyName].DisplayIndex)
                .ToList();
            for (var i = 0; i < ordered.Count; i++)
            {
                ordered[i].DisplayIndex = i;
            }
        }

        private void Grid_ColumnLayoutChanged(object sender, DataGridViewColumnEventArgs e)
        {
            if (restoringColumnLayout)
            {
                return;
            }

            var sourceGrid = sender as DataGridView;
            if (sourceGrid == null)
            {
                return;
            }

            var layout = CaptureColumnLayout(sourceGrid);
            restoringColumnLayout = true;
            try
            {
                ApplyColumnLayout(sourceGrid == grid ? deletionGrid : grid, layout);
            }
            finally
            {
                restoringColumnLayout = false;
            }

            GridColumnLayoutStore.Save(sourceGrid.Columns.Cast<DataGridViewColumn>());
        }

        private static List<GridColumnLayoutItem> CaptureColumnLayout(DataGridView sourceGrid)
        {
            return sourceGrid.Columns.Cast<DataGridViewColumn>()
                .Where(x => !string.IsNullOrWhiteSpace(x.DataPropertyName))
                .Select(x => new GridColumnLayoutItem
                {
                    PropertyName = x.DataPropertyName,
                    DisplayIndex = x.DisplayIndex,
                    Width = x.Width,
                    Visible = x.Visible
                })
                .ToList();
        }

        private void SaveColumnLayout()
        {
            GridColumnLayoutStore.Save(grid.Columns.Cast<DataGridViewColumn>());
        }

        private void RootBox_TextChanged(object sender, EventArgs e)
        {
            UpdateDashboard();
        }

        private void SearchBox_TextChanged(object sender, EventArgs e)
        {
            activeSearchText = searchBox.Text.Trim();
            PopulateSeriesPanel();
            RefreshVisibleRows();
        }

        private void CancelButton_Click(object sender, EventArgs e)
        {
            if (!busyState)
            {
                return;
            }

            cancelRequested = true;
            busyNoticeCancelButton.Enabled = false;
            busyNoticeCancelButton.Text = "Stopping...";
            UpdateActivity("Stopping after the current safe step...", true);
        }

        private void MainForm_Shown(object sender, EventArgs e)
        {
            if (BetaNoticeStore.HasSeen)
            {
                return;
            }

            ShowBetaGuide();
            try
            {
                BetaNoticeStore.MarkSeen();
            }
            catch (Exception ex)
            {
                LogException("Unable to save beta guide seen state", ex);
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            SaveUiLayoutSettings();
            selectedFeedServerRunning = false;
            try
            {
                if (selectedFeedServer != null)
                {
                    selectedFeedServer.Stop();
                }
            }
            catch
            {
            }

            base.OnFormClosing(e);
        }

        private void SaveUiLayoutSettings()
        {
            try
            {
                UiLayoutSettingsStore.Save(new UiLayoutSettings
                {
                    DarkMode = darkMode,
                    ShowSeriesCovers = showSeriesCovers,
                    CandidatesPanelCollapsed = candidatesPanelCollapsed,
                    DeletionPanelCollapsed = deletionPanelCollapsed,
                    MissingEpisodesPanelCollapsed = missingEpisodesPanelCollapsed,
                    EpisodeSearchPanelCollapsed = episodeSearchPanelCollapsed,
                    SelectedFeedPanelCollapsed = selectedFeedPanelCollapsed
                });
            }
            catch (Exception ex)
            {
                LogException("Unable to save UI layout settings", ex);
            }
        }

        private void HelpGuideMenuItem_Click(object sender, EventArgs e)
        {
            ShowBetaGuide();
        }

        private void HelpCredentialMenuItem_Click(object sender, EventArgs e)
        {
            MessageBox.Show(
                this,
                "Metadata provider settings are saved in local files next to the EXE.\r\n\r\n" +
                "AniDB HTTP XML requests use client duplikates version 1 and do not require an AniDB login.\r\n\r\n" +
                "TVDB and TMDB API credentials are stored locally and protected with Windows user-level data protection when saved by the app.\r\n\r\n" +
                "This product uses the TMDB API but is not endorsed or certified by TMDB.\r\n\r\n" +
                "Older saved AniDB UDP credentials are no longer used by the normal metadata and cover workflows.",
                "Metadata Providers",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }

        private void ShowBetaGuide()
        {
            MessageBox.Show(
                this,
                "Beta build safety notes\r\n\r\n" +
                "1. Scan a small folder first before using a full library.\r\n" +
                "2. Review Ready to Remove before pressing Delete. Delete sends files to the Recycle Bin.\r\n" +
                "3. Move selected series writes a CSV report so moves can be reviewed afterward.\r\n" +
                "4. Use Cancel if a scan or long-running operation is taking too long.\r\n" +
                "5. Error details are written to SameEpisodeDuplicateFinder.errors.log next to the EXE.\r\n\r\n" +
                "This is still a beta duplicate-file tool. Treat recommendations as review assistance, not permission to delete blindly.",
                "Beta Guide",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }

        private void BrowseButton_Click(object sender, EventArgs e)
        {
            var roots = ShowScanLocationsDialog();
            if (roots.Count > 0)
            {
                StartScan(roots);
            }
        }

        private List<string> ShowScanLocationsDialog()
        {
            var selectedRoots = GetSessionRoots().Where(Directory.Exists).ToList();
            if (selectedRoots.Count == 0)
            {
                var primaryRoot = GetPrimarySessionRoot();
                if (Directory.Exists(primaryRoot))
                {
                    selectedRoots.Add(primaryRoot);
                }
            }

            using (var dialog = new Form())
            using (var locations = new ListBox())
            using (var addButton = new Button())
            using (var removeButton = new Button())
            using (var clearButton = new Button())
            using (var scanButton = new Button())
            using (var cancelButton = new Button())
            using (var hintLabel = new Label())
            using (var buttonPanel = new TableLayoutPanel())
            {
                dialog.Text = "Scan";
                dialog.StartPosition = FormStartPosition.CenterParent;
                dialog.FormBorderStyle = FormBorderStyle.FixedDialog;
                dialog.MinimizeBox = false;
                dialog.MaximizeBox = false;
                dialog.ClientSize = new Size(640, 360);

                hintLabel.Text = "Add primary, secondary, or drive locations. The scan will merge them into one session.";
                hintLabel.Dock = DockStyle.Top;
                hintLabel.Height = 34;
                hintLabel.TextAlign = ContentAlignment.MiddleLeft;
                hintLabel.Padding = new Padding(8, 0, 8, 0);

                locations.Dock = DockStyle.Fill;
                locations.HorizontalScrollbar = true;
                foreach (var root in selectedRoots)
                {
                    locations.Items.Add(root);
                }

                buttonPanel.Dock = DockStyle.Bottom;
                buttonPanel.Height = 38;
                buttonPanel.ColumnCount = 6;
                buttonPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
                buttonPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
                buttonPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
                buttonPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
                buttonPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
                buttonPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));

                addButton.Text = "Add...";
                addButton.Dock = DockStyle.Fill;
                addButton.Click += delegate
                {
                    var current = locations.SelectedItem == null ? GetPrimarySessionRoot() : Convert.ToString(locations.SelectedItem);
                    string selectedPath;
                    if (TryShowExplorerFolderDialog(dialog, "Choose a scan location", current, out selectedPath))
                    {
                        selectedPath = selectedPath.Trim();
                        if (!locations.Items.Cast<object>().Select(Convert.ToString).Any(x => string.Equals(x, selectedPath, StringComparison.OrdinalIgnoreCase)))
                        {
                            locations.Items.Add(selectedPath);
                            locations.SelectedItem = selectedPath;
                        }
                    }
                };

                removeButton.Text = "Remove";
                removeButton.Dock = DockStyle.Fill;
                removeButton.Click += delegate
                {
                    var index = locations.SelectedIndex;
                    if (index >= 0)
                    {
                        locations.Items.RemoveAt(index);
                        if (locations.Items.Count > 0)
                        {
                            locations.SelectedIndex = Math.Min(index, locations.Items.Count - 1);
                        }
                    }
                };

                clearButton.Text = "Clear";
                clearButton.Dock = DockStyle.Fill;
                clearButton.Click += delegate { locations.Items.Clear(); };

                scanButton.Text = "Scan";
                scanButton.Dock = DockStyle.Fill;
                scanButton.DialogResult = DialogResult.OK;
                cancelButton.Text = "Cancel";
                cancelButton.Dock = DockStyle.Fill;
                cancelButton.DialogResult = DialogResult.Cancel;

                buttonPanel.Controls.Add(addButton, 1, 0);
                buttonPanel.Controls.Add(removeButton, 2, 0);
                buttonPanel.Controls.Add(clearButton, 3, 0);
                buttonPanel.Controls.Add(cancelButton, 4, 0);
                buttonPanel.Controls.Add(scanButton, 5, 0);

                dialog.Controls.Add(locations);
                dialog.Controls.Add(hintLabel);
                dialog.Controls.Add(buttonPanel);
                dialog.AcceptButton = scanButton;
                dialog.CancelButton = cancelButton;

                if (dialog.ShowDialog(this) != DialogResult.OK)
                {
                    return new List<string>();
                }

                var roots = locations.Items.Cast<object>()
                    .Select(Convert.ToString)
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Select(x => x.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var missingRoots = roots.Where(x => !Directory.Exists(x)).ToList();
                if (roots.Count == 0)
                {
                    MessageBox.Show(this, "Add at least one folder before scanning.", "Scan", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return new List<string>();
                }

                if (missingRoots.Count > 0)
                {
                    MessageBox.Show(this, "These folders do not exist:\r\n" + string.Join("\r\n", missingRoots.Take(8).ToArray()), "Scan", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return new List<string>();
                }

                return roots;
            }
        }

        private static bool TryShowExplorerFolderDialog(IWin32Window owner, string title, string selectedPath, out string folderPath)
        {
            folderPath = "";
            if (ExplorerFolderDialog.TryShow(owner, title, selectedPath, out folderPath))
            {
                return true;
            }
            return false;
        }

        private void ColumnsButton_Click(object sender, EventArgs e)
        {
            using (var dialog = new Form())
            {
                dialog.Text = "Show or Hide Columns";
                dialog.StartPosition = FormStartPosition.CenterParent;
                dialog.FormBorderStyle = FormBorderStyle.FixedDialog;
                dialog.MinimizeBox = false;
                dialog.MaximizeBox = false;
                dialog.ClientSize = new Size(330, 420);

                var list = new CheckedListBox();
                list.CheckOnClick = true;
                list.Dock = DockStyle.Top;
                list.Height = 330;
                list.IntegralHeight = false;

                foreach (DataGridViewColumn column in grid.Columns)
                {
                    list.Items.Add(column.HeaderText, column.Visible);
                }

                var buttonPanel = new FlowLayoutPanel();
                buttonPanel.Dock = DockStyle.Bottom;
                buttonPanel.Height = 50;
                buttonPanel.FlowDirection = FlowDirection.RightToLeft;
                buttonPanel.Padding = new Padding(8);

                var okButton = new Button();
                okButton.Text = "Apply";
                okButton.Width = 80;
                okButton.Click += delegate
                {
                    if (list.CheckedItems.Count == 0)
                    {
                        MessageBox.Show(dialog, "At least one column must remain visible.", "Columns", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return;
                    }

                    for (var i = 0; i < grid.Columns.Count && i < list.Items.Count; i++)
                    {
                        grid.Columns[i].Visible = list.GetItemChecked(i);
                        deletionGrid.Columns[i].Visible = list.GetItemChecked(i);
                    }

                    SaveColumnLayout();
                    dialog.DialogResult = DialogResult.OK;
                    dialog.Close();
                };

                var cancelButton = new Button();
                cancelButton.Text = "Cancel";
                cancelButton.Width = 80;
                cancelButton.DialogResult = DialogResult.Cancel;

                var showAllButton = new Button();
                showAllButton.Text = "Show All";
                showAllButton.Width = 80;
                showAllButton.Click += delegate
                {
                    for (var i = 0; i < list.Items.Count; i++)
                    {
                        list.SetItemChecked(i, true);
                    }
                };

                buttonPanel.Controls.Add(okButton);
                buttonPanel.Controls.Add(cancelButton);
                buttonPanel.Controls.Add(showAllButton);

                dialog.Controls.Add(list);
                dialog.Controls.Add(buttonPanel);
                dialog.AcceptButton = okButton;
                dialog.CancelButton = cancelButton;
                dialog.ShowDialog(this);
            }
        }

        private void LoadRowsIntoUi(List<EpisodeFile> data, List<EpisodeFile> scannedData)
        {
            allRows.Clear();
            allRows.AddRange(data ?? new List<EpisodeFile>());
            allScannedRows.Clear();
            allScannedRows.AddRange(MergeScannedRowsWithDuplicates(scannedData, allRows));
            ComputeReviewRecommendations(false);
            ShowRows(allRows);
            PopulateSeriesPanel();
            PopulateMissingEpisodesPanel();
            UpdateCandidateTotal();
            RefreshDeletionRows();
            UpdateDashboard();
        }

        private static List<EpisodeFile> MergeScannedRowsWithDuplicates(List<EpisodeFile> scannedData, List<EpisodeFile> duplicateRows)
        {
            if (scannedData == null || scannedData.Count == 0)
            {
                return duplicateRows == null ? new List<EpisodeFile>() : duplicateRows.ToList();
            }

            var duplicateByPath = (duplicateRows ?? new List<EpisodeFile>())
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.Path))
                .GroupBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            var merged = new List<EpisodeFile>();
            foreach (var row in scannedData.Where(x => x != null))
            {
                EpisodeFile duplicate;
                if (!string.IsNullOrWhiteSpace(row.Path) && duplicateByPath.TryGetValue(row.Path, out duplicate))
                {
                    duplicate.LastWriteUtcTicks = row.LastWriteUtcTicks;
                    merged.Add(duplicate);
                }
                else
                {
                    merged.Add(row);
                }
            }

            return merged;
        }

        private void ShowRows(IEnumerable<EpisodeFile> data)
        {
            var includeDeleted = string.Equals(activeReviewFilter, "Marked", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(activeReviewFilter, "Delete", StringComparison.OrdinalIgnoreCase) ||
                                 IsAutoThresholdFilter(activeReviewFilter);
            ReplaceBindingList(
                rows,
                data.Where(x => includeDeleted || !x.Delete)
                    .Where(PassesReviewFilter)
                    .Where(PassesSearchFilter));

            UpdateCandidateTotal();
            RefreshDeletionRows();
        }

        private static void ReplaceBindingList<T>(BindingList<T> target, IEnumerable<T> items)
        {
            if (target == null)
            {
                return;
            }

            target.RaiseListChangedEvents = false;
            try
            {
                target.Clear();
                foreach (var item in items ?? Enumerable.Empty<T>())
                {
                    target.Add(item);
                }
            }
            finally
            {
                target.RaiseListChangedEvents = true;
                target.ResetBindings();
            }
        }

        private bool PassesReviewFilter(EpisodeFile row)
        {
            if (row == null)
            {
                return false;
            }

            if (string.Equals(activeReviewFilter, "Delete", StringComparison.OrdinalIgnoreCase))
            {
                return string.Equals(row.Recommendation, "Delete", StringComparison.OrdinalIgnoreCase);
            }

            if (string.Equals(activeReviewFilter, "AutoHigh", StringComparison.OrdinalIgnoreCase))
            {
                return RecommendationScorer.IsAutoMarkCandidate(row, AutoMarkThreshold.High);
            }

            if (string.Equals(activeReviewFilter, "AutoMedium", StringComparison.OrdinalIgnoreCase))
            {
                return RecommendationScorer.IsAutoMarkCandidate(row, AutoMarkThreshold.Medium);
            }

            if (string.Equals(activeReviewFilter, "AutoLow", StringComparison.OrdinalIgnoreCase))
            {
                return RecommendationScorer.IsAutoMarkCandidate(row, AutoMarkThreshold.Low);
            }

            if (string.Equals(activeReviewFilter, "NeedsReview", StringComparison.OrdinalIgnoreCase))
            {
                return string.Equals(row.ReviewStatus, "Needs review", StringComparison.OrdinalIgnoreCase);
            }

            if (string.Equals(activeReviewFilter, "MissingCover", StringComparison.OrdinalIgnoreCase))
            {
                return string.Equals(row.ArtworkStatus, "Missing cover", StringComparison.OrdinalIgnoreCase);
            }

            if (string.Equals(activeReviewFilter, "Marked", StringComparison.OrdinalIgnoreCase))
            {
                return row.Delete;
            }

            return true;
        }

        private static bool IsAutoThresholdFilter(string filter)
        {
            return string.Equals(filter, "AutoHigh", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(filter, "AutoMedium", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(filter, "AutoLow", StringComparison.OrdinalIgnoreCase);
        }

        private bool PassesSearchFilter(EpisodeFile row)
        {
            if (row == null || string.IsNullOrWhiteSpace(activeSearchText))
            {
                return true;
            }

            return SeriesTitleMatchesSearch(row, activeSearchText);
        }

        internal static bool SeriesTitleMatchesSearch(EpisodeFile row, string searchText)
        {
            if (row == null || string.IsNullOrWhiteSpace(searchText))
            {
                return true;
            }

            return ContainsSearch(row.Title, searchText.Trim());
        }

        private static bool ContainsSearch(string value, string searchText)
        {
            return !string.IsNullOrWhiteSpace(value) &&
                   value.IndexOf(searchText, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void ReviewTabs_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (reviewTabs.SelectedTab == null)
            {
                activeReviewFilter = "All";
            }
            else
            {
                activeReviewFilter = Convert.ToString(reviewTabs.SelectedTab.Tag);
            }

            RefreshVisibleRows();
            LogActivity("Review view: " + reviewTabs.SelectedTab.Text);
        }

        private void UpdateCandidateTotal()
        {
            if (candidateTotalLabel == null)
            {
                return;
            }

            var totalBytes = rows.Sum(x => x.SizeBytes);
            candidateTotalLabel.Text = rows.Count == 0
                ? "No duplicate candidates in the current view."
                : string.Format("{0:N0} candidate file(s) | {1}", rows.Count, FormatByteSize(totalBytes));
            UpdateDashboard();
        }

        private void RefreshDeletionRows()
        {
            ReplaceBindingList(
                deletionRows,
                allRows.Where(x => x.Delete)
                       .OrderBy(x => x.Title, StringComparer.OrdinalIgnoreCase)
                       .ThenBy(x => x.Episode, StringComparer.OrdinalIgnoreCase)
                       .ThenBy(x => x.FileName, StringComparer.OrdinalIgnoreCase));

            UpdateDeletionTotal();
        }

        private void UpdateDeletionTotal()
        {
            if (deletionTotalLabel == null)
            {
                return;
            }

            var totalBytes = deletionRows.Sum(x => x.SizeBytes);
            deletionTotalLabel.Text = deletionRows.Count == 0
                ? "No files marked for removal."
                : string.Format("{0:N0} file(s) ready | {1}", deletionRows.Count, FormatByteSize(totalBytes));
            UpdateDashboard();
        }

        private void RefreshVisibleRows()
        {
            var gridScrollRow = GetFirstDisplayedRowIndex(grid);
            var deletionScrollRow = GetFirstDisplayedRowIndex(deletionGrid);
            if (activeSeriesTag == null)
            {
                ShowRows(allRows);
                RestoreFirstDisplayedRowIndex(grid, gridScrollRow);
                RestoreFirstDisplayedRowIndex(deletionGrid, deletionScrollRow);
                return;
            }

            var episodeFile = activeSeriesTag as EpisodeFile;
            if (episodeFile != null)
            {
                ShowRows(new List<EpisodeFile> { episodeFile });
                UpdateDetails(episodeFile);
                RestoreFirstDisplayedRowIndex(grid, gridScrollRow);
                RestoreFirstDisplayedRowIndex(deletionGrid, deletionScrollRow);
                return;
            }

            var filter = activeSeriesTag as string;
            if (filter == AllSeriesTag || string.IsNullOrWhiteSpace(filter))
            {
                ShowRows(allRows);
                RestoreFirstDisplayedRowIndex(grid, gridScrollRow);
                RestoreFirstDisplayedRowIndex(deletionGrid, deletionScrollRow);
                return;
            }

            var filteredSeries = GetSeriesSourceRows().Where(x => string.Equals(x.Title, filter, StringComparison.OrdinalIgnoreCase)).ToList();
            ShowRows(filteredSeries.Count > 0
                ? filteredSeries
                : allRows.Where(x => string.Equals(x.Title, filter, StringComparison.OrdinalIgnoreCase)));
            RestoreFirstDisplayedRowIndex(grid, gridScrollRow);
            RestoreFirstDisplayedRowIndex(deletionGrid, deletionScrollRow);
        }

        private void RefreshReviewGrids()
        {
            RefreshVisibleRows();
            deletionGrid.Refresh();
            grid.Refresh();
        }

        private void RefreshDeletionView()
        {
            RefreshVisibleRows();
            deletionGrid.Refresh();
        }

        private static int GetFirstDisplayedRowIndex(DataGridView targetGrid)
        {
            if (targetGrid == null || targetGrid.Rows.Count == 0)
            {
                return -1;
            }

            try
            {
                return targetGrid.FirstDisplayedScrollingRowIndex;
            }
            catch (InvalidOperationException)
            {
                return -1;
            }
        }

        private static void RestoreFirstDisplayedRowIndex(DataGridView targetGrid, int rowIndex)
        {
            if (targetGrid == null || rowIndex < 0 || targetGrid.Rows.Count == 0)
            {
                return;
            }

            try
            {
                targetGrid.FirstDisplayedScrollingRowIndex = Math.Min(rowIndex, targetGrid.Rows.Count - 1);
            }
            catch (InvalidOperationException)
            {
            }
        }

        private void UpdateDashboard()
        {
            if (scannedChipLabel == null)
            {
                return;
            }

            var seriesSource = GetSeriesSourceRows().ToList();
            var summary = string.Format(
                "Scanned {0:N0} | Candidates {1:N0} | Visible {2:N0} | Ready {3:N0} | Groups {4:N0} | Locations {5:N0} | {6} | {7} | {8}",
                seriesSource.Count,
                allRows.Count,
                rows.Count,
                deletionRows.Count,
                EpisodeParser.CountDuplicateEpisodeGroups(allRows),
                CountMultiLocationSeries(seriesSource),
                GetFileFormatFilterSummary(),
                GetCacheStatusSummary(),
                GetProviderStatusSummary());

            scannedChipLabel.Text = summary;
            candidatesChipLabel.Text = summary;
            visibleChipLabel.Text = summary;
            deletionChipLabel.Text = summary;
            duplicateChipLabel.Text = summary;
            locationChipLabel.Text = summary;
            filterChipLabel.Text = summary;
            cacheChipLabel.Text = summary;
            providerChipLabel.Text = summary;
            UpdateShellSeriesHeader();
        }

        private void UpdateShellSeriesHeader()
        {
            if (shellSeriesTitleLabel == null)
            {
                return;
            }

            var selectedFile = GetCurrentCandidateFile();
            var selectedTitle = GetSelectedShellSeriesTitle(selectedFile);
            var seriesRows = GetShellSeriesRows(selectedTitle, selectedFile);
            var displayTitle = string.IsNullOrWhiteSpace(selectedTitle) ? "All Series" : selectedTitle;
            var duplicateRows = string.IsNullOrWhiteSpace(selectedTitle)
                ? allRows.ToList()
                : allRows.Where(x => string.Equals(x.Title, selectedTitle, StringComparison.OrdinalIgnoreCase)).ToList();
            var missingRows = string.IsNullOrWhiteSpace(selectedTitle)
                ? missingEpisodeRows.ToList()
                : missingEpisodeRows.Where(x => string.Equals(x.Title, selectedTitle, StringComparison.OrdinalIgnoreCase)).ToList();

            shellSeriesTitleLabel.Text = displayTitle;
            shellSeriesMetaLabel.Text = string.Format(
                "{0:N0} scanned file(s) | {1:N0} duplicate group(s) | {2}",
                seriesRows.Count,
                EpisodeParser.CountDuplicateEpisodeGroups(duplicateRows),
                GetProviderStatusSummary());
            shellScannedStatLabel.Text = string.Format("Scanned\r\n{0:N0} file(s)", seriesRows.Count);
            shellDuplicateStatLabel.Text = string.Format("Duplicates\r\n{0:N0} group(s), {1:N0} file(s)", EpisodeParser.CountDuplicateEpisodeGroups(duplicateRows), duplicateRows.Count);
            shellMissingStatLabel.Text = string.Format("Missing\r\n{0:N0} episode(s)", missingRows.Sum(x => x.MissingCount));
            UpdateProviderBadge(shellAniDbBadgeLabel, "AniDB", "Ready", true);
            UpdateProviderBadge(shellTvDbBadgeLabel, "TVDB", TvDbSettingsStore.Load().HasApiKey ? "Ready" : "Setup", TvDbSettingsStore.Load().HasApiKey);
            UpdateProviderBadge(shellTmDbBadgeLabel, "TMDB", TmDbSettingsStore.Load().HasReadAccessToken ? "Ready" : "Setup", TmDbSettingsStore.Load().HasReadAccessToken);

            var coverRows = seriesRows.Count > 0 ? seriesRows : duplicateRows;
            var coverPath = FindSeriesCoverPath(coverRows);
            var oldImage = shellSeriesCoverBox.Image;
            shellSeriesCoverBox.Image = CreateSeriesCoverImage(displayTitle, coverPath);
            if (oldImage != null)
            {
                oldImage.Dispose();
            }

            if (string.IsNullOrWhiteSpace(coverPath) && !string.IsNullOrWhiteSpace(selectedTitle) && coverRows.Count > 0)
            {
                ScheduleShellSeriesFetch(selectedTitle, coverRows);
            }
            else
            {
                ClearPendingShellSeriesFetch();
            }
        }

        private void ScheduleShellSeriesFetch(string title, List<EpisodeFile> seriesRows)
        {
            if (busyState || string.IsNullOrWhiteSpace(title) || seriesRows == null || seriesRows.Count == 0)
            {
                ClearPendingShellSeriesFetch();
                return;
            }

            lock (shellCoverFetchLock)
            {
                if (shellCoverFetchAttempted.Contains(title) || shellCoverFetchInProgress.Contains(title))
                {
                    ClearPendingShellSeriesFetch();
                    return;
                }

                if (shellCoverFetchInProgress.Count > 0)
                {
                    pendingShellFetchTitle = "";
                    pendingShellFetchRows = new List<EpisodeFile>();
                    AppendDiagnosticLog("COVER", title + ": selected-series cover fetch deferred because another provider request is still running.");
                    return;
                }
            }

            pendingShellFetchTitle = title.Trim();
            pendingShellFetchRows = seriesRows.ToList();
            shellSeriesFetchDebounceTimer.Stop();
            shellSeriesFetchDebounceTimer.Start();
            shellSeriesMetaLabel.Text = shellSeriesMetaLabel.Text + " | Cover: queued";
        }

        private void ClearPendingShellSeriesFetch()
        {
            if (shellSeriesFetchDebounceTimer != null)
            {
                shellSeriesFetchDebounceTimer.Stop();
            }

            pendingShellFetchTitle = "";
            pendingShellFetchRows = new List<EpisodeFile>();
        }

        private void ShellSeriesFetchDebounceTimer_Tick(object sender, EventArgs e)
        {
            shellSeriesFetchDebounceTimer.Stop();
            var title = pendingShellFetchTitle;
            var rows = pendingShellFetchRows == null ? new List<EpisodeFile>() : pendingShellFetchRows.ToList();
            pendingShellFetchTitle = "";
            pendingShellFetchRows = new List<EpisodeFile>();

            var selectedTitle = GetSelectedShellSeriesTitle(GetCurrentCandidateFile());
            if (string.IsNullOrWhiteSpace(title) ||
                rows.Count == 0 ||
                !string.Equals(title, selectedTitle, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            QueueShellSeriesFetch(title, rows);
        }

        private void QueueShellSeriesFetch(string title, List<EpisodeFile> seriesRows)
        {
            if (busyState || string.IsNullOrWhiteSpace(title) || seriesRows == null || seriesRows.Count == 0)
            {
                return;
            }

            var key = title.Trim();
            lock (shellCoverFetchLock)
            {
                if (shellCoverFetchAttempted.Contains(key) || shellCoverFetchInProgress.Contains(key))
                {
                    return;
                }
                if (shellCoverFetchInProgress.Count > 0)
                {
                    AppendDiagnosticLog("COVER", key + ": selected-series cover fetch skipped because another provider request is still running.");
                    return;
                }

                shellCoverFetchAttempted.Add(key);
                shellCoverFetchInProgress.Add(key);
            }

            shellSeriesMetaLabel.Text = shellSeriesMetaLabel.Text + " | Cover: checking";
            UpdateActivity("Queued selected-series metadata and cover fetch: " + title, true);

            var fetchRows = seriesRows.ToList();
            var worker = new BackgroundWorker();
            worker.DoWork += delegate(object sender, DoWorkEventArgs args)
            {
                args.Result = FetchShellSeriesMetadataAndCover(key, fetchRows);
            };
            worker.RunWorkerCompleted += delegate(object sender, RunWorkerCompletedEventArgs args)
            {
                lock (shellCoverFetchLock)
                {
                    shellCoverFetchInProgress.Remove(key);
                }

                if (args.Error != null)
                {
                    UpdateActivity("Selected-series fetch failed for " + key + ": " + args.Error.Message, true);
                    UpdateShellSeriesHeader();
                    return;
                }

                var result = args.Result as ShellSeriesFetchResult;
                if (result == null)
                {
                    UpdateShellSeriesHeader();
                    return;
                }

                var currentTitle = GetSelectedShellSeriesTitle(GetCurrentCandidateFile());
                if (!string.Equals(key, currentTitle, StringComparison.OrdinalIgnoreCase))
                {
                    UpdateActivity(result.Message + " | no longer selected", true);
                    return;
                }

                if (result.MetadataMatch != null && result.MetadataMatch.Found)
                {
                    ApplyAniDbMatches(new Dictionary<string, AniDbAnimeResult>(StringComparer.OrdinalIgnoreCase)
                    {
                        { key, result.MetadataMatch }
                    });
                }

                UpdateShellSeriesHeader();
                UpdateInspectorPreviewForTitle(GetSelectedShellSeriesTitle(GetCurrentCandidateFile()));
                UpdateActivity(result.Message, true);
            };
            worker.RunWorkerAsync();
        }

        private ShellSeriesFetchResult FetchShellSeriesMetadataAndCover(string title, List<EpisodeFile> seriesRows)
        {
            var result = new ShellSeriesFetchResult { Title = title };
            var targetFolder = GetSeriesCoverTargetFolder(seriesRows);

            WaitForShellCoverFetchSlot();

            try
            {
                result.MetadataMatch = LookupAniDbMetadata(title);
            }
            catch (Exception ex)
            {
                result.MetadataError = ex.Message;
            }

            try
            {
                if (string.IsNullOrWhiteSpace(targetFolder))
                {
                    result.CoverMessage = "no writable local folder was available for the series-named cover.";
                }
                else
                {
                    var targetPath = GetSeriesCoverTargetPath(title, targetFolder);
                    var match = result.MetadataMatch != null && result.MetadataMatch.Found
                        ? result.MetadataMatch
                        : GetAniDbMatchForCover(title, seriesRows);
                    if (match != null && match.Found && string.IsNullOrWhiteSpace(match.PictureFile))
                    {
                        AppendDiagnosticLog("COVER", title + ": downloading AniDB picture for match " + match.Title + " (score " + match.Score.ToString("N0") + ")");
                        match.PictureFile = AniDbClient.GetAnimePictureFile(match.AniDbId);
                    }

                    if (match != null && match.Found && !string.IsNullOrWhiteSpace(match.PictureFile))
                    {
                        AppendDiagnosticLog("COVER", title + ": saving AniDB cover from " + match.Title + " (score " + match.Score.ToString("N0") + ")");
                        DownloadAniDbPicture(match.PictureFile, targetPath);
                        result.CoverSaved = true;
                        result.CoverMessage = "saved AniDB cover as " + Path.GetFileName(targetPath);
                    }
                    else
                    {
                        string fallbackMessage;
                        result.CoverSaved = TryDownloadFallbackCover(title, targetPath, out fallbackMessage);
                        result.CoverMessage = result.CoverSaved ? fallbackMessage : DisplayOrDash(fallbackMessage);
                    }
                }
            }
            catch (Exception ex)
            {
                result.CoverMessage = ex.Message;
            }

            result.Message = BuildShellSeriesFetchMessage(result);
            return result;
        }

        private void WaitForShellCoverFetchSlot()
        {
            var delay = TimeSpan.Zero;
            lock (shellCoverFetchLock)
            {
                var elapsed = DateTime.UtcNow - lastShellCoverFetchUtc;
                if (elapsed < TimeSpan.FromSeconds(2))
                {
                    delay = TimeSpan.FromSeconds(2) - elapsed;
                }

                lastShellCoverFetchUtc = DateTime.UtcNow + delay;
            }

            if (delay > TimeSpan.Zero)
            {
                Thread.Sleep(delay);
            }
        }

        private static string BuildShellSeriesFetchMessage(ShellSeriesFetchResult result)
        {
            var metadata = result.MetadataMatch != null && result.MetadataMatch.Found
                ? "metadata matched " + DisplayOrDash(result.MetadataMatch.Title) + " (score " + result.MetadataMatch.Score.ToString("N0") + ")"
                : "metadata " + DisplayOrDash(result.MetadataError ?? (result.MetadataMatch == null ? "" : result.MetadataMatch.Error));
            var cover = result.CoverSaved ? result.CoverMessage : "cover " + DisplayOrDash(result.CoverMessage);
            return "Selected-series fetch: " + result.Title + " | " + metadata + " | " + cover;
        }

        private string GetSelectedShellSeriesTitle(EpisodeFile selectedFile)
        {
            var seriesTitle = activeSeriesTag as string;
            if (!string.IsNullOrWhiteSpace(seriesTitle) && seriesTitle != AllSeriesTag)
            {
                return seriesTitle;
            }

            if (selectedFile != null && !string.IsNullOrWhiteSpace(selectedFile.Title))
            {
                return selectedFile.Title;
            }

            return "";
        }

        private List<EpisodeFile> GetShellSeriesRows(string selectedTitle, EpisodeFile selectedFile)
        {
            if (!string.IsNullOrWhiteSpace(selectedTitle))
            {
                var seriesRows = GetSeriesSourceRows().Where(x => string.Equals(x.Title, selectedTitle, StringComparison.OrdinalIgnoreCase)).ToList();
                if (seriesRows.Count > 0)
                {
                    return seriesRows;
                }
            }

            if (selectedFile != null)
            {
                return new List<EpisodeFile> { selectedFile };
            }

            return GetSeriesSourceRows().ToList();
        }

        private void UpdateProviderBadge(Label label, string provider, string status, bool ok)
        {
            if (label == null)
            {
                return;
            }

            label.Text = FormatProviderBadgeText(provider, status);
            label.BackColor = ok
                ? (darkMode ? Color.FromArgb(27, 67, 50) : Color.FromArgb(220, 252, 231))
                : (darkMode ? Color.FromArgb(77, 51, 31) : Color.FromArgb(255, 237, 213));
            label.ForeColor = ok
                ? (darkMode ? Color.FromArgb(187, 247, 208) : Color.FromArgb(22, 101, 52))
                : (darkMode ? Color.FromArgb(253, 230, 138) : Color.FromArgb(146, 64, 14));
        }

        private static string FormatProviderBadgeText(string provider, string status)
        {
            provider = provider ?? "";
            status = status ?? "";
            return string.IsNullOrWhiteSpace(status) ? provider.Trim() : (provider.Trim() + " " + status.Trim()).Trim();
        }

        private string GetFileFormatFilterSummary()
        {
            if (fileFormatFilter == null || fileFormatFilter.Extensions.Count == 0)
            {
                return "Formats default";
            }

            var listed = string.Join(", ", fileFormatFilter.Extensions.Take(4).ToArray());
            if (fileFormatFilter.Extensions.Count > 4)
            {
                listed += string.Format(" +{0:N0}", fileFormatFilter.Extensions.Count - 4);
            }

            return (fileFormatFilter.AllowOnlyListed ? "Allow " : "Ignore ") + listed;
        }

        private string GetProviderStatusSummary()
        {
            var aniDb = "AniDB HTTP ready";
            var tvDb = TvDbSettingsStore.Load().HasApiKey ? "TVDB ready" : "TVDB missing key";
            var tmDb = TmDbSettingsStore.Load().HasReadAccessToken ? "TMDB ready" : "TMDB missing token";
            return aniDb + " | " + tvDb + " | " + tmDb;
        }
        private string GetCacheStatusSummary()
        {
            var root = GetPrimarySessionRoot();
            if (string.IsNullOrWhiteSpace(root) || !File.Exists(GetCachePath()))
            {
                return "Cache none";
            }

            var cachedRoot = ReadCacheRoot(GetCachePath());
            try
            {
                return string.Equals(NormalizeRoot(cachedRoot), NormalizeRoot(root), StringComparison.OrdinalIgnoreCase)
                    ? "Cache current"
                    : "Cache other";
            }
            catch
            {
                return "Cache other";
            }
        }

        private void UpdateSummary(string prefix)
        {
            if (allRows.Count == 0 && allScannedRows.Count == 0)
            {
                statusLabel.Text = string.IsNullOrWhiteSpace(prefix) ? "No scan loaded." : prefix;
                UpdateDashboard();
                if (!string.IsNullOrWhiteSpace(prefix))
                {
                    UpdateActivity(prefix, false);
                }
                return;
            }

            var seriesSource = GetSeriesSourceRows().ToList();
            var seriesCount = seriesSource.Select(x => x.Title).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            var duplicateGroupCount = EpisodeParser.CountDuplicateEpisodeGroups(allRows);
            var markedCount = allRows.Count(x => x.Delete);
            var scannedCount = seriesSource.Count;
            var summary = string.Format("{0:N0} series | {1:N0} duplicate groups | {2:N0} scanned files | {3:N0} candidates | {4:N0} marked", seriesCount, duplicateGroupCount, scannedCount, allRows.Count, markedCount);
            statusLabel.Text = string.IsNullOrWhiteSpace(prefix) ? summary : prefix + "  " + summary;
            UpdateDashboard();
            if (!string.IsNullOrWhiteSpace(prefix))
            {
                LogActivity(prefix);
            }
        }

        private void UpdateActivity(string text, bool log)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                text = "Idle";
            }

            statusLabel.Text = text;
            UpdateDashboard();
            if (busyState)
            {
                UpdateBusyNotice(true, text);
                statusLabel.Text = "Working in background. " + scannedChipLabel.Text;
            }

            if (log)
            {
                LogActivity(text);
            }
            else
            {
                AppendDiagnosticLog("STATUS", text);
            }
        }

        private void UpdateBusyNotice(bool busy, string text)
        {
            if (busyNoticePanel == null)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                text = "Working...";
            }

            busyNoticeTitleLabel.Text = BuildBusyNoticeTitle(text);
            busyNoticeStatusLabel.Text = text;
            busyNoticeCancelButton.Text = cancelRequested ? "Stopping..." : "Stop Current Task";
            busyNoticeCancelButton.Enabled = busy && !cancelRequested;
            busyNoticePanel.Visible = busy;
            if (busy)
            {
                busyNoticePanel.Refresh();
            }
        }

        private static string BuildBusyNoticeTitle(string text)
        {
            text = text ?? "";
            if (text.IndexOf("scan", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "Scanning in progress";
            }

            if (text.IndexOf("lookup", StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf("fetch", StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf("cover", StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf("metadata", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "Provider request in progress";
            }

            if (text.IndexOf("move", StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf("moving", StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf("delete", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "File operation in progress";
            }

            return "Application busy";
        }

        private void LogActivity(string text)
        {
            if (activityLogBox == null || string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            var line = string.Format("[{0:HH:mm:ss}] {1}", DateTime.Now, text);
            activityLogBox.AppendText(line + Environment.NewLine);
            AppendDiagnosticLog("ACTIVITY", text);
        }

        private static void AppendDiagnosticLog(string category, string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            try
            {
                using (var writer = new StreamWriter(GetDiagnosticLogPath(), true, new UTF8Encoding(true)))
                {
                    writer.WriteLine("[{0:yyyy-MM-dd HH:mm:ss.fff}] {1}: {2}", DateTime.Now, category, ScrubDiagnosticText(text));
                }
            }
            catch
            {
            }
        }

        private static string ScrubDiagnosticText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return "";
            }

            text = Regex.Replace(text, @"(?i)(api[-_ ]?key|token|bearer|authorization)\s*[:=]\s*\S+", "$1=(hidden)");
            text = Regex.Replace(text, @"(?i)Bearer\s+[A-Za-z0-9._~+/\-=]+", "Bearer (hidden)");
            return text;
        }

        private static void LogException(string context, Exception ex)
        {
            try
            {
                using (var writer = new StreamWriter(GetErrorLogPath(), true, new UTF8Encoding(true)))
                {
                    writer.WriteLine("[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " + context);
                    writer.WriteLine(ex == null ? "(no exception details)" : ex.ToString());
                    writer.WriteLine();
                }
                AppendDiagnosticLog("ERROR", context + ": " + (ex == null ? "(no exception details)" : ex.Message));
            }
            catch
            {
            }
        }

        public static void LogUnhandledException(string context, Exception ex)
        {
            LogException(context, ex);
        }

        private void PopulateSeriesPanel()
        {
            seriesListView.BeginUpdate();
            seriesCoverView.BeginUpdate();
            try
            {
                seriesListView.Items.Clear();
                seriesCoverView.Items.Clear();
                seriesCoverImages.Images.Clear();

                var seriesSource = GetSeriesSourceRows().ToList();
                var allItem = new ListViewItem("All duplicate candidates");
                allItem.SubItems.Add(string.Format("{0:N0} duplicate files", allRows.Count));
                allItem.SubItems.Add(FormatTotalSize(allRows));
                allItem.Tag = AllSeriesTag;
                seriesListView.Items.Add(allItem);
                AddSeriesCoverItem("Duplicate Files", AllSeriesTag, allRows.Count);

                foreach (var group in seriesSource.GroupBy(x => x.Title, StringComparer.OrdinalIgnoreCase)
                                                  .Where(g => string.IsNullOrWhiteSpace(activeSearchText) || ContainsSearch(g.Key, activeSearchText))
                                                  .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
                {
                    var files = group.OrderBy(x => x.Episode, StringComparer.OrdinalIgnoreCase)
                                     .ThenBy(x => x.FileName, StringComparer.OrdinalIgnoreCase)
                                     .ToList();
                    var locations = GetDistinctLocations(files);
                    if (locations.Count < 2)
                    {
                        continue;
                    }

                    var duplicateCount = allRows.Count(x => string.Equals(x.Title, group.Key, StringComparison.OrdinalIgnoreCase));
                    var aniDbMatch = files.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.AniDbDisplay));
                    var seriesText = aniDbMatch == null ? group.Key : string.Format("{0} -> {1}", group.Key, aniDbMatch.AniDbDisplay);
                    var seriesItem = new ListViewItem(seriesText);
                    seriesItem.SubItems.Add(string.Format("{0:N0} locations | {1:N0} scanned | {2:N0} duplicate", locations.Count, files.Count, duplicateCount));
                    seriesItem.SubItems.Add(FormatTotalSize(files));
                    seriesItem.Tag = group.Key;
                    if (duplicateCount > 0)
                    {
                        seriesItem.Font = new Font(seriesListView.Font, FontStyle.Bold);
                        seriesItem.BackColor = DuplicateSeriesBackColor;
                        seriesItem.ForeColor = DuplicateSeriesForeColor;
                        seriesItem.ToolTipText = string.Format("{0:N0} duplicate candidate file(s) found in this series.", duplicateCount);
                    }
                    seriesListView.Items.Add(seriesItem);

                    AddSeriesCoverItem(group.Key, group.Key, files.Count);
                }

                activeSeriesTag = AllSeriesTag;
                if (seriesListView.Items.Count > 0)
                {
                    seriesListView.Items[0].Selected = true;
                }
                if (seriesCoverView.Items.Count > 0)
                {
                    seriesCoverView.Items[0].Selected = true;
                }
            }
            finally
            {
                seriesCoverView.EndUpdate();
                seriesListView.EndUpdate();
            }
        }

        private IEnumerable<EpisodeFile> GetSeriesSourceRows()
        {
            return allScannedRows.Count > 0 ? allScannedRows : allRows;
        }

        private void PopulateMissingEpisodesPanel()
        {
            if (missingEpisodeRows == null)
            {
                return;
            }

            ReplaceBindingList(missingEpisodeRows, BuildMissingEpisodeRows(GetSeriesSourceRows()));

            missingEpisodesTotalLabel.Text = missingEpisodeRows.Count == 0
                ? "No local episode gaps found."
                : string.Format("{0:N0} series/season gap(s) found.", missingEpisodeRows.Count);
        }

        internal static List<MissingEpisodeRow> BuildMissingEpisodeRows(IEnumerable<EpisodeFile> files)
        {
            return MissingEpisodeAnalyzer.BuildLocalGapRows(files);
        }

        private static int CountMultiLocationSeries(IEnumerable<EpisodeFile> source)
        {
            return source.GroupBy(x => x.Title, StringComparer.OrdinalIgnoreCase)
                         .Count(g => GetDistinctLocations(g).Count > 1);
        }

        private static List<string> GetDistinctLocations(IEnumerable<EpisodeFile> files)
        {
            return files.Select(GetLocationKey)
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
        }

        private static string GetLocationKey(EpisodeFile file)
        {
            if (file == null)
            {
                return "";
            }

            if (!string.IsNullOrWhiteSpace(file.FileLocation))
            {
                return file.FileLocation.Trim().TrimEnd('\\');
            }

            if (string.IsNullOrWhiteSpace(file.Path))
            {
                return "";
            }

            var folder = Path.GetDirectoryName(file.Path);
            return string.IsNullOrWhiteSpace(folder) ? "" : folder.Trim().TrimEnd('\\');
        }

        private static string FormatTotalSize(IEnumerable<EpisodeFile> files)
        {
            var bytes = files == null ? 0L : files.Sum(x => x.SizeBytes);
            return FormatByteSize(bytes);
        }

        private static string FormatByteSize(long bytes)
        {
            if (bytes <= 0)
            {
                return "0 B";
            }

            var units = new[] { "B", "KB", "MB", "GB", "TB", "PB" };
            decimal value = bytes;
            var unitIndex = 0;
            while (value >= 1024M && unitIndex < units.Length - 1)
            {
                value /= 1024M;
                unitIndex++;
            }

            return unitIndex == 0
                ? string.Format("{0:N0} {1}", value, units[unitIndex])
                : string.Format("{0:N2} {1}", value, units[unitIndex]);
        }

        private void SeriesListView_ItemSelectionChanged(object sender, ListViewItemSelectionChangedEventArgs e)
        {
            SeriesItemSelectionChanged(e);
        }

        private void SeriesCoverView_ItemSelectionChanged(object sender, ListViewItemSelectionChangedEventArgs e)
        {
            SeriesItemSelectionChanged(e);
        }

        private void SeriesItemSelectionChanged(ListViewItemSelectionChangedEventArgs e)
        {
            if (!e.IsSelected || e.Item == null)
            {
                return;
            }

            ApplySeriesFilter(e.Item.Tag);
        }

        private void MissingEpisodesGrid_CellDoubleClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || e.RowIndex >= missingEpisodesGrid.Rows.Count)
            {
                return;
            }

            var row = missingEpisodesGrid.Rows[e.RowIndex].DataBoundItem as MissingEpisodeRow;
            if (row == null || string.IsNullOrWhiteSpace(row.Title))
            {
                return;
            }

            ApplySeriesFilter(row.Title);
            RunEpisodeSearch(row);
        }

        private void MissingEpisodesGrid_SelectionChanged(object sender, EventArgs e)
        {
            var row = GetSelectedMissingEpisodeRow();
            episodeSearchButton.Enabled = !busyState && row != null;
            episodeSearchAllButton.Enabled = !busyState && row != null;
            if (row != null)
            {
                PopulateEpisodeSearchFilters(row);
                UpdateDetails(row);
            }
        }

        private void EpisodeSearchGrid_CellDoubleClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || e.RowIndex >= episodeSearchGrid.Rows.Count)
            {
                return;
            }

            var row = episodeSearchGrid.Rows[e.RowIndex].DataBoundItem as EpisodeSearchResult;
            if (row != null)
            {
                UpdateDetails(row);
                AddSelectedFeedItem(row);
            }
        }

        private void EpisodeSearchGrid_SelectionChanged(object sender, EventArgs e)
        {
            var result = GetSelectedEpisodeSearchResult();
            episodeSearchAddButton.Enabled = !busyState && result != null;
            if (result != null)
            {
                UpdateDetails(result);
            }
        }

        private void EpisodeSearchButton_Click(object sender, EventArgs e)
        {
            RunEpisodeSearch(GetSelectedMissingEpisodeRow());
        }

        private void EpisodeSearchAllButton_Click(object sender, EventArgs e)
        {
            RunEpisodeSearchBatch(GetBatchMissingEpisodeRows());
        }

        private void EpisodeSearchAddButton_Click(object sender, EventArgs e)
        {
            AddSelectedFeedItem(GetSelectedEpisodeSearchResult());
        }

        private void SelectedFeedGrid_CellDoubleClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || e.RowIndex >= selectedFeedGrid.Rows.Count)
            {
                return;
            }

            var row = selectedFeedGrid.Rows[e.RowIndex].DataBoundItem as SelectedSearchFeedItem;
            if (row != null && !string.IsNullOrWhiteSpace(row.MagnetLink))
            {
                OpenShellPath(row.MagnetLink);
            }
        }

        private void SelectedFeedGrid_SelectionChanged(object sender, EventArgs e)
        {
            var item = GetSelectedFeedItem();
            selectedFeedRemoveButton.Enabled = !busyState && item != null;
            if (item != null)
            {
                UpdateDetails(item);
            }
        }

        private void SelectedFeedOpenButton_Click(object sender, EventArgs e)
        {
            OpenShellPath(GetSelectedFeedLocation());
        }

        private void SelectedFeedCopyButton_Click(object sender, EventArgs e)
        {
            var location = GetSelectedFeedLocation();
            if (string.IsNullOrWhiteSpace(location))
            {
                MessageBox.Show(this, "No selected RSS feed location is available yet.", "Selected RSS", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            Clipboard.SetText(location);
            UpdateActivity("Copied selected RSS feed URL.", true);
        }

        private void OpenDiagnosticLogMenuItem_Click(object sender, EventArgs e)
        {
            EnsureDiagnosticLogExists();
            UpdateActivity("Opening diagnostic log: " + GetDiagnosticLogPath(), true);
            OpenShellPath(GetDiagnosticLogPath());
        }

        private void CopyDiagnosticLogMenuItem_Click(object sender, EventArgs e)
        {
            EnsureDiagnosticLogExists();
            Clipboard.SetText(GetDiagnosticLogPath());
            UpdateActivity("Copied diagnostic log path.", true);
        }

        private static void EnsureDiagnosticLogExists()
        {
            try
            {
                var path = GetDiagnosticLogPath();
                if (!File.Exists(path))
                {
                    File.WriteAllText(path, "", new UTF8Encoding(true));
                }
            }
            catch
            {
            }
        }

        private void SelectedFeedRemoveButton_Click(object sender, EventArgs e)
        {
            var item = GetSelectedFeedItem();
            if (item == null)
            {
                return;
            }

            selectedFeedRows.Remove(item);
            SaveAndRefreshSelectedFeed();
        }

        private void SelectedFeedClearButton_Click(object sender, EventArgs e)
        {
            if (selectedFeedRows.Count == 0)
            {
                return;
            }

            selectedFeedRows.Clear();
            SaveAndRefreshSelectedFeed();
        }

        private void SettingsMetadataButton_Click(object sender, EventArgs e)
        {
            HelpCredentialMenuItem_Click(sender, e);
        }

        private void SettingsFileFormatsButton_Click(object sender, EventArgs e)
        {
            FileFormatsMenuItem_Click(sender, e);
        }

        private void SettingsMonitorButton_Click(object sender, EventArgs e)
        {
            toolsMonitorFoldersMenuItem.Checked = !toolsMonitorFoldersMenuItem.Checked;
            MonitorFoldersMenuItem_Click(sender, e);
            UpdateSettingsSummary();
        }

        private void SettingsThemeButton_Click(object sender, EventArgs e)
        {
            viewDarkModeMenuItem.Checked = !viewDarkModeMenuItem.Checked;
            ToggleDarkModeMenuItem_Click(sender, e);
            UpdateSettingsSummary();
        }

        private void SettingsFileBotButton_Click(object sender, EventArgs e)
        {
            FileBotMenuItem_Click(sender, e);
        }

        private void UpdateSettingsSummary()
        {
            if (settingsSummaryLabel == null)
            {
                return;
            }

            settingsSummaryLabel.Text = string.Format(
                "Providers: {0} | File formats: {1} | Monitoring: {2} | Theme: {3}",
                GetProviderStatusSummary(),
                GetFileFormatFilterSummary(),
                toolsMonitorFoldersMenuItem.Checked ? "On" : "Off",
                darkMode ? "Dark" : "Light");
        }

        private void DetailsBox_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            var target = e.Link == null ? null : e.Link.LinkData as string;
            if (!string.IsNullOrWhiteSpace(target))
            {
                OpenShellPath(target);
            }
        }

        private MissingEpisodeRow GetSelectedMissingEpisodeRow()
        {
            if (missingEpisodesGrid == null || missingEpisodesGrid.CurrentRow == null)
            {
                return null;
            }

            return missingEpisodesGrid.CurrentRow.DataBoundItem as MissingEpisodeRow;
        }

        private List<MissingEpisodeRow> GetBatchMissingEpisodeRows()
        {
            var selected = GetSelectedMissingEpisodeRow();
            if (selected != null)
            {
                return missingEpisodeRows
                    .Where(x => x != null &&
                                string.Equals(x.Title, selected.Title, StringComparison.OrdinalIgnoreCase) &&
                                string.Equals(x.Scope, selected.Scope, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            var selectedTitle = GetSelectedShellSeriesTitle(GetCurrentCandidateFile());
            if (!string.IsNullOrWhiteSpace(selectedTitle))
            {
                return missingEpisodeRows
                    .Where(x => x != null && string.Equals(x.Title, selectedTitle, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            return missingEpisodeRows.ToList();
        }

        private void FocusMissingEpisodesForActiveSeries()
        {
            if (missingEpisodesGrid == null || missingEpisodeRows == null || missingEpisodeRows.Count == 0)
            {
                return;
            }

            var selectedTitle = GetSelectedShellSeriesTitle(GetCurrentCandidateFile());
            if (string.IsNullOrWhiteSpace(selectedTitle))
            {
                missingEpisodesTotalLabel.Text = string.Format("{0:N0} series/season gap(s) found.", missingEpisodeRows.Count);
                return;
            }

            var match = missingEpisodeRows.FirstOrDefault(x => string.Equals(x.Title, selectedTitle, StringComparison.OrdinalIgnoreCase));
            if (match == null)
            {
                missingEpisodesTotalLabel.Text = "No missing episode rows for " + selectedTitle + ".";
                return;
            }

            var index = missingEpisodeRows.IndexOf(match);
            if (index >= 0 && index < missingEpisodesGrid.Rows.Count)
            {
                missingEpisodesGrid.ClearSelection();
                missingEpisodesGrid.Rows[index].Selected = true;
                missingEpisodesGrid.CurrentCell = missingEpisodesGrid.Rows[index].Cells[0];
                missingEpisodesGrid.FirstDisplayedScrollingRowIndex = index;
                missingEpisodesTotalLabel.Text = "Focused missing episodes for " + selectedTitle + ".";
            }
        }

        private EpisodeSearchResult GetSelectedEpisodeSearchResult()
        {
            if (episodeSearchGrid == null || episodeSearchGrid.CurrentRow == null)
            {
                return null;
            }

            return episodeSearchGrid.CurrentRow.DataBoundItem as EpisodeSearchResult;
        }

        private SelectedSearchFeedItem GetSelectedFeedItem()
        {
            if (selectedFeedGrid == null || selectedFeedGrid.CurrentRow == null)
            {
                return null;
            }

            return selectedFeedGrid.CurrentRow.DataBoundItem as SelectedSearchFeedItem;
        }

        private void PopulateEpisodeSearchFilters(MissingEpisodeRow row)
        {
            var selectedGroup = Convert.ToString(episodeSearchGroupBox.SelectedItem);
            episodeSearchGroupBox.Items.Clear();
            episodeSearchGroupBox.Items.Add("Any group");
            foreach (var group in GetSeriesSourceRows()
                .Where(x => row != null && string.Equals(x.Title, row.Title, StringComparison.OrdinalIgnoreCase))
                .Select(x => x.SubtitleGroup)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                episodeSearchGroupBox.Items.Add(group);
            }

            SelectComboValueOrDefault(episodeSearchGroupBox, selectedGroup);

            var selectedResolution = Convert.ToString(episodeSearchResolutionBox.SelectedItem);
            episodeSearchResolutionBox.Items.Clear();
            episodeSearchResolutionBox.Items.Add("Any resolution");
            var resolutions = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in new[] { "2160p", "1080p", "720p", "480p" })
            {
                resolutions.Add(item);
            }
            foreach (var file in GetSeriesSourceRows().Where(x => row != null && string.Equals(x.Title, row.Title, StringComparison.OrdinalIgnoreCase)))
            {
                foreach (Match match in Regex.Matches((file.FileName ?? "") + " " + (file.Path ?? ""), @"\b(2160p|1080p|720p|576p|480p)\b", RegexOptions.IgnoreCase))
                {
                    resolutions.Add(match.Value.ToLowerInvariant());
                }
            }
            foreach (var resolution in resolutions.OrderByDescending(ParseResolutionHeight))
            {
                episodeSearchResolutionBox.Items.Add(resolution);
            }

            SelectComboValueOrDefault(episodeSearchResolutionBox, selectedResolution);
        }

        private static void SelectComboValueOrDefault(ComboBox comboBox, string value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                for (var i = 0; i < comboBox.Items.Count; i++)
                {
                    if (string.Equals(Convert.ToString(comboBox.Items[i]), value, StringComparison.OrdinalIgnoreCase))
                    {
                        comboBox.SelectedIndex = i;
                        return;
                    }
                }

                comboBox.Items.Add(value);
                comboBox.SelectedIndex = comboBox.Items.Count - 1;
                return;
            }

            comboBox.SelectedIndex = comboBox.Items.Count > 0 ? 0 : -1;
        }

        private static int ParseResolutionHeight(string value)
        {
            var match = Regex.Match(value ?? "", @"\d+");
            int parsed;
            return match.Success && int.TryParse(match.Value, out parsed) ? parsed : 0;
        }

        private static string SelectedFilterValue(ComboBox comboBox, string anyText)
        {
            var value = Convert.ToString(comboBox.SelectedItem);
            return string.Equals(value, anyText, StringComparison.OrdinalIgnoreCase) ? "" : value;
        }

        private void RunEpisodeSearch(MissingEpisodeRow row)
        {
            var missingEpisode = MissingEpisodeAnalyzer.ToSearchMissingEpisode(row);
            if (missingEpisode == null)
            {
                MessageBox.Show(this, "Select a missing episode row before searching.", "Episode Search", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            activeShellSection = "EpisodeSearch";
            episodeSearchPanelCollapsed = false;
            ApplyWorkspacePanelVisibility();
            SetBusy(true, "Searching for " + missingEpisode.SearchQuery + "...");
            var worker = new BackgroundWorker();
            worker.DoWork += delegate(object workerSender, DoWorkEventArgs args)
            {
                var service = new EpisodeSearchService();
                args.Result = service.Search(
                    missingEpisode,
                    SelectedFilterValue(episodeSearchGroupBox, "Any group"),
                    SelectedFilterValue(episodeSearchResolutionBox, "Any resolution"));
            };
            worker.RunWorkerCompleted += delegate(object workerSender, RunWorkerCompletedEventArgs args)
            {
                try
                {
                    if (args.Error != null)
                    {
                        throw args.Error;
                    }

                    episodeSearchRows.Clear();
                    foreach (var result in (List<EpisodeSearchResult>)args.Result)
                    {
                        StampEpisodeSearchResult(result, missingEpisode);
                        episodeSearchRows.Add(result);
                    }

                    episodeSearchTotalLabel.Text = string.Format("{0:N0} result(s) for {1}", episodeSearchRows.Count, missingEpisode.SearchQuery);
                    UpdateActivity("Episode search complete: " + missingEpisode.SearchQuery, true);
                }
                catch (Exception ex)
                {
                    LogException("Episode search failed", ex);
                    MessageBox.Show(this, ex.Message, "Episode search failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    UpdateActivity("Episode search failed.", true);
                }
                finally
                {
                    SetBusy(false, statusLabel.Text);
                    episodeSearchAddButton.Enabled = GetSelectedEpisodeSearchResult() != null;
                }
            };
            worker.RunWorkerAsync();
        }

        private void RunEpisodeSearchBatch(List<MissingEpisodeRow> rowsToSearch)
        {
            rowsToSearch = (rowsToSearch ?? new List<MissingEpisodeRow>())
                .Where(x => MissingEpisodeAnalyzer.ToSearchMissingEpisode(x) != null)
                .ToList();
            if (rowsToSearch.Count == 0)
            {
                MessageBox.Show(this, "Select a missing episode row before batch searching.", "Episode Search", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (rowsToSearch.Count > 5)
            {
                var confirm = MessageBox.Show(
                    this,
                    string.Format("Search {0:N0} missing episodes using the current group and resolution filters?\r\n\r\nSearches run one at a time with a short pause between RSS requests.", rowsToSearch.Count),
                    "Episode Search",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question);
                if (confirm != DialogResult.Yes)
                {
                    return;
                }
            }

            var releaseGroup = SelectedFilterValue(episodeSearchGroupBox, "Any group");
            var resolution = SelectedFilterValue(episodeSearchResolutionBox, "Any resolution");
            activeShellSection = "EpisodeSearch";
            episodeSearchPanelCollapsed = false;
            ApplyWorkspacePanelVisibility();
            SetBusy(true, string.Format("Batch searching {0:N0} missing episode(s)...", rowsToSearch.Count));
            var worker = new BackgroundWorker();
            worker.WorkerReportsProgress = true;
            worker.DoWork += delegate(object workerSender, DoWorkEventArgs args)
            {
                var backgroundWorker = (BackgroundWorker)workerSender;
                var service = new EpisodeSearchService();
                var results = new List<EpisodeSearchResult>();
                for (var i = 0; i < rowsToSearch.Count; i++)
                {
                    ThrowIfCancellationRequested(delegate { return cancelRequested; });
                    var missingEpisode = MissingEpisodeAnalyzer.ToSearchMissingEpisode(rowsToSearch[i]);
                    if (missingEpisode == null)
                    {
                        continue;
                    }

                    backgroundWorker.ReportProgress(0, string.Format("Episode search {0:N0}/{1:N0}: {2}", i + 1, rowsToSearch.Count, missingEpisode.SearchQuery));
                    foreach (var result in service.Search(missingEpisode, releaseGroup, resolution))
                    {
                        StampEpisodeSearchResult(result, missingEpisode);
                        results.Add(result);
                    }

                    if (i + 1 < rowsToSearch.Count)
                    {
                        Thread.Sleep(1500);
                    }
                }

                args.Result = results;
            };
            worker.ProgressChanged += delegate(object workerSender, ProgressChangedEventArgs args)
            {
                UpdateActivity(args.UserState as string ?? "Batch searching episodes...", false);
            };
            worker.RunWorkerCompleted += delegate(object workerSender, RunWorkerCompletedEventArgs args)
            {
                try
                {
                    if (args.Error is OperationCanceledException)
                    {
                        UpdateActivity("Episode search canceled.", true);
                        return;
                    }

                    if (args.Error != null)
                    {
                        throw args.Error;
                    }

                    var results = (List<EpisodeSearchResult>)args.Result;
                    episodeSearchRows.Clear();
                    foreach (var result in results)
                    {
                        episodeSearchRows.Add(result);
                    }

                    episodeSearchTotalLabel.Text = string.Format("{0:N0} result(s) for {1:N0} missing episode search(es)", episodeSearchRows.Count, rowsToSearch.Count);
                    UpdateActivity("Batch episode search complete.", true);
                }
                catch (Exception ex)
                {
                    LogException("Batch episode search failed", ex);
                    MessageBox.Show(this, ex.Message, "Episode search failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    UpdateActivity("Batch episode search failed.", true);
                }
                finally
                {
                    SetBusy(false, statusLabel.Text);
                    episodeSearchAddButton.Enabled = GetSelectedEpisodeSearchResult() != null;
                }
            };
            worker.RunWorkerAsync();
        }

        private static void StampEpisodeSearchResult(EpisodeSearchResult result, MissingEpisode missingEpisode)
        {
            if (result == null || missingEpisode == null)
            {
                return;
            }

            result.SeriesTitle = missingEpisode.SeriesTitle;
            result.MissingEpisode = result.IsBatchResult ? "Complete series" : missingEpisode.EpisodeCode;
            if (string.IsNullOrWhiteSpace(result.SearchQuery))
            {
                result.SearchQuery = missingEpisode.SearchQuery;
            }
        }

        private void AddSelectedFeedItem(EpisodeSearchResult result)
        {
            if (result == null)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(result.MagnetLink) && string.IsNullOrWhiteSpace(result.Link))
            {
                MessageBox.Show(this, "That search result does not include a link to add.", "Selected Feed", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var missing = GetSelectedMissingEpisodeRow();
            var search = MissingEpisodeAnalyzer.ToSearchMissingEpisode(missing);
            var key = !string.IsNullOrWhiteSpace(result.MagnetLink) ? result.MagnetLink : result.Link;
            var existing = selectedFeedRows.FirstOrDefault(x =>
                string.Equals(!string.IsNullOrWhiteSpace(x.MagnetLink) ? x.MagnetLink : x.Link, key, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                ShowSelectedFeedPanel();
                selectedFeedGrid.ClearSelection();
                var index = selectedFeedRows.IndexOf(existing);
                if (index >= 0 && index < selectedFeedGrid.Rows.Count)
                {
                    selectedFeedGrid.Rows[index].Selected = true;
                    selectedFeedGrid.CurrentCell = selectedFeedGrid.Rows[index].Cells[0];
                }
                return;
            }

            selectedFeedRows.Add(new SelectedSearchFeedItem
            {
                SeriesTitle = !string.IsNullOrWhiteSpace(result.SeriesTitle) ? result.SeriesTitle : (missing == null ? "" : missing.Title),
                MissingEpisode = !string.IsNullOrWhiteSpace(result.MissingEpisode) ? result.MissingEpisode : (missing == null ? "" : missing.MissingEpisodes),
                SearchQuery = !string.IsNullOrWhiteSpace(result.SearchQuery) ? result.SearchQuery : (search == null ? "" : search.SearchQuery),
                Provider = result.Provider,
                Title = result.Title,
                Size = result.Size,
                Seeders = result.Seeders,
                Published = result.Published,
                Link = result.Link,
                MagnetLink = result.MagnetLink,
                IsBatchResult = result.IsBatchResult,
                AddedUtc = DateTime.UtcNow
            });

            if (result.IsBatchResult)
            {
                PrepareExistingSeriesFilesForFullSeasonReplacement(result.SeriesTitle);
            }

            ShowSelectedFeedPanel();
            SaveAndRefreshSelectedFeed();
        }

        private void PrepareExistingSeriesFilesForFullSeasonReplacement(string seriesTitle)
        {
            if (string.IsNullOrWhiteSpace(seriesTitle))
            {
                return;
            }

            var existingLocalFiles = allScannedRows
                .Where(x => x != null &&
                            string.Equals(x.Title, seriesTitle, StringComparison.OrdinalIgnoreCase) &&
                            !string.IsNullOrWhiteSpace(x.Path) &&
                            File.Exists(x.Path))
                .GroupBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();
            if (existingLocalFiles.Count == 0)
            {
                UpdateActivity("Full-season result added; no existing local files were found to prep for removal: " + seriesTitle, true);
                return;
            }

            var existingCandidatePaths = new HashSet<string>(
                allRows.Where(x => x != null && !string.IsNullOrWhiteSpace(x.Path)).Select(x => x.Path),
                StringComparer.OrdinalIgnoreCase);
            var added = 0;
            foreach (var file in existingLocalFiles)
            {
                EpisodeFile candidate;
                if (existingCandidatePaths.Contains(file.Path))
                {
                    candidate = allRows.FirstOrDefault(x => string.Equals(x.Path, file.Path, StringComparison.OrdinalIgnoreCase));
                }
                else
                {
                    candidate = file;
                    allRows.Add(candidate);
                    existingCandidatePaths.Add(file.Path);
                    added++;
                }

                if (candidate == null)
                {
                    continue;
                }

                candidate.Delete = true;
                candidate.Recommendation = "Delete";
                candidate.Confidence = "High";
                candidate.ReviewStatus = "Full-season replacement pending";
                candidate.RecommendationReason = "A complete-series or full-season result was added to the selected RSS feed.";
            }

            RefreshReviewGrids();
            RefreshDeletionRows();
            UpdateSummary(string.Format("Full-season result added for {0}. Prepared {1:N0} existing local file(s) for removal.", seriesTitle, existingLocalFiles.Count));
            if (added > 0)
            {
                UpdateActivity(string.Format("Added {0:N0} non-duplicate local file(s) to Ready to Remove for full-season replacement review.", added), true);
            }
        }

        private void ShowSelectedFeedPanel()
        {
            activeShellSection = "SelectedRss";
            selectedFeedPanelCollapsed = false;
            ApplyWorkspacePanelVisibility();
        }

        private void ShellNavButton_Click(object sender, EventArgs e)
        {
            var button = sender as Button;
            var section = button == null ? "" : Convert.ToString(button.Tag);
            if (string.IsNullOrWhiteSpace(section))
            {
                section = "Duplicates";
            }

            activeShellSection = section;
            if (string.Equals(section, "Duplicates", StringComparison.OrdinalIgnoreCase))
            {
                candidatesPanelCollapsed = false;
            }
            else if (string.Equals(section, "MissingEpisodes", StringComparison.OrdinalIgnoreCase))
            {
                missingEpisodesPanelCollapsed = false;
            }
            else if (string.Equals(section, "EpisodeSearch", StringComparison.OrdinalIgnoreCase))
            {
                episodeSearchPanelCollapsed = false;
            }
            else if (string.Equals(section, "SelectedRss", StringComparison.OrdinalIgnoreCase))
            {
                selectedFeedPanelCollapsed = false;
            }

            ApplyWorkspacePanelVisibility();
        }

        private void UpdateShellNavigationState()
        {
            StyleNavButton(navLibraryButton, string.Equals(activeShellSection, "Library", StringComparison.OrdinalIgnoreCase));
            StyleNavButton(navDuplicatesButton, string.Equals(activeShellSection, "Duplicates", StringComparison.OrdinalIgnoreCase));
            StyleNavButton(navMissingEpisodesButton, string.Equals(activeShellSection, "MissingEpisodes", StringComparison.OrdinalIgnoreCase));
            StyleNavButton(navEpisodeSearchButton, string.Equals(activeShellSection, "EpisodeSearch", StringComparison.OrdinalIgnoreCase));
            StyleNavButton(navSelectedRssButton, string.Equals(activeShellSection, "SelectedRss", StringComparison.OrdinalIgnoreCase));
            StyleNavButton(navActivityButton, string.Equals(activeShellSection, "Activity", StringComparison.OrdinalIgnoreCase));
            StyleNavButton(navSettingsButton, string.Equals(activeShellSection, "Settings", StringComparison.OrdinalIgnoreCase));
        }

        private void StyleNavButton(Button button, bool selected)
        {
            if (button == null)
            {
                return;
            }

            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = selected ? 1 : 0;
            button.FlatAppearance.BorderColor = selected ? AccentColor : BorderColor;
            button.BackColor = selected
                ? (darkMode ? Color.FromArgb(30, 55, 78) : Color.FromArgb(219, 234, 254))
                : SidebarBackColor;
            button.ForeColor = selected
                ? (darkMode ? Color.White : Color.FromArgb(30, 64, 175))
                : PrimaryTextColor;
            button.Cursor = Cursors.Hand;
        }

        private void ApplySeriesFilter(object tag)
        {
            activeSeriesTag = tag;
            activeShellSection = "Duplicates";

            var episodeFile = tag as EpisodeFile;
            if (episodeFile != null)
            {
                ShowRows(new List<EpisodeFile> { episodeFile });
                UpdateDetails(episodeFile);
                ApplyWorkspacePanelVisibility();
                return;
            }

            var filter = tag as string;
            if (filter == AllSeriesTag)
            {
                ShowRows(allRows);
                UpdateDetails((EpisodeFile)null);
                ApplyWorkspacePanelVisibility();
                return;
            }

            if (!string.IsNullOrWhiteSpace(filter))
            {
                var seriesRows = GetSeriesSourceRows().Where(x => string.Equals(x.Title, filter, StringComparison.OrdinalIgnoreCase)).ToList();
                if (seriesRows.Count > 0)
                {
                    ShowRows(seriesRows);
                }
                else
                {
                    ShowRows(allRows.Where(x => string.Equals(x.Title, filter, StringComparison.OrdinalIgnoreCase)));
                }
                UpdateDetails((EpisodeFile)null);
                ApplyWorkspacePanelVisibility();
            }
        }

        private void AddSeriesCoverItem(string title, object tag, int count)
        {
            var imageKey = Convert.ToString(tag);
            if (string.IsNullOrWhiteSpace(imageKey))
            {
                imageKey = title;
            }

            seriesCoverImages.Images.Add(imageKey, CreatePlaceholderCover(title, seriesCoverImages.ImageSize));
            var item = new ListViewItem(string.Format("{0}\r\n{1:N0}", title, count));
            item.Tag = tag;
            item.ImageKey = imageKey;
            seriesCoverView.Items.Add(item);
        }

        private Image CreateSeriesCoverImage(string title, string coverPath)
        {
            if (!string.IsNullOrWhiteSpace(coverPath) && File.Exists(coverPath))
            {
                try
                {
                    using (var stream = new FileStream(coverPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (var original = Image.FromStream(stream))
                    {
                        return CreateCroppedImage(original, seriesCoverImages.ImageSize);
                    }
                }
                catch
                {
                }
            }

            return CreatePlaceholderCover(title, seriesCoverImages.ImageSize);
        }

        private static Image CreateCroppedImage(Image original, Size size)
        {
            var bitmap = new Bitmap(size.Width, size.Height);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.Black);
                graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;

                var scale = Math.Max((float)size.Width / original.Width, (float)size.Height / original.Height);
                var width = (int)Math.Ceiling(original.Width * scale);
                var height = (int)Math.Ceiling(original.Height * scale);
                var x = (size.Width - width) / 2;
                var y = (size.Height - height) / 2;
                graphics.DrawImage(original, new Rectangle(x, y, width, height));
            }

            return bitmap;
        }

        private Image CreatePlaceholderCover(string title, Size size)
        {
            var bitmap = new Bitmap(size.Width, size.Height);
            using (var graphics = Graphics.FromImage(bitmap))
            using (var back = new SolidBrush(darkMode ? Color.FromArgb(42, 48, 58) : Color.FromArgb(225, 231, 239)))
            using (var border = new Pen(BorderColor))
            using (var textBrush = new SolidBrush(PrimaryTextColor))
            using (var smallFont = new Font(Font.FontFamily, 8F, FontStyle.Bold))
            {
                graphics.Clear(PanelBackColor);
                graphics.FillRectangle(back, 0, 0, size.Width - 1, size.Height - 1);
                graphics.DrawRectangle(border, 0, 0, size.Width - 1, size.Height - 1);

                var text = GetInitials(title);
                var format = new StringFormat();
                format.Alignment = StringAlignment.Center;
                format.LineAlignment = StringAlignment.Center;
                graphics.DrawString(text, smallFont, textBrush, new RectangleF(8, 8, size.Width - 16, size.Height - 16), format);
            }

            return bitmap;
        }

        private static string GetInitials(string text)
        {
            var words = Regex.Split(text ?? "", @"\s+")
                             .Where(x => !string.IsNullOrWhiteSpace(x))
                             .Take(3)
                             .Select(x => x.Substring(0, 1).ToUpperInvariant())
                             .ToArray();
            return words.Length == 0 ? "?" : string.Join("", words);
        }

        private string FindSeriesCoverPath(IEnumerable<EpisodeFile> files)
        {
            var fileList = (files ?? Enumerable.Empty<EpisodeFile>()).Where(x => x != null).ToList();
            var cacheTitle = fileList.Select(x => x.Title).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? "";
            var cacheRoot = GetPrimarySessionRoot() ?? "";
            var cacheKey = cacheRoot + "|" + cacheTitle;
            string cachedPath;
            if (!string.IsNullOrWhiteSpace(cacheTitle) &&
                seriesCoverPathCache.TryGetValue(cacheKey, out cachedPath) &&
                File.Exists(cachedPath))
            {
                return cachedPath;
            }

            var root = "";
            var primaryRoot = GetPrimarySessionRoot();
            if (!string.IsNullOrWhiteSpace(primaryRoot))
            {
                try
                {
                    root = Path.GetFullPath(primaryRoot.Trim()).TrimEnd('\\');
                }
                catch
                {
                    root = "";
                }
            }

            foreach (var file in fileList)
            {
                var folder = GetExistingFolder(file);
                var startingFolder = folder;
                while (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
                {
                    var cover = FindCoverInFolder(folder, file.Title, PathsEqual(folder, startingFolder));
                    if (!string.IsNullOrWhiteSpace(cover))
                    {
                        if (!string.IsNullOrWhiteSpace(cacheTitle))
                        {
                            seriesCoverPathCache[cacheKey] = cover;
                        }
                        return cover;
                    }

                    if (!string.IsNullOrWhiteSpace(root) &&
                        string.Equals(Path.GetFullPath(folder).TrimEnd('\\'), root, StringComparison.OrdinalIgnoreCase))
                    {
                        break;
                    }

                    folder = Directory.GetParent(folder) == null ? null : Directory.GetParent(folder).FullName;
                }
            }

            return null;
        }

        private static string FindCoverInFolder(string folder, string title, bool allowGenericCoverNames)
        {
            foreach (var name in GetSeriesCoverCandidateNames(title))
            {
                var path = Path.Combine(folder, name);
                if (File.Exists(path))
                {
                    return path;
                }
            }

            if (!allowGenericCoverNames)
            {
                return null;
            }

            var names = new[]
            {
                "folder.jpg", "folder.jpeg", "folder.png",
                "poster.jpg", "poster.jpeg", "poster.png",
                "cover.jpg", "cover.jpeg", "cover.png",
                "series.jpg", "series.jpeg", "series.png",
                "tvshow.jpg", "tvshow.jpeg", "tvshow.png"
            };

            foreach (var name in names)
            {
                var path = Path.Combine(folder, name);
                if (File.Exists(path))
                {
                    return path;
                }
            }

            return null;
        }

        internal static string GetSeriesCoverFileName(string title)
        {
            var safeTitle = GetSafeFolderName(title);
            if (string.IsNullOrWhiteSpace(safeTitle))
            {
                safeTitle = "series-cover";
            }

            return safeTitle + ".jpg";
        }

        private static string GetSeriesCoverTargetPath(string title, string folder)
        {
            return Path.Combine(folder, GetSeriesCoverFileName(title));
        }

        private static IEnumerable<string> GetSeriesCoverCandidateNames(string title)
        {
            var safeTitle = GetSafeFolderName(title);
            if (string.IsNullOrWhiteSpace(safeTitle))
            {
                yield break;
            }

            yield return safeTitle + ".jpg";
            yield return safeTitle + ".jpeg";
            yield return safeTitle + ".png";
        }

        private void Grid_SelectionChanged(object sender, EventArgs e)
        {
            var targetGrid = sender as DataGridView;
            if (targetGrid == null || targetGrid.CurrentRow == null)
            {
                return;
            }

            activeGrid = targetGrid;
            var file = targetGrid.CurrentRow.DataBoundItem as EpisodeFile;
            UpdateDetails(file);
            UpdateShellSeriesHeader();
        }

        private void Grid_CellMouseDown(object sender, DataGridViewCellMouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right || e.RowIndex < 0)
            {
                return;
            }

            var targetGrid = sender as DataGridView;
            if (targetGrid == null)
            {
                return;
            }

            activeGrid = targetGrid;
            if (!targetGrid.Rows[e.RowIndex].Selected)
            {
                targetGrid.ClearSelection();
                targetGrid.Rows[e.RowIndex].Selected = true;
            }
            targetGrid.CurrentCell = targetGrid.Rows[e.RowIndex].Cells[Math.Max(0, e.ColumnIndex)];
            var file = targetGrid.Rows[e.RowIndex].DataBoundItem as EpisodeFile;
            var hasFile = file != null && !string.IsNullOrWhiteSpace(file.Path);
            openCandidateFileItem.Enabled = hasFile && File.Exists(file.Path);
            openCandidateFolderItem.Enabled = hasFile && Directory.Exists(GetExistingFolder(file));
            moveCandidateToNameFoldersItem.Enabled = GetSelectedFiles(targetGrid).Any(x => File.Exists(x.Path));
            fileBotCandidateItem.Enabled = moveCandidateToNameFoldersItem.Enabled;
            previewCandidateActionsItem.Enabled = GetActiveDataSet().Any(x => x.Delete);
        }

        private void OpenCandidateFileItem_Click(object sender, EventArgs e)
        {
            var file = GetCurrentCandidateFile();
            if (file == null)
            {
                return;
            }

            if (!File.Exists(file.Path))
            {
                MessageBox.Show(this, "The selected file no longer exists.", "Open file", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            OpenShellPath(file.Path);
        }

        private void OpenCandidateFolderItem_Click(object sender, EventArgs e)
        {
            var file = GetCurrentCandidateFile();
            if (file == null)
            {
                return;
            }

            var folder = GetExistingFolder(file);
            if (string.IsNullOrWhiteSpace(folder))
            {
                MessageBox.Show(this, "The selected file's folder no longer exists.", "Open local folder", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            OpenShellPath(folder);
        }

        private void InspectorKeepButton_Click(object sender, EventArgs e)
        {
            var file = GetCurrentCandidateFile();
            if (file == null)
            {
                return;
            }

            file.Delete = false;
            RefreshDeletionView();
            UpdateDetails(file);
            UpdateSummary("Marked selected file to keep.");
        }

        private void InspectorDeleteButton_Click(object sender, EventArgs e)
        {
            var file = GetCurrentCandidateFile();
            if (file == null)
            {
                return;
            }

            file.Delete = true;
            RefreshDeletionView();
            UpdateDetails(file);
            UpdateSummary("Marked selected file for removal.");
        }

        private void InspectorIgnoreButton_Click(object sender, EventArgs e)
        {
            var file = GetCurrentCandidateFile();
            if (file == null || string.IsNullOrWhiteSpace(file.Key))
            {
                return;
            }

            foreach (var row in allRows.Where(x => string.Equals(x.Key, file.Key, StringComparison.OrdinalIgnoreCase)))
            {
                row.Delete = false;
                row.ReviewStatus = "Ignored";
            }

            RefreshReviewGrids();
            UpdateDetails(file);
            UpdateSummary("Ignored selected episode group.");
        }

        private void InspectorOpenFolderButton_Click(object sender, EventArgs e)
        {
            OpenCandidateFolderItem_Click(sender, e);
        }

        private void FileBotMenuItem_Click(object sender, EventArgs e)
        {
            var sourceGrid = activeGrid == deletionGrid ? deletionGrid : grid;
            var selected = GetSelectedFiles(sourceGrid).Where(x => File.Exists(x.Path)).ToList();
            if (selected.Count == 0 && !ReferenceEquals(sourceGrid, grid))
            {
                selected = GetSelectedFiles(grid).Where(x => File.Exists(x.Path)).ToList();
            }
            if (selected.Count == 0 && !ReferenceEquals(sourceGrid, deletionGrid))
            {
                selected = GetSelectedFiles(deletionGrid).Where(x => File.Exists(x.Path)).ToList();
            }

            if (selected.Count == 0)
            {
                MessageBox.Show(this, "Select one or more files first.", "FileBot", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var paths = selected.Select(x => x.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            using (var dialog = new FileBotCommandDialog(paths))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK)
                {
                    return;
                }

                var settings = dialog.Settings;
                if (string.IsNullOrWhiteSpace(settings.FileBotPath))
                {
                    MessageBox.Show(this, "Enter the FileBot executable path.", "FileBot", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                var message = string.Equals(settings.Action, "test", StringComparison.OrdinalIgnoreCase)
                    ? "Run FileBot preview?"
                    : "Run FileBot now? This can rename or move files.";
                if (MessageBox.Show(this, message + "\r\n\r\n" + dialog.CommandPreview, "Confirm FileBot", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                {
                    return;
                }

                RunFileBotAsync(settings, paths);
            }
        }

        private void RunFileBotAsync(FileBotCommandSettings settings, List<string> paths)
        {
            SetBusy(true, "Running FileBot...");
            var worker = new BackgroundWorker();
            worker.DoWork += delegate(object workerSender, DoWorkEventArgs args)
            {
                args.Result = RunFileBot(settings, paths, delegate { return cancelRequested; });
            };
            worker.RunWorkerCompleted += delegate(object workerSender, RunWorkerCompletedEventArgs args)
            {
                try
                {
                    if (args.Error is OperationCanceledException)
                    {
                        UpdateSummary("FileBot canceled.");
                        return;
                    }

                    if (args.Error != null)
                    {
                        throw args.Error;
                    }

                    ShowOutputDialog("FileBot Output", Convert.ToString(args.Result));
                    UpdateSummary("FileBot finished.");
                    if (!string.Equals(settings.Action, "test", StringComparison.OrdinalIgnoreCase) &&
                        !string.IsNullOrWhiteSpace(GetPrimarySessionRoot()) &&
                        Directory.Exists(GetPrimarySessionRoot().Trim()))
                    {
                        StartScan(GetPrimarySessionRoot().Trim(), false);
                    }
                }
                catch (Exception ex)
                {
                    LogException("FileBot failed", ex);
                    MessageBox.Show(this, ex.Message, "FileBot failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    UpdateActivity("FileBot failed.", true);
                }
                finally
                {
                    if (!progressBar.Visible)
                    {
                        SetBusy(false, statusLabel.Text);
                    }
                }
            };
            worker.RunWorkerAsync();
        }

        public static string BuildFileBotCommandPreview(FileBotCommandSettings settings, IEnumerable<string> paths)
        {
            return QuoteCommand(settings.FileBotPath) + " " + BuildFileBotArguments(settings, paths);
        }

        private static string RunFileBot(FileBotCommandSettings settings, IEnumerable<string> paths)
        {
            return RunFileBot(settings, paths, null);
        }

        private static string RunFileBot(FileBotCommandSettings settings, IEnumerable<string> paths, Func<bool> shouldCancel)
        {
            var output = new StringBuilder();
            var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = settings.FileBotPath,
                Arguments = BuildFileBotArguments(settings, paths),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e)
            {
                if (e.Data != null)
                {
                    output.AppendLine(e.Data);
                }
            };
            process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e)
            {
                if (e.Data != null)
                {
                    output.AppendLine(e.Data);
                }
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            while (!process.WaitForExit(250))
            {
                if (shouldCancel != null && shouldCancel())
                {
                    try
                    {
                        process.Kill();
                    }
                    catch
                    {
                    }
                    throw new OperationCanceledException();
                }
            }
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException("FileBot exited with code " + process.ExitCode + "." + Environment.NewLine + output);
            }

            return output.Length == 0 ? "FileBot completed with no output." : output.ToString();
        }

        private static string BuildFileBotArguments(FileBotCommandSettings settings, IEnumerable<string> paths)
        {
            var args = new List<string>();
            args.Add("-rename");
            if (settings.Recursive)
            {
                args.Add("-r");
            }

            foreach (var path in paths)
            {
                args.Add(QuoteCommand(path));
            }

            AddOption(args, "--db", settings.Database);
            if (settings.NonStrict)
            {
                args.Add("-non-strict");
            }
            AddOption(args, "--action", settings.Action);
            AddOption(args, "--conflict", settings.Conflict);
            AddOption(args, "--output", settings.OutputFolder);
            AddOption(args, "--format", settings.Format);
            AddOption(args, "--q", settings.Query);
            return string.Join(" ", args.ToArray());
        }

        private static void AddOption(List<string> args, string option, string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            args.Add(option);
            args.Add(QuoteCommand(value));
        }

        private static string QuoteCommand(string value)
        {
            if (value == null)
            {
                value = "";
            }

            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }

        private void ShowOutputDialog(string title, string output)
        {
            using (var dialog = new Form())
            {
                dialog.Text = title;
                dialog.StartPosition = FormStartPosition.CenterParent;
                dialog.ClientSize = new Size(760, 520);
                var box = new TextBox();
                box.Multiline = true;
                box.ReadOnly = true;
                box.ScrollBars = ScrollBars.Both;
                box.WordWrap = false;
                box.Dock = DockStyle.Fill;
                box.Text = output;
                dialog.Controls.Add(box);
                dialog.ShowDialog(this);
            }
        }

        private void PreviewBatchActionsMenuItem_Click(object sender, EventArgs e)
        {
            ComputeReviewRecommendations(false);
            var previewRows = BuildActionPreviewRows().ToList();
            if (previewRows.Count == 0)
            {
                MessageBox.Show(this, "No marked or recommended batch actions are available to preview.", "Batch Preview", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            ShowBatchPreviewDialog(previewRows, false, "Close");
        }

        private List<ActionPreviewRow> BuildActionPreviewRows()
        {
            var previewRows = new List<ActionPreviewRow>();
            foreach (var row in GetActiveDataSet().Where(x => x.Delete || string.Equals(x.Recommendation, "Delete", StringComparison.OrdinalIgnoreCase)))
            {
                previewRows.Add(new ActionPreviewRow
                {
                    Action = row.Delete ? "Delete marked" : "Suggested delete",
                    Confidence = DisplayOrDash(row.Confidence),
                    Reason = DisplayOrDash(row.RecommendationReason),
                    CurrentPath = row.Path,
                    TargetPath = "Recycle Bin"
                });
            }

            var moveSeeds = GetSelectedMoveSeeds();
            var moveRows = ExpandToSelectedSeries(moveSeeds);
            var moveRoot = GetSeriesMoveBaseFolder(moveSeeds, moveRows);
            var folderNames = BuildSeriesFolderNameMap(moveRows.Select(x => x.Title).Distinct(StringComparer.OrdinalIgnoreCase).ToList());
            foreach (var row in moveRows)
            {
                string targetPath;
                previewRows.Add(new ActionPreviewRow
                {
                    Action = "Selected series-folder move",
                    Confidence = DisplayOrDash(row.Confidence),
                    Reason = "Selected series can be moved into one folder named after the series.",
                    CurrentPath = row.Path,
                    TargetPath = TryGetSeriesFolderPath(row, moveRoot, GetSeriesFolderNameForRow(row, folderNames), out targetPath) ? targetPath : ""
                });
            }

            return previewRows.GroupBy(x => x.Action + "\n" + x.CurrentPath + "\n" + x.TargetPath, StringComparer.OrdinalIgnoreCase)
                              .Select(g => g.First())
                              .OrderBy(x => x.Action, StringComparer.OrdinalIgnoreCase)
                              .ThenBy(x => x.CurrentPath, StringComparer.OrdinalIgnoreCase)
                              .ToList();
        }

        private bool ConfirmBatchPreviewDialog(List<ActionPreviewRow> previewRows, string executeText)
        {
            return ShowBatchPreviewDialog(previewRows, true, executeText);
        }

        private bool ShowBatchPreviewDialog(List<ActionPreviewRow> previewRows, bool requireExecution, string executeText)
        {
            using (var dialog = new Form())
            {
                dialog.Text = requireExecution ? "Review Batch Action" : "Batch Action Preview";
                dialog.StartPosition = FormStartPosition.CenterParent;
                dialog.ClientSize = new Size(980, 560);

                var previewGrid = new DataGridView();
                previewGrid.Dock = DockStyle.Fill;
                previewGrid.AutoGenerateColumns = false;
                previewGrid.AllowUserToAddRows = false;
                previewGrid.AllowUserToDeleteRows = false;
                previewGrid.ReadOnly = true;
                previewGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
                AddPreviewColumn(previewGrid, "Action", "Action", 140);
                AddPreviewColumn(previewGrid, "Confidence", "Confidence", 90);
                AddPreviewColumn(previewGrid, "Reason", "Reason", 260);
                AddPreviewColumn(previewGrid, "CurrentPath", "Current path", 320);
                AddPreviewColumn(previewGrid, "TargetPath", "Target", 320);
                previewGrid.DataSource = previewRows;
                StyleGrid(previewGrid);

                var footerPanel = new FlowLayoutPanel();
                footerPanel.Dock = DockStyle.Bottom;
                footerPanel.Height = 48;
                footerPanel.FlowDirection = FlowDirection.RightToLeft;
                footerPanel.Padding = new Padding(8, 6, 8, 6);

                var executeButton = new Button();
                executeButton.Text = executeText;
                executeButton.Width = requireExecution ? 150 : 100;
                executeButton.Height = 34;
                executeButton.DialogResult = DialogResult.OK;
                StyleButton(executeButton, true);

                footerPanel.Controls.Add(executeButton);

                var saveButton = new Button();
                saveButton.Text = "Save CSV...";
                saveButton.Width = 110;
                saveButton.Height = 34;
                StyleButton(saveButton, false);
                saveButton.Click += delegate
                {
                    using (var saveDialog = new SaveFileDialog())
                    {
                        saveDialog.Title = "Save dry-run report";
                        saveDialog.Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*";
                        saveDialog.FileName = "same-episode-dry-run.csv";
                        if (saveDialog.ShowDialog(dialog) == DialogResult.OK)
                        {
                            SaveActionReport(saveDialog.FileName, previewRows);
                            MessageBox.Show(dialog, "Dry-run report saved.", "Dry-run report", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        }
                    }
                };
                footerPanel.Controls.Add(saveButton);

                if (requireExecution)
                {
                    var cancelButton = new Button();
                    cancelButton.Text = "Cancel";
                    cancelButton.Width = 100;
                    cancelButton.Height = 34;
                    cancelButton.DialogResult = DialogResult.Cancel;
                    StyleButton(cancelButton, false);
                    footerPanel.Controls.Add(cancelButton);
                    dialog.CancelButton = cancelButton;
                }

                dialog.Controls.Add(previewGrid);
                dialog.Controls.Add(footerPanel);
                dialog.AcceptButton = executeButton;
                return dialog.ShowDialog(this) == DialogResult.OK;
            }
        }

        private static void AddPreviewColumn(DataGridView targetGrid, string propertyName, string headerText, int width)
        {
            var column = new DataGridViewTextBoxColumn();
            column.DataPropertyName = propertyName;
            column.HeaderText = headerText;
            column.Width = width;
            column.ReadOnly = true;
            targetGrid.Columns.Add(column);
        }

        private void MoveSelectedToNameFoldersMenuItem_Click(object sender, EventArgs e)
        {
            var selectedSeeds = GetSelectedMoveSeeds();

            if (selectedSeeds.Count == 0)
            {
                MessageBox.Show(this, "Select a file or series first.", "Move selected series", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var selected = ExpandToSelectedSeries(selectedSeeds).Where(x => File.Exists(x.Path)).ToList();
            if (selected.Count == 0)
            {
                MessageBox.Show(this, "No files from the selected series still exist on disk.", "Move selected series", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var targetRoot = GetSeriesMoveBaseFolder(selectedSeeds, selected);
            if (string.IsNullOrWhiteSpace(targetRoot))
            {
                MessageBox.Show(this, "No valid destination folder could be found. Choose a scan folder first.", "Move selected series", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var seriesTitles = selected.Select(x => x.Title).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
            var folderNames = BuildSeriesFolderNameMap(seriesTitles);
            if (seriesTitles.Count == 1)
            {
                var title = seriesTitles[0];
                var entered = Microsoft.VisualBasic.Interaction.InputBox(
                    "Folder name for the selected series:",
                    "Move selected series",
                    folderNames[title]);
                if (string.IsNullOrWhiteSpace(entered))
                {
                    return;
                }

                var safeEntered = GetSafeFolderName(entered);
                if (string.IsNullOrWhiteSpace(safeEntered))
                {
                    MessageBox.Show(this, "Enter a valid folder name.", "Move selected series", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                folderNames[title] = safeEntered;
            }

            var previewRows = BuildSeriesMovePreviewRows(selected, targetRoot, folderNames);
            if (!ConfirmBatchPreviewDialog(previewRows, "Move Files"))
            {
                LogActivity("Move batch canceled.");
                return;
            }

            SaveActionReport(GetMoveDryRunReportPath(), previewRows);
            RunMoveToSeriesFolders(selected, targetRoot, folderNames);
        }

        private List<ActionPreviewRow> BuildSeriesMovePreviewRows(List<EpisodeFile> selected, string targetRoot, Dictionary<string, string> folderNames)
        {
            return (selected ?? new List<EpisodeFile>()).Select(row =>
            {
                string targetPath;
                return new ActionPreviewRow
                {
                    Action = "Selected series-folder move",
                    Confidence = DisplayOrDash(row.Confidence),
                    Reason = "Selected series can be moved into one folder named after the series.",
                    CurrentPath = row.Path,
                    TargetPath = TryGetSeriesFolderPath(row, targetRoot, GetSeriesFolderNameForRow(row, folderNames), out targetPath) ? targetPath : ""
                };
            }).ToList();
        }

        private void RunMoveToSeriesFolders(List<EpisodeFile> selected, string targetRoot, Dictionary<string, string> folderNames)
        {
            SetBusy(true, string.Format("Moving 0/{0:N0} file(s) to series folders...", selected.Count));
            var worker = new BackgroundWorker();
            worker.WorkerReportsProgress = true;
            worker.DoWork += delegate(object workerSender, DoWorkEventArgs args)
            {
                var moved = 0;
                var skipped = 0;
                var failures = new List<string>();
                var reportRows = new List<ActionPreviewRow>();

                for (var i = 0; i < selected.Count; i++)
                {
                    ThrowIfCancellationRequested(delegate { return cancelRequested; });
                    var row = selected[i];
                    worker.ReportProgress(0, string.Format("Moving {0:N0}/{1:N0}: {2}", i + 1, selected.Count, row.FileName));
                    try
                    {
                        var oldPath = row.Path;
                        string newPath;
                        if (TryMoveToSeriesFolder(row, targetRoot, GetSeriesFolderNameForRow(row, folderNames), out newPath))
                        {
                            UpdateRowsAfterMove(row, oldPath, newPath);
                            reportRows.Add(new ActionPreviewRow
                            {
                                Action = "Moved",
                                Confidence = "Done",
                                Reason = string.Equals(Path.GetFileName(oldPath), Path.GetFileName(newPath), StringComparison.OrdinalIgnoreCase)
                                    ? "Moved to series folder."
                                    : "Moved to series folder with a conflict-safe filename.",
                                CurrentPath = oldPath,
                                TargetPath = newPath
                            });
                            moved++;
                        }
                        else
                        {
                            reportRows.Add(new ActionPreviewRow
                            {
                                Action = "Skipped",
                                Confidence = "Skipped",
                                Reason = "Target was unavailable or already in the series folder.",
                                CurrentPath = row.Path,
                                TargetPath = ""
                            });
                            skipped++;
                        }
                    }
                    catch (Exception ex)
                    {
                        reportRows.Add(new ActionPreviewRow
                        {
                            Action = "Failed",
                            Confidence = "Error",
                            Reason = ex.Message,
                            CurrentPath = row.Path,
                            TargetPath = ""
                        });
                        failures.Add(row.FileName + ": " + ex.Message);
                    }
                }

                args.Result = new object[] { moved, skipped, failures, reportRows };
            };
            worker.ProgressChanged += delegate(object workerSender, ProgressChangedEventArgs args)
            {
                UpdateActivity(args.UserState as string ?? "Moving files...", false);
            };
            worker.RunWorkerCompleted += delegate(object workerSender, RunWorkerCompletedEventArgs args)
            {
                try
                {
                    if (args.Error is OperationCanceledException)
                    {
                        UpdateSummary("Move canceled.");
                        return;
                    }

                    if (args.Error != null)
                    {
                        throw args.Error;
                    }

                    var result = (object[])args.Result;
                    var moved = (int)result[0];
                    var skipped = (int)result[1];
                    var failures = (List<string>)result[2];
                    var reportRows = (List<ActionPreviewRow>)result[3];
                    SaveMoveReport(reportRows);

                    RefreshReviewGrids();
                    PopulateSeriesPanel();
                    if (CanWriteSingleRootCache())
                    {
                        SaveCurrentSessionCache();
                    }

                    UpdateSummary(string.Format("Moved {0:N0} file(s) to series folder(s). Skipped {1:N0}. Move report saved.", moved, skipped));
                    if (failures.Count > 0)
                    {
                        MessageBox.Show(this, string.Join("\r\n", failures.Take(12).ToArray()), "Some files could not be moved", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                }
                catch (Exception ex)
                {
                    LogException("Move failed", ex);
                    MessageBox.Show(this, ex.Message, "Move failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    UpdateActivity("Move failed.", true);
                }
                finally
                {
                    SetBusy(false, statusLabel.Text);
                }
            };
            worker.RunWorkerAsync();
        }

        private void UpdateRowsAfterMove(EpisodeFile movedRow, string oldPath, string newPath)
        {
            var newFolder = Path.GetDirectoryName(newPath);
            var lastWriteTicks = File.GetLastWriteTimeUtc(newPath).Ticks;
            foreach (var row in allScannedRows.Concat(allRows).Where(x => x != null && (ReferenceEquals(x, movedRow) || string.Equals(x.Path, oldPath, StringComparison.OrdinalIgnoreCase))).Distinct())
            {
                row.Path = newPath;
                row.FileLocation = newFolder;
                row.LastWriteUtcTicks = lastWriteTicks;
            }
        }

        private List<EpisodeFile> GetSelectedMoveSeeds()
        {
            var sourceGrid = activeGrid == deletionGrid ? deletionGrid : grid;
            var selectedSeeds = GetSelectedFiles(sourceGrid);
            if (selectedSeeds.Count == 0 && !ReferenceEquals(sourceGrid, grid))
            {
                selectedSeeds = GetSelectedFiles(grid);
            }
            if (selectedSeeds.Count == 0 && !ReferenceEquals(sourceGrid, deletionGrid))
            {
                selectedSeeds = GetSelectedFiles(deletionGrid);
            }

            if (selectedSeeds.Count == 0)
            {
                var seriesTitle = activeSeriesTag as string;
                if (!string.IsNullOrWhiteSpace(seriesTitle) && seriesTitle != AllSeriesTag)
                {
                    selectedSeeds = GetSeriesSourceRows()
                        .Where(x => string.Equals(x.Title, seriesTitle, StringComparison.OrdinalIgnoreCase))
                        .Take(1)
                        .ToList();
                }
            }

            return selectedSeeds;
        }

        private List<EpisodeFile> GetSelectedFiles(DataGridView targetGrid)
        {
            var selected = new List<EpisodeFile>();
            if (targetGrid == null)
            {
                return selected;
            }

            foreach (DataGridViewRow selectedRow in targetGrid.SelectedRows)
            {
                if (selectedRow.IsNewRow)
                {
                    continue;
                }

                var file = selectedRow.DataBoundItem as EpisodeFile;
                if (file != null && !selected.Contains(file))
                {
                    selected.Add(file);
                }
            }

            if (selected.Count == 0 && targetGrid.CurrentRow != null)
            {
                var file = targetGrid.CurrentRow.DataBoundItem as EpisodeFile;
                if (file != null)
                {
                    selected.Add(file);
                }
            }

            return selected;
        }

        private List<EpisodeFile> ExpandToSelectedSeries(IEnumerable<EpisodeFile> selectedSeeds)
        {
            var titles = new HashSet<string>(
                (selectedSeeds ?? Enumerable.Empty<EpisodeFile>()).Where(x => x != null && !string.IsNullOrWhiteSpace(x.Title))
                             .Select(x => x.Title),
                StringComparer.OrdinalIgnoreCase);
            if (titles.Count == 0)
            {
                return new List<EpisodeFile>();
            }

            return (allScannedRows.Count > 0 ? allScannedRows : GetActiveDataSet())
                .Where(x => x != null && titles.Contains(x.Title))
                .Distinct()
                .ToList();
        }

        private string GetSeriesMoveBaseFolder(IEnumerable<EpisodeFile> selectedSeeds, IEnumerable<EpisodeFile> selectedRows)
        {
            var root = GetPrimarySessionRoot();
            if (!string.IsNullOrWhiteSpace(root) && Directory.Exists(root))
            {
                return Path.GetFullPath(root);
            }

            var first = (selectedSeeds ?? Enumerable.Empty<EpisodeFile>())
                .Concat(selectedRows ?? Enumerable.Empty<EpisodeFile>())
                .FirstOrDefault();
            return GetExistingFolder(first);
        }

        private static Dictionary<string, string> BuildSeriesFolderNameMap(List<string> seriesTitles)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var title in seriesTitles ?? new List<string>())
            {
                var baseName = GetDefaultSeriesFolderName(title);
                var folderName = baseName;
                var suffix = 2;
                while (used.Contains(folderName))
                {
                    folderName = string.Format("{0} ({1})", baseName, suffix++);
                }

                used.Add(folderName);
                result[title ?? ""] = folderName;
            }

            return result;
        }

        private static string GetSeriesFolderNameForRow(EpisodeFile row, Dictionary<string, string> folderNames)
        {
            if (row == null)
            {
                return GetDefaultSeriesFolderName("");
            }

            string folderName;
            return folderNames != null && folderNames.TryGetValue(row.Title ?? "", out folderName)
                ? folderName
                : GetDefaultSeriesFolderName(row.Title);
        }

        private static string GetDefaultSeriesFolderName(string title)
        {
            var folderName = GetSafeFolderName(title);
            return string.IsNullOrWhiteSpace(folderName) ? "Selected Series" : folderName;
        }

        private static bool TryMoveToSeriesFolder(EpisodeFile row, string targetRoot, string folderName, out string newPath)
        {
            newPath = null;
            if (row == null || string.IsNullOrWhiteSpace(row.Path) || !File.Exists(row.Path))
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(targetRoot) || !Directory.Exists(targetRoot))
            {
                return false;
            }

            if (!TryGetSeriesFolderPath(row, targetRoot, folderName, out newPath))
            {
                return false;
            }

            if (PathsEqual(row.Path, newPath))
            {
                return false;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(newPath));
            File.Move(row.Path, newPath);
            return true;
        }

        internal static bool TryGetSeriesFolderPath(EpisodeFile row, string targetRoot, string folderName, out string targetPath)
        {
            targetPath = null;
            if (row == null || string.IsNullOrWhiteSpace(row.Path) || string.IsNullOrWhiteSpace(row.FileName) || string.IsNullOrWhiteSpace(targetRoot))
            {
                return false;
            }

            folderName = GetSafeFolderName(folderName);
            if (string.IsNullOrWhiteSpace(folderName))
            {
                return false;
            }

            var targetFolder = Path.Combine(targetRoot, folderName);
            targetPath = GetAvailableTargetPath(targetFolder, row.FileName, row.Path);
            return true;
        }

        internal static string GetAvailableTargetPath(string targetFolder, string fileName, string currentPath)
        {
            var targetPath = Path.Combine(targetFolder, fileName);
            if (!File.Exists(targetPath) || PathsEqual(targetPath, currentPath))
            {
                return targetPath;
            }

            var baseName = Path.GetFileNameWithoutExtension(fileName);
            var extension = Path.GetExtension(fileName);
            for (var i = 2; i < 10000; i++)
            {
                targetPath = Path.Combine(targetFolder, string.Format("{0} ({1}){2}", baseName, i, extension));
                if (!File.Exists(targetPath))
                {
                    return targetPath;
                }
            }

            throw new IOException("Could not find an available destination name for: " + fileName);
        }

        internal static bool PathsEqual(string left, string right)
        {
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            {
                return false;
            }

            try
            {
                return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
            }
        }

        internal static string GetSafeFolderName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return "";
            }

            foreach (var invalid in Path.GetInvalidFileNameChars())
            {
                name = name.Replace(invalid, '_');
            }

            return name.Trim().TrimEnd('.');
        }

        private EpisodeFile GetCurrentCandidateFile()
        {
            var targetGrid = activeGrid ?? grid;
            return targetGrid.CurrentRow == null ? null : targetGrid.CurrentRow.DataBoundItem as EpisodeFile;
        }

        private static string GetExistingFolder(EpisodeFile file)
        {
            if (file == null)
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(file.FileLocation) && Directory.Exists(file.FileLocation))
            {
                return file.FileLocation;
            }

            if (!string.IsNullOrWhiteSpace(file.Path))
            {
                var folder = Path.GetDirectoryName(file.Path);
                if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
                {
                    return folder;
                }
            }

            return null;
        }

        private void OpenShellPath(string path)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Open failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void UpdateDetails(EpisodeFile file)
        {
            if (detailsBox == null)
            {
                return;
            }

            if (file == null)
            {
                detailsBox.Links.Clear();
                detailsBox.Text = "Select a file to see details.";
                metadataBox.Text = "Select a row to see provider match details.";
                UpdateInspectorPreviewForTitle("");
                return;
            }

            detailsBox.Links.Clear();
            detailsBox.Text =
                "Series: " + file.Title + Environment.NewLine +
                "Episode: " + DisplayOrDash(file.Episode) + " | Size: " + FormatByteSize(file.SizeBytes) + " | Group: " + DisplayOrDash(file.SubtitleGroup) + " | Version: " + DisplayOrDash(file.Version) + Environment.NewLine +
                "Recommendation: " + DisplayOrDash(file.Recommendation) + " | " + DisplayOrDash(file.Confidence) + " | " + DisplayOrDash(file.ReviewStatus) + " | " + DisplayOrDash(file.ArtworkStatus) + Environment.NewLine +
                "Reason: " + ShortenMiddle(DisplayOrDash(file.RecommendationReason), 170) + Environment.NewLine +
                "Location: " + ShortenMiddle(DisplayOrDash(file.FileLocation), 170) + Environment.NewLine +
                "File: " + ShortenMiddle(DisplayOrDash(file.FileName), 170);
            metadataBox.Text =
                "Provider: " + ShortenMiddle(DisplayOrDash(file.AniDbDisplay), 120) + Environment.NewLine +
                "Artwork: " + DisplayOrDash(file.ArtworkStatus) + Environment.NewLine +
                "ID: " + DisplayOrDash(file.AniDbId);
            UpdateInspectorPreview(file);
        }

        private void UpdateDetails(MissingEpisodeRow row)
        {
            if (detailsBox == null || row == null)
            {
                return;
            }

            detailsBox.Links.Clear();
            detailsBox.Text =
                "Missing Episodes: " + row.Title + Environment.NewLine +
                "Scope: " + DisplayOrDash(row.Scope) + " | Missing: " + DisplayOrDash(row.MissingEpisodes) + " | Present: " + DisplayOrDash(row.PresentRange) + Environment.NewLine +
                "Known local episodes: " + row.KnownEpisodes.ToString("N0") + " | Missing count: " + row.MissingCount.ToString("N0") + " | Locations: " + row.LocationCount.ToString("N0") + Environment.NewLine +
                "Search query: " + DisplayOrDash(GetSearchQuery(row));
            metadataBox.Text =
                "Series: " + DisplayOrDash(row.Title) + Environment.NewLine +
                "Search key: " + DisplayOrDash(row.SearchKey) + Environment.NewLine +
                "Review: missing episode workflow";
            UpdateInspectorPreviewForTitle(row.Title);
        }

        private void UpdateDetails(EpisodeSearchResult row)
        {
            if (detailsBox == null || row == null)
            {
                return;
            }

            detailsBox.Links.Clear();
            var magnetLine = string.IsNullOrWhiteSpace(row.MagnetLink) ? "Magnet: -" : "Magnet: Open magnet";
            detailsBox.Text =
                "Episode Search: " + DisplayOrDash(row.Provider) + Environment.NewLine +
                "Result: " + ShortenMiddle(DisplayOrDash(row.Title), 170) + Environment.NewLine +
                "Size: " + DisplayOrDash(row.Size) + " | Seed: " + row.Seeders.ToString("N0") + " | Leech: " + row.Leechers.ToString("N0") + " | Done: " + row.Downloads.ToString("N0") + Environment.NewLine +
                "Trusted: " + DisplayOrDash(row.Trusted) + " | Published: " + DisplayOrDash(row.Published) + Environment.NewLine +
                magnetLine + Environment.NewLine +
                "Page: " + ShortenMiddle(DisplayOrDash(row.Link), 170);
            if (!string.IsNullOrWhiteSpace(row.MagnetLink))
            {
                var start = detailsBox.Text.IndexOf("Open magnet", StringComparison.Ordinal);
                if (start >= 0)
                {
                    detailsBox.Links.Add(start, "Open magnet".Length, row.MagnetLink);
                }
            }
            metadataBox.Text =
                "Series: " + DisplayOrDash(row.SeriesTitle) + Environment.NewLine +
                "Episode: " + DisplayOrDash(row.MissingEpisode) + Environment.NewLine +
                "Provider: " + DisplayOrDash(row.Provider) + Environment.NewLine +
                "Full season: " + (row.IsBatchResult ? "Yes" : "No");
            UpdateInspectorPreviewForTitle(row.SeriesTitle);
        }

        private void UpdateDetails(SelectedSearchFeedItem row)
        {
            if (detailsBox == null || row == null)
            {
                return;
            }

            detailsBox.Links.Clear();
            var linkLine = string.IsNullOrWhiteSpace(row.MagnetLink) ? "Link: Open page" : "Magnet: Open magnet";
            detailsBox.Text =
                "Selected RSS: " + DisplayOrDash(row.Provider) + Environment.NewLine +
                "Series: " + DisplayOrDash(row.SeriesTitle) + " | Episode: " + DisplayOrDash(row.MissingEpisode) + Environment.NewLine +
                "Search: " + ShortenMiddle(DisplayOrDash(row.SearchQuery), 170) + Environment.NewLine +
                "Result: " + ShortenMiddle(DisplayOrDash(row.Title), 170) + Environment.NewLine +
                "Size: " + DisplayOrDash(row.Size) + " | Seed: " + row.Seeders.ToString("N0") + " | Published: " + DisplayOrDash(row.Published) + Environment.NewLine +
                linkLine;
            var target = string.IsNullOrWhiteSpace(row.MagnetLink) ? row.Link : row.MagnetLink;
            var linkText = string.IsNullOrWhiteSpace(row.MagnetLink) ? "Open page" : "Open magnet";
            if (!string.IsNullOrWhiteSpace(target))
            {
                var start = detailsBox.Text.IndexOf(linkText, StringComparison.Ordinal);
                if (start >= 0)
                {
                    detailsBox.Links.Add(start, linkText.Length, target);
                }
            }
            metadataBox.Text =
                "Series: " + DisplayOrDash(row.SeriesTitle) + Environment.NewLine +
                "Episode: " + DisplayOrDash(row.MissingEpisode) + Environment.NewLine +
                "Provider: " + DisplayOrDash(row.Provider) + Environment.NewLine +
                "Full season: " + (row.IsBatchResult ? "Yes" : "No");
            UpdateInspectorPreviewForTitle(row.SeriesTitle);
        }

        private void UpdateInspectorPreview(EpisodeFile file)
        {
            UpdateInspectorPreviewForTitle(file == null ? "" : file.Title);
        }

        private void UpdateInspectorPreviewForTitle(string title)
        {
            if (inspectorPreviewBox == null)
            {
                return;
            }

            var previewTitle = string.IsNullOrWhiteSpace(title) ? GetSelectedShellSeriesTitle(GetCurrentCandidateFile()) : title;
            var rowsForTitle = string.IsNullOrWhiteSpace(previewTitle)
                ? new List<EpisodeFile>()
                : GetShellSeriesRows(previewTitle, null);
            var coverPath = rowsForTitle.Count == 0 ? "" : FindSeriesCoverPath(rowsForTitle);
            var oldImage = inspectorPreviewBox.Image;
            inspectorPreviewBox.Image = CreateSeriesCoverImage(string.IsNullOrWhiteSpace(previewTitle) ? "Preview" : previewTitle, coverPath);
            if (oldImage != null)
            {
                oldImage.Dispose();
            }
        }

        private string GetSearchQuery(MissingEpisodeRow row)
        {
            var missingEpisode = MissingEpisodeAnalyzer.ToSearchMissingEpisode(row);
            return missingEpisode == null ? "" : missingEpisode.SearchQuery;
        }

        private sealed class ShellSeriesFetchResult
        {
            public string Title { get; set; }
            public AniDbAnimeResult MetadataMatch { get; set; }
            public string MetadataError { get; set; }
            public bool CoverSaved { get; set; }
            public string CoverMessage { get; set; }
            public string Message { get; set; }
        }

        private static string DisplayOrDash(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "-" : value;
        }

        private static string ShortenMiddle(string value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value) || maxLength < 8 || value.Length <= maxLength)
            {
                return value;
            }

            var keep = maxLength - 3;
            var left = keep / 2;
            var right = keep - left;
            return value.Substring(0, left) + "..." + value.Substring(value.Length - right);
        }

        private void MonitorFoldersMenuItem_Click(object sender, EventArgs e)
        {
            if (toolsMonitorFoldersMenuItem.Checked)
            {
                var roots = GetSessionRoots().Where(Directory.Exists).ToList();
                if (roots.Count == 0)
                {
                    toolsMonitorFoldersMenuItem.Checked = false;
                    MessageBox.Show(this, "Scan or add a folder before enabling monitoring.", "Monitor Scan Roots", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                nextMonitorCheckUtc = DateTime.UtcNow.AddDays(5);
                monitorTimer.Start();
                UpdateActivity("Monitoring enabled. Next folder check: " + nextMonitorCheckUtc.ToLocalTime().ToString("g"), true);
            }
            else
            {
                monitorTimer.Stop();
                UpdateActivity("Monitoring disabled.", true);
            }
        }

        private void MonitorTimer_Tick(object sender, EventArgs e)
        {
            if (!toolsMonitorFoldersMenuItem.Checked || busyState || DateTime.UtcNow < nextMonitorCheckUtc)
            {
                return;
            }

            nextMonitorCheckUtc = DateTime.UtcNow.AddDays(5);
            StartMonitorScan();
        }

        private void StartMonitorScan()
        {
            var roots = GetSessionRoots().Where(Directory.Exists).ToList();
            if (roots.Count == 0)
            {
                toolsMonitorFoldersMenuItem.Checked = false;
                monitorTimer.Stop();
                UpdateActivity("Monitoring stopped because no scan roots are available.", true);
                return;
            }

            SetBusy(true, "Monitoring scan roots...");
            var worker = new BackgroundWorker();
            var scanFilter = fileFormatFilter.Clone();
            worker.DoWork += delegate(object workerSender, DoWorkEventArgs args)
            {
                var combined = new List<EpisodeFile>();
                foreach (var root in roots)
                {
                    ThrowIfCancellationRequested(delegate { return cancelRequested; });
                    combined.AddRange(ScanWithDetails(root, scanFilter, null, delegate { return cancelRequested; }).ScannedRows);
                }

                args.Result = BuildMergedScanResult(new List<EpisodeFile>(), combined);
            };
            worker.RunWorkerCompleted += delegate(object workerSender, RunWorkerCompletedEventArgs args)
            {
                try
                {
                    if (args.Error is OperationCanceledException)
                    {
                        UpdateActivity("Monitoring scan canceled.", true);
                        return;
                    }

                    if (args.Error != null)
                    {
                        throw args.Error;
                    }

                    var result = (ScanResult)args.Result;
                    LoadRowsIntoUi(result.DuplicateRows, result.ScannedRows);
                    var missingCount = missingEpisodeRows.Count;
                    UpdateSummary(string.Format("Monitor check complete: {0:N0} scanned | {1:N0} missing episode row(s). Next check: {2}", result.ScannedRows.Count, missingCount, nextMonitorCheckUtc.ToLocalTime().ToString("g")));
                    if (missingCount > 0)
                    {
                        MessageBox.Show(this, string.Format("Monitor check found {0:N0} missing episode row(s).", missingCount), "Missing Episodes", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                }
                catch (Exception ex)
                {
                    LogException("Monitor scan failed", ex);
                    UpdateActivity("Monitor scan failed: " + ex.Message, true);
                }
                finally
                {
                    SetBusy(false, statusLabel.Text);
                }
            };
            worker.RunWorkerAsync();
        }

        private void ToggleCandidatesButton_Click(object sender, EventArgs e)
        {
            activeShellSection = "Duplicates";
            candidatesPanelCollapsed = !candidatesPanelCollapsed;
            ApplyWorkspacePanelVisibility();
        }

        private void ToggleReadyButton_Click(object sender, EventArgs e)
        {
            activeShellSection = "Duplicates";
            deletionPanelCollapsed = !deletionPanelCollapsed;
            ApplyWorkspacePanelVisibility();
        }

        private void ToggleMissingEpisodesButton_Click(object sender, EventArgs e)
        {
            missingEpisodesPanelCollapsed = !missingEpisodesPanelCollapsed;
            if (missingEpisodesPanelCollapsed)
            {
                episodeSearchPanelCollapsed = true;
            }
            else
            {
                activeShellSection = "MissingEpisodes";
            }
            ApplyWorkspacePanelVisibility();
        }

        private void ToggleEpisodeSearchButton_Click(object sender, EventArgs e)
        {
            episodeSearchPanelCollapsed = !episodeSearchPanelCollapsed;
            if (!episodeSearchPanelCollapsed)
            {
                activeShellSection = "EpisodeSearch";
            }
            ApplyWorkspacePanelVisibility();
        }

        private void ToggleSelectedFeedButton_Click(object sender, EventArgs e)
        {
            selectedFeedPanelCollapsed = !selectedFeedPanelCollapsed;
            if (!selectedFeedPanelCollapsed)
            {
                activeShellSection = "SelectedRss";
            }
            ApplyWorkspacePanelVisibility();
        }

        private void FileFormatsMenuItem_Click(object sender, EventArgs e)
        {
            using (var dialog = new FileFormatFilterDialog(fileFormatFilter))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK)
                {
                    return;
                }

                fileFormatFilter = dialog.Filter;
                FileFormatFilterStore.Save(fileFormatFilter);
                UpdateActivity(fileFormatFilter.AllowOnlyListed
                    ? "File format filter saved. Next scan will only include selected formats."
                    : "File format filter saved. Next scan will skip selected formats.", true);
            }
        }

        private void StartScan(string root, bool append)
        {
            if (!Directory.Exists(root))
            {
                MessageBox.Show(this, "That folder does not exist.", "Folder not found", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (!append)
            {
                SetSessionRoots(new[] { root });
                rootBox.Text = root;
            }

            SetBusy(true, append ? "Preparing additional scan..." : "Preparing scan...");
            if (!append)
            {
                rows.Clear();
                UpdateCandidateTotal();
                deletionRows.Clear();
                UpdateDeletionTotal();
                allRows.Clear();
                allScannedRows.Clear();
                missingEpisodeRows.Clear();
                episodeSearchRows.Clear();
                selectedFeedRows.Clear();
                SaveAndRefreshSelectedFeed();
                seriesListView.Items.Clear();
                seriesCoverView.Items.Clear();
                seriesCoverImages.Images.Clear();
                activeSeriesTag = null;
                UpdateDetails((EpisodeFile)null);
            }

            var worker = new BackgroundWorker();
            worker.WorkerReportsProgress = true;
            var scanFilter = fileFormatFilter.Clone();
            var existingScannedRows = append ? allScannedRows.ToList() : new List<EpisodeFile>();
            worker.DoWork += delegate(object workerSender, DoWorkEventArgs args)
            {
                var backgroundWorker = (BackgroundWorker)workerSender;
                Action<string> report = delegate(string message)
                {
                    backgroundWorker.ReportProgress(0, message);
                };
                var result = ScanWithDetails(root, scanFilter, report, delegate { return cancelRequested; });
                ThrowIfCancellationRequested(delegate { return cancelRequested; });
                if (append)
                {
                    report("Merging scan results...");
                    result = BuildMergedScanResult(existingScannedRows, result.ScannedRows);
                }
                else
                {
                    report("Writing duplicate candidate cache...");
                    SaveCachedScan(root, result.DuplicateRows);
                }
                args.Result = result;
            };
            worker.ProgressChanged += delegate(object workerSender, ProgressChangedEventArgs args)
            {
                UpdateActivity(args.UserState as string ?? "Scanning...", false);
            };
            worker.RunWorkerCompleted += delegate(object workerSender, RunWorkerCompletedEventArgs args)
            {
                try
                {
                    if (args.Error is OperationCanceledException)
                    {
                        UpdateSummary("Scan canceled.");
                        return;
                    }

                    if (args.Error != null)
                    {
                        throw args.Error;
                    }

                    var result = (ScanResult)args.Result;
                    if (append)
                    {
                        AddSessionRoot(root);
                        rootBox.Text = BuildSessionRootLabel(GetSessionRoots());
                    }

                    UpdateActivity("Building scan results view...", false);
                    LoadRowsIntoUi(result.DuplicateRows, result.ScannedRows);

                    UpdateSummary(append
                        ? string.Format("Added scan location. Combined session: {0:N0} scanned | {1:N0} duplicate candidates | {2:N0} duplicate groups.", result.ScannedRows.Count, result.DuplicateRows.Count, result.DuplicateGroups)
                        : result.Summary);
                }
                catch (Exception ex)
                {
                    LogException("Scan failed", ex);
                    MessageBox.Show(this, ex.Message, "Scan failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    UpdateActivity("Scan failed.", true);
                }
                finally
                {
                    SetBusy(false, statusLabel.Text);
                }
            };
            worker.RunWorkerAsync();
        }

        private void StartScan(List<string> roots)
        {
            roots = (roots ?? new List<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (roots.Count == 0)
            {
                MessageBox.Show(this, "Add at least one folder before scanning.", "Scan", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (roots.Count == 1)
            {
                StartScan(roots[0], false);
                return;
            }

            var missingRoots = roots.Where(x => !Directory.Exists(x)).ToList();
            if (missingRoots.Count > 0)
            {
                MessageBox.Show(this, "These folders do not exist:\r\n" + string.Join("\r\n", missingRoots.Take(8).ToArray()), "Scan", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            SetSessionRoots(roots);
            rootBox.Text = BuildSessionRootLabel(roots);
            SetBusy(true, string.Format("Preparing scan for {0:N0} locations...", roots.Count));
            rows.Clear();
            UpdateCandidateTotal();
            deletionRows.Clear();
            UpdateDeletionTotal();
            allRows.Clear();
            allScannedRows.Clear();
            missingEpisodeRows.Clear();
            episodeSearchRows.Clear();
            selectedFeedRows.Clear();
            SaveAndRefreshSelectedFeed();
            seriesListView.Items.Clear();
            seriesCoverView.Items.Clear();
            seriesCoverImages.Images.Clear();
            activeSeriesTag = null;
            UpdateDetails((EpisodeFile)null);

            var worker = new BackgroundWorker();
            worker.WorkerReportsProgress = true;
            var scanFilter = fileFormatFilter.Clone();
            worker.DoWork += delegate(object workerSender, DoWorkEventArgs args)
            {
                var backgroundWorker = (BackgroundWorker)workerSender;
                var combined = new List<EpisodeFile>();
                for (var i = 0; i < roots.Count; i++)
                {
                    ThrowIfCancellationRequested(delegate { return cancelRequested; });
                    var root = roots[i];
                    Action<string> report = delegate(string message)
                    {
                        backgroundWorker.ReportProgress(0, string.Format("[{0:N0}/{1:N0}] {2}", i + 1, roots.Count, message));
                    };
                    combined.AddRange(ScanWithDetails(root, scanFilter, report, delegate { return cancelRequested; }).ScannedRows);
                }

                backgroundWorker.ReportProgress(0, "Merging scan results...");
                args.Result = BuildMergedScanResult(new List<EpisodeFile>(), combined);
            };
            worker.ProgressChanged += delegate(object workerSender, ProgressChangedEventArgs args)
            {
                UpdateActivity(args.UserState as string ?? "Scanning...", false);
            };
            worker.RunWorkerCompleted += delegate(object workerSender, RunWorkerCompletedEventArgs args)
            {
                try
                {
                    if (args.Error is OperationCanceledException)
                    {
                        UpdateSummary("Scan canceled.");
                        return;
                    }

                    if (args.Error != null)
                    {
                        throw args.Error;
                    }

                    var result = (ScanResult)args.Result;
                    UpdateActivity("Building scan results view...", false);
                    LoadRowsIntoUi(result.DuplicateRows, result.ScannedRows);
                    UpdateSummary(string.Format("Scan complete across {0:N0} locations: {1:N0} scanned | {2:N0} duplicate candidates | {3:N0} duplicate groups.", roots.Count, result.ScannedRows.Count, result.DuplicateRows.Count, result.DuplicateGroups));
                }
                catch (Exception ex)
                {
                    LogException("Scan failed", ex);
                    MessageBox.Show(this, ex.Message, "Scan failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    UpdateActivity("Scan failed.", true);
                }
                finally
                {
                    SetBusy(false, statusLabel.Text);
                }
            };
            worker.RunWorkerAsync();
        }

        private void LoadSavedButton_Click(object sender, EventArgs e)
        {
            var root = GetPrimarySessionRoot().Trim();
            if (string.IsNullOrWhiteSpace(root) || string.Equals(root, "No folder scanned", StringComparison.OrdinalIgnoreCase) || !Directory.Exists(root))
            {
                var cachedRoot = ReadCacheRoot(GetCachePath());
                if (!string.IsNullOrWhiteSpace(cachedRoot))
                {
                    root = cachedRoot;
                    rootBox.Text = cachedRoot;
                    SetSessionRoots(new[] { cachedRoot });
                }
            }

            StartLoadCachedScan(root, false);
        }

        private void AniDbButton_Click(object sender, EventArgs e)
        {
            var hasScannedData = allRows.Count > 0 || allScannedRows.Count > 0;
            if (!hasScannedData)
            {
                MessageBox.Show(this, "Load or scan files before updating metadata or covers.", "Update Metadata & Covers", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            bool updateMetadata;
            bool fetchMissingCovers;
            if (!ShowMetadataUpdateDialog(out updateMetadata, out fetchMissingCovers))
            {
                return;
            }

            if (updateMetadata)
            {
                RunMetadataLookup(fetchMissingCovers);
            }
            else if (fetchMissingCovers)
            {
                RunAniDbMissingCoverScan();
            }
        }

        private bool ShowMetadataUpdateDialog(out bool updateMetadata, out bool fetchMissingCovers)
        {
            updateMetadata = false;
            fetchMissingCovers = false;

            using (var dialog = new Form())
            using (var metadataBox = new CheckBox())
            using (var coversBox = new CheckBox())
            using (var okButton = new Button())
            using (var cancelButton = new Button())
            {
                dialog.Text = "Update Metadata & Covers";
                dialog.StartPosition = FormStartPosition.CenterParent;
                dialog.ClientSize = new Size(420, 170);
                dialog.FormBorderStyle = FormBorderStyle.FixedDialog;
                dialog.MaximizeBox = false;
                dialog.MinimizeBox = false;

                var layout = new TableLayoutPanel();
                layout.Dock = DockStyle.Fill;
                layout.Padding = new Padding(14);
                layout.ColumnCount = 1;
                layout.RowCount = 4;
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
                layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));

                metadataBox.Text = "Update metadata matches";
                metadataBox.Checked = true;
                metadataBox.Dock = DockStyle.Fill;
                coversBox.Text = "Fetch missing covers after metadata";
                coversBox.Checked = true;
                coversBox.Dock = DockStyle.Fill;

                var noteLabel = new Label();
                noteLabel.Text = "Provider work runs in the background and uses the current AniDB HTTP XML, TVDB, and TMDB settings.";
                noteLabel.Dock = DockStyle.Fill;
                noteLabel.AutoEllipsis = true;

                var buttonPanel = new FlowLayoutPanel();
                buttonPanel.Dock = DockStyle.Fill;
                buttonPanel.FlowDirection = FlowDirection.RightToLeft;
                okButton.Text = "Run";
                okButton.Width = 90;
                okButton.DialogResult = DialogResult.OK;
                cancelButton.Text = "Cancel";
                cancelButton.Width = 90;
                cancelButton.DialogResult = DialogResult.Cancel;
                StyleButton(okButton, true);
                StyleButton(cancelButton, false);
                buttonPanel.Controls.Add(okButton);
                buttonPanel.Controls.Add(cancelButton);

                layout.Controls.Add(metadataBox, 0, 0);
                layout.Controls.Add(coversBox, 0, 1);
                layout.Controls.Add(noteLabel, 0, 2);
                layout.Controls.Add(buttonPanel, 0, 3);
                dialog.Controls.Add(layout);
                dialog.AcceptButton = okButton;
                dialog.CancelButton = cancelButton;

                if (dialog.ShowDialog(this) != DialogResult.OK)
                {
                    return false;
                }

                updateMetadata = metadataBox.Checked;
                fetchMissingCovers = coversBox.Checked;
                if (!updateMetadata && !fetchMissingCovers)
                {
                    MessageBox.Show(this, "Select at least one update to run.", "Update Metadata & Covers", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return false;
                }

                return true;
            }
        }

        private void RunAniDbMissingCoverScan()
        {
            var missing = GetSeriesSourceRows().GroupBy(x => x.Title, StringComparer.OrdinalIgnoreCase)
                                 .Where(g => !string.IsNullOrWhiteSpace(g.Key) && string.IsNullOrWhiteSpace(FindSeriesCoverPath(g)))
                                 .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                                 .ToList();
            if (missing.Count == 0)
            {
                MessageBox.Show(this, "No missing series covers were found.", "Missing Covers", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var confirm = MessageBox.Show(
                this,
                string.Format("Fetch poster art for {0:N0} series with missing local covers?\r\n\r\nThis can make one or more provider requests per series. AniDB will be tried first at a throttled pace; TVDB and TMDB will be used as backups when configured. Images will be saved beside the first loaded file for each series using the series name, for example Air Gear.jpg.", missing.Count),
                "Missing Covers",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);
            if (confirm != DialogResult.Yes)
            {
                return;
            }

            SetBusy(true, "Fetching missing covers...");
            var worker = new BackgroundWorker();
            worker.WorkerReportsProgress = true;
            worker.DoWork += delegate(object workerSender, DoWorkEventArgs args)
            {
                var saved = 0;
                var skipped = 0;
                var failures = new List<string>();
                var manualCandidates = new List<AniDbTitleCandidate>();

                for (var i = 0; i < missing.Count; i++)
                {
                    ThrowIfCancellationRequested(delegate { return cancelRequested; });
                    var group = missing[i];
                    worker.ReportProgress(0, string.Format("Cover lookup {0:N0}/{1:N0}: {2}", i + 1, missing.Count, group.Key));

                    var targetFolder = GetSeriesCoverTargetFolder(group);
                    try
                    {
                        var match = GetAniDbMatchForCover(group.Key, group);
                        if (string.IsNullOrWhiteSpace(targetFolder))
                        {
                            skipped++;
                            failures.Add(group.Key + ": no local folder was available to save the series-named cover.");
                        }
                        else if (match != null && match.Found && !string.IsNullOrWhiteSpace(match.PictureFile))
                        {
                            AppendDiagnosticLog("COVER", group.Key + ": downloading AniDB picture " + match.PictureFile);
                            DownloadAniDbPicture(match.PictureFile, GetSeriesCoverTargetPath(group.Key, targetFolder));
                            saved++;
                            AppendDiagnosticLog("COVER", group.Key + ": saved AniDB cover.");
                        }
                        else
                        {
                            string fallbackMessage;
                            var targetPath = GetSeriesCoverTargetPath(group.Key, targetFolder);
                            AppendDiagnosticLog("COVER", group.Key + ": AniDB cover unavailable; trying fallback providers.");
                            if (TryDownloadFallbackCover(group.Key, targetPath, out fallbackMessage))
                            {
                                saved++;
                                AppendDiagnosticLog("COVER", group.Key + ": saved fallback cover.");
                            }
                            else
                            {
                                skipped++;
                                if (!string.IsNullOrWhiteSpace(fallbackMessage))
                                {
                                    failures.Add(group.Key + " fallback: " + fallbackMessage);
                                    AppendDiagnosticLog("COVER", group.Key + ": fallback failed - " + fallbackMessage);
                                }
                                AddAniDbCoverCandidates(manualCandidates, failures, group.Key, targetFolder);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        var savedByFallback = false;
                        if (!string.IsNullOrWhiteSpace(targetFolder))
                        {
                            string fallbackMessage;
                            savedByFallback = TryDownloadFallbackCover(group.Key, GetSeriesCoverTargetPath(group.Key, targetFolder), out fallbackMessage);
                            if (!savedByFallback && !string.IsNullOrWhiteSpace(fallbackMessage))
                            {
                                failures.Add(group.Key + " fallback: " + fallbackMessage);
                                AppendDiagnosticLog("COVER", group.Key + ": fallback after exception failed - " + fallbackMessage);
                            }
                        }

                        if (savedByFallback)
                        {
                            saved++;
                        }
                        else
                        {
                            failures.Add(group.Key + ": " + ex.Message);
                            AppendDiagnosticLog("COVER", group.Key + ": cover fetch failed - " + ex.Message);
                            AddAniDbCoverCandidates(manualCandidates, failures, group.Key, targetFolder);
                        }
                    }

                    if (i + 1 < missing.Count)
                    {
                        Thread.Sleep(2200);
                    }
                }

                args.Result = new object[] { saved, skipped, failures, manualCandidates };
            };
            worker.ProgressChanged += delegate(object workerSender, ProgressChangedEventArgs args)
            {
                UpdateActivity(args.UserState as string ?? "Fetching AniDB covers...", false);
            };
            worker.RunWorkerCompleted += delegate(object workerSender, RunWorkerCompletedEventArgs args)
            {
                try
                {
                    if (args.Error is OperationCanceledException)
                    {
                        UpdateSummary("AniDB cover scan canceled.");
                        return;
                    }

                    if (args.Error != null)
                    {
                        throw args.Error;
                    }

                    var result = (object[])args.Result;
                    var saved = (int)result[0];
                    var skipped = (int)result[1];
                    var failures = (List<string>)result[2];
                    var manualCandidates = (List<AniDbTitleCandidate>)result[3];
                        PopulateSeriesPanel();
                        UpdateSummary(string.Format("AniDB cover scan complete. Saved {0:N0}; skipped {1:N0}.", saved, skipped));
                        AppendDiagnosticLog("COVER", string.Format("AniDB cover scan complete. Saved {0:N0}; skipped {1:N0}; failures {2:N0}.", saved, skipped, failures.Count));
                    if (failures.Count > 0)
                    {
                        MessageBox.Show(this, string.Join("\r\n", failures.Take(12).ToArray()), "Some covers could not be fetched", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                    if (manualCandidates.Count > 0)
                    {
                        BeginInvoke(new Action(delegate
                        {
                            ShowAniDbCoverMatchDialog(manualCandidates);
                        }));
                    }
                }
                catch (Exception ex)
                {
                    LogException("AniDB cover scan failed", ex);
                    MessageBox.Show(this, ex.Message, "AniDB cover scan failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    UpdateActivity("AniDB cover scan failed.", true);
                }
                finally
                {
                    SetBusy(false, statusLabel.Text);
                }
            };
            worker.RunWorkerAsync();
        }

        private AniDbAnimeResult GetAniDbMatchForCover(string title, IEnumerable<EpisodeFile> files)
        {
            var existing = files.FirstOrDefault(x => IsAniDbHttpId(x.AniDbId));
            var match = new AniDbAnimeResult { QueryTitle = title };
            if (existing != null)
            {
                match.AniDbId = existing.AniDbId;
                match.Title = string.IsNullOrWhiteSpace(existing.AniDbTitle) ? title : existing.AniDbTitle;
                match.Year = existing.AniDbYear;
            }
            else
            {
                var candidate = FindAniDbCandidates(title, "", 1).FirstOrDefault();
                if (candidate == null)
                {
                    match.Error = "No AniDB match";
                    return match;
                }

                if (!IsAutomaticAniDbMatchConfident(candidate))
                {
                    match.Error = BuildLowConfidenceAniDbMessage(candidate);
                    AppendDiagnosticLog("METADATA", title + ": " + match.Error);
                    return match;
                }

                match.AniDbId = candidate.AniDbId;
                match.Title = candidate.Title;
                match.Score = candidate.Score;
            }

            if (match.Found)
            {
                match.PictureFile = AniDbClient.GetAnimePictureFile(match.AniDbId);
            }

            return match;
        }

        private static bool IsAniDbHttpId(string value)
        {
            return !string.IsNullOrWhiteSpace(value) && Regex.IsMatch(value.Trim(), @"^\d+$");
        }

        private static void AddAniDbCoverCandidates(List<AniDbTitleCandidate> candidates, List<string> failures, string queryTitle, string targetFolder)
        {
            if (string.IsNullOrWhiteSpace(targetFolder))
            {
                return;
            }

            try
            {
                candidates.AddRange(FindAniDbCandidates(queryTitle, targetFolder, 8));
            }
            catch (Exception ex)
            {
                failures.Add(queryTitle + " candidate search: " + ex.Message);
            }
        }

        internal static List<string> BuildCoverSearchTitles(string title)
        {
            var titles = new List<string>();
            var cleaned = CleanCoverSearchTitle(title);
            AddCoverSearchTitle(titles, NormalizeCoverSearchPunctuation(cleaned));
            AddCoverSearchTitle(titles, cleaned);

            var withoutPartSuffix = StripCoverSearchSeasonSuffix(cleaned);
            if (!string.Equals(withoutPartSuffix, cleaned, StringComparison.OrdinalIgnoreCase))
            {
                AddCoverSearchTitle(titles, NormalizeCoverSearchPunctuation(withoutPartSuffix));
                AddCoverSearchTitle(titles, withoutPartSuffix);
            }

            var separatorTitle = withoutPartSuffix;
            var separatorIndex = FindCoverSearchSubtitleSeparator(separatorTitle);
            if (separatorIndex > 0)
            {
                AddCoverSearchTitle(titles, NormalizeCoverSearchPunctuation(separatorTitle.Substring(0, separatorIndex)));
                AddCoverSearchTitle(titles, separatorTitle.Substring(0, separatorIndex));
            }

            if (titles.Count == 0 && !string.IsNullOrWhiteSpace(title))
            {
                AddCoverSearchTitle(titles, title.Trim());
            }

            return titles.Take(5).ToList();
        }

        private static string BuildAniDbCoverSearchTitle(string title)
        {
            var titles = BuildCoverSearchTitles(title);
            return titles.Count == 0 ? title : titles[0];
        }

        private static string CleanCoverSearchTitle(string title)
        {
            var cleaned = Regex.Replace(title ?? "", @"\[[^\]]+\]|\([^\)]*\)", " ");
            cleaned = Regex.Replace(cleaned, @"\b(480p|576p|720p|1080p|2160p|x264|x265|h264|h265|hevc|avc|aac|flac|multi\s*sub|dual audio|bluray|blu ray|bdrip|webrip|web dl|web-dl|cr)\b", " ", RegexOptions.IgnoreCase);
            cleaned = Regex.Replace(cleaned, @"[_\.]+", " ");
            cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim();
            return cleaned;
        }

        private static string NormalizeCoverSearchPunctuation(string title)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                return "";
            }

            var normalized = Regex.Replace(title.Trim(), @"\bDr\s+Stone\b", "Dr. Stone", RegexOptions.IgnoreCase);
            normalized = Regex.Replace(normalized, @"\s+-\s+", ": ");
            normalized = Regex.Replace(normalized, @"\s+", " ").Trim();
            return normalized;
        }

        private static string StripCoverSearchSeasonSuffix(string title)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                return "";
            }

            var stripped = Regex.Replace(title.Trim(), @"(?:\s+[-:])?\s+\b(?:Part|Cour|Season)\s+\d{1,2}\b$", "", RegexOptions.IgnoreCase);
            stripped = Regex.Replace(stripped, @"\s+\bS\d{1,2}\b$", "", RegexOptions.IgnoreCase);
            return Regex.Replace(stripped, @"\s+", " ").Trim();
        }

        private static int FindCoverSearchSubtitleSeparator(string title)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                return -1;
            }

            var dashIndex = title.IndexOf(" - ", StringComparison.Ordinal);
            var colonIndex = title.IndexOf(": ", StringComparison.Ordinal);
            if (dashIndex < 0)
            {
                return colonIndex;
            }

            if (colonIndex < 0)
            {
                return dashIndex;
            }

            return Math.Min(dashIndex, colonIndex);
        }

        private static void AddCoverSearchTitle(List<string> titles, string title)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                return;
            }

            title = Regex.Replace(title, @"\s+", " ").Trim();
            if (!titles.Any(x => string.Equals(x, title, StringComparison.OrdinalIgnoreCase)))
            {
                titles.Add(title);
            }
        }

        private void ShowAniDbCoverMatchDialog(List<AniDbTitleCandidate> candidates)
        {
            var deduped = candidates.Where(x => !string.IsNullOrWhiteSpace(x.TargetFolder))
                                    .GroupBy(x => x.QueryTitle + "|" + x.AniDbId, StringComparer.OrdinalIgnoreCase)
                                    .Select(g => g.OrderByDescending(x => x.Score).First())
                                    .OrderBy(x => x.QueryTitle, StringComparer.OrdinalIgnoreCase)
                                    .ThenByDescending(x => x.Score)
                                    .ToList();
            if (deduped.Count == 0)
            {
                MessageBox.Show(this, "No manual AniDB cover candidates were found for skipped series.", "AniDB Cover Matches", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using (var dialog = new AniDbCoverMatchDialog(deduped))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK)
                {
                    return;
                }

                var selected = dialog.SelectedCandidates;
                if (selected.Count == 0)
                {
                    MessageBox.Show(this, "No AniDB cover matches were selected.", "AniDB Cover Matches", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                RunManualAniDbCoverFetch(selected);
            }
        }

        private void RunManualAniDbCoverFetch(List<AniDbTitleCandidate> selected)
        {
            SetBusy(true, "Fetching selected AniDB covers...");
            var worker = new BackgroundWorker();
            worker.WorkerReportsProgress = true;
            worker.DoWork += delegate(object workerSender, DoWorkEventArgs args)
            {
                var saved = 0;
                var skipped = 0;
                var failures = new List<string>();
                for (var i = 0; i < selected.Count; i++)
                    {
                        ThrowIfCancellationRequested(delegate { return cancelRequested; });
                        var candidate = selected[i];
                        worker.ReportProgress(0, string.Format("Fetching selected cover {0:N0}/{1:N0}: {2}", i + 1, selected.Count, candidate.Title));
                        try
                        {
                            var pictureFile = AniDbClient.GetAnimePictureFile(candidate.AniDbId);
                            if (string.IsNullOrWhiteSpace(pictureFile) || string.IsNullOrWhiteSpace(candidate.TargetFolder))
                            {
                                skipped++;
                            }
                            else
                            {
                                AppendDiagnosticLog("COVER", candidate.QueryTitle + " -> " + candidate.Title + ": downloading selected AniDB picture " + pictureFile);
                                DownloadAniDbPicture(pictureFile, GetSeriesCoverTargetPath(candidate.QueryTitle ?? candidate.Title, candidate.TargetFolder));
                                saved++;
                                AppendDiagnosticLog("COVER", candidate.QueryTitle + " -> " + candidate.Title + ": saved selected cover.");
                            }
                        }
                        catch (Exception ex)
                        {
                            failures.Add(candidate.QueryTitle + " -> " + candidate.Title + ": " + ex.Message);
                            AppendDiagnosticLog("COVER", candidate.QueryTitle + " -> " + candidate.Title + ": selected cover fetch failed - " + ex.Message);
                        }

                        if (i + 1 < selected.Count)
                        {
                            Thread.Sleep(1200);
                        }
                    }

                args.Result = new object[] { saved, skipped, failures };
            };
            worker.ProgressChanged += delegate(object workerSender, ProgressChangedEventArgs args)
            {
                UpdateActivity(args.UserState as string ?? "Fetching selected AniDB covers...", false);
            };
            worker.RunWorkerCompleted += delegate(object workerSender, RunWorkerCompletedEventArgs args)
            {
                try
                {
                    if (args.Error is OperationCanceledException)
                    {
                        UpdateSummary("Manual AniDB cover fetch canceled.");
                        return;
                    }

                    if (args.Error != null)
                    {
                        throw args.Error;
                    }

                    var result = (object[])args.Result;
                    var saved = (int)result[0];
                    var skipped = (int)result[1];
                    var failures = (List<string>)result[2];
                    PopulateSeriesPanel();
                    ComputeReviewRecommendations(false);
                    RefreshReviewGrids();
                    UpdateSummary(string.Format("Manual AniDB cover fetch complete. Saved {0:N0}; skipped {1:N0}.", saved, skipped));
                    AppendDiagnosticLog("COVER", string.Format("Manual AniDB cover fetch complete. Saved {0:N0}; skipped {1:N0}; failures {2:N0}.", saved, skipped, failures.Count));
                    if (failures.Count > 0)
                    {
                        MessageBox.Show(this, string.Join("\r\n", failures.Take(12).ToArray()), "Some selected covers could not be fetched", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                }
                catch (Exception ex)
                {
                    LogException("Manual AniDB cover fetch failed", ex);
                    MessageBox.Show(this, ex.Message, "Manual AniDB cover fetch failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    UpdateActivity("Manual AniDB cover fetch failed.", true);
                }
                finally
                {
                    SetBusy(false, statusLabel.Text);
                }
            };
            worker.RunWorkerAsync();
        }

        private static void DownloadAniDbPicture(string pictureFile, string targetPath)
        {
            if (File.Exists(targetPath))
            {
                return;
            }

            var url = "https://cdn-eu.anidb.net/images/main/" + Uri.EscapeDataString(pictureFile);
            using (var webClient = new HttpTimeoutWebClient())
            {
                webClient.Headers[HttpRequestHeader.UserAgent] = "SameEpisodeDuplicateFinder";
                webClient.DownloadFile(url, targetPath);
            }
        }

        private bool TryDownloadFallbackCover(string title, string targetPath, out string message)
        {
            var failures = new List<string>();
            var searchTitles = BuildCoverSearchTitles(title).Take(3).ToList();
            if (searchTitles.Count == 0)
            {
                searchTitles.Add(title);
            }

            foreach (var searchTitle in searchTitles)
            {
                string tvDbMessage;
                if (TryDownloadTvDbCover(searchTitle, targetPath, out tvDbMessage))
                {
                    message = "TVDB (" + searchTitle + "): " + tvDbMessage;
                    return true;
                }

                if (!string.IsNullOrWhiteSpace(tvDbMessage))
                {
                    failures.Add("TVDB " + searchTitle + ": " + tvDbMessage);
                }

                string tmDbMessage;
                if (TryDownloadTmDbCover(searchTitle, targetPath, out tmDbMessage))
                {
                    message = "TMDB (" + searchTitle + "): " + tmDbMessage;
                    return true;
                }

                if (!string.IsNullOrWhiteSpace(tmDbMessage))
                {
                    failures.Add("TMDB " + searchTitle + ": " + tmDbMessage);
                }
            }

            message = string.Join("; ", failures.Take(6).ToArray());
            return false;
        }

        private bool TryDownloadTvDbCover(string title, string targetPath, out string message)
        {
            message = "";
            var settings = TvDbSettingsStore.Load();
            if (!settings.HasApiKey)
            {
                message = "TVDB API key is not configured.";
                return false;
            }

            try
            {
                var client = new TvDbClient(settings);
                return client.TryDownloadSeriesCover(title, targetPath, out message);
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return false;
            }
        }

        private bool TryDownloadTmDbCover(string title, string targetPath, out string message)
        {
            message = "";
            var settings = TmDbSettingsStore.Load();
            if (!settings.HasReadAccessToken)
            {
                message = "TMDB read access token is not configured.";
                return false;
            }

            try
            {
                var client = new TmDbClient(settings);
                return client.TryDownloadSeriesCover(title, targetPath, out message);
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return false;
            }
        }
        private string GetSeriesCoverTargetFolder(IEnumerable<EpisodeFile> files)
        {
            var first = files.FirstOrDefault();
            var folder = GetExistingFolder(first);
            return string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder) ? null : folder;
        }

        private void RunMetadataLookup()
        {
            RunMetadataLookup(false);
        }

        private void RunMetadataLookup(bool fetchMissingCoversAfter)
        {
            var titles = GetSeriesSourceRows().Select(x => x.Title)
                                    .Where(x => !string.IsNullOrWhiteSpace(x))
                                    .Distinct(StringComparer.OrdinalIgnoreCase)
                                    .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                                    .ToList();
            if (titles.Count == 0)
            {
                MessageBox.Show(this, "No series titles are available to look up.", "AniDB Lookup", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            SetBusy(true, "Looking up metadata...");
            var worker = new BackgroundWorker();
            worker.WorkerReportsProgress = true;
            worker.DoWork += delegate(object workerSender, DoWorkEventArgs args)
            {
                var matches = new Dictionary<string, AniDbAnimeResult>(StringComparer.OrdinalIgnoreCase);
                var tvDbSettings = TvDbSettingsStore.Load();
                var tvDbClient = tvDbSettings.HasApiKey ? new TvDbClient(tvDbSettings) : null;
                var tmDbSettings = TmDbSettingsStore.Load();
                var tmDbClient = tmDbSettings.HasReadAccessToken ? new TmDbClient(tmDbSettings) : null;
                var aniDbLookupAvailable = true;
                for (var i = 0; i < titles.Count; i++)
                {
                    ThrowIfCancellationRequested(delegate { return cancelRequested; });
                    var title = titles[i];
                    AniDbAnimeResult match = null;
                    if (aniDbLookupAvailable)
                    {
                        try
                        {
                            worker.ReportProgress(0, string.Format("AniDB title-cache lookup {0:N0}/{1:N0}: {2}", i + 1, titles.Count, title));
                            match = LookupAniDbMetadata(title);
                        }
                        catch (Exception ex)
                        {
                            aniDbLookupAvailable = ex.Message.IndexOf("title index", StringComparison.OrdinalIgnoreCase) < 0;
                            match = new AniDbAnimeResult { QueryTitle = title, Error = ex.Message };
                        }
                    }

                    if ((match == null || !match.Found) && tvDbClient != null)
                    {
                        worker.ReportProgress(0, string.Format("TVDB fallback {0:N0}/{1:N0}: {2}", i + 1, titles.Count, title));
                        match = tvDbClient.LookupSeries(title).ToMetadataResult();
                    }
                    if ((match == null || !match.Found) && tmDbClient != null)
                    {
                        worker.ReportProgress(0, string.Format("TMDB fallback {0:N0}/{1:N0}: {2}", i + 1, titles.Count, title));
                        match = tmDbClient.LookupSeries(title).ToMetadataResult();
                    }
                    if (match == null)
                    {
                        match = new AniDbAnimeResult { QueryTitle = title, Error = "No metadata match" };
                    }

                    matches[title] = match;
                    if (i + 1 < titles.Count)
                    {
                        Thread.Sleep(match.Found && IsAniDbHttpId(match.AniDbId) ? 250 : 1200);
                    }
                }

                args.Result = matches;
            };
            worker.ProgressChanged += delegate(object workerSender, ProgressChangedEventArgs args)
            {
                UpdateActivity(args.UserState as string ?? "Looking up metadata matches...", false);
            };
            worker.RunWorkerCompleted += delegate(object workerSender, RunWorkerCompletedEventArgs args)
            {
                var runCoverScan = false;
                try
                {
                    if (args.Error is OperationCanceledException)
                    {
                        UpdateSummary("Metadata lookup canceled.");
                        return;
                    }

                    if (args.Error != null)
                    {
                        throw args.Error;
                    }

                    UpdateAniDbButtonState(false);
                    var matches = (Dictionary<string, AniDbAnimeResult>)args.Result;
                    ApplyAniDbMatches(matches);
                    var found = matches.Values.Count(x => x.Found);
                    var failed = matches.Values.Where(x => !x.Found && !string.IsNullOrWhiteSpace(x.Error)).Take(5).ToList();
                    var message = string.Format("Metadata lookup complete. {0:N0}/{1:N0} series matched.", found, matches.Count);
                    if (failed.Count > 0)
                    {
                        message += " First misses: " + string.Join("; ", failed.Select(x => x.QueryTitle + " - " + x.Error).ToArray());
                    }
                    UpdateSummary(message);
                    runCoverScan = fetchMissingCoversAfter;
                }
                catch (Exception ex)
                {
                    LogException("AniDB lookup failed", ex);
                    MessageBox.Show(this, ex.Message, "AniDB lookup failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    UpdateActivity("AniDB lookup failed.", true);
                }
                finally
                {
                    SetBusy(false, statusLabel.Text);
                    if (runCoverScan)
                    {
                        BeginInvoke(new Action(RunAniDbMissingCoverScan));
                    }
                }
            };
            worker.RunWorkerAsync();
        }

        private AniDbAnimeResult LookupAniDbMetadata(string title)
        {
            var candidate = FindAniDbCandidates(title, "", 1).FirstOrDefault();
            if (candidate == null)
            {
                return new AniDbAnimeResult { QueryTitle = title, Error = "No AniDB match" };
            }

            if (!IsAutomaticAniDbMatchConfident(candidate))
            {
                return new AniDbAnimeResult { QueryTitle = title, Error = BuildLowConfidenceAniDbMessage(candidate), Score = candidate.Score };
            }

            return new AniDbAnimeResult
            {
                AniDbId = candidate.AniDbId,
                QueryTitle = title,
                Title = candidate.Title,
                Score = candidate.Score
            };
        }

        internal static bool IsAutomaticAniDbMatchConfident(AniDbTitleCandidate candidate)
        {
            return candidate != null && candidate.Score >= MinimumAutomaticAniDbMatchScore;
        }

        private static string BuildLowConfidenceAniDbMessage(AniDbTitleCandidate candidate)
        {
            if (candidate == null)
            {
                return "No AniDB match";
            }

            return string.Format(
                "Low-confidence AniDB match ignored: {0} (score {1:N0})",
                string.IsNullOrWhiteSpace(candidate.Title) ? "unknown title" : candidate.Title,
                candidate.Score);
        }

        private static List<AniDbTitleCandidate> FindAniDbCandidates(string title, string targetFolder, int maxResults)
        {
            var found = new List<AniDbTitleCandidate>();
            foreach (var searchTitle in BuildCoverSearchTitles(title))
            {
                var candidates = AniDbTitleIndex.FindCandidates(searchTitle, targetFolder, maxResults);
                foreach (var candidate in candidates)
                {
                    candidate.QueryTitle = title;
                    if (!found.Any(x => string.Equals(x.AniDbId, candidate.AniDbId, StringComparison.OrdinalIgnoreCase)))
                    {
                        found.Add(candidate);
                    }
                }

                if (maxResults <= 1 && found.Count > 0)
                {
                    break;
                }
            }

            if (found.Count == 0)
            {
                found = AniDbTitleIndex.FindCandidates(title, targetFolder, maxResults);
                foreach (var candidate in found)
                {
                    candidate.QueryTitle = title;
                }
            }

            return found.OrderByDescending(x => x.Score).Take(maxResults).ToList();
        }

        private void ApplyAniDbMatches(Dictionary<string, AniDbAnimeResult> matches)
        {
            foreach (var row in allRows.Concat(allScannedRows).Where(x => x != null).Distinct())
            {
                AniDbAnimeResult match;
                if (!matches.TryGetValue(row.Title ?? "", out match))
                {
                    continue;
                }

                row.AniDbId = match.AniDbId;
                row.AniDbTitle = match.Title;
                row.AniDbYear = match.Year;
            }

            RefreshReviewGrids();
            PopulateSeriesPanel();
            if (CanWriteSingleRootCache())
            {
                SaveCurrentSessionCache();
            }
        }

        private static ScanResult ScanWithDetails(string root, FileFormatFilter filter, Action<string> progress, Func<bool> shouldCancel)
        {
            var rootFull = new DirectoryInfo(root).FullName.TrimEnd('\\');
            var parsed = new List<EpisodeFile>();
            if (filter == null)
            {
                filter = FileFormatFilter.CreateDefault();
            }
            var cachedRoot = "";
            ReportProgress(progress, "Opening scan cache...");
            var cache = ReadParseCache(out cachedRoot, progress);
            if (!string.Equals(NormalizeRoot(cachedRoot), NormalizeRoot(root), StringComparison.OrdinalIgnoreCase))
            {
                if (cache.Count > 0)
                {
                    ReportProgress(progress, "Scan cache belongs to a different folder; rebuilding it.");
                }
                cache.Clear();
            }

            var scanProgressUtc = DateTime.MinValue;
            var visited = 0;
            var ignored = 0;
            var cacheHits = 0;
            ReportProgress(progress, "Scanning files under " + ShortenMiddle(rootFull, 120) + "...");
            foreach (var file in LongPath.EnumerateFiles(rootFull, delegate(string directory)
            {
                ReportDirectoryScanProgress(progress, directory, ref scanProgressUtc);
            }))
            {
                ThrowIfCancellationRequested(shouldCancel);
                visited++;
                if (filter.ShouldIgnore(file))
                {
                    ignored++;
                    ReportScanProgress(progress, visited, parsed.Count, cacheHits, ignored, file.FullName, ref scanProgressUtc);
                    continue;
                }

                EpisodeFile info;
                CachedParsedFile cached;
                if (cache.TryGetValue(file.FullName, out cached) &&
                    cached.SizeBytes == file.Length &&
                    cached.LastWriteUtcTicks == file.LastWriteUtcTicks &&
                    cached.File != null)
                {
                    cached.File.LastWriteUtcTicks = cached.LastWriteUtcTicks;
                    parsed.Add(cached.File);
                    cacheHits++;
                    ReportScanProgress(progress, visited, parsed.Count, cacheHits, ignored, file.FullName, ref scanProgressUtc);
                    continue;
                }

                if (EpisodeParser.TryParseFile(file, rootFull, out info))
                {
                    parsed.Add(info);
                }

                ReportScanProgress(progress, visited, parsed.Count, cacheHits, ignored, file.FullName, ref scanProgressUtc);
            }

            ThrowIfCancellationRequested(shouldCancel);
            ReportProgress(progress, string.Format("Writing scan cache for {0:N0} accepted files...", parsed.Count));
            SaveParseCache(root, parsed, progress, shouldCancel);
            ThrowIfCancellationRequested(shouldCancel);
            ReportProgress(progress, "Finding duplicate episode groups...");

            var duplicateKeys = new HashSet<string>(
                parsed.GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                      .Where(g => g.Count() > 1)
                      .Select(g => g.Key),
                StringComparer.OrdinalIgnoreCase);

            var duplicates = parsed.Where(x => duplicateKeys.Contains(x.Key))
                                   .OrderBy(x => x.Title, StringComparer.OrdinalIgnoreCase)
                                   .ThenBy(x => x.Episode, StringComparer.OrdinalIgnoreCase)
                                   .ThenBy(x => x.FileName, StringComparer.OrdinalIgnoreCase)
                                   .ToList();

            ReportProgress(progress, string.Format("Scan matched {0:N0} duplicate candidates across {1:N0} groups.", duplicates.Count, duplicateKeys.Count));
            return new ScanResult
            {
                DuplicateRows = duplicates,
                ScannedRows = parsed.OrderBy(x => x.Title, StringComparer.OrdinalIgnoreCase)
                                    .ThenBy(x => x.Episode, StringComparer.OrdinalIgnoreCase)
                                    .ThenBy(x => x.FileName, StringComparer.OrdinalIgnoreCase)
                                    .ToList(),
                VisitedFiles = visited,
                IgnoredFiles = ignored,
                CacheHits = cacheHits,
                DuplicateGroups = duplicateKeys.Count
            };
        }

        internal static ScanResult BuildMergedScanResult(IEnumerable<EpisodeFile> existingRows, IEnumerable<EpisodeFile> addedRows)
        {
            var merged = (existingRows ?? Enumerable.Empty<EpisodeFile>())
                .Concat(addedRows ?? Enumerable.Empty<EpisodeFile>())
                .Where(x => x != null)
                .Where(x => !string.IsNullOrWhiteSpace(x.Path))
                .GroupBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .OrderBy(x => x.Title, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.Episode, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.FileName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var duplicateKeys = new HashSet<string>(
                merged.GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                      .Where(g => g.Count() > 1)
                      .Select(g => g.Key),
                StringComparer.OrdinalIgnoreCase);

            var duplicates = merged.Where(x => duplicateKeys.Contains(x.Key))
                                   .OrderBy(x => x.Title, StringComparer.OrdinalIgnoreCase)
                                   .ThenBy(x => x.Episode, StringComparer.OrdinalIgnoreCase)
                                   .ThenBy(x => x.FileName, StringComparer.OrdinalIgnoreCase)
                                   .ToList();

            return new ScanResult
            {
                DuplicateRows = duplicates,
                ScannedRows = merged,
                VisitedFiles = merged.Count,
                IgnoredFiles = 0,
                CacheHits = 0,
                DuplicateGroups = duplicateKeys.Count
            };
        }

        private static string BuildSessionRootLabel(List<string> roots)
        {
            roots = roots ?? new List<string>();
            if (roots.Count == 0)
            {
                return "No folder scanned";
            }

            if (roots.Count == 1)
            {
                return roots[0];
            }

            return roots[0] + string.Format(" + {0:N0} location(s)", roots.Count - 1);
        }

        private List<string> GetSessionRoots()
        {
            var roots = rootBox == null ? null : rootBox.Tag as List<string>;
            return roots == null ? new List<string>() : roots.ToList();
        }

        private string GetPrimarySessionRoot()
        {
            var roots = GetSessionRoots();
            if (roots.Count > 0)
            {
                return roots[0];
            }

            return rootBox == null ? "" : rootBox.Text;
        }

        private void SetSessionRoots(IEnumerable<string> roots)
        {
            rootBox.Tag = (roots ?? Enumerable.Empty<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private void AddSessionRoot(string root)
        {
            var roots = GetSessionRoots();
            if (!string.IsNullOrWhiteSpace(root) && !roots.Contains(root, StringComparer.OrdinalIgnoreCase))
            {
                roots.Add(root.Trim());
            }

            SetSessionRoots(roots);
        }

        private bool CanWriteSingleRootCache()
        {
            var roots = GetSessionRoots();
            return roots.Count == 1 && Directory.Exists(roots[0]);
        }

        private void SaveCurrentSessionCache()
        {
            var root = GetPrimarySessionRoot();
            if (string.IsNullOrWhiteSpace(root))
            {
                return;
            }

            SaveCachedScan(root, allRows);
            SaveParseCache(root, allScannedRows.Count > 0 ? allScannedRows : allRows);
        }

        private static void ReportProgress(Action<string> progress, string message)
        {
            if (progress != null && !string.IsNullOrWhiteSpace(message))
            {
                progress(message);
            }
        }

        private static void ReportProgress(Action<string> progress, string message, ref DateTime lastReportUtc, int minimumMilliseconds)
        {
            if (progress == null || string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            var now = DateTime.UtcNow;
            if (lastReportUtc != DateTime.MinValue &&
                (now - lastReportUtc).TotalMilliseconds < minimumMilliseconds)
            {
                return;
            }

            lastReportUtc = now;
            progress(message);
        }

        private static void ReportDirectoryScanProgress(Action<string> progress, string directory, ref DateTime lastReportUtc)
        {
            ReportProgress(
                progress,
                "Scanning folder: " + ShortenMiddle(directory, 120),
                ref lastReportUtc,
                750);
        }

        private static void ReportScanProgress(Action<string> progress, int visited, int accepted, int cacheHits, int ignored, string currentPath, ref DateTime lastReportUtc)
        {
            var current = string.IsNullOrWhiteSpace(currentPath)
                ? ""
                : " | " + ShortenMiddle(currentPath, 90);

            ReportProgress(
                progress,
                string.Format("Scanning files: {0:N0} checked | {1:N0} accepted | {2:N0} cached | {3:N0} ignored{4}", visited, accepted, cacheHits, ignored, current),
                ref lastReportUtc,
                500);
        }

        private static void ThrowIfCancellationRequested(Func<bool> shouldCancel)
        {
            if (shouldCancel != null && shouldCancel())
            {
                throw new OperationCanceledException();
            }
        }

        private void ExportButton_Click(object sender, EventArgs e)
        {
            if (rows.Count == 0)
            {
                return;
            }

            using (var dialog = new SaveFileDialog())
            {
                dialog.Title = "Export duplicate episode report";
                dialog.Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*";
                dialog.FileName = "same-episode-duplicates.csv";
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    ExportCsv(dialog.FileName);
                    MessageBox.Show(this, "Report exported.", "Export complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
        }

        private void ExportCsv(string path)
        {
            using (var writer = new StreamWriter(path, false, new UTF8Encoding(true)))
            {
                writer.WriteLine("Delete,Episode,FileLocation,EpisodeFile,SubtitleGroup,SizeMB,Version,AniDbId,AniDbTitle,AniDbYear,Key,Title,SizeBytes,Path");
                foreach (var row in GetActiveDataSet())
                {
                    writer.WriteLine(string.Join(",", new[]
                    {
                        Csv(row.Delete ? "TRUE" : "FALSE"),
                        Csv(row.Episode),
                        Csv(row.FileLocation),
                        Csv(row.SimplifiedFileName),
                        Csv(row.SubtitleGroup),
                        Csv(row.SizeMB.ToString()),
                        Csv(row.Version),
                        Csv(row.AniDbId),
                        Csv(row.AniDbTitle),
                        Csv(row.AniDbYear),
                        Csv(row.Key),
                        Csv(row.Title),
                        Csv(row.SizeBytes.ToString()),
                        Csv(row.Path)
                    }));
                }
            }
        }

        private static string GetCachePath()
        {
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SameEpisodeDuplicateFinder.lastscan.duplicates-by-series.csv");
        }

        private static string GetParseCachePath()
        {
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SameEpisodeDuplicateFinder.lastscan.allfiles.csv");
        }

        private static string GetMoveReportPath()
        {
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SameEpisodeDuplicateFinder.last-move-report.csv");
        }

        private static string GetMoveDryRunReportPath()
        {
            return GetTimestampedReportPath("SameEpisodeDuplicateFinder.move-dry-run");
        }

        private static string GetDeleteDryRunReportPath()
        {
            return GetTimestampedReportPath("SameEpisodeDuplicateFinder.delete-dry-run");
        }

        private static string GetTimestampedReportPath(string prefix)
        {
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, prefix + "." + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".csv");
        }

        private static string GetSelectedFeedPath()
        {
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SameEpisodeDuplicateFinder.selected-results.rss");
        }

        private static string GetErrorLogPath()
        {
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SameEpisodeDuplicateFinder.errors.log");
        }

        private static string GetDiagnosticLogPath()
        {
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SameEpisodeDuplicateFinder.diagnostics.log");
        }

        private void SaveSelectedFeed()
        {
            try
            {
                var path = GetSelectedFeedPath();
                using (var writer = XmlWriter.Create(path, new XmlWriterSettings
                {
                    Encoding = new UTF8Encoding(false),
                    Indent = true
                }))
                {
                    writer.WriteStartDocument();
                    writer.WriteStartElement("rss");
                    writer.WriteAttributeString("version", "2.0");
                    writer.WriteStartElement("channel");
                    writer.WriteElementString("title", "Same Episode Duplicate Finder Selected Results");
                    writer.WriteElementString("description", "Search results selected inside Same Episode Duplicate Finder.");
                    writer.WriteElementString("link", string.IsNullOrWhiteSpace(selectedFeedUrl) ? "http://127.0.0.1/" : selectedFeedUrl);
                    writer.WriteElementString("lastBuildDate", DateTime.UtcNow.ToString("r"));

                    foreach (var item in selectedFeedRows)
                    {
                        var link = string.IsNullOrWhiteSpace(item.MagnetLink) ? item.Link : item.MagnetLink;
                        writer.WriteStartElement("item");
                        writer.WriteElementString("title", item.Title ?? "");
                        writer.WriteElementString("description", BuildSelectedFeedDescription(item));
                        writer.WriteElementString("link", link ?? "");
                        writer.WriteElementString("guid", link ?? item.Title ?? "");
                        writer.WriteElementString("pubDate", (item.AddedUtc == DateTime.MinValue ? DateTime.UtcNow : item.AddedUtc).ToString("r"));
                        writer.WriteEndElement();
                    }

                    writer.WriteEndElement();
                    writer.WriteEndElement();
                    writer.WriteEndDocument();
                }
            }
            catch (Exception ex)
            {
                LogException("Save selected RSS feed failed", ex);
            }
        }

        private static string BuildSelectedFeedDescription(SelectedSearchFeedItem item)
        {
            if (item == null)
            {
                return "";
            }

            return string.Join(" | ", new[]
            {
                "Series: " + DisplayOrDash(item.SeriesTitle),
                "Missing: " + DisplayOrDash(item.MissingEpisode),
                "Query: " + DisplayOrDash(item.SearchQuery),
                "Provider: " + DisplayOrDash(item.Provider),
                "Full season: " + (item.IsBatchResult ? "Yes" : "No"),
                "Size: " + DisplayOrDash(item.Size),
                "Seeders: " + item.Seeders.ToString("N0")
            });
        }

        private void UpdateSelectedFeedTotal()
        {
            var location = GetSelectedFeedLocation();
            selectedFeedTotalLabel.Text = string.Format("{0:N0} selected result(s) | {1}", selectedFeedRows.Count, location);
            selectedFeedOpenButton.Enabled = !busyState && File.Exists(GetSelectedFeedPath());
            selectedFeedCopyButton.Enabled = !busyState && !string.IsNullOrWhiteSpace(location);
            selectedFeedRemoveButton.Enabled = !busyState && GetSelectedFeedItem() != null;
            selectedFeedClearButton.Enabled = !busyState && selectedFeedRows.Count > 0;
        }

        private string GetSelectedFeedLocation()
        {
            return string.IsNullOrWhiteSpace(selectedFeedUrl) ? GetSelectedFeedPath() : selectedFeedUrl;
        }

        private void SaveAndRefreshSelectedFeed()
        {
            SaveSelectedFeed();
            UpdateSelectedFeedTotal();
        }

        private void StartSelectedFeedServer()
        {
            for (var port = 8765; port < 8785; port++)
            {
                try
                {
                    selectedFeedServer = new TcpListener(IPAddress.Loopback, port);
                    selectedFeedServer.Start();
                    selectedFeedServerRunning = true;
                    selectedFeedUrl = "http://127.0.0.1:" + port.ToString() + "/selected-feed.rss";
                    selectedFeedServerThread = new Thread(SelectedFeedServerLoop);
                    selectedFeedServerThread.IsBackground = true;
                    selectedFeedServerThread.Start();
                    UpdateSelectedFeedTotal();
                    return;
                }
                catch (SocketException)
                {
                    selectedFeedServer = null;
                }
            }

            selectedFeedUrl = "";
            UpdateActivity("Selected RSS feed URL could not start; the RSS file is still written locally.", true);
            UpdateSelectedFeedTotal();
        }

        private void SelectedFeedServerLoop()
        {
            while (selectedFeedServerRunning)
            {
                try
                {
                    var client = selectedFeedServer.AcceptTcpClient();
                    ThreadPool.QueueUserWorkItem(delegate { ServeSelectedFeedClient(client); });
                }
                catch
                {
                    if (selectedFeedServerRunning)
                    {
                        Thread.Sleep(250);
                    }
                }
            }
        }

        private void ServeSelectedFeedClient(object state)
        {
            using (var client = state as TcpClient)
            {
                if (client == null)
                {
                    return;
                }

                try
                {
                    using (var stream = client.GetStream())
                    using (var reader = new StreamReader(stream, Encoding.ASCII))
                    {
                        var request = reader.ReadLine() ?? "";
                        while (!string.IsNullOrWhiteSpace(reader.ReadLine()))
                        {
                        }

                        var path = GetSelectedFeedPath();
                        var isFeedRequest = request.StartsWith("GET /selected-feed.rss ", StringComparison.OrdinalIgnoreCase) ||
                                            request.StartsWith("GET / ", StringComparison.OrdinalIgnoreCase);
                        if (!isFeedRequest)
                        {
                            WriteHttpResponse(stream, "404 Not Found", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("Not found"));
                            return;
                        }

                        if (!File.Exists(path))
                        {
                            SaveSelectedFeed();
                        }

                        WriteHttpResponse(stream, "200 OK", "application/rss+xml; charset=utf-8", File.ReadAllBytes(path));
                    }
                }
                catch
                {
                }
            }
        }

        private static void WriteHttpResponse(Stream stream, string status, string contentType, byte[] body)
        {
            body = body ?? new byte[0];
            var header = "HTTP/1.1 " + status + "\r\n" +
                         "Content-Type: " + contentType + "\r\n" +
                         "Content-Length: " + body.Length.ToString() + "\r\n" +
                         "Cache-Control: no-cache\r\n" +
                         "Connection: close\r\n\r\n";
            var headerBytes = Encoding.ASCII.GetBytes(header);
            stream.Write(headerBytes, 0, headerBytes.Length);
            stream.Write(body, 0, body.Length);
        }

        private void SaveMoveReport(List<ActionPreviewRow> reportRows)
        {
            SaveActionReport(GetMoveReportPath(), reportRows);

            toolsOpenMoveReportMenuItem.Enabled = File.Exists(GetMoveReportPath());
        }

        internal static void SaveActionReport(string path, IEnumerable<ActionPreviewRow> reportRows)
        {
            using (var writer = new StreamWriter(path, false, new UTF8Encoding(true)))
            {
                WriteActionReport(writer, reportRows);
            }
        }

        internal static void WriteActionReport(TextWriter writer, IEnumerable<ActionPreviewRow> reportRows)
        {
            writer.WriteLine("Action,Status,Reason,OldPath,NewPath");
            foreach (var row in reportRows ?? new List<ActionPreviewRow>())
            {
                writer.WriteLine(string.Join(",", new[]
                {
                    Csv(row.Action),
                    Csv(row.Confidence),
                    Csv(row.Reason),
                    Csv(row.CurrentPath),
                    Csv(row.TargetPath)
                }));
            }
        }

        private void OpenMoveReportMenuItem_Click(object sender, EventArgs e)
        {
            if (!File.Exists(GetMoveReportPath()))
            {
                MessageBox.Show(this, "No move report has been created yet.", "Move Report", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            OpenShellPath(GetMoveReportPath());
        }

        private static string ReadCacheRoot(string cachePath)
        {
            if (!File.Exists(cachePath))
            {
                return null;
            }

            using (var parser = new TextFieldParser(cachePath, Encoding.UTF8))
            {
                parser.TextFieldType = FieldType.Delimited;
                parser.SetDelimiters(",");
                parser.HasFieldsEnclosedInQuotes = true;

                if (parser.EndOfData) return null;
                parser.ReadFields();
                if (parser.EndOfData) return null;
                var meta = parser.ReadFields();
                return meta == null || meta.Length < 1 ? null : meta[0];
            }
        }

        private bool CanLoadCachedScan(string root)
        {
            if (!File.Exists(GetCachePath()))
            {
                return false;
            }

            var cachedRoot = ReadCacheRoot(GetCachePath());
            return !string.IsNullOrWhiteSpace(cachedRoot) &&
                   string.Equals(NormalizeRoot(cachedRoot), NormalizeRoot(root), StringComparison.OrdinalIgnoreCase);
        }

        private void StartLoadCachedScan(string root, bool alreadyConfirmed)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                MessageBox.Show(this, "Choose a folder before loading a saved scan.", "No folder selected", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var cachedRoot = ReadCacheRoot(GetCachePath());
            if (string.IsNullOrWhiteSpace(cachedRoot) ||
                !string.Equals(NormalizeRoot(cachedRoot), NormalizeRoot(root), StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(this, "No saved scan was found for this folder.", "No saved scan", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (!alreadyConfirmed)
            {
                var confirm = MessageBox.Show(
                    this,
                    "Load the saved scan for this folder?",
                    "Load saved scan",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question);
                if (confirm != DialogResult.Yes)
                {
                    return;
                }
            }

            SetBusy(true, "Loading saved scan cache...");
            rows.Clear();
            UpdateCandidateTotal();
            deletionRows.Clear();
            UpdateDeletionTotal();
            allRows.Clear();
            allScannedRows.Clear();
            seriesListView.Items.Clear();
            seriesCoverView.Items.Clear();
            seriesCoverImages.Images.Clear();
            activeSeriesTag = null;
            UpdateDetails((EpisodeFile)null);

            var worker = new BackgroundWorker();
            worker.WorkerReportsProgress = true;
            worker.DoWork += delegate(object workerSender, DoWorkEventArgs args)
            {
                var backgroundWorker = (BackgroundWorker)workerSender;
                Action<string> report = delegate(string message)
                {
                    backgroundWorker.ReportProgress(0, message);
                };

                report("Reading duplicate cache rows...");
                ThrowIfCancellationRequested(delegate { return cancelRequested; });
                string readRoot;
                var cachedRows = ReadCachedRows(out readRoot, report);
                ThrowIfCancellationRequested(delegate { return cancelRequested; });
                if (cachedRows == null ||
                    !string.Equals(NormalizeRoot(readRoot), NormalizeRoot(root), StringComparison.OrdinalIgnoreCase))
                {
                    args.Result = null;
                    return;
                }

                args.Result = new object[] { cachedRows, readRoot };
            };
            worker.ProgressChanged += delegate(object workerSender, ProgressChangedEventArgs args)
            {
                UpdateActivity(args.UserState as string ?? "Loading saved scan cache...", false);
            };
            worker.RunWorkerCompleted += delegate(object workerSender, RunWorkerCompletedEventArgs args)
            {
                try
                {
                    if (args.Error is OperationCanceledException)
                    {
                        UpdateSummary("Saved scan load canceled.");
                        return;
                    }

                    if (args.Error != null)
                    {
                        throw args.Error;
                    }

                    if (args.Result == null)
                    {
                        MessageBox.Show(this, "No saved scan was found for this folder.", "No saved scan", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        UpdateActivity("Saved scan cache did not match the selected folder.", true);
                        return;
                    }

                    var result = (object[])args.Result;
                    var cachedRows = (List<EpisodeFile>)result[0];
                    var readRoot = Convert.ToString(result[1]);
                    LoadRowsIntoUi(cachedRows, cachedRows);
                    UpdateSummary(string.Format("Loaded saved scan: {0:N0} duplicate candidate(s). Loading full scan cache...", cachedRows.Count));
                    UpdateCommandAvailability();
                    BeginInvoke(new Action(delegate
                    {
                        StartLoadFullScanCache(readRoot);
                    }));
                }
                catch (Exception ex)
                {
                    LogException("Load saved scan failed", ex);
                    MessageBox.Show(this, ex.Message, "Load saved scan failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    UpdateActivity("Saved scan cache load failed.", true);
                }
                finally
                {
                    SetBusy(false, statusLabel.Text);
                }
            };
            worker.RunWorkerAsync();
        }

        private void StartLoadFullScanCache(string root)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                return;
            }

            SetBusy(true, "Loading full scanned file cache...");
            var worker = new BackgroundWorker();
            worker.WorkerReportsProgress = true;
            worker.DoWork += delegate(object workerSender, DoWorkEventArgs args)
            {
                var backgroundWorker = (BackgroundWorker)workerSender;
                Action<string> report = delegate(string message)
                {
                    backgroundWorker.ReportProgress(0, message);
                };

                args.Result = ReadAllParsedRows(root, report);
            };
            worker.ProgressChanged += delegate(object workerSender, ProgressChangedEventArgs args)
            {
                UpdateActivity(args.UserState as string ?? "Loading full scanned file cache...", false);
            };
            worker.RunWorkerCompleted += delegate(object workerSender, RunWorkerCompletedEventArgs args)
            {
                try
                {
                    if (args.Error != null)
                    {
                        throw args.Error;
                    }

                    var scannedRows = (List<EpisodeFile>)args.Result;
                    if (scannedRows.Count > 0)
                    {
                        allScannedRows.Clear();
                        allScannedRows.AddRange(MergeScannedRowsWithDuplicates(scannedRows, allRows));
                        PopulateSeriesPanel();
                        PopulateMissingEpisodesPanel();
                        UpdateSummary(string.Format("Full scan cache loaded: {0:N0} scanned file(s).", allScannedRows.Count));
                    }
                    else
                    {
                        UpdateActivity("Full scanned file cache was empty.", true);
                    }
                }
                catch (Exception ex)
                {
                    LogException("Load full scan cache failed", ex);
                    UpdateActivity("Full scanned file cache load failed.", true);
                }
                finally
                {
                    SetBusy(false, statusLabel.Text);
                }
            };
            worker.RunWorkerAsync();
        }

        private void SaveCachedScan(string root, List<EpisodeFile> data)
        {
            using (var writer = new StreamWriter(GetCachePath(), false, new UTF8Encoding(true)))
            {
                writer.WriteLine("CacheRoot,CreatedUtc");
                writer.WriteLine(string.Join(",", new[] { Csv(NormalizeRoot(root)), Csv(DateTime.UtcNow.ToString("o")) }));
                writer.WriteLine("Delete,Episode,FileLocation,OriginalFileName,SubtitleGroup,SizeMB,Version,AniDbId,AniDbTitle,AniDbYear,Key,Title,SizeBytes,Path");
                foreach (var row in data)
                {
                    writer.WriteLine(string.Join(",", new[]
                    {
                        Csv(row.Delete ? "TRUE" : "FALSE"),
                        Csv(row.Episode),
                        Csv(row.FileLocation),
                        Csv(row.FileName),
                        Csv(row.SubtitleGroup),
                        Csv(row.SizeMB.ToString()),
                        Csv(row.Version),
                        Csv(row.AniDbId),
                        Csv(row.AniDbTitle),
                        Csv(row.AniDbYear),
                        Csv(row.Key),
                        Csv(row.Title),
                        Csv(row.SizeBytes.ToString()),
                        Csv(row.Path)
                    }));
                }
            }
        }

        private static void SaveParseCache(string root, List<EpisodeFile> data)
        {
            SaveParseCache(root, data, null);
        }

        private static void SaveParseCache(string root, List<EpisodeFile> data, Action<string> progress)
        {
            SaveParseCache(root, data, progress, null);
        }

        private static void SaveParseCache(string root, List<EpisodeFile> data, Action<string> progress, Func<bool> shouldCancel)
        {
            using (var writer = new StreamWriter(GetParseCachePath(), false, new UTF8Encoding(true)))
            {
                writer.WriteLine("CacheRoot,CreatedUtc");
                writer.WriteLine(string.Join(",", new[] { Csv(NormalizeRoot(root)), Csv(DateTime.UtcNow.ToString("o")) }));
                writer.WriteLine("Path,SizeBytes,LastWriteUtcTicks,Episode,FileLocation,OriginalFileName,SubtitleGroup,SizeMB,Version,AniDbId,AniDbTitle,AniDbYear,Key,Title");
                var progressUtc = DateTime.MinValue;
                var written = 0;
                foreach (var row in data)
                {
                    ThrowIfCancellationRequested(shouldCancel);
                    writer.WriteLine(string.Join(",", new[]
                    {
                        Csv(row.Path),
                        Csv(row.SizeBytes.ToString()),
                        Csv(row.LastWriteUtcTicks.ToString()),
                        Csv(row.Episode),
                        Csv(row.FileLocation),
                        Csv(row.FileName),
                        Csv(row.SubtitleGroup),
                        Csv(row.SizeMB.ToString()),
                        Csv(row.Version),
                        Csv(row.AniDbId),
                        Csv(row.AniDbTitle),
                        Csv(row.AniDbYear),
                        Csv(row.Key),
                        Csv(row.Title)
                    }));
                    written++;
                    if (written % 500 == 0)
                    {
                        ReportProgress(progress, string.Format("Writing scan cache: {0:N0}/{1:N0} files", written, data.Count), ref progressUtc, 500);
                    }
                }
            }
        }

        private static List<EpisodeFile> ReadAllParsedRows(string root)
        {
            return ReadAllParsedRows(root, null);
        }

        private static List<EpisodeFile> ReadAllParsedRows(string root, Action<string> progress)
        {
            string cachedRoot;
            var cache = ReadParseCache(out cachedRoot, progress);
            if (cache.Count == 0 ||
                !string.Equals(NormalizeRoot(cachedRoot), NormalizeRoot(root), StringComparison.OrdinalIgnoreCase))
            {
                ReportProgress(progress, "Full scanned file cache is empty or belongs to another folder.");
                return new List<EpisodeFile>();
            }

            var rows = cache.Values
                        .Where(x => x != null && x.File != null)
                        .Select(x => x.File)
                        .OrderBy(x => x.Title, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(x => x.Episode, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(x => x.FileName, StringComparer.OrdinalIgnoreCase)
                        .ToList();
            ReportProgress(progress, string.Format("Loaded full scanned file cache: {0:N0} files.", rows.Count));
            return rows;
        }

        private static Dictionary<string, CachedParsedFile> ReadParseCache(out string cachedRoot)
        {
            return ReadParseCache(out cachedRoot, null);
        }

        private static Dictionary<string, CachedParsedFile> ReadParseCache(out string cachedRoot, Action<string> progress)
        {
            cachedRoot = null;
            var result = new Dictionary<string, CachedParsedFile>(StringComparer.OrdinalIgnoreCase);
            var cachePath = GetParseCachePath();
            if (!File.Exists(cachePath))
            {
                return result;
            }

            using (var parser = new TextFieldParser(cachePath, Encoding.UTF8))
            {
                parser.TextFieldType = FieldType.Delimited;
                parser.SetDelimiters(",");
                parser.HasFieldsEnclosedInQuotes = true;

                if (parser.EndOfData) return result;
                parser.ReadFields();
                if (parser.EndOfData) return result;
                var meta = parser.ReadFields();
                if (meta == null || meta.Length < 1) return result;
                cachedRoot = meta[0];

                if (parser.EndOfData) return result;
                var dataHeader = parser.ReadFields();
                var headerMap = BuildHeaderMap(dataHeader);

                var progressUtc = DateTime.MinValue;
                var read = 0;
                while (!parser.EndOfData)
                {
                    var fields = parser.ReadFields();
                    if (fields == null)
                    {
                        continue;
                    }

                    long sizeBytes;
                    long lastWriteUtcTicks;
                    decimal sizeMB;
                    var path = ReadField(headerMap, fields, "Path");
                    if (string.IsNullOrWhiteSpace(path))
                    {
                        continue;
                    }

                    long.TryParse(ReadField(headerMap, fields, "SizeBytes", "Size Bytes"), out sizeBytes);
                    long.TryParse(ReadField(headerMap, fields, "LastWriteUtcTicks"), out lastWriteUtcTicks);
                    decimal.TryParse(ReadField(headerMap, fields, "SizeMB", "MB"), out sizeMB);

                    var file = new EpisodeFile
                    {
                        Delete = false,
                        Path = path,
                        SizeBytes = sizeBytes,
                        LastWriteUtcTicks = lastWriteUtcTicks,
                        Episode = ReadField(headerMap, fields, "Episode"),
                        FileLocation = ReadField(headerMap, fields, "FileLocation", "Location"),
                        FileName = ReadField(headerMap, fields, "OriginalFileName", "FileName"),
                        SubtitleGroup = ReadField(headerMap, fields, "SubtitleGroup", "Group"),
                        SizeMB = sizeMB,
                        Version = ReadField(headerMap, fields, "Version"),
                        AniDbId = ReadField(headerMap, fields, "AniDbId", "AniDB ID"),
                        AniDbTitle = ReadField(headerMap, fields, "AniDbTitle", "AniDB Title"),
                        AniDbYear = ReadField(headerMap, fields, "AniDbYear", "AniDB Year"),
                        Key = ReadField(headerMap, fields, "Key", "GroupKey", "Group Key"),
                        Title = ReadField(headerMap, fields, "Title")
                    };

                    result[path] = new CachedParsedFile
                    {
                        SizeBytes = sizeBytes,
                        LastWriteUtcTicks = lastWriteUtcTicks,
                        File = file
                    };
                    read++;
                    if (read % 500 == 0)
                    {
                        ReportProgress(progress, string.Format("Reading full scanned file cache: {0:N0} rows", read), ref progressUtc, 500);
                    }
                }
            }

            return result;
        }

        private static List<EpisodeFile> ReadCachedRows(out string cachedRoot)
        {
            return ReadCachedRows(out cachedRoot, null);
        }

        private static List<EpisodeFile> ReadCachedRows(out string cachedRoot, Action<string> progress)
        {
            cachedRoot = null;
            var cachePath = GetCachePath();
            if (!File.Exists(cachePath))
            {
                return null;
            }

            var result = new List<EpisodeFile>();
            using (var parser = new TextFieldParser(cachePath, Encoding.UTF8))
            {
                parser.TextFieldType = FieldType.Delimited;
                parser.SetDelimiters(",");
                parser.HasFieldsEnclosedInQuotes = true;

                if (parser.EndOfData) return null;
                parser.ReadFields();
                if (parser.EndOfData) return null;
                var meta = parser.ReadFields();
                if (meta == null || meta.Length < 1) return null;
                cachedRoot = meta[0];

                if (parser.EndOfData) return null;
                var dataHeader = parser.ReadFields();
                var headerMap = BuildHeaderMap(dataHeader);

                var progressUtc = DateTime.MinValue;
                var read = 0;
                while (!parser.EndOfData)
                {
                    var fields = parser.ReadFields();
                    if (fields == null || fields.Length < 10)
                    {
                        continue;
                    }

                    long sizeBytes;
                    decimal sizeMB;
                    var path = ReadField(headerMap, fields, "Path");
                    var fileLocation = ReadField(headerMap, fields, "FileLocation", "Location");
                    var fileName = ReadField(headerMap, fields, "OriginalFileName", "FileName");
                    if (!string.IsNullOrWhiteSpace(path) &&
                        (string.IsNullOrWhiteSpace(fileName) || !System.IO.Path.HasExtension(fileName)))
                    {
                        fileName = System.IO.Path.GetFileName(path);
                    }

                    var subtitleGroup = ReadField(headerMap, fields, "SubtitleGroup", "Group");
                    if (string.IsNullOrWhiteSpace(subtitleGroup))
                    {
                        subtitleGroup = EpisodeParser.GetSubtitleGroup(fileName);
                    }

                    var episode = ReadField(headerMap, fields, "Episode");
                    var sizeMBText = ReadField(headerMap, fields, "SizeMB", "MB");
                    var version = ReadField(headerMap, fields, "Version");
                    var aniDbId = ReadField(headerMap, fields, "AniDbId", "AniDB ID");
                    var aniDbTitle = ReadField(headerMap, fields, "AniDbTitle", "AniDB Title");
                    var aniDbYear = ReadField(headerMap, fields, "AniDbYear", "AniDB Year");
                    var key = ReadField(headerMap, fields, "Key", "GroupKey", "Group Key");
                    var title = ReadField(headerMap, fields, "Title");
                    var sizeBytesText = ReadField(headerMap, fields, "SizeBytes", "Size Bytes");

                    long.TryParse(sizeBytesText, out sizeBytes);
                    decimal.TryParse(sizeMBText, out sizeMB);

                    result.Add(new EpisodeFile
                    {
                        Delete = string.Equals(fields[0], "TRUE", StringComparison.OrdinalIgnoreCase),
                        FileLocation = fileLocation,
                        FileName = fileName,
                        SubtitleGroup = subtitleGroup,
                        Episode = episode,
                        SizeMB = sizeMB,
                        Version = version,
                        AniDbId = aniDbId,
                        AniDbTitle = aniDbTitle,
                        AniDbYear = aniDbYear,
                        Key = key,
                        Title = title,
                        SizeBytes = sizeBytes,
                        Path = path
                    });
                    read++;
                    if (read % 500 == 0)
                    {
                        ReportProgress(progress, string.Format("Reading duplicate cache: {0:N0} rows", read), ref progressUtc, 500);
                    }
                }
            }

            return result;
        }

        private static Dictionary<string, int> BuildHeaderMap(string[] header)
        {
            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (header == null)
            {
                return map;
            }

            for (var i = 0; i < header.Length; i++)
            {
                var name = NormalizeHeaderName(header[i]);
                if (!string.IsNullOrWhiteSpace(name) && !map.ContainsKey(name))
                {
                    map.Add(name, i);
                }
            }

            return map;
        }

        private static string ReadField(Dictionary<string, int> headerMap, string[] fields, params string[] names)
        {
            foreach (var name in names)
            {
                int index;
                if (headerMap.TryGetValue(NormalizeHeaderName(name), out index) &&
                    index >= 0 &&
                    index < fields.Length)
                {
                    return fields[index] ?? "";
                }
            }

            return "";
        }

        private static string NormalizeHeaderName(string name)
        {
            return Regex.Replace(name ?? "", @"[\s_]+", "").Trim();
        }

        private static string NormalizeRoot(string root)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                return "";
            }

            try
            {
                return Path.GetFullPath(root).TrimEnd('\\');
            }
            catch
            {
                return root.Trim().TrimEnd('\\');
            }
        }

        private static string Csv(string value)
        {
            if (value == null)
            {
                value = "";
            }

            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        private void DeleteButton_Click(object sender, EventArgs e)
        {
            grid.EndEdit();
            deletionGrid.EndEdit();
            var marked = GetActiveDataSet().Where(r => r.Delete).ToList();
            if (marked.Count == 0)
            {
                MessageBox.Show(this, "No files are marked for deletion.", "Nothing marked", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var previewRows = marked.Select(row => new ActionPreviewRow
            {
                Action = "Delete marked",
                Confidence = DisplayOrDash(row.Confidence),
                Reason = DisplayOrDash(row.RecommendationReason),
                CurrentPath = row.Path,
                TargetPath = "Recycle Bin"
            }).ToList();

            if (!ConfirmBatchPreviewDialog(previewRows, "Move to Recycle Bin"))
            {
                LogActivity("Deletion batch canceled.");
                return;
            }

            var networkDeletes = marked.Where(row => IsNetworkPath(row.Path)).Take(8).ToList();
            if (networkDeletes.Count > 0)
            {
                var message = "One or more selected files are on a network or remote drive. Windows may not be able to use the Recycle Bin there, so deletion can become permanent." +
                              Environment.NewLine + Environment.NewLine +
                              string.Join(Environment.NewLine, networkDeletes.Select(row => ShortenMiddle(row.Path, 120)).ToArray()) +
                              Environment.NewLine + Environment.NewLine +
                              "Continue only if you have reviewed the dry-run list and accept that risk.";
                if (MessageBox.Show(this, message, "Network delete warning", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                {
                    LogActivity("Deletion batch canceled because network paths were selected.");
                    return;
                }
            }

            SaveActionReport(GetDeleteDryRunReportPath(), previewRows);
            LogActivity(string.Format("Deleting {0:N0} marked file(s).", marked.Count));
            var deleted = 0;
            var failures = new List<string>();
            foreach (var row in marked)
            {
                try
                {
                    UpdateActivity("Deleting: " + row.FileName, false);
                    DeleteToRecycleBin(row.Path);
                    deleted++;
                }
                catch (Exception ex)
                {
                    LogException("Delete failed for " + row.FileName, ex);
                    failures.Add(row.FileName + ": " + ex.Message);
                }
            }

            foreach (var row in marked.Where(r => !File.Exists(r.Path)).ToList())
            {
                allRows.Remove(row);
                allScannedRows.RemoveAll(x => ReferenceEquals(x, row) || string.Equals(x.Path, row.Path, StringComparison.OrdinalIgnoreCase));
                rows.Remove(row);
            }

            RefreshDeletionRows();
            PopulateSeriesPanel();
            if (CanWriteSingleRootCache())
            {
                SaveCurrentSessionCache();
            }

            UpdateSummary(string.Format("Moved {0} file(s) to the Recycle Bin.", deleted));
            if (failures.Count > 0)
            {
                MessageBox.Show(this, string.Join("\r\n", failures.Take(12).ToArray()), "Some files could not be deleted", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private static void DeleteToRecycleBin(string path)
        {
            try
            {
                FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            }
            catch (PathTooLongException)
            {
                FileSystem.DeleteFile(LongPath.ToExtendedPath(path), UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            }
        }

        internal static bool IsNetworkPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            if (path.StartsWith(@"\\", StringComparison.Ordinal))
            {
                return true;
            }

            try
            {
                var root = Path.GetPathRoot(path);
                if (string.IsNullOrWhiteSpace(root))
                {
                    return false;
                }

                return new DriveInfo(root).DriveType == DriveType.Network;
            }
            catch
            {
                return false;
            }
        }

        private void ClearMarksButton_Click(object sender, EventArgs e)
        {
            foreach (var row in GetActiveDataSet())
            {
                row.Delete = false;
            }
            RefreshDeletionView();
            UpdateSummary(null);
            UpdateSummary("Cleared delete marks.");
        }

        private void AutoMarkButton_Click(object sender, EventArgs e)
        {
            ApplyBestActionMarks("Auto Mark", "Auto-marked best actions");
        }

        private void AutoMarkThresholdMenuItem_Click(object sender, EventArgs e)
        {
            var item = sender as ToolStripMenuItem;
            if (item == null || !(item.Tag is AutoMarkThreshold))
            {
                return;
            }

            autoMarkThreshold = (AutoMarkThreshold)item.Tag;
            try
            {
                AutoMarkThresholdStore.Save(autoMarkThreshold);
            }
            catch (Exception ex)
            {
                LogException("Auto mark threshold save failed", ex);
            }

            UpdateAutoMarkThresholdUi();
            RefreshVisibleRows();
            UpdateSummary("Auto Mark Level set to " + GetAutoMarkThresholdLabel(autoMarkThreshold) + ".");
        }

        private void ApplyBestActionMarks(string title, string summaryPrefix)
        {
            if (!GetActiveDataSet().Any())
            {
                MessageBox.Show(this, "No duplicate rows are loaded.", title, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var marked = ComputeReviewRecommendations(true);
            RefreshReviewGrids();
            UpdateSummary(string.Format("{0}. Marked {1:N0} duplicate file(s) using {2} confidence.", summaryPrefix, marked, GetAutoMarkThresholdLabel(autoMarkThreshold)));
        }

        private void UpdateAutoMarkThresholdUi()
        {
            if (toolsAutoMarkHighMenuItem == null)
            {
                return;
            }

            toolsAutoMarkHighMenuItem.Checked = autoMarkThreshold == AutoMarkThreshold.High;
            toolsAutoMarkMediumMenuItem.Checked = autoMarkThreshold == AutoMarkThreshold.Medium;
            toolsAutoMarkLowMenuItem.Checked = autoMarkThreshold == AutoMarkThreshold.Low;
            toolsAutoMarkMenuItem.Text = "Auto Mark (" + GetAutoMarkThresholdLabel(autoMarkThreshold) + ")";
            toolsAutoMarkLevelMenuItem.Text = "Auto Mark Level: " + GetAutoMarkThresholdLabel(autoMarkThreshold);
        }

        private static string GetAutoMarkThresholdLabel(AutoMarkThreshold threshold)
        {
            if (threshold == AutoMarkThreshold.Low)
            {
                return "Low+";
            }

            if (threshold == AutoMarkThreshold.Medium)
            {
                return "Medium+";
            }

            return "High";
        }

        private int ComputeReviewRecommendations(bool applyDeleteMarks)
        {
            foreach (var row in allRows)
            {
                row.Recommendation = "";
                row.Confidence = "";
                row.ReviewStatus = "";
                row.RecommendationReason = "";
                row.ArtworkStatus = "";
                if (applyDeleteMarks)
                {
                    row.Delete = false;
                }
            }

            var marked = 0;
            foreach (var seriesGroup in allRows.GroupBy(x => x.Title, StringComparer.OrdinalIgnoreCase))
            {
                var seriesFiles = GetSeriesSourceRows().Where(x => string.Equals(x.Title, seriesGroup.Key, StringComparison.OrdinalIgnoreCase)).ToList();
                var hasCover = !string.IsNullOrWhiteSpace(FindSeriesCoverPath(seriesFiles.Count > 0 ? seriesFiles : seriesGroup.ToList()));
                foreach (var row in seriesGroup)
                {
                    row.ArtworkStatus = hasCover ? "Cover OK" : "Missing cover";
                }
            }

            foreach (var group in allRows.GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            {
                var ranked = group.OrderByDescending(RecommendationScorer.GetAutoKeepScore)
                                  .ThenByDescending(x => x.SizeBytes)
                                  .ThenBy(x => x.FileName, StringComparer.OrdinalIgnoreCase)
                                  .ToList();
                var keep = ranked.First();
                var keepScore = RecommendationScorer.GetAutoKeepScore(keep);

                keep.Recommendation = "Keep";
                keep.Confidence = "High";
                keep.ReviewStatus = "Best keep";
                keep.RecommendationReason = "Highest quality score in this duplicate group.";

                foreach (var row in ranked.Skip(1))
                {
                    var score = RecommendationScorer.GetAutoKeepScore(row);
                    var gap = keepScore - score;
                    row.Recommendation = "Delete";
                    row.Confidence = RecommendationScorer.GetDeleteConfidence(gap);
                    row.ReviewStatus = string.Equals(row.Confidence, "High", StringComparison.OrdinalIgnoreCase) ? "Likely duplicate" : "Needs review";
                    row.RecommendationReason = RecommendationScorer.BuildRecommendationReason(row, keep);
                    if (applyDeleteMarks && RecommendationScorer.IsAutoMarkCandidate(row, autoMarkThreshold))
                    {
                        row.Delete = true;
                        marked++;
                    }
                }
            }

            foreach (var row in allRows.Where(x => string.IsNullOrWhiteSpace(x.Recommendation)))
            {
                row.Recommendation = "Review";
                row.Confidence = "Low";
                row.ReviewStatus = "Needs review";
                row.RecommendationReason = "No duplicate peer was found in the current grouping.";
            }

            return marked;
        }

        private IEnumerable<EpisodeFile> GetActiveDataSet()
        {
            return allRows.Count > 0 ? allRows : rows.Cast<EpisodeFile>();
        }

        private void Grid_CurrentCellDirtyStateChanged(object sender, EventArgs e)
        {
            var targetGrid = sender as DataGridView;
            if (targetGrid != null && targetGrid.IsCurrentCellDirty)
            {
                targetGrid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            }
        }

        private void Grid_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode != Keys.Enter)
            {
                return;
            }

            ToggleDeleteForSelectedRows(sender as DataGridView);
            e.Handled = true;
            e.SuppressKeyPress = true;
        }

        private void ToggleDeleteForSelectedRows(DataGridView targetGrid)
        {
            if (targetGrid == null)
            {
                targetGrid = grid;
            }

            var targetRows = targetGrid == deletionGrid ? deletionRows : rows;
            var indexes = new SortedSet<int>();
            foreach (DataGridViewRow selectedRow in targetGrid.SelectedRows)
            {
                if (!selectedRow.IsNewRow && selectedRow.Index >= 0 && selectedRow.Index < targetRows.Count)
                {
                    indexes.Add(selectedRow.Index);
                }
            }

            if (indexes.Count == 0 && targetGrid.CurrentRow != null && targetGrid.CurrentRow.Index >= 0 && targetGrid.CurrentRow.Index < targetRows.Count)
            {
                indexes.Add(targetGrid.CurrentRow.Index);
            }

            foreach (var index in indexes)
            {
                targetRows[index].Delete = !targetRows[index].Delete;
            }

            RefreshDeletionView();
        }

        private void Grid_CellValueChanged(object sender, DataGridViewCellEventArgs e)
        {
            var targetGrid = sender as DataGridView;
            if (targetGrid == null || e.RowIndex < 0 || e.ColumnIndex < 0)
            {
                return;
            }

            if (targetGrid.Columns[e.ColumnIndex].DataPropertyName == "Delete")
            {
                BeginInvoke(new Action(delegate
                {
                    RefreshDeletionView();
                    UpdateSummary(null);
                }));
                return;
            }

            targetGrid.InvalidateRow(e.RowIndex);
        }

        private void Grid_CellFormatting(object sender, DataGridViewCellFormattingEventArgs e)
        {
            var targetRows = sender == deletionGrid ? deletionRows : rows;
            if (e.RowIndex < 0 || e.RowIndex >= targetRows.Count)
            {
                return;
            }

            var row = targetRows[e.RowIndex];
            if (row.Delete)
            {
                e.CellStyle.BackColor = DeleteMarkColor;
                e.CellStyle.ForeColor = PrimaryTextColor;
            }
            else if (string.Equals(row.ReviewStatus, "Best keep", StringComparison.OrdinalIgnoreCase))
            {
                e.CellStyle.BackColor = darkMode ? Color.FromArgb(29, 63, 47) : Color.FromArgb(218, 245, 230);
                e.CellStyle.ForeColor = PrimaryTextColor;
            }
            else if (string.Equals(row.ReviewStatus, "Needs review", StringComparison.OrdinalIgnoreCase))
            {
                e.CellStyle.BackColor = darkMode ? Color.FromArgb(75, 61, 32) : Color.FromArgb(255, 244, 210);
                e.CellStyle.ForeColor = PrimaryTextColor;
            }
            else
            {
                e.CellStyle.BackColor = e.RowIndex % 2 == 0 ? PanelBackColor : AlternateRowColor;
                e.CellStyle.ForeColor = PrimaryTextColor;
            }

            var targetGrid = sender as DataGridView;
            if (targetGrid != null && e.ColumnIndex >= 0 && e.ColumnIndex < targetGrid.Columns.Count)
            {
                ApplyStatusCellStyle(e.CellStyle, targetGrid.Columns[e.ColumnIndex].DataPropertyName, row);
            }
        }

        private void ApplyStatusCellStyle(DataGridViewCellStyle style, string propertyName, EpisodeFile row)
        {
            if (style == null || string.IsNullOrWhiteSpace(propertyName) || row == null)
            {
                return;
            }

            if (string.Equals(propertyName, "Confidence", StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(row.Confidence, "High", StringComparison.OrdinalIgnoreCase))
                {
                    style.BackColor = darkMode ? Color.FromArgb(27, 67, 50) : Color.FromArgb(220, 252, 231);
                    style.ForeColor = darkMode ? Color.FromArgb(187, 247, 208) : Color.FromArgb(22, 101, 52);
                }
                else if (string.Equals(row.Confidence, "Medium", StringComparison.OrdinalIgnoreCase))
                {
                    style.BackColor = darkMode ? Color.FromArgb(75, 61, 32) : Color.FromArgb(254, 249, 195);
                    style.ForeColor = darkMode ? Color.FromArgb(253, 230, 138) : Color.FromArgb(133, 77, 14);
                }
                else if (string.Equals(row.Confidence, "Low", StringComparison.OrdinalIgnoreCase))
                {
                    style.BackColor = darkMode ? Color.FromArgb(64, 55, 43) : Color.FromArgb(255, 237, 213);
                    style.ForeColor = darkMode ? Color.FromArgb(253, 186, 116) : Color.FromArgb(154, 52, 18);
                }
            }
            else if (string.Equals(propertyName, "Recommendation", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(propertyName, "ReviewStatus", StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(row.Recommendation, "Delete", StringComparison.OrdinalIgnoreCase))
                {
                    style.BackColor = DeleteMarkColor;
                    style.ForeColor = DeleteButtonTextColor;
                }
                else if (string.Equals(row.ReviewStatus, "Best keep", StringComparison.OrdinalIgnoreCase))
                {
                    style.BackColor = darkMode ? Color.FromArgb(29, 63, 47) : Color.FromArgb(218, 245, 230);
                    style.ForeColor = darkMode ? Color.FromArgb(187, 247, 208) : Color.FromArgb(22, 101, 52);
                }
            }
            else if (string.Equals(propertyName, "ArtworkStatus", StringComparison.OrdinalIgnoreCase) &&
                     string.Equals(row.ArtworkStatus, "Missing cover", StringComparison.OrdinalIgnoreCase))
            {
                style.BackColor = darkMode ? Color.FromArgb(77, 51, 31) : Color.FromArgb(255, 237, 213);
                style.ForeColor = darkMode ? Color.FromArgb(253, 186, 116) : Color.FromArgb(154, 52, 18);
            }
        }

        private void SetBusy(bool busy, string text)
        {
            if (busy && !busyState)
            {
                cancelRequested = false;
                if (busyNoticeCancelButton != null)
                {
                    busyNoticeCancelButton.Text = "Stop Current Task";
                }
                LogActivity("Started: " + text);
            }
            else if (!busy && busyState)
            {
                LogActivity("Finished: " + text);
            }
            busyState = busy;

            UpdateCommandAvailability();
            progressBar.Style = busy ? ProgressBarStyle.Marquee : ProgressBarStyle.Blocks;
            progressBar.Visible = busy;
            UpdateBusyNotice(busy, text);
            UpdateActivity(text, false);
        }

        private void UpdateCommandAvailability()
        {
            var busy = busyState;
            var hasCandidateData = allRows.Count > 0 || rows.Count > 0 || deletionRows.Count > 0;
            var hasScannedData = hasCandidateData || allScannedRows.Count > 0;
            rootBox.Enabled = !busy;
            searchBox.Enabled = !busy || rows.Count > 0;
            fileBrowseMenuItem.Enabled = !busy;
            topScanButton.Enabled = !busy;
            viewColumnsMenuItem.Enabled = !busy;
            fileLoadSavedMenuItem.Enabled = !busy && File.Exists(GetCachePath());
            fileExportMenuItem.Enabled = !busy && hasCandidateData;
            deleteButton.Enabled = !busy && hasCandidateData;
            toolsClearMarksMenuItem.Enabled = !busy && hasCandidateData;
            viewCandidatesMenuItem.Enabled = true;
            viewReadyMenuItem.Enabled = !candidatesPanelCollapsed;
            viewMissingEpisodesMenuItem.Enabled = true;
            viewEpisodeSearchMenuItem.Enabled = true;
            viewSelectedFeedMenuItem.Enabled = true;
            viewRestoreWorkspaceMenuItem.Enabled = candidatesPanelCollapsed || deletionPanelCollapsed || missingEpisodesPanelCollapsed || episodeSearchPanelCollapsed || selectedFeedPanelCollapsed;
            episodeSearchButton.Enabled = !busy && GetSelectedMissingEpisodeRow() != null;
            episodeSearchAllButton.Enabled = !busy && GetSelectedMissingEpisodeRow() != null;
            episodeSearchAddButton.Enabled = !busy && GetSelectedEpisodeSearchResult() != null;
            selectedFeedOpenButton.Enabled = !busy && File.Exists(GetSelectedFeedPath());
            selectedFeedCopyButton.Enabled = !busy && !string.IsNullOrWhiteSpace(GetSelectedFeedLocation());
            selectedFeedRemoveButton.Enabled = !busy && GetSelectedFeedItem() != null;
            selectedFeedClearButton.Enabled = !busy && selectedFeedRows.Count > 0;
            UpdateWorkspaceMenuState();
            toolsAutoMarkMenuItem.Enabled = !busy && hasCandidateData;
            toolsAutoMarkLevelMenuItem.Enabled = !busy;
            toolsMoveToNameFoldersMenuItem.Enabled = !busy && hasScannedData;
            toolsFileBotMenuItem.Enabled = !busy && hasScannedData;
            toolsMonitorFoldersMenuItem.Enabled = !busy || toolsMonitorFoldersMenuItem.Checked;
            toolsPreviewActionsMenuItem.Enabled = !busy && hasCandidateData;
            toolsOpenMoveReportMenuItem.Enabled = !busy && File.Exists(GetMoveReportPath());
            UpdateAniDbButtonState(busy);
        }

        private void UpdateAniDbButtonState(bool busy)
        {
            toolsAniDbMenuItem.Text = "Update Metadata && Covers...";
            toolsAniDbMenuItem.Enabled = !busy && (allRows.Count > 0 || rows.Count > 0 || deletionRows.Count > 0 || allScannedRows.Count > 0);
            UpdateDashboard();
        }
    }

    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs args)
            {
                MainForm.LogUnhandledException("UI thread exception", args.Exception);
                MessageBox.Show(args.Exception.Message, "Unexpected error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            };
            AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs args)
            {
                MainForm.LogUnhandledException("Unhandled exception", args.ExceptionObject as Exception);
            };
            Application.Run(new MainForm());
        }
    }
}
