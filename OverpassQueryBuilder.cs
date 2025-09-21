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

        public string CreateCountQuery(double latitude, double longitude, string tagKey, string radiusInMeters)
        {
            return $"[out:json][timeout:25];(nwr(around:{radiusInMeters}.00,{latitude},{longitude})[\"{tagKey}\"];);out count;";
        }

        private static long lastQueryTime = DateTime.MinValue.Ticks / TimeSpan.TicksPerMillisecond;
        private static readonly long queryThrottle = 500;

        public string SendQuery(string overpassQuery)
{
    const int maxRetries = 3;
    const int delayMilliseconds = 10000;

    for (int attempt = 1; attempt <= maxRetries; attempt++)
    {
        try
        {
            long currentTime = DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond;

            if (currentTime - lastQueryTime < queryThrottle)
            {
                int sleepTime = (int)(queryThrottle - (currentTime - lastQueryTime));
                Thread.Sleep(sleepTime);
                Console.WriteLine($"Slept for {sleepTime / 1000.0} seconds");
            }

            string overpassUrl = "https://overpass.private.coffee/api/interpreter";

            HttpRequestMessage request = new(HttpMethod.Post, overpassUrl);
            request.Headers.Add("User-Agent", Program.userAgent);
            request.Content = new StringContent(overpassQuery);

            lastQueryTime = DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond;

            HttpResponseMessage response = httpClient.Send(request);
            response.EnsureSuccessStatusCode();

            var contentTask = response.Content.ReadAsStringAsync();
            contentTask.Wait();

            return contentTask.Result;
        }
        catch (HttpRequestException ex)
        {
            Console.WriteLine($"Attempt {attempt} failed: {ex.Message}");

            if (attempt == maxRetries)
            {
                throw new TimeoutException("SendQuery failed after 3 attempts due to network issues.", ex);
            }

            Thread.Sleep(delayMilliseconds);
        }
    }

    throw new InvalidOperationException("Unexpected error in SendQuery.");
}

        public int SendCountQuery(string overpassQuery)
        {
            var jsonResult = SendQuery(overpassQuery);
            return Int32.Parse(JsonConvert.DeserializeObject<CountQueryData>(jsonResult).elements[0].tags.total);
        }
    }
}