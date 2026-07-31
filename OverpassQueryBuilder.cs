using GeoCoordinatePortable;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SmallCityMastodonBot;
using System.Net;
using System.Text;
using System.Xml;
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

        /// <summary>
        /// Builds a diff query returning every element changed between <paramref name="sinceUtc"/>
        /// and now within the radius. Requires an endpoint with attic (history) data, and the
        /// output is XML: Overpass refuses to serve diff results as JSON.
        /// </summary>
        public string CreateDiffQuery(double latitude, double longitude, string radiusInMeters, DateTime sinceUtc)
        {
            string since = sinceUtc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
            return $"[timeout:60][diff:\"{since}\"];nwr(around:{radiusInMeters}.00,{latitude},{longitude});out meta;";
        }

        private static long lastQueryTime = DateTime.MinValue.Ticks / TimeSpan.TicksPerMillisecond;
        private static readonly long queryThrottle = 500;

        public string SendQuery(string overpassQuery) => SendWithRetries(overpassQuery, GetResponseRejectReason);

        /// <summary>
        /// Variant of <see cref="SendQuery"/> for queries whose output is XML, such as
        /// [diff:...] queries, which Overpass will not serve as JSON.
        /// </summary>
        public string SendXmlQuery(string overpassQuery) => SendWithRetries(overpassQuery, GetXmlResponseRejectReason);

        private string SendWithRetries(string overpassQuery, Func<string, string?> getRejectReason)
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

                        // Validate response: must parse, no remark, and fresh data
                        string? rejectReason = getRejectReason(result);
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
                $"Overpass query failed after {MaxPasses} passes across {OverpassEndpoints.Length} endpoints.",
                lastException);
        }

        /// <summary>
        /// Runs a diff query (see <see cref="CreateDiffQuery"/>) and summarizes the changed
        /// elements: counts by action type plus the distinct editors and changesets involved.
        /// </summary>
        public OverpassDiffStats GetDiffStats(string diffQuery)
        {
            string xmlResult = SendXmlQuery(diffQuery);
            var doc = new XmlDocument();
            doc.LoadXml(xmlResult);

            var stats = new OverpassDiffStats();
            var actions = doc.SelectNodes("/osm/action");
            if (actions == null)
                return stats;

            foreach (XmlElement action in actions)
            {
                string actionType = action.GetAttribute("type");

                // create actions hold the element directly; modify/delete wrap old/new versions
                XmlElement? element = actionType == "create"
                    ? FirstElementChild(action)
                    : FirstElementChild(action["new"]) ?? FirstElementChild(action["old"]);
                if (element == null)
                    continue;

                switch (actionType)
                {
                    case "create": stats.Created++; break;
                    case "modify": stats.Modified++; break;
                    case "delete": stats.Deleted++; break;
                    default: continue;
                }

                string user = element.GetAttribute("user");
                if (!string.IsNullOrEmpty(user))
                    stats.Users.Add(user);

                string changeset = element.GetAttribute("changeset");
                if (!string.IsNullOrEmpty(changeset))
                    stats.Changesets.Add(changeset);
            }

            return stats;
        }

        private static XmlElement? FirstElementChild(XmlElement? node)
        {
            if (node == null)
                return null;

            foreach (XmlNode child in node.ChildNodes)
            {
                if (child is XmlElement element)
                    return element;
            }

            return null;
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

        private static string? GetXmlResponseRejectReason(string response)
        {
            var doc = new XmlDocument();
            try
            {
                doc.LoadXml(response);
            }
            catch
            {
                return "non-XML response (e.g. HTML error page)";
            }

            // Overpass reports errors (including missing attic data) via remark elements
            if (doc.SelectSingleNode("//remark") != null)
                return "server-side error (remark element present)";

            var osmBase = (doc.SelectSingleNode("/osm/meta") as XmlElement)?.GetAttribute("osm_base");
            if (!string.IsNullOrEmpty(osmBase) && DateTime.TryParse(osmBase, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dataTime))
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

    /// <summary>
    /// Summary of an Overpass diff query: how many elements were created/modified/deleted in the
    /// window, and which editors and changesets were involved.
    /// </summary>
    public class OverpassDiffStats
    {
        public int Created { get; set; }
        public int Modified { get; set; }
        public int Deleted { get; set; }
        public HashSet<string> Users { get; } = new();
        public HashSet<string> Changesets { get; } = new();
        public int TotalChanges => Created + Modified + Deleted;
    }
}