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

        /// <summary>
        /// Environment variable holding a private Overpass instance URL. When set, it is tried
        /// before the public endpoints below, which remain as fallback.
        /// </summary>
        public const string PrimaryEndpointEnvVar = "OVERPASS_PRIMARY_URL";

        private static readonly string[] PublicOverpassEndpoints =
        [
            "https://overpass-api.de/api/interpreter",
            "https://overpass.kumi.systems/api/interpreter",
            "https://overpass.private.coffee/api/interpreter",
        ];

        private static readonly string[] OverpassEndpoints = BuildEndpointList();

        private static string[] BuildEndpointList()
        {
            var primary = Environment.GetEnvironmentVariable(PrimaryEndpointEnvVar)?.Trim();

            if (string.IsNullOrEmpty(primary))
            {
                Console.WriteLine($"{PrimaryEndpointEnvVar} not set, using public Overpass endpoints only");
                return PublicOverpassEndpoints;
            }

            if (!Uri.TryCreate(primary, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                Console.WriteLine($"WARNING: {PrimaryEndpointEnvVar} is not a valid http(s) URL, ignoring it and using public Overpass endpoints only");
                return PublicOverpassEndpoints;
            }

            Console.WriteLine($"Using primary Overpass endpoint from {PrimaryEndpointEnvVar} ({uri.Host}), with public endpoints as fallback");
            return [primary, .. PublicOverpassEndpoints];
        }

        private const int MaxPasses = 2;
        private const int RequestTimeoutSeconds = 60;
        private const int RateLimitDelayMs = 10000;
        private const int MaxDataLagHours = 48;

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

                        // Validate response: must be JSON, no remark, and fresh data
                        string? rejectReason = GetResponseRejectReason(result);
                        if (rejectReason != null)
                        {
                            Console.WriteLine($"Rejecting response from {endpoint}: {rejectReason}, trying next endpoint");
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

        private static string? GetResponseRejectReason(string response)
        {
            JObject obj;
            try
            {
                obj = JObject.Parse(response);
            }
            catch
            {
                return "non-JSON response (e.g. HTML error page)";
            }

            if (obj.ContainsKey("remark"))
                return "server-side error (remark field present)";

            // Check data freshness via osm3s.timestamp_osm_base
            var timestamp = obj.SelectToken("osm3s.timestamp_osm_base")?.ToString();
            if (timestamp != null && DateTime.TryParse(timestamp, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dataTime))
            {
                var lag = DateTime.UtcNow - dataTime;
                if (lag.TotalHours > MaxDataLagHours)
                    return $"stale data ({lag.TotalHours:F0}h old, max {MaxDataLagHours}h)";
            }

            return null;
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