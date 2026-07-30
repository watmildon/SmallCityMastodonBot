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

        /// <summary>
        /// Placeholder logged in place of the private endpoint URL. The private instance URL is a
        /// secret: it must never appear in console output, which lands in public CI logs.
        /// </summary>
        private const string PrivateEndpointLabel = "<private Overpass instance>";

        private static readonly string[] OverpassEndpoints = BuildEndpointList();

        /// <summary>
        /// The private endpoint URL, or null when none is configured. Used only to decide whether a
        /// given endpoint must be redacted before logging — never logged itself.
        /// </summary>
        private static string? privateEndpoint;

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
                // Deliberately does not echo the offending value — it may be a malformed secret.
                Console.WriteLine($"WARNING: {PrimaryEndpointEnvVar} is not a valid http(s) URL, ignoring it and using public Overpass endpoints only");
                return PublicOverpassEndpoints;
            }

            privateEndpoint = primary;
            Console.WriteLine($"Using primary Overpass endpoint from {PrimaryEndpointEnvVar}, with public endpoints as fallback");
            return [primary, .. PublicOverpassEndpoints];
        }

        /// <summary>
        /// Returns a log-safe name for an endpoint, replacing the private instance URL with a
        /// placeholder. Always use this instead of interpolating an endpoint into log output.
        /// </summary>
        private static string SafeName(string endpoint) =>
            endpoint == privateEndpoint ? PrivateEndpointLabel : endpoint;

        /// <summary>
        /// Strips any occurrence of the private endpoint URL (and its host) from arbitrary text such
        /// as exception messages, which often embed the host they failed to reach.
        /// </summary>
        private static string Redact(string text)
        {
            if (string.IsNullOrEmpty(privateEndpoint) || string.IsNullOrEmpty(text))
                return text;

            text = text.Replace(privateEndpoint, PrivateEndpointLabel, StringComparison.OrdinalIgnoreCase);

            if (Uri.TryCreate(privateEndpoint, UriKind.Absolute, out var uri))
                text = text.Replace(uri.Host, PrivateEndpointLabel, StringComparison.OrdinalIgnoreCase);

            return text;
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

                        Console.WriteLine($"Querying {SafeName(endpoint)} (pass {pass + 1}/{MaxPasses})");

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
                            Console.WriteLine($"Rate limited (429) by {SafeName(endpoint)}, trying next endpoint");
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
                            Console.WriteLine($"Rejecting response from {SafeName(endpoint)}: {rejectReason}, trying next endpoint");
                            continue;
                        }

                        return result;
                    }
                    catch (OperationCanceledException)
                    {
                        Console.WriteLine($"Request to {SafeName(endpoint)} timed out after {RequestTimeoutSeconds}s");
                        lastException = new TimeoutException($"Request to {SafeName(endpoint)} timed out");
                    }
                    catch (HttpRequestException ex)
                    {
                        // Exception messages embed the unreachable host, so redact before logging.
                        Console.WriteLine($"Request to {SafeName(endpoint)} failed: {Redact(ex.Message)}");
                        lastException = new HttpRequestException(Redact(ex.Message));
                    }
                    catch (AggregateException ex) when (ex.InnerException is OperationCanceledException)
                    {
                        Console.WriteLine($"Request to {SafeName(endpoint)} timed out after {RequestTimeoutSeconds}s");
                        lastException = new TimeoutException($"Request to {SafeName(endpoint)} timed out");
                    }
                    catch (AggregateException ex) when (ex.InnerException is HttpRequestException)
                    {
                        Console.WriteLine($"Request to {SafeName(endpoint)} failed: {Redact(ex.InnerException.Message)}");
                        lastException = new HttpRequestException(Redact(ex.InnerException.Message));
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