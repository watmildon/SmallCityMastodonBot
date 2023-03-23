
using Mastodon.Api;
using Newtonsoft.Json;
using overpass_parser;

namespace SmallCityMastodonBot
{
    public class Program
    {
        public static readonly string userAgent = "smalltownsusa/0.1";
        public static readonly int BUILDING_COUNT_MAXIMUM = 10;
        static void Main(string[] args)
        {
            using (StreamWriter sw = new StreamWriter("smallbot.log"))
            {
                try
                {
                    GeneratePost(args, sw);
                }
                catch (Exception ex)
                {
                    sw.WriteLine(ex.ToString());
                }
            }
        }

        private static void GeneratePost(string[] args, StreamWriter sw)
        {
            string apiToken = args[0];

            string allText = System.IO.File.ReadAllText("townsList.json");

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

                int buildingCount = queryBuilder.SendCountQuery(queryBuilder.CreateCountQuery(pickedTown.lat, pickedTown.lon, "building"));
                if (buildingCount > BUILDING_COUNT_MAXIMUM)
                    continue;
                int tigerRoadwaysData = queryBuilder.SendCountQuery(queryBuilder.CreateCountQuery(pickedTown.lat, pickedTown.lon, "tiger:reviewed"));

                string osmLink = $"https://www.openstreetmap.org/#map=16/{pickedTown.lat}/{pickedTown.lon}";
                string state = GetState(pickedTown.lat, pickedTown.lon, httpClient).Result;
                var postContent = $"{pickedTown.tags.name}, {state} seems like it could use some mapping!\r\n\r\nPopulation: {pickedTown.tags.population}\r\nBuilding count: {buildingCount}\r\nRoads to review: {tigerRoadwaysData}\r\n\r\nMap link: {osmLink}\r\n#OpenStreetMap";
                Console.WriteLine(postContent);

                sw.WriteLine("POST TEXT GENERATED:");
                sw.WriteLine(postContent);

                var tasks = PostTown(postContent, apiToken);
                tasks.Wait();
                posted = true;
                sw.WriteLine($"INFO - TOWNS SEARCHED: {townsSearched}");
            }
        }

        private static async Task PostTown(string postContent, string token)
        {
            var domain = "en.osm.town";
            var toot = await Statuses.Posting(domain, token, postContent);
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