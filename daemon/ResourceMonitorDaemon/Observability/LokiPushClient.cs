using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace ResourceMonitorDaemon.Observability
{
    /// <summary>
    /// Builds the JSON body of a Loki push request.
    /// </summary>
    /// <remarks>
    /// Format per the Grafana Loki HTTP API, "Ingest logs" (POST /loki/api/v1/push,
    /// Content-Type: application/json, optional Content-Encoding: gzip):
    /// https://grafana.com/docs/loki/latest/reference/loki-http-api/#ingest-logs
    /// <code>
    /// {"streams":[{"stream":{"label":"value"},"values":[["&lt;unix epoch in nanoseconds&gt;","&lt;log line&gt;"]]}]}
    /// </code>
    /// The timestamp must be a string. Entries are grouped per label set and sorted by timestamp
    /// within each stream.
    /// </remarks>
    public static class LokiPushPayload
    {
        public static byte[] BuildJson(IReadOnlyList<LokiEntry> entries, string? hostLabel)
        {
            // Group per stream in first-seen order; OrderBy is stable for equal timestamps.
            var streams = entries
                .GroupBy(entry => (entry.Kind, entry.Level))
                .Select(group => (group.Key, Values: group.OrderBy(entry => entry.TimestampNs).ToList()));

            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WriteStartArray("streams");
                foreach (var (key, values) in streams)
                {
                    writer.WriteStartObject();
                    writer.WriteStartObject("stream");
                    foreach (var label in LokiLabels.For(key.Kind, key.Level, hostLabel))
                    {
                        writer.WriteString(label.Key, label.Value);
                    }

                    writer.WriteEndObject();
                    writer.WriteStartArray("values");
                    foreach (var entry in values)
                    {
                        writer.WriteStartArray();
                        writer.WriteStringValue(entry.TimestampNs.ToString(System.Globalization.CultureInfo.InvariantCulture));
                        writer.WriteStringValue(entry.Line);
                        writer.WriteEndArray();
                    }

                    writer.WriteEndArray();
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            return buffer.ToArray();
        }

        public static byte[] Gzip(byte[] body)
        {
            using var output = new MemoryStream();
            using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
            {
                gzip.Write(body);
            }

            return output.ToArray();
        }

        /// <summary>The machine name as a host label value, or null when the label is disabled.</summary>
        public static string? HostLabel(LokiSettings settings) => settings.IncludeHostLabel ? Environment.MachineName : null;
    }

    public enum LokiPushOutcome
    {
        /// <summary>2xx: the batch was accepted.</summary>
        Success,

        /// <summary>429, 5xx, timeout or network error: keep the batch and retry with backoff.</summary>
        Retry,

        /// <summary>Other status (4xx, 3xx) or an invalid request: drop the batch.</summary>
        Drop,
    }

    /// <summary>Result of one push. <see cref="Detail"/> never contains the token or credentials.</summary>
    public sealed record LokiPushResult(LokiPushOutcome Outcome, int? StatusCode, string Detail, TimeSpan? RetryAfter = null)
    {
        public override string ToString() => StatusCode is { } code ? $"HTTP {code} {Detail}".TrimEnd() : Detail;
    }

    /// <summary>
    /// Sends push requests. One <see cref="HttpClient"/> for the sink's lifetime; URL, headers and
    /// gzip are taken per request from the settings snapshot, so live reconfiguration needs no new
    /// client. Redirects are not followed (a redirect would be a misconfigured URL).
    /// </summary>
    public sealed class LokiPushClient : IDisposable
    {
        public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
        public const int MaxBodySnippetLength = 300;
        public const string TestLine = "Resource Monitor daemon: observability test line";

        private readonly HttpClient _http;

        /// <param name="handler">Test hook; by default a <see cref="SocketsHttpHandler"/> without redirects.</param>
        public LokiPushClient(HttpMessageHandler? handler = null)
        {
            _http = handler == null
                ? new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, PooledConnectionLifetime = TimeSpan.FromMinutes(5) }, disposeHandler: true)
                : new HttpClient(handler, disposeHandler: false);
            _http.Timeout = RequestTimeout;
        }

        /// <summary>Creates the request (exposed for tests: URL, headers, body).</summary>
        public static HttpRequestMessage CreateRequest(LokiSettings settings, IReadOnlyList<LokiEntry> entries)
        {
            var body = LokiPushPayload.BuildJson(entries, LokiPushPayload.HostLabel(settings));
            var content = new ByteArrayContent(settings.Gzip ? LokiPushPayload.Gzip(body) : body);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            if (settings.Gzip)
            {
                content.Headers.ContentEncoding.Add("gzip");
            }

            var request = new HttpRequestMessage(HttpMethod.Post, LokiUrl.BuildPushUri(settings.Url)) { Content = content };
            request.Headers.UserAgent.ParseAdd("resmon-daemon");
            switch (settings.AuthMode)
            {
                case LokiAuthMode.Basic when settings.HasToken:
                    request.Headers.Authorization = new AuthenticationHeaderValue(
                        "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{settings.Username}:{settings.Token}")));
                    break;
                case LokiAuthMode.Bearer when settings.HasToken:
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.Token);
                    break;
            }

            if (settings.TenantId.Length > 0)
            {
                request.Headers.TryAddWithoutValidation("X-Scope-OrgID", settings.TenantId);
            }

            return request;
        }

        /// <summary>
        /// Pushes <paramref name="entries"/> and classifies the outcome. Throws only
        /// <see cref="OperationCanceledException"/> when <paramref name="cancellationToken"/> is cancelled.
        /// </summary>
        public async Task<LokiPushResult> PushAsync(LokiSettings settings, IReadOnlyList<LokiEntry> entries, CancellationToken cancellationToken)
        {
            try
            {
                using var request = CreateRequest(settings, entries);
                using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
                var code = (int)response.StatusCode;
                if (code is >= 200 and < 300)
                {
                    return new LokiPushResult(LokiPushOutcome.Success, code, response.ReasonPhrase ?? response.StatusCode.ToString());
                }

                var detail = $"{response.ReasonPhrase ?? response.StatusCode.ToString()}{await ReadBodySnippetAsync(response, settings, cancellationToken).ConfigureAwait(false)}";
                if (response.StatusCode == HttpStatusCode.TooManyRequests || code >= 500)
                {
                    return new LokiPushResult(LokiPushOutcome.Retry, code, detail, RetryAfter(response));
                }

                return new LokiPushResult(LokiPushOutcome.Drop, code, detail);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                return new LokiPushResult(LokiPushOutcome.Retry, null, $"timed out after {RequestTimeout.TotalSeconds:F0} s");
            }
            catch (HttpRequestException ex)
            {
                return new LokiPushResult(LokiPushOutcome.Retry, null, LokiRedactor.Redact(ex.GetBaseException().Message, settings));
            }
            catch (Exception ex)
            {
                // Invalid URL/header or similar configuration problem: retrying would not help.
                return new LokiPushResult(LokiPushOutcome.Drop, null, $"{ex.GetType().Name}: {LokiRedactor.Redact(ex.Message, settings)}");
            }
        }

        /// <summary>
        /// Pushes one test line to <c>{app="resmon-daemon",kind="log",level="info"}</c> with the given
        /// (possibly unsaved) settings, regardless of <see cref="LokiSettings.Enabled"/>.
        /// </summary>
        public static async Task<LokiPushResult> TestAsync(LokiSettings settings, CancellationToken cancellationToken, HttpMessageHandler? handler = null)
        {
            if (LokiUrl.Validate(settings.Url) is { } error)
            {
                return new LokiPushResult(LokiPushOutcome.Drop, null, error);
            }

            using var client = new LokiPushClient(handler);
            var entry = new LokiEntry(LokiStreamKind.Log, "info", MonotonicNanoClock.Shared.Next(), TestLine);
            return await client.PushAsync(settings, new[] { entry }, cancellationToken).ConfigureAwait(false);
        }

        public void Dispose() => _http.Dispose();

        private static TimeSpan? RetryAfter(HttpResponseMessage response)
        {
            var retryAfter = response.Headers.RetryAfter;
            if (retryAfter?.Delta is { } delta)
            {
                return delta;
            }

            return retryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : null;
        }

        private static async Task<string> ReadBodySnippetAsync(HttpResponseMessage response, LokiSettings settings, CancellationToken cancellationToken)
        {
            try
            {
                var body = (await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)).Trim();
                if (body.Length == 0)
                {
                    return string.Empty;
                }

                if (body.Length > MaxBodySnippetLength)
                {
                    body = body[..MaxBodySnippetLength] + "…";
                }

                return ": " + LokiRedactor.Redact(body.ReplaceLineEndings(" "), settings);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                return string.Empty;
            }
        }
    }

    /// <summary>Removes secrets from text before it is logged or shown.</summary>
    public static class LokiRedactor
    {
        public const string Mask = "***";

        public static string Redact(string text, LokiSettings settings)
        {
            if (string.IsNullOrEmpty(text))
            {
                return text;
            }

            if (settings.HasToken)
            {
                text = text.Replace(settings.Token, Mask, StringComparison.Ordinal);
                var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{settings.Username}:{settings.Token}"));
                text = text.Replace(basic, Mask, StringComparison.Ordinal);
            }

            // Full URLs may carry a path or query the user considers private: keep the host only.
            if (settings.Url.Length > 0 && LokiUrl.Validate(settings.Url) == null)
            {
                var host = LokiUrl.DescribeHost(settings.Url);
                text = text.Replace(LokiUrl.BuildPushUri(settings.Url).AbsoluteUri, host, StringComparison.OrdinalIgnoreCase)
                    .Replace(settings.Url, host, StringComparison.OrdinalIgnoreCase);
            }

            return text;
        }
    }
}
