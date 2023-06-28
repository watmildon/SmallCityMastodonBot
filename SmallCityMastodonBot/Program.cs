
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
            try
            {
                List<TownInfo> townData = new List<TownInfo>();

                string allText = System.IO.File.ReadAllText("townsList.json");

                HttpClient httpClient = new();
                TownsData2 data = JsonConvert.DeserializeObject<TownsData2>(allText);

                Console.WriteLine($"Total towns to scan {data.elements.Length}");

                OverpassQueryBuilder queryBuilder = new OverpassQueryBuilder(httpClient);

                int townsSearched = 0;
                foreach (var pickedTown in data.elements)
                {
                    if (pickedTown.tags.population == "0") // skip ghost towns for now, too many old rail stops as place=locality
                        continue;

                    try
                    {
                        int buildingCount = queryBuilder.SendCountQuery(queryBuilder.CreateCountQuery(pickedTown.lat, pickedTown.lon, "building"));
                        int tigerRoadwaysData = queryBuilder.SendCountQuery(queryBuilder.CreateCountQuery(pickedTown.lat, pickedTown.lon, "tiger:reviewed"));

                        var town = new TownInfo();
                        town.Name = pickedTown.tags.name;
                        town.Population = pickedTown.tags.population;
                        town.NumBuilding = buildingCount;
                        town.NumReviewableRoads = tigerRoadwaysData;

                        townData.Add(town);

                        //townsSearched++;
                        //if (townsSearched > 1000)
                        //{
                        //    break;
                        //}
                    }
                    catch (Exception e) {  Console.WriteLine(e); Thread.Sleep(1000); };
                }

                File.WriteAllText(@"C:\OSM\SmallTownScanDataAll.json", JsonConvert.SerializeObject(townData));
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
            }
            
        }


        public struct TownInfo
        {
            public string Name;
            public string Population;
            public int NumBuilding;
            public int NumReviewableRoads;
        }



    }
}