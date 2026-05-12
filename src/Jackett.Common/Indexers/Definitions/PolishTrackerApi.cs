using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using CommandLine;
using Jackett.Common.Exceptions;
using Jackett.Common.Models;
using Jackett.Common.Models.IndexerConfig;
using Jackett.Common.Services.Interfaces;
using Jackett.Common.Utils;
using Jackett.Common.Utils.Clients;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NLog;
using WebClient = Jackett.Common.Utils.Clients.WebClient;

namespace Jackett.Common.Indexers.Definitions
{
    [ExcludeFromCodeCoverage]
    public class PolishTracker : IndexerBase
    {
        public override string Id => "polishtracker-api";
        public override string Name => "PolishTracker (API)";

        public override string Description =>
            "PolishTracker is a POLISH Private Torrent Tracker for 0DAY / MOVIES / TV SERIES / GENERAL";

        public override string SiteLink { get; protected set; } = "https://pte.nu/";
        private string ApiUrl => "https://api-test.pte.nu/api/v1/";
        public override string Language => "pl-PL";
        public override string Type => "private";

        public override bool SupportsPagination => true;
        public override int PageSize => 50;

        public override TorznabCapabilities TorznabCaps => SetCapabilities();

        private new ConfigurationDataAPIKey configData
        {
            get => (ConfigurationDataAPIKey)base.configData;
            set => base.configData = value;
        }

        public PolishTracker(IIndexerConfigurationService configurationService, WebClient c, Logger l, IProtectionService ps,
                             ICacheService cs) : base(
            configService: configurationService, client: c, logger: l, p: ps, cacheService: cs,
            configData: new ConfigurationDataAPIKey())
        {
            configData.AddDynamic(
                "keyInfo",
                new ConfigurationData.DisplayInfoConfigurationItem(
                    String.Empty,
                    "Find your API Key by accessing your <a href=\"https://pte.nu/\" target =_blank>PolishTracker</a> account <i>Settings</i> page and clicking on the <b>API</b> section."));
            configData.AddDynamic(
                "multilang",
                new ConfigurationData.BoolConfigurationItem("Replace MULTi by another language in release name")
                {
                    Value = false
                });
            configData.AddDynamic(
                "multilanguage",
                new ConfigurationData.SingleSelectConfigurationItem(
                    "Replace MULTi by this language",
                    new Dictionary<string, string> { { "POLISH", "POLISH" }, { "MULTi.POLISH", "MULTi.POLISH" } }
                ));
        }

        private static TorznabCapabilities SetCapabilities()
        {
            var caps = new TorznabCapabilities
            {
                LimitsDefault = 50,
                LimitsMax = 50,
                MovieSearchParams = new List<MovieSearchParam> { MovieSearchParam.Q, MovieSearchParam.ImdbId },
                TvSearchParams =
                    new List<TvSearchParam>
                    {
                        TvSearchParam.Q, TvSearchParam.ImdbId, TvSearchParam.Season, TvSearchParam.Ep
                    },
                BookSearchParams = new List<BookSearchParam>() { BookSearchParam.Q },
                MusicSearchParams = new List<MusicSearchParam>() { MusicSearchParam.Q },
                TvSearchImdbAvailable = true
            };
            caps.Categories.AddCategoryMapping(1, TorznabCatType.PC0day, "0-Day");
            caps.Categories.AddCategoryMapping(2, TorznabCatType.AudioVideo, "Music Video");
            caps.Categories.AddCategoryMapping(3, TorznabCatType.PC0day, "Apps");
            caps.Categories.AddCategoryMapping(4, TorznabCatType.Console, "Consoles");
            caps.Categories.AddCategoryMapping(5, TorznabCatType.Books, "E-book");
            caps.Categories.AddCategoryMapping(6, TorznabCatType.MoviesHD, "Movies HD");
            caps.Categories.AddCategoryMapping(7, TorznabCatType.MoviesSD, "Movies SD");
            caps.Categories.AddCategoryMapping(8, TorznabCatType.Audio, "Music");
            caps.Categories.AddCategoryMapping(9, TorznabCatType.MoviesUHD, "Movies UHD");
            caps.Categories.AddCategoryMapping(10, TorznabCatType.PCGames, "PC Games");
            caps.Categories.AddCategoryMapping(11, TorznabCatType.TVHD, "TV HD");
            caps.Categories.AddCategoryMapping(12, TorznabCatType.TVSD, "TV SD");
            caps.Categories.AddCategoryMapping(13, TorznabCatType.XXX, "XXX");
            caps.Categories.AddCategoryMapping(14, TorznabCatType.TVUHD, "TV UHD");
            caps.Categories.AddCategoryMapping(15, TorznabCatType.AudioAudiobook, "Audiobook");
            return caps;
        }

        public override async Task<IndexerConfigurationStatus> ApplyConfiguration(JToken configJson)
        {
            LoadValuesFromJson(configJson);
            IsConfigured = false;
            try
            {
                var results = await PerformQuery(new TorznabQuery());
                if (!results.Any())
                {
                    throw new Exception("Testing returned no results!");
                }

                IsConfigured = true;
                SaveConfig();
            }
            catch (Exception e)
            {
                throw new ExceptionWithConfigData(e.Message, configData);
            }

            return IndexerConfigurationStatus.Completed;
        }

        protected override async Task<IEnumerable<ReleaseInfo>> PerformQuery(TorznabQuery query)
        {
            var releases = new List<ReleaseInfo>();
            var qc = new List<KeyValuePair<string, string>> { };
            if (query.Limit > 0 && query.Offset > 0)
            {
                var page = query.Offset / 50 + 1;
                qc.Add("tpage", page.ToString());
            }

            foreach (var cat in MapTorznabCapsToTrackers(query))
            {
                qc.Add("cat", cat);
            }

            if (query.IsImdbQuery)
            {
                qc.Add("imdb_id", query.ImdbIDShort);
            }
            else
            {
                qc.Add("search", query.GetQueryString());
            }

            var searchUrl = ApiUrl + "torrents?" + qc.GetQueryString();
            var response = await RequestWithCookiesAndRetryAsync(searchUrl, headers: GetAuthorizationHeaders());
            if (response.Status == HttpStatusCode.Unauthorized)
                throw new Exception("401 Unauthorized");

            if ((int)response.Status == 409)
                throw new TooManyRequestsException("Rate limited", response);

            try
            {
                var result = JsonConvert.DeserializeObject<PolishTrackerResponse>(response.ContentString);

                foreach (var resultTorrent in result.torrents)
                {
                    var torrentName = resultTorrent.name;
                    if (((ConfigurationData.BoolConfigurationItem)configData.GetDynamic("multilang")).Value)
                    {
                        torrentName = Regex.Replace(
                            torrentName,
                            @"(?i)\b(MULTI(?!.*(?:POLISH|ENGLISH|\bPL\b)))\b", ((ConfigurationData.SingleSelectConfigurationItem)configData.GetDynamic("multilanguage")).Value,
                            RegexOptions.None
                            );
                        torrentName = Regex.Replace(
                            torrentName,
                            @"(?i)\b(pl)\b",
                            "POLISH",
                            RegexOptions.None
                            );
                    }
                    var release = new ReleaseInfo()
                    {
                        Title = torrentName,
                        Category = MapTrackerCatToNewznab(resultTorrent.category.ToString()),
                        Details = new Uri($"{SiteLink}/torrents/{resultTorrent.id}"),
                        Imdb = long.TryParse(resultTorrent.imdb_id, out var imdb) ? imdb : null,
                        Genres = ValidateTags(resultTorrent.tags),
                        Description = resultTorrent.tags != null ? string.Join(",", resultTorrent.tags) : null,
                        Seeders = resultTorrent.seeders,
                        Peers = resultTorrent.seeders + resultTorrent.leechers,
                        Grabs = resultTorrent.completed,
                        PublishDate = DateTime.Parse(resultTorrent.added),
                        Size = resultTorrent.size,
                        Link = new Uri($"{ApiUrl}download/{resultTorrent.id}"),
                    };
                    releases.Add(release);
                }
            }
            catch (Exception ex)
            {
                OnParseError(response.ContentString, ex);
            }

            return releases;
        }

        public override async Task<byte[]> Download(Uri link)
        {
            var response = await base.RequestWithCookiesAsync(
                link.ToString(), null, RequestType.GET, headers: GetAuthorizationHeaders());
            var content = response.ContentBytes;
            return content;
        }

        private Dictionary<string, string> GetAuthorizationHeaders() => new() { { "API-Key", configData.Key.Value } };

        private static string[] ValidateTags(string[] tags)
        {
            if (tags == null || !tags.Any())
            {
                return Array.Empty<string>();
            }
            var ValidList = new string[] { "animation", "comedy", "family", "strategy", "action", "adventure", "indie", "rpg", "simulation", "early", "crime", "thriller", "drama", "rock", "fantasy", "sci-fi", "horror", "pop", "war", "mystery", "oldies", "hardcore", "sport", "biography", "music", "rap", "romance", "dance", "hip-hop", "house", "punk_rock", "disco", "casual", "bass", "history", "racing", "metal", "electronic", "alternative", "funk", "short", "classical", "acoustic", "soundtrack", "punk", "ambient", "talk-show", "sports", "reggae", "documentary", "progressive_rock", "other", "western", "dance_hall", "trance", "folk", "classic_rock", "jazz", "hard rock", "trip-hop", "r&b", "blues", "musical", "club", "techno", "cabaret", "black_metal", "easy_listening", "goa", "free", "massively", "reality-tv", "grunge", "synthpop", "ballad", "top_40", "news", "industrial", "psychedelic_rock", "heavy_metal", "beat", "alternative rock", "drum_&_bass", "film-noir", "rock_&_roll", "death_metal", "lo-fi", "country", "instrumental_pop", "game-show", "soul", "retro", "noise", "latin", "design", "education", "software", "utilities", "pop-folk", "instrumental", "game", "acid_jazz", "acid", "gothic_rock", "fusion", "darkwave", "meditative", "crossover", "thrash_metal", "new_wave", "opera", "ethnic", "instrumental_rock", "new_age", "gangsta", "speech", "gothic", "gospel", "symphonic_rock", "ska", "jpop", "avantgarde", "tango", "vocal", "folk-rock", "celtic" };

            return ValidList.Intersect(tags.Select(t => t.ToLower())).ToArray();
        }
    }

    public class PolishTrackerResponse
    {
        public int count { get; set; }
        public Torrent[] torrents { get; set; }
    }

    public class Torrent
    {
        public int id { get; set; }
        public string name { get; set; }
        public long size { get; set; }
        public int category { get; set; }
        public string added { get; set; }
        public int seeders { get; set; }
        public int leechers { get; set; }
        public int completed { get; set; }
        public string imdb_id { get; set; }
        public string[] tags { get; set; }
    }
}
