using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace SameEpisodeDuplicateFinder
{
    internal sealed class SeriesIdentity
    {
        public SeriesIdentity()
        {
            Aliases = new List<string>();
            TvDbIds = new List<string>();
            TmDbIds = new List<string>();
        }

        public string Title { get; set; }
        public List<string> Aliases { get; private set; }
        public List<string> TvDbIds { get; private set; }
        public List<string> TmDbIds { get; private set; }
        public string ReviewReason { get; set; }
    }

    internal static class SeriesIdentityResolver
    {
        public static SeriesIdentity Resolve(string title, int? referenceYear)
        {
            var identity = new SeriesIdentity { Title = title ?? "" };
            AddAlias(identity, title);

            var normalized = Normalize(title);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return identity;
            }

            ApplyKnownTvDbIds(identity, normalized, referenceYear);
            ApplyKnownAliases(identity, normalized);
            return identity;
        }

        public static string NormalizeForTest(string title)
        {
            return Normalize(title);
        }

        private static void ApplyKnownTvDbIds(SeriesIdentity identity, string normalized, int? referenceYear)
        {
            if (string.Equals(normalized, "macgyver", StringComparison.OrdinalIgnoreCase) && !referenceYear.HasValue)
            {
                identity.ReviewReason = "MacGyver title is ambiguous without a year; original and 2016 series both exist.";
            }
        }

        private static void ApplyKnownAliases(SeriesIdentity identity, string normalized)
        {
            if (IsGozyuger(normalized))
            {
                AddAlias(identity, "No.1 Sentai Gozyuger");
                AddAlias(identity, "No 1 Sentai Gozyuger");
                AddAlias(identity, "Nanbaa Wan Sentai Gojuuja");
                AddAlias(identity, "Gozyuger");
                AddAlias(identity, "GoJyuujer");
            }
            else if (string.Equals(normalized, "engine sentai go onger", StringComparison.OrdinalIgnoreCase))
            {
                AddAlias(identity, "Engine Sentai Go-Onger");
                AddAlias(identity, "Engine Squadron Go-Onger");
            }
            else if (string.Equals(normalized, "machine sentai kiramager", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(normalized, "mashin sentai kiramager", StringComparison.OrdinalIgnoreCase))
            {
                AddAlias(identity, "Mashin Sentai Kiramager");
            }
            else if (string.Equals(normalized, "special police dekaranger", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(normalized, "tokusou sentai dekaranger", StringComparison.OrdinalIgnoreCase))
            {
                AddAlias(identity, "Tokusou Sentai Dekaranger");
                AddAlias(identity, "Special Police Dekaranger");
            }
            else if (string.Equals(normalized, "kamen rider decade", StringComparison.OrdinalIgnoreCase))
            {
                AddAlias(identity, "Masked Rider Decade");
                AddAlias(identity, "Kamen Rider DCD");
                AddAlias(identity, "Kamen Raida Dikeido");
            }
            else if (string.Equals(normalized, "seal team six", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(normalized, "seal team 6", StringComparison.OrdinalIgnoreCase))
            {
                AddAlias(identity, "SIX");
                AddAlias(identity, "Seal Team 6");
            }
        }

        private static bool IsGozyuger(string normalized)
        {
            return string.Equals(normalized, "super sentai gojyuujer", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(normalized, "super sentai gozyuger", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(normalized, "no 1 sentai gozyuger", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(normalized, "number one sentai gozyuger", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(normalized, "gojyuujer", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(normalized, "gozyuger", StringComparison.OrdinalIgnoreCase);
        }

        private static void AddAlias(SeriesIdentity identity, string alias)
        {
            if (identity == null || string.IsNullOrWhiteSpace(alias))
            {
                return;
            }

            if (!identity.Aliases.Any(x => string.Equals(x, alias.Trim(), StringComparison.OrdinalIgnoreCase)))
            {
                identity.Aliases.Add(alias.Trim());
            }
        }

        private static string Normalize(string title)
        {
            var normalized = Regex.Replace((title ?? "").ToLowerInvariant(), @"[^a-z0-9]+", " ").Trim();
            normalized = Regex.Replace(normalized, @"\s+", " ");
            return normalized;
        }
    }
}
