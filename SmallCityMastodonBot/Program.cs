using Newtonsoft.Json;
using overpass_parser;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Net.Http.Headers;
using Mastonet;
using System.Text;
using Mastonet.Entities;

namespace SmallCityMastodonBot
{
    public class Program
    {
        public static readonly string userAgent = "smalltownsusa/0.1";
        public static readonly int BUILDING_COUNT_MAXIMUM = 10;
        static void Main(string[] args)
        {
            var botConfigInfo = JsonConvert.DeserializeObject<BotConfigFile>(File.ReadAllText("SmallCityBotConfig.json"));
            HttpClient httpClient = new();

            using (StreamWriter logger = new StreamWriter("smallbot.log"))
            {
                foreach (var bot in botConfigInfo.botInfo)
                {
                    try
                    {
                        GeneratePost(args[0], logger, bot, httpClient);
                        var task = ReplyToMappedItPosts(httpClient, args[0]);
                        task.Wait();
                    }
                    catch (Exception ex)
                    {
                        logger.WriteLine(ex.ToString());
                    }
                }
            }
        }

        private static void GeneratePost(string apiToken, StreamWriter logger, Botinfo bot, HttpClient httpClient)
        {
            string allText = System.IO.File.ReadAllText(bot.townFile);


            TownsData2 data = JsonConvert.DeserializeObject<TownsData2>(allText);
            Random rnd = new Random(Guid.NewGuid().GetHashCode());
            OverpassQueryBuilder queryBuilder = new OverpassQueryBuilder(httpClient);

            bool posted = false;
            int townsSearched = 0;
            while (!posted)
            {
                townsSearched++;
                var pickedTown = data.elements[rnd.Next(data.elements.Length)];
                if (pickedTown.tags.population == "0") // skip ghost towns for now, too many old rail stops as place=locality
                    continue;

                List<string> queryResultPostText = new List<string>();

                bool skipTown = false;
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

                if (skipTown) continue; // town was over one of the maximums

                string osmLink = $"https://www.openstreetmap.org/#map=16/{pickedTown.lat}/{pickedTown.lon}";
                string state = "";

                try
                {
                    logger.WriteLine($"Nominatim state lookup for: {osmLink}");
                    state = GetStateNameFromNominatim(pickedTown.lat, pickedTown.lon, httpClient).Result;
                }
                catch
                {
                    // very occasionally this nominatim lookup fails, we will try again unless we've been looping on it
                    if (townsSearched >= 100)
                        break;
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

                logger.WriteLine("POST TEXT GENERATED:");
                logger.WriteLine(postContent.ToString());

                // generate image from tiles
                string imagePath = $"{pickedTown.tags.name}_TownImage.png";
                GenerateImageFromOSMTiles(httpClient, 16, pickedTown.lat, pickedTown.lon, imagePath);
                var imageBytes = File.ReadAllBytes(imagePath); //todo, get this from a memory stream from the call above

                if (apiToken != "12345") // skip posting if we are running local
                {
                    var tasks = PostTown(httpClient, postContent.ToString(), apiToken, imageBytes, imagePath, "Map image of the town showing the status as of the time of this posting");
                    tasks.Wait();
                }
                posted = true;
                logger.WriteLine($"INFO - TOWNS SEARCHED: {townsSearched}");
            }
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

            var geoCodeResult = JsonConvert.DeserializeObject<ReverseGeocodeResult>(content);

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

        static readonly int NUM_TILES_WIDE = 7;
        static readonly int TILE_COUNT_OFFSET = 3; //used to center the town in the downloaded area
        private static void GenerateImageFromOSMTiles(HttpClient httpClient, int zoom, double lat, double lon, string outputFilePath)
        {
            var p = new PointF();

            p.X = (float)((lon + 180.0) / 360.0 * (1 << zoom));
            p.Y = (float)((1.0 - Math.Log(Math.Tan(lat * Math.PI / 180.0) +
                1.0 / Math.Cos(lat * Math.PI / 180.0)) / Math.PI) / 2.0 * (1 << zoom));

            System.Drawing.Image[,] images = new System.Drawing.Image[NUM_TILES_WIDE, NUM_TILES_WIDE];

            for (int i = 0; i < NUM_TILES_WIDE; i++)
            {
                for (int j = 0; j < NUM_TILES_WIDE; j++)
                {
                    var productValue = new ProductInfoHeaderValue("SmallTownUSABot", "0.1");
                    var commentValue = new ProductInfoHeaderValue("(https://en.osm.town/@SmallTownUSA)");

                    httpClient.DefaultRequestHeaders.UserAgent.Add(productValue);
                    httpClient.DefaultRequestHeaders.UserAgent.Add(commentValue);
                    string url = $"https://tile.openstreetmap.org/{zoom}/{Math.Floor(p.X+i-TILE_COUNT_OFFSET)}/{Math.Floor(p.Y+j-TILE_COUNT_OFFSET)}.png";
                    Debug.WriteLine(url);
                    var imageTask = httpClient.GetByteArrayAsync(url);
                    Task.WaitAll(imageTask);
                    var imageContent = imageTask.Result;

                    using (MemoryStream mem = new MemoryStream(imageContent))
                    {
                        var yourImage = System.Drawing.Image.FromStream(mem);
                        {
                            images[i, j] = yourImage;
                        }
                    }
                }
            }

            using (Bitmap result = new Bitmap(NUM_TILES_WIDE * 256, NUM_TILES_WIDE * 256))
            {
                for (int x = 0; x < NUM_TILES_WIDE; x++)
                    for (int y = 0; y < NUM_TILES_WIDE; y++)
                        using (Graphics g = Graphics.FromImage(result))
                        {
                            var img = images[x, y];
                            g.DrawImage(img, x * 256, y * 256, 256, 256);
                        }
                result.Save(outputFilePath, ImageFormat.Png);
            }
        }

        private static async Task ReplyToMappedItPosts(HttpClient client, string token)
        {
            var domain = "en.osm.town";
            var mastodonClient = new MastodonClient(domain, token, client);
            var botAccount = await mastodonClient.GetCurrentUser();
            var options = new ArrayOptions();

            var posts = await mastodonClient.GetHomeTimeline(options);
            while (posts.Count > 0)
            {
                foreach (var post in posts)
                {
                    Console.WriteLine(post.Url);

                    if (post.Account.Id == botAccount.Id)
                    {
                        if (post.RepliesCount == 0)
                            continue;

                        var context = await mastodonClient.GetStatusContext(post.Id);

                        // are there any replies?
                        if (context.Descendants.Count() > 0)
                        {
                            foreach (var reply in context.Descendants)
                            {
                                // check all replies for a mapped it post.
                                if (reply.Content.Contains("I mapped it!"))
                                {
                                    Console.WriteLine($"\t{reply.Url}");

                                    bool alreadyReplied = false;

                                    // check to see if the bot has already replied
                                    if (reply.RepliesCount != 0)
                                    {
                                        var replyContext = await mastodonClient.GetStatusContext(reply.Id);
                                        foreach (var subReply in replyContext.Descendants)
                                        {
                                            if (post.Id == subReply.Id) // the first descendant is the original status message, skip
                                                continue;

                                            Console.WriteLine($"\t\t{subReply.Url}");

                                            if (subReply.Account.Id == botAccount.Id)
                                                alreadyReplied = true;
                                        }
                                    }

                                    if (!alreadyReplied)
                                    {
                                        await PostMappingReply(client, token, reply, ParseStatus(post));
                                    }
                                }
                            }
                        }
                    }
                }

                options.MaxId = posts.NextPageMaxId;
                posts = await mastodonClient.GetHomeTimeline(options);
            }
        }

        private static async Task PostMappingReply(HttpClient httpClient, String token, Status mappedItPost, PostContent originalContent)
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

            GenerateImageFromOSMTiles(httpClient, 16, originalContent.Lattitude, originalContent.Longitude, imagePath);
            Console.WriteLine("Generated image");

            var attachment = await mastodonClient.UploadMedia(new MemoryStream(File.ReadAllBytes(imagePath)), imagePath, "Map image of the town showing the status as of the time of this posting.");
            var mediaIds = new List<string>() { attachment.Id };

            await mastodonClient.PublishStatus(thankYouText, replyStatusId: mappedItPost.Id, mediaIds: mediaIds);
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