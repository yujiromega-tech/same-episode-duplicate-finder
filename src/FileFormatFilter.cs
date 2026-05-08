using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace SameEpisodeDuplicateFinder
{
    internal sealed class FileFormatFilter
    {
        public bool AllowOnlyListed { get; set; }
        public List<string> Extensions { get; private set; }

        public FileFormatFilter()
        {
            Extensions = new List<string>();
        }

        public bool ShouldIgnore(ScannedFile file)
        {
            if (file == null || string.IsNullOrWhiteSpace(file.Name))
            {
                return true;
            }

            var extension = NormalizeExtension(Path.GetExtension(file.Name));
            if (string.IsNullOrWhiteSpace(extension))
            {
                return AllowOnlyListed;
            }

            var listed = Extensions.Any(x => string.Equals(x, extension, StringComparison.OrdinalIgnoreCase));
            return AllowOnlyListed ? !listed : listed;
        }

        public FileFormatFilter Clone()
        {
            var clone = new FileFormatFilter();
            clone.AllowOnlyListed = AllowOnlyListed;
            clone.Extensions.AddRange(Extensions);
            return clone;
        }

        public static string NormalizeExtension(string extension)
        {
            if (string.IsNullOrWhiteSpace(extension))
            {
                return "";
            }

            extension = extension.Trim().ToLowerInvariant();
            return extension.StartsWith(".", StringComparison.Ordinal) ? extension : "." + extension;
        }

        public static FileFormatFilter CreateDefault()
        {
            var filter = new FileFormatFilter();
            filter.AllowOnlyListed = false;
            filter.Extensions.AddRange(new[]
            {
                ".ass", ".flac", ".mp3", ".m4a", ".aac", ".ogg", ".opus", ".wav", ".wma", ".alac", ".ape"
            });
            return filter;
        }
    }

    internal static class FileFormatFilterStore
    {
        public static string SettingsPath
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SameEpisodeDuplicateFinder.fileformats"); }
        }

        public static FileFormatFilter Load()
        {
            var filter = FileFormatFilter.CreateDefault();
            if (!File.Exists(SettingsPath))
            {
                return filter;
            }

            var loaded = new FileFormatFilter();
            foreach (var line in File.ReadAllLines(SettingsPath, Encoding.UTF8))
            {
                var split = line.IndexOf('=');
                if (split < 0)
                {
                    continue;
                }

                var key = line.Substring(0, split);
                var value = line.Substring(split + 1);
                if (string.Equals(key, "Mode", StringComparison.OrdinalIgnoreCase))
                {
                    loaded.AllowOnlyListed = string.Equals(value, "AllowOnly", StringComparison.OrdinalIgnoreCase);
                }
                else if (string.Equals(key, "Extensions", StringComparison.OrdinalIgnoreCase))
                {
                    loaded.Extensions.Clear();
                    foreach (var part in value.Split(new[] { ';', ',', ' ' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        var extension = FileFormatFilter.NormalizeExtension(part);
                        if (!string.IsNullOrWhiteSpace(extension) &&
                            !loaded.Extensions.Any(x => string.Equals(x, extension, StringComparison.OrdinalIgnoreCase)))
                        {
                            loaded.Extensions.Add(extension);
                        }
                    }
                }
            }

            return loaded;
        }

        public static void Save(FileFormatFilter filter)
        {
            using (var writer = new StreamWriter(SettingsPath, false, new UTF8Encoding(false)))
            {
                writer.WriteLine("Mode=" + (filter.AllowOnlyListed ? "AllowOnly" : "Disallow"));
                writer.WriteLine("Extensions=" + string.Join(";", filter.Extensions.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray()));
            }
        }
    }
}
