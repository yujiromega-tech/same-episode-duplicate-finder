using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
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
            ServicePointManager.SecurityProtocol = ServicePointManager.SecurityProtocol | (SecurityProtocolType)3072;
        }
    }

    internal static class AniDbTitleIndex
    {
        private static readonly string[] TitleUrls =
        {
            "https://anidb.net/api/anime-titles.xml.gz",
            "http://anidb.net/api/anime-titles.xml.gz"
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

            foreach (Match animeMatch in Regex.Matches(xml, @"<anime\s+aid=""(?<aid>\d+)""[^>]*>(?<body>.*?)</anime>", RegexOptions.Singleline | RegexOptions.IgnoreCase))
            {
                var aid = animeMatch.Groups["aid"].Value;
                foreach (Match titleMatch in Regex.Matches(animeMatch.Groups["body"].Value, @"<title(?<attrs>[^>]*)>(?<title>.*?)</title>", RegexOptions.Singleline | RegexOptions.IgnoreCase))
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
                    using (var webClient = new WebClient())
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

            throw new InvalidOperationException("AniDB title index could not be downloaded. " + (lastError == null ? "" : lastError.Message), lastError);
        }

        private static string ExtractXmlAttribute(string attributes, string name)
        {
            var match = Regex.Match(attributes ?? "", @"\b" + Regex.Escape(name) + @"\s*=\s*[""'](?<value>[^""']+)[""']", RegexOptions.IgnoreCase);
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
            var rootFull = Path.GetFullPath(root).TrimEnd('\\');
            var pending = new Stack<string>();
            pending.Push(rootFull);

            while (pending.Count > 0)
            {
                var directory = pending.Pop();
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
            using (var webClient = new WebClient())
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

    internal sealed class MainForm : Form
    {
        private const string AllSeriesTag = "__ALL_SERIES__";

        private readonly Label rootBox;
        private readonly TextBox searchBox;
        private readonly Button deleteButton;
        private readonly ToolTip toolTip;
        private readonly MenuStrip mainMenu;
        private readonly ToolStripMenuItem fileBrowseMenuItem;
        private readonly ToolStripMenuItem fileAddScanLocationMenuItem;
        private readonly ToolStripMenuItem fileLoadSavedMenuItem;
        private readonly ToolStripMenuItem fileExportMenuItem;
        private readonly ToolStripMenuItem viewColumnsMenuItem;
        private readonly ToolStripMenuItem viewCandidatesMenuItem;
        private readonly ToolStripMenuItem viewReadyMenuItem;
        private readonly ToolStripMenuItem viewMissingEpisodesMenuItem;
        private readonly ToolStripMenuItem viewEpisodeSearchMenuItem;
        private readonly ToolStripMenuItem viewRestoreWorkspaceMenuItem;
        private readonly ToolStripMenuItem viewSeriesCoversMenuItem;
        private readonly ToolStripMenuItem viewDarkModeMenuItem;
        private readonly ToolStripMenuItem toolsClearMarksMenuItem;
        private readonly ToolStripMenuItem toolsAniDbMenuItem;
        private readonly ToolStripMenuItem toolsAniDbCoversMenuItem;
        private readonly ToolStripMenuItem toolsSuggestActionsMenuItem;
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
        private readonly ToolStripMenuItem helpGuideMenuItem;
        private readonly ToolStripMenuItem helpCredentialMenuItem;
        private readonly Label statusLabel;
        private readonly TextBox activityLogBox;
        private readonly ProgressBar progressBar;
        private readonly Button cancelButton;
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
        private readonly ComboBox episodeSearchGroupBox;
        private readonly ComboBox episodeSearchResolutionBox;
        private readonly Button episodeSearchButton;
        private readonly LinkLabel detailsBox;
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
        private readonly TableLayoutPanel workspacePanel;
        private readonly GroupBox seriesGroup;
        private readonly GroupBox activityGroup;
        private readonly GroupBox detailsGroup;
        private readonly GroupBox candidatesGroup;
        private readonly GroupBox deletionGroup;
        private readonly GroupBox missingEpisodesGroup;
        private readonly GroupBox episodeSearchGroup;
        private readonly Button candidatesCloseButton;
        private readonly Button deletionCloseButton;
        private readonly Button missingEpisodesCloseButton;
        private readonly Button episodeSearchCloseButton;
        private readonly List<EpisodeFile> allRows;
        private readonly List<EpisodeFile> allScannedRows;
        private readonly BindingList<EpisodeFile> rows;
        private readonly BindingList<EpisodeFile> deletionRows;
        private readonly BindingList<MissingEpisodeRow> missingEpisodeRows;
        private readonly BindingList<EpisodeSearchResult> episodeSearchRows;
        private readonly BindingSource source;
        private readonly BindingSource deletionSource;
        private readonly BindingSource missingEpisodesSource;
        private readonly BindingSource episodeSearchSource;
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
        private bool restoringColumnLayout;

        public MainForm()
        {
            Text = "Same Episode Duplicate Finder";
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(1100, 700);
            Size = Screen.PrimaryScreen.WorkingArea.Size;
            WindowState = FormWindowState.Maximized;
            Font = new Font("Segoe UI", 9F);
            BackColor = AppBackColor;

            allRows = new List<EpisodeFile>();
            allScannedRows = new List<EpisodeFile>();
            rows = new BindingList<EpisodeFile>();
            deletionRows = new BindingList<EpisodeFile>();
            missingEpisodeRows = new BindingList<MissingEpisodeRow>();
            episodeSearchRows = new BindingList<EpisodeSearchResult>();
            source = new BindingSource();
            source.DataSource = rows;
            deletionSource = new BindingSource();
            deletionSource.DataSource = deletionRows;
            missingEpisodesSource = new BindingSource();
            missingEpisodesSource.DataSource = missingEpisodeRows;
            episodeSearchSource = new BindingSource();
            episodeSearchSource.DataSource = episodeSearchRows;
            fileFormatFilter = FileFormatFilterStore.Load();
            autoMarkThreshold = AutoMarkThresholdStore.Load();
            activeReviewFilter = "All";
            activeSearchText = "";
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
            fileBrowseMenuItem = new ToolStripMenuItem("Browse and Scan...");
            fileBrowseMenuItem.ToolTipText = "Choose a folder and immediately scan it for duplicate episode candidates.";
            fileBrowseMenuItem.Click += BrowseButton_Click;
            fileAddScanLocationMenuItem = new ToolStripMenuItem("Add Scan Location...");
            fileAddScanLocationMenuItem.ToolTipText = "Scan another folder or drive and merge it into the current session.";
            fileAddScanLocationMenuItem.Click += AddScanLocationMenuItem_Click;
            fileLoadSavedMenuItem = new ToolStripMenuItem("Open Last Scan");
            fileLoadSavedMenuItem.ToolTipText = "Open the cached results from the last scanned folder.";
            fileLoadSavedMenuItem.Enabled = File.Exists(GetCachePath());
            fileLoadSavedMenuItem.Click += LoadSavedButton_Click;
            fileExportMenuItem = new ToolStripMenuItem("Export CSV...");
            fileExportMenuItem.ToolTipText = "Export the current candidate list to a CSV file.";
            fileExportMenuItem.Enabled = false;
            fileExportMenuItem.Click += ExportButton_Click;
            fileMenu.DropDownItems.Add(fileBrowseMenuItem);
            fileMenu.DropDownItems.Add(fileAddScanLocationMenuItem);
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
            viewReadyMenuItem = new ToolStripMenuItem("Hide Ready");
            viewReadyMenuItem.ToolTipText = "Show or hide the Deletion Ready panel.";
            viewReadyMenuItem.Click += ToggleReadyButton_Click;
            viewMissingEpisodesMenuItem = new ToolStripMenuItem("Hide Missing Episodes");
            viewMissingEpisodesMenuItem.ToolTipText = "Show or hide the Missing Episodes panel.";
            viewMissingEpisodesMenuItem.Click += ToggleMissingEpisodesButton_Click;
            viewEpisodeSearchMenuItem = new ToolStripMenuItem("Hide Episode Search");
            viewEpisodeSearchMenuItem.ToolTipText = "Show or hide the Episode Search panel.";
            viewEpisodeSearchMenuItem.Click += ToggleEpisodeSearchButton_Click;
            viewRestoreWorkspaceMenuItem = new ToolStripMenuItem("Restore Workspace");
            viewRestoreWorkspaceMenuItem.ToolTipText = "Show the default review panels again.";
            viewRestoreWorkspaceMenuItem.Click += RestoreWorkspaceMenuItem_Click;
            viewSeriesCoversMenuItem = new ToolStripMenuItem("Series Covers");
            viewSeriesCoversMenuItem.ToolTipText = "Switch the series panel between text names and cover-art tiles.";
            viewSeriesCoversMenuItem.CheckOnClick = true;
            viewSeriesCoversMenuItem.Click += ToggleSeriesCoversMenuItem_Click;
            viewDarkModeMenuItem = new ToolStripMenuItem("Dark Mode");
            viewDarkModeMenuItem.ToolTipText = "Toggle the application between dark and light mode.";
            viewDarkModeMenuItem.CheckOnClick = true;
            viewDarkModeMenuItem.Click += ToggleDarkModeMenuItem_Click;
            viewMenu.DropDownItems.Add(viewColumnsMenuItem);
            viewMenu.DropDownItems.Add(viewCandidatesMenuItem);
            viewMenu.DropDownItems.Add(viewReadyMenuItem);
            viewMenu.DropDownItems.Add(viewMissingEpisodesMenuItem);
            viewMenu.DropDownItems.Add(viewEpisodeSearchMenuItem);
            viewMenu.DropDownItems.Add(viewRestoreWorkspaceMenuItem);
            viewMenu.DropDownItems.Add(viewSeriesCoversMenuItem);
            viewMenu.DropDownItems.Add(new ToolStripSeparator());
            viewMenu.DropDownItems.Add(viewDarkModeMenuItem);
            
            var toolsMenu = new ToolStripMenuItem("Tools");
            toolsClearMarksMenuItem = new ToolStripMenuItem("Clear Marks");
            toolsClearMarksMenuItem.ToolTipText = "Remove all current deletion marks without changing files on disk.";
            toolsClearMarksMenuItem.Enabled = false;
            toolsClearMarksMenuItem.Click += ClearMarksButton_Click;
            toolsAniDbMenuItem = new ToolStripMenuItem("Metadata Lookup");
            toolsAniDbMenuItem.ToolTipText = "Use AniDB HTTP XML/title cache first, then TVDB and TMDB fallback where configured. No AniDB login is required.";
            toolsAniDbMenuItem.Click += AniDbButton_Click;
            toolsAniDbCoversMenuItem = new ToolStripMenuItem("Fetch Missing Covers...");
            toolsAniDbCoversMenuItem.ToolTipText = "Find missing cover art through AniDB, with TVDB and TMDB used as backup providers.";
            toolsAniDbCoversMenuItem.Enabled = false;
            toolsAniDbCoversMenuItem.Click += AniDbMissingCoversMenuItem_Click;
            toolsSuggestActionsMenuItem = new ToolStripMenuItem("Suggest Best Actions");
            toolsSuggestActionsMenuItem.ToolTipText = "Analyze duplicate groups and add recommendation grades and reasons.";
            toolsSuggestActionsMenuItem.Enabled = false;
            toolsSuggestActionsMenuItem.Click += SuggestBestActionsMenuItem_Click;
            toolsPreviewActionsMenuItem = new ToolStripMenuItem("Review Suggested Actions...");
            toolsPreviewActionsMenuItem.ToolTipText = "Preview recommended actions before you mark or move anything.";
            toolsPreviewActionsMenuItem.Enabled = false;
            toolsPreviewActionsMenuItem.Click += PreviewBatchActionsMenuItem_Click;
            toolsAutoMarkMenuItem = new ToolStripMenuItem("Auto Mark");
            toolsAutoMarkMenuItem.ToolTipText = "Mark the app's recommended delete candidates for later review.";
            toolsAutoMarkMenuItem.Enabled = false;
            toolsAutoMarkMenuItem.Click += AutoMarkButton_Click;
            toolsAutoMarkLevelMenuItem = new ToolStripMenuItem("Auto Mark Level");
            toolsAutoMarkLevelMenuItem.ToolTipText = "Choose how aggressive Auto Mark and Suggest Best Actions are.";
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
            toolsMenu.DropDownItems.Add(toolsClearMarksMenuItem);
            toolsMenu.DropDownItems.Add(toolsAniDbMenuItem);
            toolsMenu.DropDownItems.Add(toolsAniDbCoversMenuItem);
            toolsMenu.DropDownItems.Add(new ToolStripSeparator());
            toolsMenu.DropDownItems.Add(toolsSuggestActionsMenuItem);
            toolsMenu.DropDownItems.Add(toolsPreviewActionsMenuItem);
            toolsMenu.DropDownItems.Add(new ToolStripSeparator());
            toolsMenu.DropDownItems.Add(toolsAutoMarkMenuItem);
            toolsMenu.DropDownItems.Add(toolsAutoMarkLevelMenuItem);
            toolsMenu.DropDownItems.Add(toolsMoveToNameFoldersMenuItem);
            toolsMenu.DropDownItems.Add(toolsFileBotMenuItem);
            toolsMenu.DropDownItems.Add(new ToolStripSeparator());
            toolsMenu.DropDownItems.Add(toolsFileFormatsMenuItem);
            toolsMenu.DropDownItems.Add(toolsOpenMoveReportMenuItem);
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
            topPanel.Height = 132;
            topPanel.Padding = new Padding(10, 8, 10, 6);
            topPanel.BackColor = PanelBackColor;
            topPanel.ColumnCount = 1;
            topPanel.RowCount = 3;
            topPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            topPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            topPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 80));
            topPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 6));

            var rootLabel = new Label();
            rootLabel.Text = "Folder";
            rootLabel.TextAlign = ContentAlignment.MiddleLeft;
            rootLabel.Dock = DockStyle.Fill;

            rootBox = new Label();
            rootBox.Text = "No folder scanned";
            rootBox.Dock = DockStyle.Fill;
            rootBox.AutoEllipsis = true;
            rootBox.TextAlign = ContentAlignment.MiddleLeft;
            rootBox.BorderStyle = BorderStyle.None;
            rootBox.Padding = new Padding(6, 0, 6, 0);
            rootBox.Margin = new Padding(0, 4, 8, 4);
            rootBox.TextChanged += RootBox_TextChanged;
            toolTip.SetToolTip(rootBox, "Last scanned folder. Use File > Browse and Scan to choose a different folder.");
            rootBox.Tag = "";

            var searchLabel = new Label();
            searchLabel.Text = "Search";
            searchLabel.TextAlign = ContentAlignment.MiddleLeft;
            searchLabel.Dock = DockStyle.Fill;

            searchBox = new TextBox();
            searchBox.Dock = DockStyle.Fill;
            searchBox.BorderStyle = BorderStyle.FixedSingle;
            searchBox.Margin = new Padding(0, 5, 8, 5);
            searchBox.TextChanged += SearchBox_TextChanged;
            toolTip.SetToolTip(searchBox, "Filter visible rows by series name.");

            cancelButton = new Button();
            cancelButton.Text = "Cancel";
            cancelButton.Dock = DockStyle.Fill;
            cancelButton.Enabled = false;
            cancelButton.Click += CancelButton_Click;
            StyleButton(cancelButton, false);
            toolTip.SetToolTip(cancelButton, "Request cancellation for the current scan or long-running operation.");

            deleteButton = new Button();
            deleteButton.Text = "Delete";
            deleteButton.Dock = DockStyle.Fill;
            deleteButton.MinimumSize = new Size(0, 28);
            deleteButton.Enabled = false;
            deleteButton.Click += DeleteButton_Click;
            StyleDeleteButton(deleteButton);
            toolTip.SetToolTip(deleteButton, "Move Deletion Ready files to the Recycle Bin.");

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
            dashboardInputs.ColumnCount = 5;
            dashboardInputs.RowCount = 1;
            dashboardInputs.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 48));
            dashboardInputs.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58F));
            dashboardInputs.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 54));
            dashboardInputs.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42F));
            dashboardInputs.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 86));
            dashboardInputs.Controls.Add(rootLabel, 0, 0);
            dashboardInputs.Controls.Add(rootBox, 1, 0);
            dashboardInputs.Controls.Add(searchLabel, 2, 0);
            dashboardInputs.Controls.Add(searchBox, 3, 0);
            dashboardInputs.Controls.Add(cancelButton, 4, 0);

            var dashboardChips = new TableLayoutPanel();
            dashboardChips.Dock = DockStyle.Fill;
            dashboardChips.ColumnCount = 5;
            dashboardChips.RowCount = 2;
            dashboardChips.Margin = new Padding(0, 4, 0, 0);
            for (var i = 0; i < dashboardChips.ColumnCount; i++)
            {
                dashboardChips.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20F));
            }
            dashboardChips.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));
            dashboardChips.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));
            dashboardChips.Controls.Add(statusLabel, 0, 0);
            dashboardChips.SetColumnSpan(statusLabel, 2);
            dashboardChips.Controls.Add(scannedChipLabel, 2, 0);
            dashboardChips.Controls.Add(candidatesChipLabel, 3, 0);
            dashboardChips.Controls.Add(visibleChipLabel, 4, 0);
            dashboardChips.Controls.Add(deletionChipLabel, 0, 1);
            dashboardChips.Controls.Add(duplicateChipLabel, 1, 1);
            dashboardChips.Controls.Add(locationChipLabel, 2, 1);
            dashboardChips.Controls.Add(filterChipLabel, 3, 1);
            dashboardChips.Controls.Add(cacheChipLabel, 4, 1);

            topPanel.Controls.Add(dashboardInputs, 0, 0);
            topPanel.Controls.Add(dashboardChips, 0, 1);
            topPanel.Controls.Add(progressBar, 0, 2);

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
            AddReviewTab("Delete Recommendations", "Delete");
            AddReviewTab("Auto High", "AutoHigh");
            AddReviewTab("Auto Medium", "AutoMedium");
            AddReviewTab("Auto Low", "AutoLow");
            AddReviewTab("Needs Review", "NeedsReview");
            AddReviewTab("Missing Cover", "MissingCover");
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
            candidateTotalLabel.Text = "Grand total: 0 file(s) | 0 B";

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
            deletionTotalLabel.Text = "Grand total: 0 file(s) | 0 B";

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
            AddMissingEpisodeColumn("MissingEpisodes", "Missing", 220);
            AddMissingEpisodeColumn("PresentRange", "Present Range", 110);
            AddMissingEpisodeColumn("KnownEpisodes", "Known", 70);
            AddMissingEpisodeColumn("MissingCount", "Missing Count", 92);
            AddMissingEpisodeColumn("LocationCount", "Locations", 78);

            missingEpisodesTotalLabel = new Label();
            missingEpisodesTotalLabel.Dock = DockStyle.Fill;
            missingEpisodesTotalLabel.TextAlign = ContentAlignment.MiddleLeft;
            missingEpisodesTotalLabel.Padding = new Padding(4, 0, 0, 0);
            missingEpisodesTotalLabel.Text = "No missing episode gaps found.";

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
            episodeSearchGrid.CellDoubleClick += EpisodeSearchGrid_CellDoubleClick;
            StyleGrid(episodeSearchGrid);
            AddEpisodeSearchColumn("Provider", "Provider", 72);
            AddEpisodeSearchColumn("Title", "Result", 300);
            AddEpisodeSearchColumn("Size", "Size", 80);
            AddEpisodeSearchColumn("Seeders", "Seed", 58);
            AddEpisodeSearchColumn("Leechers", "Leech", 58);
            AddEpisodeSearchColumn("Downloads", "Done", 62);
            AddEpisodeSearchColumn("Trusted", "Trusted", 70);
            AddEpisodeSearchColumn("Published", "Published", 120);

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

            episodeSearchTotalLabel = new Label();
            episodeSearchTotalLabel.Dock = DockStyle.Fill;
            episodeSearchTotalLabel.TextAlign = ContentAlignment.MiddleLeft;
            episodeSearchTotalLabel.Padding = new Padding(4, 0, 0, 0);
            episodeSearchTotalLabel.Text = "Select a missing episode, then Search.";

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

            detailsGroup = new GroupBox();
            detailsGroup.Text = "Details";
            detailsGroup.Dock = DockStyle.Fill;
            detailsGroup.Padding = new Padding(8);
            StyleGroupBox(detailsGroup);
            detailsGroup.Controls.Add(detailsBox);

            seriesGroup = new GroupBox();
            seriesGroup.Text = "Series";
            seriesGroup.Dock = DockStyle.Fill;
            seriesGroup.Padding = new Padding(8);
            StyleGroupBox(seriesGroup);
            seriesGroup.Controls.Add(seriesListView);
            seriesGroup.Controls.Add(seriesCoverView);
            StyleSeriesListView();
            StyleSeriesCoverView();
            UpdateSeriesPanelMode();

            candidatesGroup = new GroupBox();
            candidatesGroup.Text = "Candidates";
            candidatesGroup.Dock = DockStyle.Fill;
            candidatesGroup.Padding = new Padding(8);
            StyleGroupBox(candidatesGroup);

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

            deletionGroup = new GroupBox();
            deletionGroup.Text = "Deletion Ready";
            deletionGroup.Dock = DockStyle.Fill;
            deletionGroup.Padding = new Padding(8);
            StyleGroupBox(deletionGroup);

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
            deletionCloseButton = CreatePanelCloseButton("Hide the Deletion Ready panel.", ToggleReadyButton_Click);
            AttachPanelCloseButton(deletionGroup, deletionCloseButton);

            missingEpisodesGroup = new GroupBox();
            missingEpisodesGroup.Text = "Missing Episodes";
            missingEpisodesGroup.Dock = DockStyle.Fill;
            missingEpisodesGroup.Padding = new Padding(8);
            StyleGroupBox(missingEpisodesGroup);

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

            episodeSearchGroup = new GroupBox();
            episodeSearchGroup.Text = "Episode Search";
            episodeSearchGroup.Dock = DockStyle.Fill;
            episodeSearchGroup.Padding = new Padding(8);
            StyleGroupBox(episodeSearchGroup);

            var episodeSearchPanel = new TableLayoutPanel();
            episodeSearchPanel.Dock = DockStyle.Fill;
            episodeSearchPanel.ColumnCount = 4;
            episodeSearchPanel.RowCount = 3;
            episodeSearchPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            episodeSearchPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 84));
            episodeSearchPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 84));
            episodeSearchPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72));
            episodeSearchPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
            episodeSearchPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            episodeSearchPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            episodeSearchPanel.Controls.Add(episodeSearchTotalLabel, 0, 0);
            episodeSearchPanel.SetColumnSpan(episodeSearchTotalLabel, 4);
            episodeSearchPanel.Controls.Add(episodeSearchGroupBox, 1, 1);
            episodeSearchPanel.Controls.Add(episodeSearchResolutionBox, 2, 1);
            episodeSearchPanel.Controls.Add(episodeSearchButton, 3, 1);
            episodeSearchPanel.Controls.Add(episodeSearchGrid, 0, 2);
            episodeSearchPanel.SetColumnSpan(episodeSearchGrid, 4);
            episodeSearchGroup.Controls.Add(episodeSearchPanel);
            episodeSearchCloseButton = CreatePanelCloseButton("Hide the Episode Search panel.", ToggleEpisodeSearchButton_Click);
            AttachPanelCloseButton(episodeSearchGroup, episodeSearchCloseButton);

            activityGroup = new GroupBox();
            activityGroup.Text = "History / Alerts";
            activityGroup.Dock = DockStyle.Fill;
            activityGroup.Padding = new Padding(8);
            StyleGroupBox(activityGroup);
            activityGroup.Controls.Add(activityLogBox);

            workspacePanel = new TableLayoutPanel();
            workspacePanel.Dock = DockStyle.Fill;
            workspacePanel.Padding = new Padding(10);
            workspacePanel.BackColor = AppBackColor;
            workspacePanel.ColumnCount = 5;
            workspacePanel.RowCount = 2;
            workspacePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20F));
            workspacePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20F));
            workspacePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20F));
            workspacePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20F));
            workspacePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20F));
            workspacePanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            workspacePanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 150));
            workspacePanel.Controls.Add(seriesGroup, 0, 0);
            workspacePanel.Controls.Add(candidatesGroup, 1, 0);
            workspacePanel.Controls.Add(deletionGroup, 2, 0);
            workspacePanel.Controls.Add(missingEpisodesGroup, 3, 0);
            workspacePanel.Controls.Add(episodeSearchGroup, 4, 0);
            workspacePanel.Controls.Add(activityGroup, 0, 1);
            workspacePanel.Controls.Add(detailsGroup, 1, 1);
            workspacePanel.SetColumnSpan(detailsGroup, 4);
            ApplyWorkspacePanelVisibility();

            Controls.Add(workspacePanel);
            Controls.Add(topPanel);
            Controls.Add(mainMenu);
            ApplyTheme();
            UpdateDashboard();
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
            get { return darkMode ? Color.FromArgb(17, 24, 39) : Color.FromArgb(245, 247, 250); }
        }

        private Color PanelBackColor
        {
            get { return darkMode ? Color.FromArgb(31, 41, 55) : Color.White; }
        }

        private Color HeaderBackColor
        {
            get { return darkMode ? Color.FromArgb(55, 65, 81) : Color.FromArgb(241, 245, 249); }
        }

        private Color PrimaryTextColor
        {
            get { return darkMode ? Color.FromArgb(229, 231, 235) : Color.FromArgb(39, 49, 64); }
        }

        private Color SecondaryTextColor
        {
            get { return darkMode ? Color.FromArgb(156, 163, 175) : Color.FromArgb(78, 88, 102); }
        }

        private Color BorderColor
        {
            get { return darkMode ? Color.FromArgb(75, 85, 99) : Color.FromArgb(196, 205, 218); }
        }

        private Color GridLineColor
        {
            get { return darkMode ? Color.FromArgb(55, 65, 81) : Color.FromArgb(226, 232, 240); }
        }

        private Color AlternateRowColor
        {
            get { return darkMode ? Color.FromArgb(24, 34, 49) : Color.FromArgb(248, 250, 252); }
        }

        private Color SelectionBackColor
        {
            get { return darkMode ? Color.FromArgb(37, 99, 235) : Color.FromArgb(219, 234, 254); }
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
            get { return darkMode ? Color.FromArgb(49, 57, 70) : Color.FromArgb(248, 250, 252); }
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

        private void StyleButton(Button button, bool emphasis)
        {
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 1;
            button.FlatAppearance.BorderColor = emphasis ? Color.FromArgb(42, 91, 215) : BorderColor;
            button.BackColor = emphasis ? Color.FromArgb(42, 91, 215) : (darkMode ? Color.FromArgb(42, 52, 68) : Color.FromArgb(250, 251, 253));
            button.ForeColor = emphasis ? Color.White : PrimaryTextColor;
            button.Margin = new Padding(3);
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

            label.BackColor = PanelBackColor;
            label.ForeColor = SecondaryTextColor;
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
            targetGrid.RowTemplate.Height = 24;
            targetGrid.Refresh();
        }

        private void StyleTree(TreeView tree)
        {
            tree.BorderStyle = BorderStyle.None;
            tree.BackColor = PanelBackColor;
            tree.ForeColor = PrimaryTextColor;
            tree.LineColor = BorderColor;
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
            groupBox.ForeColor = PrimaryTextColor;
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

        private void ApplyWorkspacePanelVisibility()
        {
            var candidatesVisible = !candidatesPanelCollapsed;
            var deletionVisible = candidatesVisible && !deletionPanelCollapsed;
            var missingEpisodesVisible = !missingEpisodesPanelCollapsed;
            var episodeSearchVisible = !episodeSearchPanelCollapsed;
            candidatesGroup.Visible = candidatesVisible;
            deletionGroup.Visible = deletionVisible;
            missingEpisodesGroup.Visible = missingEpisodesVisible;
            episodeSearchGroup.Visible = episodeSearchVisible;

            var visiblePanelCount = 1;
            if (candidatesVisible)
            {
                visiblePanelCount++;
            }
            if (deletionVisible)
            {
                visiblePanelCount++;
            }
            if (missingEpisodesVisible)
            {
                visiblePanelCount++;
            }
            if (episodeSearchVisible)
            {
                visiblePanelCount++;
            }

            var visibleWidth = 100F / visiblePanelCount;
            workspacePanel.ColumnStyles[0].Width = visibleWidth;
            workspacePanel.ColumnStyles[1].Width = candidatesVisible ? visibleWidth : 0F;
            workspacePanel.ColumnStyles[2].Width = deletionVisible ? visibleWidth : 0F;
            workspacePanel.ColumnStyles[3].Width = missingEpisodesVisible ? visibleWidth : 0F;
            workspacePanel.ColumnStyles[4].Width = episodeSearchVisible ? visibleWidth : 0F;
            UpdateCandidatesToggleText();
            UpdateReadyToggleText();
            UpdateMissingEpisodesToggleText();
            UpdateEpisodeSearchToggleText();
        }

        private void UpdateWorkspaceMenuState()
        {
            viewCandidatesMenuItem.Checked = !candidatesPanelCollapsed;
            viewReadyMenuItem.Checked = !deletionPanelCollapsed && !candidatesPanelCollapsed;
            viewMissingEpisodesMenuItem.Checked = !missingEpisodesPanelCollapsed;
            viewEpisodeSearchMenuItem.Checked = !episodeSearchPanelCollapsed;
            viewReadyMenuItem.Enabled = !busyState && !candidatesPanelCollapsed;
            viewMissingEpisodesMenuItem.Enabled = !busyState;
            viewEpisodeSearchMenuItem.Enabled = !busyState;
            viewRestoreWorkspaceMenuItem.Enabled = !busyState && (candidatesPanelCollapsed || deletionPanelCollapsed || missingEpisodesPanelCollapsed || episodeSearchPanelCollapsed);
        }

        private void RestoreWorkspaceMenuItem_Click(object sender, EventArgs e)
        {
            candidatesPanelCollapsed = false;
            deletionPanelCollapsed = false;
            missingEpisodesPanelCollapsed = false;
            episodeSearchPanelCollapsed = false;
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
            StyleGrid(grid);
            StyleGrid(deletionGrid);
            StyleGrid(missingEpisodesGrid);
            StyleGrid(episodeSearchGrid);
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
                control.BackColor = control == this || control == workspacePanel ? AppBackColor : PanelBackColor;
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
                if (button == deleteButton)
                {
                    StyleDeleteButton(button);
                }
                else
                {
                    StyleButton(button, false);
                }
            }
            else if (control is TreeView)
            {
                StyleTree((TreeView)control);
            }
            else if (control is ListView)
            {
                ((ListView)control).BackColor = PanelBackColor;
                ((ListView)control).ForeColor = PrimaryTextColor;
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
            RefreshVisibleRows();
        }

        private void CancelButton_Click(object sender, EventArgs e)
        {
            if (!busyState)
            {
                return;
            }

            cancelRequested = true;
            cancelButton.Enabled = false;
            UpdateActivity("Cancel requested. Finishing the current file, then stopping...", true);
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
                "2. Review Deletion Ready before pressing Delete. Delete sends files to the Recycle Bin.\r\n" +
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
            using (var dialog = new FolderBrowserDialog())
            {
                dialog.Description = "Choose the folder to scan";
                dialog.SelectedPath = Directory.Exists(rootBox.Text) ? rootBox.Text : "";
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    rootBox.Text = dialog.SelectedPath;
                    StartScan(dialog.SelectedPath, false);
                }
            }
        }

        private void AddScanLocationMenuItem_Click(object sender, EventArgs e)
        {
            if (allRows.Count == 0 && allScannedRows.Count == 0)
            {
                MessageBox.Show(this, "Run Browse and Scan first, then add another location to the loaded session.", "Add Scan Location", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using (var dialog = new FolderBrowserDialog())
            {
                dialog.Description = "Choose another folder or drive to add to the current scan";
                dialog.SelectedPath = Directory.Exists(rootBox.Text) ? rootBox.Text : "";
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    StartScan(dialog.SelectedPath, true);
                }
            }
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

        private void LoadRowsIntoUi(List<EpisodeFile> data)
        {
            LoadRowsIntoUi(data, null);
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
            rows.Clear();
            var includeDeleted = string.Equals(activeReviewFilter, "Marked", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(activeReviewFilter, "Delete", StringComparison.OrdinalIgnoreCase) ||
                                 IsAutoThresholdFilter(activeReviewFilter);
            foreach (var item in data.Where(x => includeDeleted || !x.Delete).Where(PassesReviewFilter).Where(PassesSearchFilter))
            {
                rows.Add(item);
            }

            UpdateCandidateTotal();
            RefreshDeletionRows();
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
            candidateTotalLabel.Text = string.Format("Grand total: {0:N0} file(s) | {1}", rows.Count, FormatByteSize(totalBytes));
            UpdateDashboard();
        }

        private void RefreshDeletionRows()
        {
            deletionRows.Clear();
            foreach (var item in allRows.Where(x => x.Delete)
                                        .OrderBy(x => x.Title, StringComparer.OrdinalIgnoreCase)
                                        .ThenBy(x => x.Episode, StringComparer.OrdinalIgnoreCase)
                                        .ThenBy(x => x.FileName, StringComparer.OrdinalIgnoreCase))
            {
                deletionRows.Add(item);
            }

            UpdateDeletionTotal();
        }

        private void UpdateDeletionTotal()
        {
            if (deletionTotalLabel == null)
            {
                return;
            }

            var totalBytes = deletionRows.Sum(x => x.SizeBytes);
            deletionTotalLabel.Text = string.Format("Grand total: {0:N0} file(s) | {1}", deletionRows.Count, FormatByteSize(totalBytes));
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
            scannedChipLabel.Text = string.Format("Scanned: {0:N0}", seriesSource.Count);
            candidatesChipLabel.Text = string.Format("Candidates {0:N0}", allRows.Count);
            visibleChipLabel.Text = string.Format("Visible: {0:N0}", rows.Count);
            deletionChipLabel.Text = string.Format("Ready {0:N0}", deletionRows.Count);
            duplicateChipLabel.Text = string.Format("Groups {0:N0}", EpisodeParser.CountDuplicateEpisodeGroups(allRows));
            locationChipLabel.Text = string.Format("Locations {0:N0}", CountMultiLocationSeries(seriesSource));
            filterChipLabel.Text = GetFileFormatFilterSummary();
            cacheChipLabel.Text = GetCacheStatusSummary();
            providerChipLabel.Text = GetProviderStatusSummary();
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
            if (string.IsNullOrWhiteSpace(rootBox.Text) || !File.Exists(GetCachePath()))
            {
                return "Cache none";
            }

            var cachedRoot = ReadCacheRoot(GetCachePath());
            try
            {
                return string.Equals(NormalizeRoot(cachedRoot), NormalizeRoot(rootBox.Text), StringComparison.OrdinalIgnoreCase)
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

            if (log)
            {
                LogActivity(text);
            }
        }

        private void LogActivity(string text)
        {
            if (activityLogBox == null || string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            var line = string.Format("[{0:HH:mm:ss}] {1}", DateTime.Now, text);
            activityLogBox.AppendText(line + Environment.NewLine);
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
                AddSeriesCoverItem("Duplicate Files", AllSeriesTag, allRows.Count, null);

                foreach (var group in seriesSource.GroupBy(x => x.Title, StringComparer.OrdinalIgnoreCase)
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

                    AddSeriesCoverItem(group.Key, group.Key, files.Count, FindSeriesCoverPath(files));
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

            missingEpisodeRows.Clear();
            foreach (var row in BuildMissingEpisodeRows(GetSeriesSourceRows()))
            {
                missingEpisodeRows.Add(row);
            }

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
            if (!e.IsSelected || e.Item == null)
            {
                return;
            }

            ApplySeriesFilter(e.Item.Tag);
        }

        private void SeriesCoverView_ItemSelectionChanged(object sender, ListViewItemSelectionChangedEventArgs e)
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
            }
        }

        private void EpisodeSearchButton_Click(object sender, EventArgs e)
        {
            RunEpisodeSearch(GetSelectedMissingEpisodeRow());
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
                }
            };
            worker.RunWorkerAsync();
        }

        private void ApplySeriesFilter(object tag)
        {
            activeSeriesTag = tag;

            var episodeFile = tag as EpisodeFile;
            if (episodeFile != null)
            {
                ShowRows(new List<EpisodeFile> { episodeFile });
                UpdateDetails(episodeFile);
                return;
            }

            var filter = tag as string;
            if (filter == AllSeriesTag)
            {
                ShowRows(allRows);
                UpdateDetails((EpisodeFile)null);
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
            }
        }

        private void AddSeriesCoverItem(string title, object tag, int count, string coverPath)
        {
            var imageKey = Convert.ToString(tag);
            if (string.IsNullOrWhiteSpace(imageKey))
            {
                imageKey = title;
            }

            seriesCoverImages.Images.Add(imageKey, CreateSeriesCoverImage(title, coverPath));
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
            var root = "";
            if (!string.IsNullOrWhiteSpace(rootBox.Text))
            {
                try
                {
                    root = Path.GetFullPath(rootBox.Text.Trim()).TrimEnd('\\');
                }
                catch
                {
                    root = "";
                }
            }

            foreach (var file in files)
            {
                var folder = GetExistingFolder(file);
                while (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
                {
                    var cover = FindCoverInFolder(folder);
                    if (!string.IsNullOrWhiteSpace(cover))
                    {
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

        private static string FindCoverInFolder(string folder)
        {
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
                        !string.IsNullOrWhiteSpace(rootBox.Text) &&
                        Directory.Exists(rootBox.Text.Trim()))
                    {
                        StartScan(rootBox.Text.Trim(), false);
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

            ShowBatchPreviewDialog(previewRows);
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

        private void ShowBatchPreviewDialog(List<ActionPreviewRow> previewRows)
        {
            ShowBatchPreviewDialog(previewRows, false, "Close");
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

                    RefreshVisibleRows();
                    deletionGrid.Refresh();
                    grid.Refresh();
                    PopulateSeriesPanel();
                    if (!string.IsNullOrWhiteSpace(rootBox.Text))
                    {
                        SaveCachedScan(rootBox.Text.Trim(), allRows);
                        SaveParseCache(rootBox.Text.Trim(), allScannedRows.Count > 0 ? allScannedRows : allRows);
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
            var root = rootBox.Text == null ? "" : rootBox.Text.Trim();
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
                return;
            }

            detailsBox.Links.Clear();
            detailsBox.Text =
                "Series: " + file.Title + Environment.NewLine +
                "Episode: " + DisplayOrDash(file.Episode) + " | Size: " + FormatByteSize(file.SizeBytes) + " | Group: " + DisplayOrDash(file.SubtitleGroup) + " | Version: " + DisplayOrDash(file.Version) + Environment.NewLine +
                "Recommendation: " + DisplayOrDash(file.Recommendation) + " | " + DisplayOrDash(file.Confidence) + " | " + DisplayOrDash(file.ReviewStatus) + " | " + DisplayOrDash(file.ArtworkStatus) + Environment.NewLine +
                "Reason: " + ShortenMiddle(DisplayOrDash(file.RecommendationReason), 170) + Environment.NewLine +
                "Metadata: " + ShortenMiddle(DisplayOrDash(file.AniDbDisplay), 170) + Environment.NewLine +
                "Location: " + ShortenMiddle(DisplayOrDash(file.FileLocation), 170) + Environment.NewLine +
                "File: " + ShortenMiddle(DisplayOrDash(file.FileName), 170);
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
        }

        private string GetSearchQuery(MissingEpisodeRow row)
        {
            var missingEpisode = MissingEpisodeAnalyzer.ToSearchMissingEpisode(row);
            return missingEpisode == null ? "" : missingEpisode.SearchQuery;
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

        private void ToggleCandidatesButton_Click(object sender, EventArgs e)
        {
            candidatesPanelCollapsed = !candidatesPanelCollapsed;
            ApplyWorkspacePanelVisibility();
        }

        private void ToggleReadyButton_Click(object sender, EventArgs e)
        {
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
            ApplyWorkspacePanelVisibility();
        }

        private void ToggleEpisodeSearchButton_Click(object sender, EventArgs e)
        {
            episodeSearchPanelCollapsed = !episodeSearchPanelCollapsed;
            ApplyWorkspacePanelVisibility();
        }

        private void ScanButton_Click(object sender, EventArgs e)
        {
            StartScan(rootBox.Text.Trim(), false);
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
                        rootBox.Text = BuildSessionRootLabel(rootBox.Text, root);
                    }
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

        private void LoadSavedButton_Click(object sender, EventArgs e)
        {
            var root = rootBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(root) || string.Equals(root, "No folder scanned", StringComparison.OrdinalIgnoreCase) || !Directory.Exists(root))
            {
                var cachedRoot = ReadCacheRoot(GetCachePath());
                if (!string.IsNullOrWhiteSpace(cachedRoot))
                {
                    root = cachedRoot;
                    rootBox.Text = cachedRoot;
                }
            }

            StartLoadCachedScan(root, false);
        }

        private void AniDbButton_Click(object sender, EventArgs e)
        {
            var hasScannedData = allRows.Count > 0 || allScannedRows.Count > 0;
            if (!hasScannedData)
            {
                MessageBox.Show(this, "Load or scan files before running metadata lookup.", "Metadata Lookup", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            RunMetadataLookup();
        }

        private void AniDbMissingCoversMenuItem_Click(object sender, EventArgs e)
        {
            if (allRows.Count == 0 && allScannedRows.Count == 0)
            {
                MessageBox.Show(this, "Load or scan files before checking for missing covers.", "Missing Covers", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            RunAniDbMissingCoverScan();
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
                string.Format("Fetch poster art for {0:N0} series with missing local covers?\r\n\r\nAniDB will be tried first; TVDB and TMDB will be used as backups when configured. Images will be saved as folder.jpg beside the first loaded file for each series.", missing.Count),
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
                        }
                        else if (match != null && match.Found && !string.IsNullOrWhiteSpace(match.PictureFile))
                        {
                            DownloadAniDbPicture(match.PictureFile, Path.Combine(targetFolder, "folder.jpg"));
                            saved++;
                        }
                        else
                        {
                            string fallbackMessage;
                            if (TryDownloadFallbackCover(group.Key, Path.Combine(targetFolder, "folder.jpg"), out fallbackMessage))
                            {
                                saved++;
                            }
                            else
                            {
                                skipped++;
                                if (!string.IsNullOrWhiteSpace(fallbackMessage))
                                {
                                    failures.Add(group.Key + " fallback: " + fallbackMessage);
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
                            savedByFallback = TryDownloadFallbackCover(group.Key, Path.Combine(targetFolder, "folder.jpg"), out fallbackMessage);
                            if (!savedByFallback && !string.IsNullOrWhiteSpace(fallbackMessage))
                            {
                                failures.Add(group.Key + " fallback: " + fallbackMessage);
                            }
                        }

                        if (savedByFallback)
                        {
                            saved++;
                        }
                        else
                        {
                            failures.Add(group.Key + ": " + ex.Message);
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
                var candidates = AniDbTitleIndex.FindCandidates(title, "", 1);
                if (candidates.Count == 0)
                {
                    var relaxedTitle = BuildAniDbCoverSearchTitle(title);
                    if (!string.Equals(relaxedTitle, title, StringComparison.OrdinalIgnoreCase))
                    {
                        candidates = AniDbTitleIndex.FindCandidates(relaxedTitle, "", 1);
                    }
                }

                var candidate = candidates.FirstOrDefault();
                if (candidate == null)
                {
                    match.Error = "No AniDB match";
                    return match;
                }

                match.AniDbId = candidate.AniDbId;
                match.Title = candidate.Title;
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
                var found = AniDbTitleIndex.FindCandidates(queryTitle, targetFolder, 8);
                if (found.Count == 0)
                {
                    var relaxedTitle = BuildAniDbCoverSearchTitle(queryTitle);
                    if (!string.Equals(relaxedTitle, queryTitle, StringComparison.OrdinalIgnoreCase))
                    {
                        found = AniDbTitleIndex.FindCandidates(relaxedTitle, targetFolder, 8);
                        foreach (var candidate in found)
                        {
                            candidate.QueryTitle = queryTitle;
                        }
                    }
                }

                candidates.AddRange(found);
            }
            catch (Exception ex)
            {
                failures.Add(queryTitle + " candidate search: " + ex.Message);
            }
        }

        private static string BuildAniDbCoverSearchTitle(string title)
        {
            var cleaned = Regex.Replace(title ?? "", @"\[[^\]]+\]|\([^\)]*\)", " ");
            cleaned = Regex.Replace(cleaned, @"\b(480p|576p|720p|1080p|2160p|x264|x265|h264|h265|hevc|avc|aac|flac|dual audio|bluray|blu ray|bdrip|webrip|web dl)\b", " ", RegexOptions.IgnoreCase);
            cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim();
            return string.IsNullOrWhiteSpace(cleaned) ? title : cleaned;
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
                                DownloadAniDbPicture(pictureFile, Path.Combine(candidate.TargetFolder, "folder.jpg"));
                                saved++;
                            }
                        }
                        catch (Exception ex)
                        {
                            failures.Add(candidate.QueryTitle + " -> " + candidate.Title + ": " + ex.Message);
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
                    RefreshVisibleRows();
                    grid.Refresh();
                    deletionGrid.Refresh();
                    UpdateSummary(string.Format("Manual AniDB cover fetch complete. Saved {0:N0}; skipped {1:N0}.", saved, skipped));
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
            using (var webClient = new WebClient())
            {
                webClient.Headers[HttpRequestHeader.UserAgent] = "SameEpisodeDuplicateFinder";
                webClient.DownloadFile(url, targetPath);
            }
        }

        private bool TryDownloadFallbackCover(string title, string targetPath, out string message)
        {
            message = "";
            string tvDbMessage;
            if (TryDownloadTvDbCover(title, targetPath, out tvDbMessage))
            {
                message = "TVDB: " + tvDbMessage;
                return true;
            }

            string tmDbMessage;
            if (TryDownloadTmDbCover(title, targetPath, out tmDbMessage))
            {
                message = "TMDB: " + tmDbMessage;
                return true;
            }

            message = string.Join("; ", new[] { "TVDB: " + tvDbMessage, "TMDB: " + tmDbMessage }.Where(x => !string.IsNullOrWhiteSpace(x)).ToArray());
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
                            worker.ReportProgress(0, string.Format("AniDB HTTP lookup {0:N0}/{1:N0}: {2}", i + 1, titles.Count, title));
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
                        Thread.Sleep(match.Found && IsAniDbHttpId(match.AniDbId) ? 2200 : 1200);
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
                try
                {
                    if (args.Error is OperationCanceledException)
                    {
                        UpdateSummary("AniDB lookup canceled.");
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
                }
            };
            worker.RunWorkerAsync();
        }

        private AniDbAnimeResult LookupAniDbMetadata(string title)
        {
            var candidates = AniDbTitleIndex.FindCandidates(title, "", 1);
            if (candidates.Count == 0)
            {
                var relaxedTitle = BuildAniDbCoverSearchTitle(title);
                if (!string.Equals(relaxedTitle, title, StringComparison.OrdinalIgnoreCase))
                {
                    candidates = AniDbTitleIndex.FindCandidates(relaxedTitle, "", 1);
                }
            }

            var candidate = candidates.FirstOrDefault();
            if (candidate == null)
            {
                return new AniDbAnimeResult { QueryTitle = title, Error = "No AniDB match" };
            }

            return AniDbClient.LookupAnimeById(candidate.AniDbId, title, candidate.Title);
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

            RefreshVisibleRows();
            deletionGrid.Refresh();
            grid.Refresh();
            PopulateSeriesPanel();
            if (!string.IsNullOrWhiteSpace(rootBox.Text))
            {
                SaveCachedScan(rootBox.Text.Trim(), allRows);
                SaveParseCache(rootBox.Text.Trim(), allScannedRows.Count > 0 ? allScannedRows : allRows);
            }
        }

        private static List<EpisodeFile> Scan(string root, FileFormatFilter filter)
        {
            return ScanWithDetails(root, filter, null, null).DuplicateRows;
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
            foreach (var file in LongPath.EnumerateFiles(rootFull))
            {
                ThrowIfCancellationRequested(shouldCancel);
                visited++;
                if (filter.ShouldIgnore(file))
                {
                    ignored++;
                    if (visited % 250 == 0)
                    {
                        ReportProgress(progress, string.Format("Scanning files: {0:N0} checked | {1:N0} accepted | {2:N0} cached | {3:N0} ignored", visited, parsed.Count, cacheHits, ignored), ref scanProgressUtc, 500);
                    }
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
                    if (visited % 250 == 0)
                    {
                        ReportProgress(progress, string.Format("Scanning files: {0:N0} checked | {1:N0} accepted | {2:N0} cached | {3:N0} ignored", visited, parsed.Count, cacheHits, ignored), ref scanProgressUtc, 500);
                    }
                    continue;
                }

                if (EpisodeParser.TryParseFile(file, rootFull, out info))
                {
                    parsed.Add(info);
                }

                if (visited % 250 == 0)
                {
                    ReportProgress(progress, string.Format("Scanning files: {0:N0} checked | {1:N0} accepted | {2:N0} cached | {3:N0} ignored", visited, parsed.Count, cacheHits, ignored), ref scanProgressUtc, 500);
                }
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

        private static string BuildSessionRootLabel(string current, string added)
        {
            var addedLabel = string.IsNullOrWhiteSpace(added) ? "" : added.Trim();
            if (string.IsNullOrWhiteSpace(current) || string.Equals(current, "No folder scanned", StringComparison.OrdinalIgnoreCase))
            {
                return addedLabel;
            }

            return current.IndexOf(" + ", StringComparison.Ordinal) >= 0
                ? current + " + " + addedLabel
                : current + " + " + addedLabel;
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
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SameEpisodeDuplicateFinder.last-move-dry-run.csv");
        }

        private static string GetDeleteDryRunReportPath()
        {
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SameEpisodeDuplicateFinder.last-delete-dry-run.csv");
        }

        private static string GetErrorLogPath()
        {
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SameEpisodeDuplicateFinder.errors.log");
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

            return Path.GetFullPath(root).TrimEnd('\\');
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
            if (!string.IsNullOrWhiteSpace(rootBox.Text))
            {
                SaveCachedScan(rootBox.Text.Trim(), allRows);
                SaveParseCache(rootBox.Text.Trim(), allScannedRows.Count > 0 ? allScannedRows : allRows);
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

        private void ClearMarksButton_Click(object sender, EventArgs e)
        {
            foreach (var row in GetActiveDataSet())
            {
                row.Delete = false;
            }
            RefreshVisibleRows();
            deletionGrid.Refresh();
            UpdateSummary(null);
            UpdateSummary("Cleared delete marks.");
        }

        private void SuggestBestActionsMenuItem_Click(object sender, EventArgs e)
        {
            ApplyBestActionMarks("Suggest Best Actions", "Suggested best actions");
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
            RefreshVisibleRows();
            deletionGrid.Refresh();
            grid.Refresh();
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

            RefreshVisibleRows();
            deletionGrid.Refresh();
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
                    RefreshVisibleRows();
                    deletionGrid.Refresh();
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
        }

        private void SetBusy(bool busy, string text)
        {
            if (busy && !busyState)
            {
                cancelRequested = false;
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
            cancelButton.Enabled = busy;
            UpdateActivity(text, false);
        }

        private void UpdateCommandAvailability()
        {
            var busy = busyState;
            var hasCandidateData = allRows.Count > 0 || rows.Count > 0 || deletionRows.Count > 0;
            var hasScannedData = hasCandidateData || allScannedRows.Count > 0;
            rootBox.Enabled = !busy;
            searchBox.Enabled = !busy || rows.Count > 0;
            cancelButton.Enabled = busy && !cancelRequested;
            fileBrowseMenuItem.Enabled = !busy;
            viewColumnsMenuItem.Enabled = !busy;
            fileLoadSavedMenuItem.Enabled = !busy && File.Exists(GetCachePath());
            fileExportMenuItem.Enabled = !busy && hasCandidateData;
            deleteButton.Enabled = !busy && hasCandidateData;
            toolsClearMarksMenuItem.Enabled = !busy && hasCandidateData;
            viewCandidatesMenuItem.Enabled = !busy;
            viewReadyMenuItem.Enabled = !busy && !candidatesPanelCollapsed;
            viewMissingEpisodesMenuItem.Enabled = !busy;
            viewEpisodeSearchMenuItem.Enabled = !busy;
            viewRestoreWorkspaceMenuItem.Enabled = !busy && (candidatesPanelCollapsed || deletionPanelCollapsed || missingEpisodesPanelCollapsed || episodeSearchPanelCollapsed);
            episodeSearchButton.Enabled = !busy && GetSelectedMissingEpisodeRow() != null;
            UpdateWorkspaceMenuState();
            toolsAutoMarkMenuItem.Enabled = !busy && hasCandidateData;
            toolsAutoMarkLevelMenuItem.Enabled = !busy;
            toolsMoveToNameFoldersMenuItem.Enabled = !busy && hasScannedData;
            toolsFileBotMenuItem.Enabled = !busy && hasScannedData;
            toolsAniDbCoversMenuItem.Enabled = !busy && hasScannedData;
            toolsSuggestActionsMenuItem.Enabled = !busy && hasCandidateData;
            toolsPreviewActionsMenuItem.Enabled = !busy && hasCandidateData;
            toolsOpenMoveReportMenuItem.Enabled = !busy && File.Exists(GetMoveReportPath());
            UpdateAniDbButtonState(busy);
        }

        private void UpdateAniDbButtonState(bool busy)
        {
            toolsAniDbMenuItem.Text = "Metadata Lookup";
            toolsAniDbMenuItem.Enabled = !busy;
            toolsAniDbCoversMenuItem.Enabled = !busy && (allRows.Count > 0 || rows.Count > 0 || deletionRows.Count > 0 || allScannedRows.Count > 0);
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
