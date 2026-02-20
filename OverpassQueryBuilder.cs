using GeoCoordinatePortable;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SmallCityMastodonBot;
using System.Net;
using System.Text;
using System.Xml.Serialization;

namespace overpass_parser
{
    public class OverpassQueryBuilder
    {
        private readonly HttpClient httpClient;

        private static readonly string[] OverpassEndpoints =
        [
            "https://overpass-api.de/api/interpreter",
            "https://overpass.kumi.systems/api/interpreter",
            "https://maps.mail.ru/osm/tools/overpass/api/interpreter",
            "https://overpass.private.coffee/api/interpreter",
        ];

        private const int MaxPasses = 2;
        private const int RequestTimeoutSeconds = 60;
        private const int RateLimitDelayMs = 2000;

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
            Exception? lastException = null;

            for (int pass = 0; pass < MaxPasses; pass++)
            {
                foreach (var endpoint in OverpassEndpoints)
                {
                    try
                    {
                        // Throttle between queries regardless of endpoint
                        long currentTime = DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond;
                        if (currentTime - lastQueryTime < queryThrottle)
                        {
                            int sleepTime = (int)(queryThrottle - (currentTime - lastQueryTime));
                            Thread.Sleep(sleepTime);
                        }

                        Console.WriteLine($"Querying {endpoint} (pass {pass + 1}/{MaxPasses})");

                        HttpRequestMessage request = new(HttpMethod.Post, endpoint);
                        request.Headers.Add("User-Agent", Program.userAgent);
                        request.Content = new StringContent(overpassQuery);

                        lastQueryTime = DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond;

                        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(RequestTimeoutSeconds));
                        var responseTask = httpClient.SendAsync(request, cts.Token);
                        responseTask.Wait(cts.Token);
                        var response = responseTask.Result;

                        // Handle rate limiting: wait briefly then try next endpoint
                        if (response.StatusCode == HttpStatusCode.TooManyRequests)
                        {
                            Console.WriteLine($"Rate limited (429) by {endpoint}, trying next endpoint");
                            Thread.Sleep(RateLimitDelayMs);
                            continue;
                        }

                        response.EnsureSuccessStatusCode();

                        var contentTask = response.Content.ReadAsStringAsync();
                        contentTask.Wait();
                        string result = contentTask.Result;

                        // Check for Overpass server-side errors returned as HTTP 200
                        if (ResponseHasRemark(result))
                        {
                            Console.WriteLine($"Overpass remark (server-side error) from {endpoint}, trying next endpoint");
                            continue;
                        }

                        return result;
                    }
                    catch (OperationCanceledException)
                    {
                        Console.WriteLine($"Request to {endpoint} timed out after {RequestTimeoutSeconds}s");
                        lastException = new TimeoutException($"Request to {endpoint} timed out");
                    }
                    catch (HttpRequestException ex)
                    {
                        Console.WriteLine($"Request to {endpoint} failed: {ex.Message}");
                        lastException = ex;
                    }
                    catch (AggregateException ex) when (ex.InnerException is OperationCanceledException)
                    {
                        Console.WriteLine($"Request to {endpoint} timed out after {RequestTimeoutSeconds}s");
                        lastException = new TimeoutException($"Request to {endpoint} timed out");
                    }
                    catch (AggregateException ex) when (ex.InnerException is HttpRequestException)
                    {
                        Console.WriteLine($"Request to {endpoint} failed: {ex.InnerException.Message}");
                        lastException = ex.InnerException;
                    }
                }
            }

            throw new TimeoutException(
                $"SendQuery failed after {MaxPasses} passes across {OverpassEndpoints.Length} endpoints.",
                lastException);
        }

        private static bool ResponseHasRemark(string jsonResponse)
        {
            try
            {
                var obj = JObject.Parse(jsonResponse);
                return obj.ContainsKey("remark");
            }
            catch
            {
                return false; // If it's not valid JSON, let the caller handle it
            }
        }

        public int SendCountQuery(string overpassQuery)
        {
            string jsonResult = "";
            try
            {
                jsonResult = SendQuery(overpassQuery);
                var cqd = JsonConvert.DeserializeObject<CountQueryData>(jsonResult)
                    ?? throw new InvalidOperationException("Failed to deserialize Overpass count query response");
                var tags = cqd.elements[0].tags;
                string count = tags.total;

                return Int32.Parse(count);
            }
            catch (JsonReaderException ex)
            {
                Console.WriteLine("ERROR: JsonReaderException");
                Console.WriteLine();
                Console.WriteLine($"Overpass Query: {overpassQuery}");
                Console.WriteLine();
                Console.WriteLine($"JsonResult: {jsonResult}");
                Console.WriteLine();
                Console.WriteLine(ex.Message);

                throw;
            }
        }
    }
}