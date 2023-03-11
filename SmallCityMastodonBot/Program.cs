
using Mastodon.Api;
using Newtonsoft.Json;
using overpass_parser;

namespace SmallCityMastodonBot
{
    public class Program
    {
        public static readonly string userAgent = "smalltownsusa/0.1 @watmildon@en.osm.town";
        public static readonly int BUILDING_COUNT_MAXIMUM = 10;
        static void Main(string[] args)
        {
            string apiToken = args[0];
            string allText = System.IO.File.ReadAllText("townsList.json");

            HttpClient httpClient = new();
            TownsData2 data = JsonConvert.DeserializeObject<TownsData2>(allText);
            Random rnd = new Random(Guid.NewGuid().GetHashCode());
            OverpassQueryBuilder queryBuilder = new OverpassQueryBuilder(httpClient);

            bool posted = false;

            while (!posted)
            {
                var pickedTown = data.elements[rnd.Next(data.elements.Length)];
                int buildingCount = queryBuilder.SendCountQuery(queryBuilder.CreateCountQuery(pickedTown.lat, pickedTown.lon, "building"));
                if (buildingCount > BUILDING_COUNT_MAXIMUM)
                    continue;
                int tigerRoadwaysData = queryBuilder.SendCountQuery(queryBuilder.CreateCountQuery(pickedTown.lat, pickedTown.lon, "tiger:reviewed"));

                string osmLink = $"https://www.openstreetmap.org/#map=16/{pickedTown.lat}/{pickedTown.lon}";

                var postContent = $"{pickedTown.tags.name}, population {pickedTown.tags.population}, seems like it could use some mapping!\r\nBuilding count: {buildingCount}\r\nRoads to review: {tigerRoadwaysData}\r\n{osmLink}";
                Console.WriteLine(postContent);

                var tasks = PostTown(postContent, apiToken);
                tasks.Wait();
                posted = true;
            }
        }

        private static async Task PostTown(string postContent, string token)
        {
            var domain = "en.osm.town";
            var toot = await Statuses.Posting(domain, token, postContent);
        }


    }
}