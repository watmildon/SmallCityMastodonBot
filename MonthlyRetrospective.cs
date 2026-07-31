using Mastonet;
using Newtonsoft.Json;
using overpass_parser;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace SmallCityMastodonBot
{
    /// <summary>
    /// Generates the monthly retrospective post. Run on the 10th of each month, it pulls the
    /// bot's own posts from the previous calendar month, measures how much mapping happened
    /// around each featured town since its post date, and publishes a summary with fresh map
    /// images of the most improved towns.
    /// </summary>
    public class MonthlyRetrospective
    {
        private const int MaxPostCharacters = 500; // en.osm.town status limit
        private const int TopTownCount = 3;
        private const int MaxStatusPages = 25;

        /// <summary>
        /// Wait between the priming tile fetch and the real one. The tile CDN serves a stale tile
        /// while it revalidates (stale-while-revalidate), and the render server re-renders dirty
        /// tiles on demand — the first fetch triggers both, the second collects the fresh tile.
        /// </summary>
        private static readonly TimeSpan TileRefreshDelay = TimeSpan.FromMinutes(3);

        private class StatusLite
        {
            [JsonProperty("id")] public string Id { get; set; } = "";
            [JsonProperty("created_at")] public DateTime CreatedAt { get; set; }
            [JsonProperty("content")] public string Content { get; set; } = "";
        }

        private class TownActivity
        {
            public string Name = "";
            public string State = "";
            public double Lat;
            public double Lon;
            public DateTime PostedAt;
            public int BuildingsAtPost;
            public int BuildingsNow;
            public int TigerAtPost = -1;
            public int TigerNow = -1;
            public OverpassDiffStats? Diff;

            public int BuildingsAdded => Math.Max(0, BuildingsNow - BuildingsAtPost);
            public int TigerReviewed => TigerAtPost >= 0 && TigerNow >= 0 ? Math.Max(0, TigerAtPost - TigerNow) : 0;
            public bool WasMapped => BuildingsAdded > 0 || (Diff?.TotalChanges ?? 0) > 0;
        }

        public static async Task Run(HttpClient apiClient, HttpClient tileClient, string apiKey, Botinfo bot)
        {
            var monthly = bot.monthlyPostText;
            if (monthly == null)
            {
                Console.WriteLine($"INFO - {bot.botName} has no monthlyPostText config, skipping monthly post");
                return;
            }

            var nowUtc = DateTime.UtcNow;
            var monthStart = new DateTime(nowUtc.Year, nowUtc.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(-1);
            var monthEnd = monthStart.AddMonths(1);
            var culture = new CultureInfo(monthly.culture);
            string monthName = culture.DateTimeFormat.GetMonthName(monthStart.Month);

            Console.WriteLine($"Building monthly retrospective for {monthStart:yyyy-MM}");

            var statuses = await FetchOwnStatuses(apiClient, bot, monthStart, monthEnd);
            Console.WriteLine($"Found {statuses.Count} own statuses in {monthStart:yyyy-MM}");

            var towns = new List<TownActivity>();
            foreach (var status in statuses)
            {
                var town = TryParseTownPost(status, bot);
                if (town != null)
                    towns.Add(town);
            }

            if (towns.Count == 0)
            {
                Console.WriteLine("No parsable town posts found for the month, skipping monthly post");
                return;
            }

            Console.WriteLine($"Parsed {towns.Count} town posts, measuring activity");

            var queryBuilder = new OverpassQueryBuilder(apiClient);
            var buildingQuery = bot.overpassQuery.First(q => q.featureTag == "building");
            var tigerQuery = bot.overpassQuery.FirstOrDefault(q => q.featureTag == "tiger:reviewed");

            foreach (var town in towns)
            {
                town.BuildingsNow = queryBuilder.SendCountQuery(
                    queryBuilder.CreateCountQuery(town.Lat, town.Lon, "building", buildingQuery.radiusInMeters));

                if (tigerQuery != null && town.TigerAtPost >= 0)
                {
                    town.TigerNow = queryBuilder.SendCountQuery(
                        queryBuilder.CreateCountQuery(town.Lat, town.Lon, "tiger:reviewed", tigerQuery.radiusInMeters));
                }

                try
                {
                    town.Diff = queryBuilder.GetDiffStats(
                        queryBuilder.CreateDiffQuery(town.Lat, town.Lon, buildingQuery.radiusInMeters, town.PostedAt));
                }
                catch (Exception ex)
                {
                    // Redacted by OverpassQueryBuilder; town still counts via building delta
                    Console.WriteLine($"WARNING: diff query failed for {town.Name}: {ex.Message}");
                }

                Console.WriteLine($"  {town.Name}: buildings {town.BuildingsAtPost} -> {town.BuildingsNow}, " +
                    $"changes={town.Diff?.TotalChanges ?? -1}, mappers={town.Diff?.Users.Count ?? -1}");
            }

            // Aggregate the month
            int mappedCount = towns.Count(t => t.WasMapped);
            int totalBuildings = towns.Sum(t => t.BuildingsAdded);
            int totalTigerReviewed = towns.Sum(t => t.TigerReviewed);
            bool haveDiffStats = towns.Any(t => t.Diff != null);
            var allUsers = new HashSet<string>();
            var allChangesets = new HashSet<string>();
            foreach (var town in towns.Where(t => t.Diff != null))
            {
                allUsers.UnionWith(town.Diff!.Users);
                allChangesets.UnionWith(town.Diff!.Changesets);
            }

            var topTowns = towns
                .Where(t => t.BuildingsAdded > 0)
                .OrderByDescending(t => t.BuildingsAdded)
                .Take(TopTownCount)
                .ToList();

            // Fetch each image twice: the first pass primes the CDN/render queue, the second
            // (after a delay) picks up the freshly rendered tiles.
            var imagePaths = new Dictionary<TownActivity, string>();
            foreach (var town in topTowns)
                await PrimeTiles(tileClient, town.Lat, town.Lon);

            if (topTowns.Count > 0)
            {
                Console.WriteLine($"Waiting {TileRefreshDelay.TotalSeconds:F0}s for tile re-render before final image fetch");
                await Task.Delay(TileRefreshDelay);
            }

            foreach (var town in topTowns)
            {
                string imagePath = $"{town.Name}_MonthlyRetro.png";
                await Program.GenerateImageFromOSMTiles(tileClient, 16, town.Lat, town.Lon, imagePath);
                imagePaths[town] = imagePath;
            }

            string postText = BuildPostText(monthly, culture, monthName, tigerQuery != null, towns.Count,
                mappedCount, totalBuildings, totalTigerReviewed, haveDiffStats, allChangesets.Count,
                allUsers.Count, topTowns);

            Console.WriteLine("MONTHLY POST TEXT GENERATED:");
            Console.WriteLine(postText);

            if (apiKey == "12345")
            {
                Console.WriteLine("Not posting monthly retrospective (test mode).");
                foreach (var town in topTowns)
                    Console.WriteLine($"IMAGE: {imagePaths[town]} ALT: {FormatAltText(monthly, town, monthName)}");
                return;
            }

            Console.WriteLine("Posting monthly retrospective to mastodon account");
            var mastodonClient = new MastodonClient(bot.botDomain, apiKey, apiClient);
            var mediaIds = new List<string>();
            foreach (var town in topTowns)
            {
                var attachment = await mastodonClient.UploadMedia(
                    new MemoryStream(File.ReadAllBytes(imagePaths[town])),
                    imagePaths[town],
                    FormatAltText(monthly, town, monthName));
                mediaIds.Add(attachment.Id);
            }

            await mastodonClient.PublishStatus(postText, mediaIds: mediaIds, language: monthly.language);
        }

        private static async Task<List<StatusLite>> FetchOwnStatuses(HttpClient apiClient, Botinfo bot, DateTime monthStart, DateTime monthEnd)
        {
            // The bot's own posts are public, so read them unauthenticated — this keeps test mode
            // (dummy API key) fully functional.
            string accountName = bot.botUrl.TrimStart('@').Split('@')[0];
            string lookupJson = await apiClient.GetStringAsync(
                $"https://{bot.botDomain}/api/v1/accounts/lookup?acct={accountName}");
            var account = JsonConvert.DeserializeAnonymousType(lookupJson, new { id = "" })
                ?? throw new InvalidOperationException($"Failed to look up account {accountName}");

            var statuses = new List<StatusLite>();
            string? maxId = null;

            for (int page = 0; page < MaxStatusPages; page++)
            {
                string url = $"https://{bot.botDomain}/api/v1/accounts/{account.id}/statuses" +
                    "?limit=40&exclude_replies=true&exclude_reblogs=true";
                if (maxId != null)
                    url += $"&max_id={maxId}";

                var batch = JsonConvert.DeserializeObject<List<StatusLite>>(await apiClient.GetStringAsync(url));
                if (batch == null || batch.Count == 0)
                    break;

                foreach (var status in batch)
                {
                    if (status.CreatedAt < monthStart)
                        return statuses; // statuses are newest-first, we've paged past the month

                    if (status.CreatedAt < monthEnd)
                        statuses.Add(status);
                }

                maxId = batch[^1].Id;
                await Task.Delay(300);
            }

            return statuses;
        }

        /// <summary>
        /// Extracts town facts from one of the bot's own daily posts. Returns null for statuses
        /// that are not daily town posts (e.g. a previous monthly retrospective).
        /// </summary>
        private static TownActivity? TryParseTownPost(StatusLite status, Botinfo bot)
        {
            string text = WebUtility.HtmlDecode(Regex.Replace(status.Content, "<[^>]+>", ""));

            int greetingIndex = text.IndexOf(bot.postText.greetingText);
            int mapIndex = text.IndexOf("#map=16/");
            if (greetingIndex < 0 || mapIndex < 0)
                return null;

            try
            {
                var town = new TownActivity { PostedAt = status.CreatedAt };

                int commaIndex = text.IndexOf(',');
                town.Name = text.Substring(0, commaIndex).Trim();
                town.State = text.Substring(commaIndex + 1, greetingIndex - commaIndex - 1).Trim();

                var coords = text.Substring(mapIndex + "#map=16/".Length).Split('/');
                town.Lat = double.Parse(coords[0], CultureInfo.InvariantCulture);
                town.Lon = double.Parse(Regex.Match(coords[1], @"-?\d+\.?\d*").Value, CultureInfo.InvariantCulture);

                foreach (var query in bot.overpassQuery)
                {
                    int count = ParseLabeledCount(text, query.message);
                    if (query.featureTag == "building")
                        town.BuildingsAtPost = count;
                    else if (query.featureTag == "tiger:reviewed")
                        town.TigerAtPost = count;
                }

                return town;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"WARNING: could not parse own status from {status.CreatedAt:yyyy-MM-dd}: {ex.Message}");
                return null;
            }
        }

        private static int ParseLabeledCount(string text, string label)
        {
            int labelIndex = text.IndexOf(label + ":");
            if (labelIndex < 0)
                throw new FormatException($"Label '{label}' not found in post");

            return int.Parse(Regex.Match(text.Substring(labelIndex + label.Length + 1), @"\d+").Value);
        }

        private static string BuildPostText(Monthlyposttext monthly, CultureInfo culture, string monthName,
            bool hasTigerQuery, int featuredCount, int mappedCount, int totalBuildings, int totalTigerReviewed,
            bool haveDiffStats, int changesetCount, int mapperCount, List<TownActivity> topTowns)
        {
            // If the text runs over the instance limit, drop "most improved" entries until it fits
            for (int starCount = topTowns.Count; ; starCount--)
            {
                var sb = new StringBuilder();
                sb.AppendLine(string.Format(culture, monthly.headerText, monthName));
                sb.AppendLine();
                sb.AppendLine(string.Format(culture, monthly.introText, monthName, featuredCount, mappedCount));
                sb.AppendLine();
                sb.AppendLine(string.Format(culture, monthly.buildingsLine, totalBuildings.ToString("N0", culture)));
                if (hasTigerQuery && monthly.roadsLine != null && totalTigerReviewed > 0)
                    sb.AppendLine(string.Format(culture, monthly.roadsLine, totalTigerReviewed.ToString("N0", culture)));
                if (haveDiffStats)
                    sb.AppendLine(string.Format(culture, monthly.changesetsLine, changesetCount.ToString("N0", culture), mapperCount.ToString("N0", culture)));

                if (starCount > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine(monthly.mostImprovedText);
                    foreach (var town in topTowns.Take(starCount))
                        sb.AppendLine(string.Format(culture, monthly.starLine, town.Name, town.State, town.BuildingsAdded));
                }

                sb.AppendLine();
                sb.AppendLine(monthly.thanksText);
                sb.AppendLine();
                sb.Append(monthly.hashtags);

                string text = sb.ToString();
                if (text.Length <= MaxPostCharacters || starCount == 0)
                {
                    if (text.Length > MaxPostCharacters)
                        Console.WriteLine($"WARNING: monthly post is {text.Length} characters, over the {MaxPostCharacters} limit");
                    else if (starCount < topTowns.Count)
                        Console.WriteLine($"INFO - dropped {topTowns.Count - starCount} 'most improved' lines to fit the character limit");

                    return text;
                }
            }
        }

        private static string FormatAltText(Monthlyposttext monthly, TownActivity town, string monthName)
        {
            return string.Format(monthly.imageAltText, town.Name, town.State, town.BuildingsAdded, monthName);
        }

        private static async Task PrimeTiles(HttpClient tileClient, double lat, double lon)
        {
            foreach (var url in Program.GetTileUrls(16, lat, lon))
            {
                try
                {
                    using var response = await tileClient.GetAsync(url);
                    response.EnsureSuccessStatusCode();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"WARNING: tile priming fetch failed: {ex.Message}");
                }

                await Task.Delay(250);
            }
        }
    }
}
