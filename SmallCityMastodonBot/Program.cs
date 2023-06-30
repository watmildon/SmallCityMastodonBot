
using Mastodon.Api;
using Mastonet;
using Newtonsoft.Json;
using overpass_parser;
using System.Text;

namespace SmallCityMastodonBot
{
    public class Program
    {
        public static readonly string userAgent = "smalltownsusa/0.1";
        public static readonly int BUILDING_COUNT_MAXIMUM = 10;
        static void Main(string[] args)
        {            
            var botConfigInfo = JsonConvert.DeserializeObject<BotConfigFile>(File.ReadAllText("SmallCityBotConfig.json"));

            using (StreamWriter logger = new StreamWriter("smallbot.log"))
            {
                foreach (var bot in botConfigInfo.botInfo)
                {
                    try
                    {
                        GeneratePost(args, logger, bot);
                    }
                    catch (Exception ex)
                    {
                        logger.WriteLine(ex.ToString());
                    }
                }
            }
        }

        private static void GeneratePost(string[] args, StreamWriter logger, Botinfo bot)
        {
            string apiToken = args[0];

            string allText = System.IO.File.ReadAllText(bot.townFile);

            HttpClient httpClient = new();
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
                    int count = queryBuilder.SendCountQuery(queryBuilder.CreateCountQuery(pickedTown.lat, pickedTown.lon, query.featureTag,query.radiusInMeters));

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
                string state = GetState(pickedTown.lat, pickedTown.lon, httpClient).Result;

                StringBuilder postContent = new StringBuilder();
                postContent.Append($"{pickedTown.tags.name}, {state} {bot.postText.greetingText}\r\n\r\n{bot.postText.populationText}: {pickedTown.tags.population}\r\n");
                
                foreach(var postText in queryResultPostText) 
                {
                    postContent.AppendLine(postText);
                }
                
                postContent.Append($"\r\n{bot.postText.mapLinkText}: {osmLink}\r\n#OpenStreetMap");
                Console.WriteLine(postContent.ToString());

                logger.WriteLine("POST TEXT GENERATED:");
                logger.WriteLine(postContent.ToString());

                if (apiToken != "12345") // skip posting if we are running local
                {
                    var tasks = PostTown(httpClient, postContent.ToString(), apiToken);
                    tasks.Wait();
                }
                posted = true;
                logger.WriteLine($"INFO - TOWNS SEARCHED: {townsSearched}");
            }
        }

        private static async Task PostTown(HttpClient client, string postContent, string token)
        {
            var domain = "en.osm.town";
            var mastodonClient = new MastodonClient(domain, token, client);
            var result = await mastodonClient.PublishStatus(postContent, language: "en");
        }

        private async static Task<string> GetState(double lat, double lon, HttpClient client)
        {
            var url = $"https://nominatim.openstreetmap.org/reverse?format=json&lat={lat}&lon={lon}&zoom=5";
            var msg = new HttpRequestMessage(HttpMethod.Get, url);
            msg.Headers.Add("User-Agent", userAgent);
            var res = await client.SendAsync(msg);
            var content = await res.Content.ReadAsStringAsync();

            var geoCodeResult = JsonConvert.DeserializeObject<ReverseGeocodeResult>(content);

            return geoCodeResult.address.state;
        }


    }
}