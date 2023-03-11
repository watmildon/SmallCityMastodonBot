using GeoCoordinatePortable;
using Newtonsoft.Json;
using SmallCityMastodonBot;
using System.Text;
using System.Xml.Serialization;

namespace overpass_parser
{
    public class OverpassQueryBuilder
    {
        private readonly HttpClient httpClient;

        public OverpassQueryBuilder(HttpClient httpClient)
        {
            this.httpClient = httpClient;
        }

        public string CreateQuery(double latitude, double longitude, string tagKey)
        {
            return $"[out:json][timeout:25];(nwr(around:800.00,{latitude},{longitude})[\"{tagKey}\"];);out body;>;out skel qt;";
        }
        public string CreateCountQuery(double latitude, double longitude, string tagKey)
        {
            return $"[out:json][timeout:25];(nwr(around:800.00,{latitude},{longitude})[\"{tagKey}\"];);out count;";
        }

        private static long lastQueryTime = DateTime.MinValue.Ticks / TimeSpan.TicksPerMillisecond;
        private static readonly long queryThrottle = 500;

        public string SendQuery(string overpassQuery)
        {
            long currentTime = DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond;

            if (currentTime - lastQueryTime < queryThrottle)
            {
                Thread.Sleep((int)(queryThrottle - (currentTime - lastQueryTime)));
                Console.WriteLine($"Slept for {(queryThrottle - (currentTime - lastQueryTime)) / 1000.0} seconds");
            }

            // URL for the Overpass API endpoint
            //string overpassUrl = "https://overpass.kumi.systems/api/interpreter";
            //string overpassUrl = "https://maps.mail.ru/osm/tools/overpass/api/interpreter";
            string overpassUrl = "https://overpass-api.de/api/interpreter";
            //string overpassUrl = "http://192.168.1.43/api/interpreter";

            // set up the request
            HttpRequestMessage request = new(HttpMethod.Post, overpassUrl);
            request.Headers.Add("User-Agent", Program.userAgent);
            request.Content = new StringContent(overpassQuery);

            // record time we sent the query
            lastQueryTime = DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond;

            // send the query
            HttpResponseMessage response = httpClient.Send(request);

            response.EnsureSuccessStatusCode();

            var contentTask = response.Content.ReadAsStringAsync();
            contentTask.Wait();

            return contentTask.Result;
        }

        public int SendCountQuery(string overpassQuery)
        {
            var jsonResult = SendQuery(overpassQuery);
            return Int32.Parse(JsonConvert.DeserializeObject<CountQueryData>(jsonResult).elements[0].tags.total);
        }
    }
}