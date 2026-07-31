using Mastonet.Entities;
using Mastonet;
using Newtonsoft.Json;
using overpass_parser;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Numerics;
using System.Text;

namespace SmallCityMastodonBot
{
    public class Program
    {
        public static readonly string userAgent = "smalltownsusa/0.1";
        public static readonly int BUILDING_COUNT_MAXIMUM = 10;
        public static bool postTown = false;
        public static bool postReplies = false;
        public static bool postMonthly = false;
        public static string targetBotName = "";
        public static string apiKey = "";
        static void Main(string[] args)
        {
            if (args.Length == 0)
            {
                Console.WriteLine("USAGE: SmallCityMastodonBot apiKey [/postTown] [/postReplies] [/postMonthly]");
                return;
            }

            ParseArgs(args);

            foreach (var file in Directory.GetFiles(Directory.GetCurrentDirectory(), "*.png"))
            {
                if (!file.Contains("OSM_copyright.png"))
                {
                    Console.WriteLine($"Deleting {file}");
                    File.Delete(file);
                }
            }

            var botConfigInfo = JsonConvert.DeserializeObject<BotConfigFile>(File.ReadAllText("SmallCityBotConfig.json"))
                ?? throw new InvalidOperationException("Failed to deserialize SmallCityBotConfig.json");
            HttpClient httpClient = new HttpClient()
            {
                DefaultRequestHeaders =
                {
                    CacheControl = CacheControlHeaderValue.Parse("no-cache, no-store"),
                    Pragma = { NameValueHeaderValue.Parse("no-cache")}
                }
            };

            var productValue = new ProductInfoHeaderValue("SmallTownUSABot", "0.1");
            var commentValue = new ProductInfoHeaderValue("(https://en.osm.town/@SmallTownUSA)");

            httpClient.DefaultRequestHeaders.UserAgent.Add(productValue);
            httpClient.DefaultRequestHeaders.UserAgent.Add(commentValue);
            httpClient.DefaultRequestHeaders.Referrer = new Uri("https://www.openstreetmap.org/");

            // Tile downloads use a separate client: the OSMF tile usage policy forbids sending
            // no-cache headers, which httpClient sets by default for API freshness.
            HttpClient tileClient = new HttpClient();
            tileClient.DefaultRequestHeaders.UserAgent.Add(productValue);
            tileClient.DefaultRequestHeaders.UserAgent.Add(commentValue);
            tileClient.DefaultRequestHeaders.Referrer = new Uri("https://www.openstreetmap.org/");

            bool botFound = false;
            foreach (var bot in botConfigInfo.botInfo)
            {
                if (bot.botName == targetBotName)
                {
                    botFound = true;
                    Console.WriteLine($"INFO - Running {bot.botName}");
                    try
                    {
                        if (postTown)
                        {
                            Console.WriteLine("Posting Town");
                            GeneratePost(apiKey, bot, httpClient, tileClient);
                        }
                        if (postReplies)
                        {
                            var task = ReplyToMappedItPosts(httpClient, tileClient, apiKey);
                            task.Wait();
                        }
                        if (postMonthly)
                        {
                            Console.WriteLine("Posting monthly retrospective");
                            MonthlyRetrospective.Run(httpClient, tileClient, apiKey, bot).Wait();
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine(ex.ToString());
                    }
                }
            }

            if (!botFound)
            {
                Console.WriteLine($"ERROR: Bot '{targetBotName}' not found in SmallCityBotConfig.json");
                Console.WriteLine($"Available bots: {string.Join(", ", botConfigInfo.botInfo.Select(b => b.botName))}");
            }
        }        

        private static void ParseArgs(string[] args)
        {
            try
            {
                apiKey = args[0];
                targetBotName = args[1];
                foreach (var arg in args)
                {
                    if (arg.ToLowerInvariant().Contains("posttown"))
                    {
                        Console.WriteLine("Set to post town");
                        postTown = true;
                    }
                    else if (arg.ToLowerInvariant().Contains("postreplies"))
                    {
                        Console.WriteLine("Set to post replies");
                        postReplies = true;
                    }
                    else if (arg.ToLowerInvariant().Contains("postmonthly"))
                    {
                        Console.WriteLine("Set to post monthly retrospective");
                        postMonthly = true;
                    }
                }
            }
            catch(Exception ex)
            {
                Console.WriteLine(ex.ToString());
                Console.WriteLine();
                Console.WriteLine("USAGE: SmallCityMastodonBot apiKey botName [/postTown] [/postReplies] [/postMonthly]");
            }
        }

        private static void GeneratePost(string apiToken, Botinfo bot, HttpClient httpClient, HttpClient tileClient)
        {
            string allText = System.IO.File.ReadAllText(bot.townFile);

            TownsData2 data = JsonConvert.DeserializeObject<TownsData2>(allText)
                ?? throw new InvalidOperationException($"Failed to deserialize {bot.townFile}");
            Random rnd = new Random(Guid.NewGuid().GetHashCode());
            OverpassQueryBuilder queryBuilder = new OverpassQueryBuilder(httpClient);

            bool posted = false;
            int townsSearched = 0;

            Console.WriteLine("Begin town search");

            while (!posted)
            {
                townsSearched++;
                var pickedTown = data.elements[rnd.Next(data.elements.Length)];
                if (pickedTown.tags.population == "0") // skip ghost towns for now, too many old rail stops as place=locality
                    continue;

                Console.WriteLine($"Picked town: {pickedTown.id} {pickedTown.tags.name}");

                List<string> queryResultPostText = new List<string>();

                bool skipTown = false;

                Console.WriteLine("Begin overpass querying");
                foreach (var query in bot.overpassQuery)
                {
                    int count = queryBuilder.SendCountQuery(queryBuilder.CreateCountQuery(pickedTown.lat, pickedTown.lon, query.featureTag, query.radiusInMeters));

                    if (query.countMaximum != -1)
                    {
                        if (count > query.countMaximum)
                        {
                            Console.WriteLine($"{query.featureTag} returned {count}. Max value {query.countMaximum}");
                            skipTown = true;
                            break;
                        }
                    }

                    queryResultPostText.Add($"{query.message}: {count}");
                }

                if (skipTown)
                {
                    Console.WriteLine("Skipping town");
                    continue; // town was over one of the maximums
                }                

                string osmLink = $"https://www.openstreetmap.org/#map=16/{pickedTown.lat}/{pickedTown.lon}";
                string state = "";

                try
                {
                    Console.WriteLine($"Nominatim state lookup for: {osmLink}");
                    state = GetStateNameFromNominatim(pickedTown.lat, pickedTown.lon, httpClient).Result;
                }
                catch
                {
                    // very occasionally this nominatim lookup fails, we will try again unless we've been looping on it
                    if (townsSearched >= 100)
                    {
                        Console.WriteLine("Aborting town lookup");
                        break;
                    }
                    continue;
                }

                StringBuilder postContent = new StringBuilder();
                postContent.Append($"{pickedTown.tags.name}, {state} {bot.postText.greetingText}\r\n\r\n{bot.postText.populationText}: {pickedTown.tags.population}\r\n");

                foreach (var postText in queryResultPostText)
                {
                    postContent.AppendLine(postText);
                }

                postContent.Append($"\r\n{bot.postText.mapLinkText}: {osmLink}\r\n#OpenStreetMap");
                Console.WriteLine(postContent.ToString());

                Console.WriteLine("POST TEXT GENERATED:");
                Console.WriteLine(postContent.ToString());

                Console.WriteLine("Begin image generation");
                // generate image from tiles
                string imagePath = $"{pickedTown.tags.name}_TownImage.png";
                var taskReturn = GenerateImageFromOSMTiles(tileClient, 16, pickedTown.lat, pickedTown.lon, imagePath);
                taskReturn.Wait();
                var imageBytes = File.ReadAllBytes(imagePath); //todo, get this from a memory stream from the call above

                if (apiToken != "12345")  // skip posting if we are running with the dummy key
                {
                    Console.WriteLine("Posting town to mastodon account");
                    var tasks = PostTown(httpClient, postContent.ToString(), apiToken, imageBytes, imagePath, "Map image of the town showing the status as of the time of this posting");
                    tasks.Wait();
                }
                else
                {
                    Console.WriteLine("Not posting town.");
                }
            
                posted = true;
            }

            Console.WriteLine($"INFO - TOWNS SEARCHED: {townsSearched}");
        }

        private static async Task PostTown(HttpClient client, string postContent, string token, byte[] image, string fileName, string altText)
        {
            var domain = "en.osm.town";
            var mastodonClient = new MastodonClient(domain, token, client);
            var attachment = await mastodonClient.UploadMedia(new MemoryStream(image), fileName, altText);
            var mediaIds = new List<string>() { attachment.Id };
            var result = await mastodonClient.PublishStatus(postContent, mediaIds: mediaIds, language: "en");
        }

        private async static Task<string> GetStateNameFromNominatim(double lat, double lon, HttpClient client)
        {
            var url = $"https://nominatim.openstreetmap.org/reverse?format=json&lat={lat}&lon={lon}&zoom=5";
            var msg = new HttpRequestMessage(HttpMethod.Get, url);
            msg.Headers.Add("User-Agent", userAgent);
            var res = await client.SendAsync(msg);
            var content = await res.Content.ReadAsStringAsync();

            var geoCodeResult = JsonConvert.DeserializeObject<ReverseGeocodeResult>(content)
                ?? throw new InvalidOperationException("Failed to deserialize Nominatim response");

            return geoCodeResult.address.state;
        }

        private static PostContent ParseStatus(Status post)
        {
            var content = new PostContent();
            string postContentString = post.Content;

            content.CityName = postContentString.Split(",")[0].Substring(3);
            content.StateName = postContentString.Split(",")[1].Split(" ")[1];
            content.Population = int.Parse(postContentString.Split(":")[1].Split("<")[0].Trim());
            content.BuildingCount = int.Parse(postContentString.Split(":")[2].Split("<")[0].Trim());
            content.RoadsToReview = int.Parse(postContentString.Split(":")[3].Split("<")[0].Trim());
            content.Lattitude = double.Parse(postContentString.Substring(postContentString.IndexOf("#map=16")+8).Split("/")[0]);
            content.Longitude = double.Parse(postContentString.Substring(postContentString.IndexOf("#map=16")+8).Split("/")[1].Split("\"")[0]);

            return content;
        }

        private const int NUM_TILES_WIDE = 3; // Adjust as needed

        /// <summary>
        /// URLs of the tile grid centered on the coordinate, in the placement order used by
        /// <see cref="GenerateImageFromOSMTiles"/> (columns left to right, rows top to bottom).
        /// </summary>
        internal static List<string> GetTileUrls(int zoom, double lat, double lon)
        {
            const int TILE_COUNT_OFFSET = NUM_TILES_WIDE / 2;

            // Convert lat/lon to tile coordinates
            float x = (float)((lon + 180.0) / 360.0 * (1 << zoom));
            float y = (float)((1.0 - Math.Log(Math.Tan(lat * Math.PI / 180.0) +
                        1.0 / Math.Cos(lat * Math.PI / 180.0)) / Math.PI) / 2.0 * (1 << zoom));

            var urls = new List<string>();
            for (int i = 0; i < NUM_TILES_WIDE; i++)
            {
                for (int j = 0; j < NUM_TILES_WIDE; j++)
                {
                    int tileX = (int)Math.Floor(x + i - TILE_COUNT_OFFSET);
                    int tileY = (int)Math.Floor(y + j - TILE_COUNT_OFFSET);
                    urls.Add($"https://tile.openstreetmap.org/{zoom}/{tileX}/{tileY}.png");
                }
            }

            return urls;
        }

        internal static async Task GenerateImageFromOSMTiles(HttpClient httpClient, int zoom, double lat, double lon, string outputFilePath)
        {
            const int TILE_SIZE = 256;

            var tileUrls = GetTileUrls(zoom, lat, lon);

            // Create the final stitched image
            using var resultImage = new Image<Rgba32>(NUM_TILES_WIDE * TILE_SIZE, NUM_TILES_WIDE * TILE_SIZE);

            for (int i = 0; i < NUM_TILES_WIDE; i++)
            {
                for (int j = 0; j < NUM_TILES_WIDE; j++)
                {
                    string url = tileUrls[i * NUM_TILES_WIDE + j];

                    Debug.WriteLine(url);

                    byte[] imageBytes = await httpClient.GetByteArrayAsync(url);
                    using var tileImage = Image.Load<Rgba32>(imageBytes);

                    int destX = i * TILE_SIZE;
                    int destY = j * TILE_SIZE;

                    resultImage.Mutate(ctx => ctx.DrawImage(tileImage, new Point(destX, destY), 1f));
                }
            }

            // Overlay copyright image
            using var copyrightImage = Image.Load<Rgba32>("OSM_copyright.png");
            int copyrightX = resultImage.Width - copyrightImage.Width;
            int copyrightY = resultImage.Height - copyrightImage.Height;

            resultImage.Mutate(ctx => ctx.DrawImage(copyrightImage, new Point(copyrightX, copyrightY), 1f));

            // Save the final image
            Console.WriteLine($"Saving image to {outputFilePath}");
            await resultImage.SaveAsPngAsync(outputFilePath);
        }


        private static async Task ReplyToMappedItPosts(HttpClient client, HttpClient tileClient, string token)
        {
            var domain = "en.osm.town";
            var mastodonClient = new MastodonClient(domain, token, client);
            var botAccount = await mastodonClient.GetCurrentUser();

            // Read last-processed notification ID from Mastodon Markers API
            string? sinceId = null;
            try
            {
                var markers = await mastodonClient.GetMarkers(notifications: true);
                if (markers?.Notifications != null && markers.Notifications.LastReadId > 0)
                {
                    sinceId = markers.Notifications.LastReadId.ToString();
                    Console.WriteLine($"Resuming from notification marker: {sinceId}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"WARNING: Could not read notification marker: {ex.Message}");
                Console.WriteLine("Will process recent notifications without since_id filter");
            }

            // Fetch mention-only notifications since last processed
            var excludeTypes = NotificationType.Follow
                             | NotificationType.Favourite
                             | NotificationType.Reblog
                             | NotificationType.Poll
                             | NotificationType.FollowRequest;

            var options = new ArrayOptions { SinceId = sinceId };
            string? highestNotificationId = null;

            var allNotifications = new List<Notification>();
            var notifications = await mastodonClient.GetNotifications(options, excludeTypes);
            while (notifications.Count > 0)
            {
                allNotifications.AddRange(notifications);

                if (notifications.NextPageMaxId == null)
                    break;

                options = new ArrayOptions
                {
                    SinceId = sinceId,
                    MaxId = notifications.NextPageMaxId
                };
                notifications = await mastodonClient.GetNotifications(options, excludeTypes);
            }

            Console.WriteLine($"Found {allNotifications.Count} new mention notifications");

            // Track highest notification ID for saving marker later
            // Notifications come most-recent-first, so first one has the highest ID
            if (allNotifications.Count > 0)
            {
                highestNotificationId = allNotifications[0].Id;
            }

            // Process each mention notification
            foreach (var notification in allNotifications)
            {
                if (notification.Type != "mention" || notification.Status == null)
                    continue;

                var replyStatus = notification.Status;
                Console.WriteLine($"Processing notification {notification.Id}: {replyStatus.Url}");

                // Check if the reply contains "mapped it!"
                if (!replyStatus.Content.Contains(" mapped it!"))
                {
                    Console.WriteLine($"\tNot a 'mapped it!' reply, skipping");
                    continue;
                }

                // Check if this is a reply to one of the bot's posts
                if (replyStatus.InReplyToAccountId != botAccount.Id)
                {
                    Console.WriteLine($"\tNot a reply to bot, skipping");
                    continue;
                }

                if (string.IsNullOrEmpty(replyStatus.InReplyToId))
                {
                    Console.WriteLine($"\tNo parent status ID, skipping");
                    continue;
                }

                // Fetch the original bot post to parse city/coordinates
                Status originalPost;
                try
                {
                    originalPost = await mastodonClient.GetStatus(replyStatus.InReplyToId);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"\tFailed to fetch parent status {replyStatus.InReplyToId}: {ex.Message}");
                    continue;
                }

                PostContent pc;
                try
                {
                    pc = ParseStatus(originalPost);
                }
                catch
                {
                    Console.WriteLine($"\tUnparsable parent status: {originalPost.Url}");
                    continue;
                }

                // Check if bot already replied to this "mapped it!" post
                bool alreadyReplied = false;
                if (replyStatus.RepliesCount > 0)
                {
                    try
                    {
                        var replyContext = await mastodonClient.GetStatusContext(replyStatus.Id);
                        foreach (var subReply in replyContext.Descendants)
                        {
                            if (subReply.Account.Id == botAccount.Id)
                            {
                                alreadyReplied = true;
                                break;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"\tFailed to check reply context: {ex.Message}");
                        alreadyReplied = true; // err on the side of not double-replying
                    }
                }

                if (alreadyReplied)
                {
                    Console.WriteLine($"\tAlready replied, skipping");
                    continue;
                }

                Console.WriteLine($"\tPosting mapping reply for {pc.CityName}");
                await PostMappingReply(client, tileClient, token, replyStatus, pc);
            }

            // Save the highest notification ID to the marker
            if (highestNotificationId != null)
            {
                try
                {
                    await mastodonClient.SetMarkers(notificationLastReadId: highestNotificationId);
                    Console.WriteLine($"Saved notification marker: {highestNotificationId}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"WARNING: Failed to save notification marker: {ex.Message}");
                }
            }
        }

        private static async Task PostMappingReply(HttpClient httpClient, HttpClient tileClient, String token, Status mappedItPost, PostContent originalContent)
        {
            var domain = "en.osm.town";
            var mastodonClient = new MastodonClient(domain, token, httpClient);

            // pull new stats to see if work has happened, only respond if it's different
            OverpassQueryBuilder queryBuilder = new OverpassQueryBuilder(httpClient);
            int buildingCount = queryBuilder.SendCountQuery(queryBuilder.CreateCountQuery(originalContent.Lattitude, originalContent.Longitude, "building", "800"));
            int roadwayCount = queryBuilder.SendCountQuery(queryBuilder.CreateCountQuery(originalContent.Lattitude, originalContent.Longitude, "tiger:reviewed", "800"));
            int landuseCout = queryBuilder.SendCountQuery(queryBuilder.CreateCountQuery(originalContent.Lattitude, originalContent.Longitude, "landuse", "800"));
            
            string thankYouText = $"@{mappedItPost.Account.AccountName} thanks for helping out!\r\n\r\n{originalContent.CityName} now has {buildingCount - originalContent.BuildingCount} more buildings and {roadwayCount} roads to review.\r\n\r\n#SmallTownUSAUpdate";

            Console.WriteLine($"POST TEXT: {thankYouText}");
            string imagePath = $"{originalContent.CityName}_TownImage_reply.png";

            // if two folks ask for the same town, we don't need to generate the image twice
            if (!File.Exists(imagePath))
            {
                await GenerateImageFromOSMTiles(tileClient, 16, originalContent.Lattitude, originalContent.Longitude, imagePath);
                Console.WriteLine("Generated image");
            }

            var attachment = await mastodonClient.UploadMedia(new MemoryStream(File.ReadAllBytes(imagePath)), imagePath, "Map image of the town showing the status as of the time of this posting.");
            var mediaIds = new List<string>() { attachment.Id };

            await mastodonClient.PublishStatus(thankYouText, replyStatusId: mappedItPost.Id, mediaIds: mediaIds, visibility: Visibility.Unlisted);
        }        
    }
    public struct PostContent
    {
        public int Population;
        public int BuildingCount;
        public int RoadsToReview;
        public double Lattitude;
        public double Longitude;
        public string CityName;
        public string StateName;
    }
}