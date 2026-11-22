using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Server;
using ResourceMonitorDaemon;
using ResourceMonitorDaemon.Observability;

/// <summary>Tests for the optional Loki sink, its sources, settings and the device status topic.</summary>
internal static class ObservabilityTests
{
    private const string Secret = "tok-SECRET-0123456789abcdef";

    public static IEnumerable<(string Name, Action Run)> All => new (string Name, Action Run)[]
    {
        // ---- Push payload -------------------------------------------------------------------
        ("Loki payload: one stream per label set, string ns timestamps sorted per stream", () =>
        {
            var entries = new[]
            {
                new LokiEntry(LokiStreamKind.Log, "info", 300, "late"),
                new LokiEntry(LokiStreamKind.Metrics, null, 150, "{\"cpu\":1}"),
                new LokiEntry(LokiStreamKind.Log, "warn", 200, "warning"),
                new LokiEntry(LokiStreamKind.Log, "info", 100, "early"),
                new LokiEntry(LokiStreamKind.Status, null, 250, "{\"v\":1}"),
            };
            var root = JsonNode.Parse(LokiPushPayload.BuildJson(entries, hostLabel: null))!.AsObject();
            AssertEqual("streams", string.Join(",", root.Select(p => p.Key)));
            var streams = root["streams"]!.AsArray();
            AssertEqual(4, streams.Count);

            var info = FindStream(streams, "log", "info");
            AssertEqual("app=resmon-daemon,kind=log,level=info", Labels(info));
            var values = info["values"]!.AsArray();
            AssertEqual("100|early;300|late", string.Join(";", values.Select(v => $"{v![0]!.GetValue<string>()}|{v[1]!.GetValue<string>()}")));
            AssertTrue(values.All(v => v![0]!.GetValueKind() == JsonValueKind.String), "timestamps must be JSON strings");

            AssertEqual("app=resmon-daemon,kind=metrics", Labels(FindStream(streams, "metrics")));
            AssertEqual("app=resmon-device,kind=status", Labels(FindStream(streams, "status")));
            AssertEqual("{\"v\":1}", FindStream(streams, "status")["values"]![0]![1]!.GetValue<string>());

            var withHost = JsonNode.Parse(LokiPushPayload.BuildJson(entries, "PC-1"))!["streams"]!.AsArray();
            AssertTrue(withHost.All(s => s!["stream"]!["host"]!.GetValue<string>() == "PC-1"), "host label missing");
        }),
        ("MonotonicNanoClock and queue timestamps are strictly increasing, also across threads", () =>
        {
            var fixedTime = DateTimeOffset.UtcNow;
            var clock = new MonotonicNanoClock(() => fixedTime);
            var stamps = new ConcurrentBag<long>();
            Parallel.For(0, 4, _ =>
            {
                for (var i = 0; i < 1000; i++)
                {
                    stamps.Add(clock.Next());
                }
            });
            AssertEqual(4000, stamps.Distinct().Count());
            AssertEqual((fixedTime - DateTimeOffset.UnixEpoch).Ticks * 100, stamps.Min());

            var queue = new LokiEntryQueue(10000, new MonotonicNanoClock(() => fixedTime));
            Parallel.For(0, 4, t =>
            {
                for (var i = 0; i < 500; i++)
                {
                    queue.TryEnqueue(i % 2 == 0 ? LokiStreamKind.Log : LokiStreamKind.Metrics, "info", $"{t}-{i}");
                }
            });
            var batch = queue.DequeueBatch(int.MaxValue);
            AssertEqual(2000, batch.Count);
            for (var i = 1; i < batch.Count; i++)
            {
                AssertTrue(batch[i].TimestampNs > batch[i - 1].TimestampNs, "queue order differs from timestamp order");
            }

            var realNs = MonotonicNanoClock.Shared.Next();
            var nowNs = (DateTimeOffset.UtcNow - DateTimeOffset.UnixEpoch).Ticks * 100;
            AssertTrue(Math.Abs(nowNs - realNs) < 60_000_000_000L, "shared clock is not unix-epoch nanoseconds");
        }),
        ("Loki URL: push path appended only without a path; user info stripped; host-only description", () =>
        {
            AssertEqual("http://loki:3100/loki/api/v1/push", LokiUrl.BuildPushUri("http://loki:3100").AbsoluteUri);
            AssertEqual("http://loki:3100/loki/api/v1/push", LokiUrl.BuildPushUri("http://loki:3100/").AbsoluteUri);
            AssertEqual("https://logs.example.net/custom/push", LokiUrl.BuildPushUri("https://logs.example.net/custom/push").AbsoluteUri);
            AssertEqual("https://logs.example.net/loki/api/v1/push", LokiUrl.BuildPushUri("https://user:" + Secret + "@logs.example.net").AbsoluteUri);
            AssertEqual("http://loki:3100/loki/api/v1/push?x=1", LokiUrl.BuildPushUri("http://loki:3100?x=1").AbsoluteUri);

            var stripped = LokiUrl.StripUserInfo("https://user:" + Secret + "@host.example/x");
            AssertTrue(!stripped.Contains(Secret) && !stripped.Contains('@'), "user info not stripped: " + stripped);
            AssertEqual("host.example:8443", LokiUrl.DescribeHost("https://user:" + Secret + "@host.example:8443/a/b?c=d"));
            AssertEqual("host.example", LokiUrl.DescribeHost("https://host.example/a"));
            AssertEqual("(none)", LokiUrl.DescribeHost(""));

            AssertTrue(LokiUrl.Validate("http://loki:3100") == null, "valid http url");
            AssertTrue(LokiUrl.Validate("https://logs-prod.example.net/loki/api/v1/push") == null, "valid https url");
            AssertTrue(LokiUrl.Validate("ftp://loki") != null, "ftp accepted");
            AssertTrue(LokiUrl.Validate("loki:3100/x") != null, "relative accepted");
            AssertTrue(LokiUrl.Validate("https://u:p@loki") != null, "user info accepted");
            AssertTrue(LokiUrl.Validate("") != null, "empty accepted");

            var warnings = new List<string>();
            var sanitized = LokiSettingsValidator.Sanitize(new LokiSettings { Enabled = true, Url = "https://user:" + Secret + "@logs.example.net" }, warnings);
            AssertEqual("https://logs.example.net/", sanitized.Url);
            AssertTrue(warnings.Count > 0 && warnings.All(w => !w.Contains(Secret)), "warning missing or leaks the secret");
        }),

        // ---- Request headers ----------------------------------------------------------------
        ("Loki request: auth header per mode, tenant header, gzip encoding and JSON content type", () =>
        {
            var entries = new[] { new LokiEntry(LokiStreamKind.Log, "info", 1, "x") };
            var basic = new LokiSettings { Enabled = true, Url = "http://loki:3100", AuthMode = LokiAuthMode.Basic, Username = "123456", Token = Secret, TenantId = "tenant-1" };
            using (var request = LokiPushClient.CreateRequest(basic, entries))
            {
                AssertEqual("Basic", request.Headers.Authorization!.Scheme);
                AssertEqual("123456:" + Secret, Encoding.UTF8.GetString(Convert.FromBase64String(request.Headers.Authorization.Parameter!)));
                AssertEqual("tenant-1", request.Headers.GetValues("X-Scope-OrgID").Single());
                AssertEqual("application/json", request.Content!.Headers.ContentType!.MediaType);
                AssertEqual("gzip", request.Content.Headers.ContentEncoding.Single());
                var body = Gunzip(request.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult());
                AssertEqual(1, JsonNode.Parse(body)!["streams"]!.AsArray().Count);
            }

            using (var request = LokiPushClient.CreateRequest(basic with { AuthMode = LokiAuthMode.Bearer, TenantId = "", Gzip = false }, entries))
            {
                AssertEqual("Bearer " + Secret, request.Headers.Authorization!.ToString());
                AssertTrue(!request.Headers.Contains("X-Scope-OrgID"), "tenant header without tenant");
                AssertEqual(0, request.Content!.Headers.ContentEncoding.Count);
                JsonNode.Parse(request.Content.ReadAsStringAsync().GetAwaiter().GetResult());
            }

            using (var request = LokiPushClient.CreateRequest(basic with { AuthMode = LokiAuthMode.None }, entries))
            {
                AssertTrue(request.Headers.Authorization == null, "auth header with AuthMode None");
            }

            using (var request = LokiPushClient.CreateRequest(basic with { Token = "" }, entries))
            {
                AssertTrue(request.Headers.Authorization == null, "auth header without a token");
            }
        }),
        ("Loki token never appears in ToString, Describe, results or sink logs", () =>
        {
            var settings = new LokiSettings { Enabled = true, Url = "http://loki.test", AuthMode = LokiAuthMode.Bearer, Token = Secret, TokenSource = LokiTokenSource.Protected, Gzip = false };
            AssertTrue(!settings.ToString().Contains(Secret) && !settings.Describe().Contains(Secret), "settings print the token");
            AssertTrue(settings.Describe().Contains("token configured=yes"), "describe should say whether a token exists");
            var patch = new UserLokiSettingsPatch { TokenProtected = "Q0lQSEVSVEVYVA==", Url = "https://logs.example.net/private/path" };
            AssertTrue(!patch.ToString().Contains("Q0lQSEVSVEVYVA==") && !patch.ToString().Contains("private"), "patch prints ciphertext or url path");

            var logs = new CapturingLogs();
            var responses = new Queue<Func<HttpResponseMessage>>(new Func<HttpResponseMessage>[]
            {
                () => new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("invalid token " + Secret + " for http://loki.test/loki/api/v1/push") },
                () => throw new HttpRequestException("connection reset while sending Bearer " + Secret),
                () => throw new InvalidOperationException("header Bearer " + Secret + " rejected"),
            });
            var handler = new FakeHandler(_ => responses.Dequeue()());
            var now = DateTimeOffset.UnixEpoch;
            using var factory = logs.Factory();
            var sink = new LokiSink(new LokiEntryQueue(), factory.CreateLogger<LokiSink>(), handler, () => now);
            sink.Configure(settings);
            var results = new List<LokiPushResult?>();
            for (var i = 0; i < 3; i++)
            {
                sink.Queue.TryEnqueue(LokiStreamKind.Log, "info", "line " + i);
                results.Add(sink.FlushOnceAsync(CancellationToken.None).GetAwaiter().GetResult());
                now += TimeSpan.FromMinutes(5);
            }

            AssertEqual("Drop,Retry,Drop", string.Join(",", results.Select(r => r!.Outcome)));
            AssertTrue(results.All(r => !r!.ToString().Contains(Secret)), "result leaks the token: " + string.Join(" | ", results));
            AssertTrue(!results[0]!.ToString().Contains("/loki/api/v1/push"), "result leaks the url path");
            AssertTrue(logs.Messages.Count() >= 3, "expected sink log messages");
            AssertTrue(logs.Messages.All(m => !m.Contains(Secret)), "log leaks the token: " + string.Join(" | ", logs.Messages));
        }),

        // ---- Queue and batching -------------------------------------------------------------
        ("Loki queue drops the oldest entries when full and counts the drops", () =>
        {
            var queue = new LokiEntryQueue(3);
            for (var i = 1; i <= 5; i++)
            {
                AssertTrue(queue.TryEnqueue(LokiStreamKind.Log, "info", i.ToString()), "enqueue refused");
            }

            AssertEqual(3, queue.Count);
            AssertEqual(2L, queue.TotalDropped);
            AssertEqual(2L, queue.TakeDroppedCount());
            AssertEqual(0L, queue.TakeDroppedCount());
            AssertEqual("3,4,5", string.Join(",", queue.DequeueBatch(10).Select(e => e.Line)));

            queue.TryEnqueue(LokiStreamKind.Log, "info", "a");
            queue.TryEnqueue(LokiStreamKind.Log, "info", "b");
            queue.SetCapacity(1);
            AssertEqual("b", queue.DequeueBatch(10).Single().Line);
            AssertEqual(1L, queue.TakeDroppedCount());

            queue.Accepting = false;
            AssertTrue(!queue.TryEnqueue(LokiStreamKind.Log, "info", "x"), "enqueued while not accepting");
            AssertEqual(0, queue.Count);

            queue.Accepting = true;
            queue.TryEnqueue(LokiStreamKind.Log, "info", new string('x', LokiEntryQueue.MaxLineLength + 100));
            AssertTrue(queue.DequeueBatch(1).Single().Line.Length < LokiEntryQueue.MaxLineLength + 20, "long line not truncated");
        }),
        ("Loki sink batches by size and by flush interval", () =>
        {
            var now = DateTimeOffset.UnixEpoch;
            var sizes = new List<int>();
            var handler = new FakeHandler(request =>
            {
                sizes.Add(JsonNode.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult())!["streams"]!.AsArray().Sum(s => s!["values"]!.AsArray().Count));
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            });
            var sink = new LokiSink(new LokiEntryQueue(), NullLogger<LokiSink>.Instance, handler, () => now);
            sink.Configure(new LokiSettings { Enabled = true, Url = "http://loki.test", BatchMaxEntries = 500, FlushIntervalMs = 2000, Gzip = false });
            AssertTrue(!sink.IsFlushDue(now), "due with an empty queue");

            var signalled = 0;
            var previous = sink.Queue.ThresholdReached;
            sink.Queue.ThresholdReached = () => { signalled++; previous?.Invoke(); };
            for (var i = 0; i < 1200; i++)
            {
                sink.Queue.TryEnqueue(LokiStreamKind.Log, "info", "line " + i);
            }

            AssertEqual(1, signalled);
            AssertTrue(sink.IsFlushDue(now), "full batch not due");
            sink.FlushOnceAsync(CancellationToken.None).GetAwaiter().GetResult();
            AssertTrue(sink.IsFlushDue(now), "second full batch not due");
            sink.FlushOnceAsync(CancellationToken.None).GetAwaiter().GetResult();
            AssertTrue(!sink.IsFlushDue(now), "partial batch due before the flush interval");
            now += TimeSpan.FromMilliseconds(1999);
            AssertTrue(!sink.IsFlushDue(now), "partial batch due 1 ms early");
            now += TimeSpan.FromMilliseconds(1);
            AssertTrue(sink.IsFlushDue(now), "partial batch not due after the flush interval");
            sink.FlushOnceAsync(CancellationToken.None).GetAwaiter().GetResult();
            AssertEqual("500,500,200", string.Join(",", sizes));
            AssertEqual(0, sink.Queue.Count);
        }),
        ("Loki sink retries 429/5xx with backoff, honours Retry-After, drops 4xx and logs it once", () =>
        {
            var now = DateTimeOffset.UnixEpoch;
            var statuses = new Queue<HttpResponseMessage>();
            var bodies = new List<string>();
            var handler = new FakeHandler(request =>
            {
                bodies.Add(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                return statuses.Dequeue();
            });
            var logs = new CapturingLogs();
            using var factory = logs.Factory();
            var sink = new LokiSink(new LokiEntryQueue(), factory.CreateLogger<LokiSink>(), handler, () => now);
            sink.Configure(new LokiSettings { Enabled = true, Url = "http://loki.test", Gzip = false });
            statuses.Enqueue(new HttpResponseMessage(HttpStatusCode.TooManyRequests));
            statuses.Enqueue(new HttpResponseMessage(HttpStatusCode.InternalServerError));
            statuses.Enqueue(new HttpResponseMessage(HttpStatusCode.NoContent));
            statuses.Enqueue(new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("entry too far behind") });
            statuses.Enqueue(new HttpResponseMessage(HttpStatusCode.BadRequest));

            for (var i = 0; i < 3; i++)
            {
                sink.Queue.TryEnqueue(LokiStreamKind.Log, "info", "first " + i);
            }

            AssertEqual(LokiPushOutcome.Retry, sink.FlushOnceAsync(CancellationToken.None).GetAwaiter().GetResult()!.Outcome);
            AssertEqual(3, sink.PendingCount);
            AssertEqual(now + TimeSpan.FromSeconds(1), sink.RetryAt);
            AssertTrue(!sink.IsFlushDue(now), "retry due before the backoff delay");
            sink.Queue.TryEnqueue(LokiStreamKind.Log, "info", "queued meanwhile 1");
            sink.Queue.TryEnqueue(LokiStreamKind.Log, "info", "queued meanwhile 2");

            now += TimeSpan.FromSeconds(1);
            AssertTrue(sink.IsFlushDue(now), "retry not due after the backoff delay");
            AssertEqual(LokiPushOutcome.Retry, sink.FlushOnceAsync(CancellationToken.None).GetAwaiter().GetResult()!.Outcome);
            AssertEqual(now + TimeSpan.FromSeconds(2), sink.RetryAt);

            now += TimeSpan.FromSeconds(2);
            AssertEqual(LokiPushOutcome.Success, sink.FlushOnceAsync(CancellationToken.None).GetAwaiter().GetResult()!.Outcome);
            AssertEqual(bodies[0], bodies[1]);
            AssertEqual(bodies[0], bodies[2]);               // the same batch (same timestamps) was retried
            AssertEqual(0, sink.PendingCount);
            AssertEqual(DateTimeOffset.MinValue, sink.RetryAt);
            AssertTrue(logs.Messages.Any(m => m.Contains("recovered after 2")), "recovery not logged");

            AssertEqual(LokiPushOutcome.Drop, sink.FlushOnceAsync(CancellationToken.None).GetAwaiter().GetResult()!.Outcome);
            AssertTrue(bodies[3].Contains("queued meanwhile 2"), "entries queued during the retry were not sent next");
            sink.Queue.TryEnqueue(LokiStreamKind.Log, "info", "rejected again");
            AssertEqual(LokiPushOutcome.Drop, sink.FlushOnceAsync(CancellationToken.None).GetAwaiter().GetResult()!.Outcome);
            AssertEqual(3L, sink.DroppedByServer);
            AssertEqual(0, sink.PendingCount);
            AssertEqual(1, logs.Entries.Count(e => e.Level == LogLevel.Warning && e.Message.Contains("rejected")));
            AssertTrue(logs.Messages.Any(m => m.Contains("entry too far behind")), "response body not reported");

            // Retry-After beats a shorter backoff; the backoff doubles up to 60 s.
            statuses.Enqueue(new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Headers = { RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(30)) } });
            sink.Queue.TryEnqueue(LokiStreamKind.Log, "info", "throttled");
            sink.FlushOnceAsync(CancellationToken.None).GetAwaiter().GetResult();
            AssertEqual(now + TimeSpan.FromSeconds(30), sink.RetryAt);
            var delays = new List<double>();
            for (var i = 0; i < 6; i++)
            {
                now = sink.RetryAt;
                statuses.Enqueue(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
                sink.FlushOnceAsync(CancellationToken.None).GetAwaiter().GetResult();
                delays.Add((sink.RetryAt - now).TotalSeconds);
            }

            AssertEqual("2,4,8,16,32,60", string.Join(",", delays));
            AssertEqual(1, sink.PendingCount);

            // 5xx is the server's problem: no attempt limit, the batch is kept until it is accepted.
            for (var i = 0; i < 10; i++)
            {
                now = sink.RetryAt;
                statuses.Enqueue(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
                sink.FlushOnceAsync(CancellationToken.None).GetAwaiter().GetResult();
                AssertEqual(now + TimeSpan.FromSeconds(60), sink.RetryAt);
            }

            AssertEqual(1, sink.PendingCount);
            now = sink.RetryAt;
            statuses.Enqueue(new HttpResponseMessage(HttpStatusCode.NoContent));
            AssertEqual(LokiPushOutcome.Success, sink.FlushOnceAsync(CancellationToken.None).GetAwaiter().GetResult()!.Outcome);
            AssertTrue(bodies[^1].Contains("throttled"), "the retried batch was not delivered");
            AssertEqual(0, sink.PendingCount);
            AssertEqual(3L, sink.DroppedByServer);
            AssertTrue(logs.Messages.All(m => !m.Contains("gave up")), "a 5xx batch was given up");
        }),
        ("Loki logger provider: level filter, disabled sink, and no recursion from the sink or HttpClient", () =>
        {
            var queue = new LokiEntryQueue(100);
            using var factory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Trace).AddProvider(new LokiLoggerProvider(queue)));
            factory.CreateLogger("ResourceMonitorDaemon.MonitorService").LogInformation("hello {Name}", "world");
            factory.CreateLogger("ResourceMonitorDaemon.MonitorService").LogDebug("below min level");
            factory.CreateLogger<LokiSink>().LogWarning("sink failure");
            factory.CreateLogger<ObservabilityService>().LogWarning("pipeline message");
            factory.CreateLogger("System.Net.Http.HttpClient.Default.LogicalHandler").LogInformation("http log");
            var entry = queue.DequeueBatch(10).Single();
            AssertEqual("MonitorService: hello world", entry.Line);
            AssertEqual("info", entry.Level);
            AssertEqual(LokiStreamKind.Log, entry.Kind);

            queue.MinLogLevel = LogLevel.Warning;
            factory.CreateLogger("Tray").LogInformation("info dropped");
            factory.CreateLogger("Tray").LogError(new InvalidOperationException("boom"), "failed");
            entry = queue.DequeueBatch(10).Single();
            AssertEqual("error", entry.Level);
            AssertTrue(entry.Line.StartsWith("Tray: failed\n") && entry.Line.Contains("boom"), "exception not appended: " + entry.Line);

            queue.Accepting = false;
            factory.CreateLogger("Tray").LogError("while disabled");
            AssertEqual(0, queue.Count);

            // A failing sink logs through a factory that includes the Loki provider: nothing is re-enqueued.
            var sinkQueue = new LokiEntryQueue(100);
            using var sinkFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Trace).AddProvider(new LokiLoggerProvider(sinkQueue)));
            var sink = new LokiSink(sinkQueue, sinkFactory.CreateLogger<LokiSink>(), new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)));
            sink.Configure(new LokiSettings { Enabled = true, Url = "http://loki.test" });
            sinkQueue.TryEnqueue(LokiStreamKind.Log, "info", "x");
            sink.FlushOnceAsync(CancellationToken.None).GetAwaiter().GetResult();
            AssertEqual(0, sinkQueue.Count);
            AssertEqual(1, sink.PendingCount);
        }),

        // ---- Settings -----------------------------------------------------------------------
        ("Loki settings: defaults, lenient parsing and sanitizing with warnings", () =>
        {
            var defaults = new LokiSettings();
            AssertTrue(!defaults.Enabled && !defaults.IsActive && defaults.Url == "" && defaults.AuthMode == LokiAuthMode.None, "defaults");
            AssertEqual(10, defaults.MetricsIntervalSeconds);
            AssertEqual(LogLevel.Information, defaults.MinLevel);
            AssertEqual(500, defaults.BatchMaxEntries);
            AssertEqual(5000, defaults.QueueCapacity);
            AssertEqual(2000, defaults.FlushIntervalMs);
            AssertEqual(0, LokiSettingsValidator.Validate(defaults).Count);
            AssertTrue(LokiSettingsLoader.Load(new ConfigurationBuilder().Build(), new List<string>(), null) == defaults, "empty configuration differs from defaults");

            var warnings = new List<string>();
            var loaded = LokiSettingsLoader.Load(Config(new()
            {
                ["Observability:Loki:Enabled"] = "yes",
                ["Observability:Loki:Url"] = "ftp://loki",
                ["Observability:Loki:AuthMode"] = "Digest",
                ["Observability:Loki:Username"] = "a:b",
                ["Observability:Loki:TenantId"] = "bad tenant!",
                ["Observability:Loki:MetricsIntervalSeconds"] = "-5",
                ["Observability:Loki:MinLevel"] = "Loud",
                ["Observability:Loki:BatchMaxEntries"] = "999999",
                ["Observability:Loki:QueueCapacity"] = "abc",
                ["Observability:Loki:FlushIntervalMs"] = "5",
            }), warnings, null);
            AssertTrue(!loaded.Enabled && loaded.Url == "" && loaded.AuthMode == LokiAuthMode.None, "invalid values not replaced");
            AssertEqual("", loaded.Username);
            AssertEqual("", loaded.TenantId);
            AssertEqual(0, loaded.MetricsIntervalSeconds);
            AssertEqual(LogLevel.Information, loaded.MinLevel);
            AssertEqual(LokiSettingsValidator.MaxBatchMaxEntries, loaded.BatchMaxEntries);
            AssertEqual(LokiSettings.DefaultQueueCapacity, loaded.QueueCapacity);
            AssertEqual(LokiSettingsValidator.MinFlushIntervalMs, loaded.FlushIntervalMs);
            AssertTrue(warnings.Count >= 10, $"expected one warning per bad value, got {warnings.Count}: {string.Join(" | ", warnings)}");
            AssertEqual(0, LokiSettingsValidator.Validate(loaded).Count);

            var valid = LokiSettingsLoader.Load(Config(new()
            {
                ["Observability:Loki:Enabled"] = "true",
                ["Observability:Loki:Url"] = " http://loki:3100 ",
                ["Observability:Loki:AuthMode"] = "bearer",
                ["Observability:Loki:MinLevel"] = "warning",
                ["Observability:Loki:Gzip"] = "false",
            }), warnings = new List<string>(), null);
            AssertTrue(valid.IsActive && valid.Url == "http://loki:3100" && valid.AuthMode == LokiAuthMode.Bearer && !valid.Gzip, "valid values");
            AssertEqual(LogLevel.Warning, valid.MinLevel);
            AssertTrue(warnings.Count == 1 && warnings[0].Contains("no token"), "expected only the missing-token warning: " + string.Join(" | ", warnings));
            AssertTrue(!LokiSettingsLoader.Load(Config(new() { ["Observability:Loki:AuthMode"] = "1" }), new List<string>(), null).AuthMode.Equals(LokiAuthMode.Basic), "numeric enum accepted");
        }),
        ("Loki token: plain token in a settings file is ignored, environment token is used", () =>
        {
            using var temp = new TempDir();
            File.WriteAllText(Path.Combine(temp.Path, "appsettings.json"), "{ \"Observability\": { \"Loki\": { \"Token\": \"" + Secret + "\" } } }");
            var userPath = Path.Combine(temp.Path, "settings.json");
            using var env = new EnvironmentVariable(LokiSettings.TokenEnvironmentVariable, null);
            var warnings = new List<string>();
            var fromFile = LokiSettingsLoader.Load(new ConfigurationBuilder().AddDaemonSources(temp.Path, userPath, reloadOnChange: false).Build(), warnings, new DpapiSecretProtector());
            AssertEqual("", fromFile.Token);
            AssertTrue(warnings.Any(w => w.Contains("ignored")) && warnings.All(w => !w.Contains(Secret)), "plain file token not reported safely");

            env.Set("env-token-1");
            var fromEnv = LokiSettingsLoader.Load(new ConfigurationBuilder().AddDaemonSources(temp.Path, userPath, reloadOnChange: false).Build(), new List<string>(), new DpapiSecretProtector());
            AssertEqual("env-token-1", fromEnv.Token);
            AssertEqual(LokiTokenSource.Environment, fromEnv.TokenSource);
        }),
        ("Loki token: DPAPI round-trip, env precedence, never persisted in plain text or from env", () =>
        {
            var protector = new DpapiSecretProtector();
            var cipher = protector.Protect(Secret);
            AssertTrue(!cipher.Contains(Secret), "ciphertext contains the secret");
            AssertTrue(protector.TryUnprotect(cipher, out var plain) && plain == Secret, "round-trip failed");
            AssertTrue(!protector.TryUnprotect("not base64!", out _), "garbage decrypted");
            AssertTrue(!protector.TryUnprotect(Convert.ToBase64String(new byte[64]), out _), "random bytes decrypted");

            using var temp = new TempDir();
            var userPath = Path.Combine(temp.Path, "settings.json");
            var store = new UserSettingsStore(userPath);
            store.Update(patch => patch.Port = 1885);
            store.UpdateLoki(patch =>
            {
                patch.Enabled = true;
                patch.Url = "http://loki:3100";
                patch.AuthMode = LokiAuthMode.Bearer;
                patch.SetToken(Secret, protector);
            });
            var text = File.ReadAllText(userPath);
            AssertTrue(!text.Contains(Secret), "plain token written to settings.json");
            var root = JsonNode.Parse(text)!.AsObject();
            AssertEqual(1885, root["Device"]!["Port"]!.GetValue<int>());
            AssertEqual("Bearer", root["Observability"]!["Loki"]!["AuthMode"]!.GetValue<string>());
            AssertTrue(root["Observability"]!["Loki"]!["TokenProtected"] != null, "TokenProtected missing");
            AssertTrue(store.LoadLoki().AuthMode == LokiAuthMode.Bearer && store.LoadLoki().Enabled == true, "patch round-trip");
            store.Update(patch => patch.Port = 1886);
            AssertTrue(File.ReadAllText(userPath).Contains("TokenProtected"), "device update dropped the Loki section");

            using var envToken = new EnvironmentVariable(LokiSettings.TokenEnvironmentVariable, null);
            using var envUrl = new EnvironmentVariable("RESMON_Observability__Loki__Url", null);
            var effective = LoadLoki(temp.Path, userPath, protector);
            AssertEqual(Secret, effective.Token);
            AssertEqual(LokiTokenSource.Protected, effective.TokenSource);

            envToken.Set("env-wins");
            envUrl.Set("http://env-loki:3100");
            var envFields = DaemonConfiguration.GetEnvironmentOverriddenLokiFields();
            AssertTrue(envFields.Contains("Token") && envFields.Contains("Url"), "env fields: " + string.Join(",", envFields));
            var displayed = LoadLoki(temp.Path, userPath, protector);
            AssertEqual("env-wins", displayed.Token);
            AssertEqual(LokiTokenSource.Environment, displayed.TokenSource);
            AssertEqual("http://env-loki:3100", displayed.Url);

            // The dialog never persists env values: only the changed, non-env field is written.
            var lower = DaemonConfiguration.LoadLowerLokiLayers(temp.Path);
            store.UpdateLoki(patch => patch.ApplyDialogEdit(lower, displayed, displayed with { MetricsIntervalSeconds = 30, Url = "http://edited:3100" }, envFields));
            text = File.ReadAllText(userPath);
            AssertTrue(!text.Contains("env-wins") && !text.Contains("env-loki") && !text.Contains("edited"), "env value persisted: " + text);
            AssertEqual(30, store.LoadLoki().MetricsIntervalSeconds);
            AssertEqual("http://loki:3100", store.LoadLoki().Url);

            // Clearing the token and every other field removes the whole section again.
            store.UpdateLoki(patch => patch.TokenProtected = null);
            AssertTrue(!File.ReadAllText(userPath).Contains("TokenProtected"), "token not cleared");
            store.UpdateLoki(patch =>
            {
                patch.Enabled = null;
                patch.Url = null;
                patch.AuthMode = null;
                patch.MetricsIntervalSeconds = null;
            });
            AssertTrue(store.LoadLoki().IsEmpty, "patch not empty: " + store.LoadLoki());
            AssertTrue(JsonNode.Parse(File.ReadAllText(userPath))!["Observability"] == null, "empty Observability section left behind");
            AssertThrows<ArgumentException>(() => store.UpdateLoki(patch => patch.Url = "ftp://nope"));
            AssertThrows<ArgumentException>(() => store.UpdateLoki(patch => patch.TokenProtected = "not base64!"));
        }),
        ("Live reload: enabling Loki in settings.json reaches IOptionsMonitor without restart", () =>
        {
            using var temp = new TempDir();
            var userPath = Path.Combine(temp.Path, "settings.json");
            var configuration = new ConfigurationBuilder().AddDaemonSources(temp.Path, userPath).Build();
            using var services = new ServiceCollection()
                .AddLogging()
                .AddSingleton<IConfiguration>(configuration)
                .AddSingleton<ISecretProtector, DpapiSecretProtector>()
                .AddLokiSettings()
                .BuildServiceProvider();
            var monitor = services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<LokiSettings>>();
            AssertTrue(!monitor.CurrentValue.IsActive, "active by default");
            var changes = 0;
            using var subscription = monitor.OnChange((_, _) => Interlocked.Increment(ref changes));
            try
            {
                new UserSettingsStore(userPath).UpdateLoki(patch => { patch.Enabled = true; patch.Url = "http://loki:3100"; });
                var stopwatch = Stopwatch.StartNew();
                while (!monitor.CurrentValue.IsActive && stopwatch.ElapsedMilliseconds < 5000)
                {
                    Thread.Sleep(50);
                }

                AssertTrue(monitor.CurrentValue.IsActive, "enable not picked up");
                AssertEqual("loki:3100", monitor.CurrentValue.UrlHost);
                AssertTrue(changes > 0, "no change notification");
            }
            finally
            {
                (configuration as IDisposable)?.Dispose();
            }
        }),
        ("Sink Configure: disabling stops collection and clears queued entries; re-enabling accepts again", () =>
        {
            var sink = new LokiSink(new LokiEntryQueue(), NullLogger<LokiSink>.Instance, new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent)));
            var enabled = new LokiSettings { Enabled = true, Url = "http://loki.test", QueueCapacity = 200, MinLevel = LogLevel.Warning };
            sink.Configure(enabled);
            AssertTrue(sink.Queue.Accepting && sink.Queue.Capacity == 200 && sink.Queue.MinLogLevel == LogLevel.Warning, "settings not applied to the queue");
            sink.Queue.TryEnqueue(LokiStreamKind.Log, "warn", "x");
            sink.Configure(enabled with { Enabled = false });
            AssertTrue(!sink.Queue.Accepting && sink.Queue.Count == 0, "disabled sink still collects");
            AssertTrue(!sink.Queue.TryEnqueue(LokiStreamKind.Log, "warn", "y"), "enqueued while disabled");
            sink.Configure(enabled with { Url = "http://other.test" });
            AssertTrue(sink.Queue.TryEnqueue(LokiStreamKind.Log, "warn", "z"), "re-enabled sink refuses entries");
        }),

        // ---- Device status --------------------------------------------------------------------
        ("Device status validation: object with v=1 accepted; v=2, non-object, oversize, malformed rejected", () =>
        {
            static bool Valid(string text) => DeviceStatusValidator.TryValidate(Encoding.UTF8.GetBytes(text), out _, out _);
            const string sample = "{\"v\":1,\"build\":\"phase4-ble-prov\",\"uptime_s\":1234,\"heap_free\":182344,\"heap_min\":150000,\"rssi\":-61,\"wifi\":\"connected\",\"prov\":\"provisioned\",\"dac\":true}";
            AssertTrue(DeviceStatusValidator.TryValidate(Encoding.UTF8.GetBytes(sample), out var json, out _) && json == sample, "contract sample rejected or altered");
            AssertTrue(Valid("{\"v\":1,\"future_field\":[1,2]}"), "unknown fields must be ignored");
            AssertTrue(!Valid("{\"v\":2,\"build\":\"x\"}"), "v=2 accepted");
            AssertTrue(!Valid("{\"v\":0}"), "v=0 accepted");
            AssertTrue(!Valid("{\"v\":1.5}"), "non-integer v accepted");
            AssertTrue(!Valid("{\"v\":\"1\"}"), "string v accepted");
            AssertTrue(!Valid("{\"build\":\"x\"}"), "missing v accepted");
            AssertTrue(!Valid("[1,2]"), "array accepted");
            AssertTrue(!Valid("42.5"), "number accepted");
            AssertTrue(!Valid("{\"v\":1,"), "malformed accepted");
            AssertTrue(!Valid(""), "empty accepted");
            AssertTrue(!Valid("{\"v\":1,\"pad\":\"" + new string('x', 1100) + "\"}"), "oversize accepted");
            AssertTrue(Valid("{\"v\":1,\"pad\":\"" + new string('x', 1000) + "\"}"), "1 KB payload rejected");
            AssertTrue(!DeviceStatusValidator.TryValidate(new byte[] { 0x7B, 0xFF, 0x7D }, out _, out _), "invalid UTF-8 accepted");
        }),
        ("DeviceSettings.StatusTopic: default, validation and collision with metric topics", () =>
        {
            AssertEqual("resmon/status", new DeviceSettings().StatusTopic);
            AssertEqual(0, DeviceSettingsValidator.Validate(new DeviceSettings()).Count);
            AssertTrue(DeviceSettingsValidator.Validate(new DeviceSettings { StatusTopic = "resmon/#" }).Count > 0, "wildcard status topic");
            AssertTrue(DeviceSettingsValidator.Validate(new DeviceSettings { StatusTopic = "resmon/cpu" }).Count > 0, "status equal to cpu topic");
            var warnings = new List<string>();
            var sanitized = DeviceSettingsValidator.Sanitize(new DeviceSettings { StatusTopic = "resmon/mem" }, warnings);
            AssertEqual("resmon/status", sanitized.StatusTopic);
            AssertEqual(1, warnings.Count);
            var loaded = DeviceSettingsLoader.Load(Config(new() { ["Device:StatusTopic"] = "lab/status" }), new List<string>());
            AssertEqual("lab/status", loaded.StatusTopic);

            using var temp = new TempDir();
            var store = new UserSettingsStore(Path.Combine(temp.Path, "settings.json"));
            store.Update(patch => patch.StatusTopic = "lab/status");
            AssertEqual("lab/status", store.Load().StatusTopic);
            AssertThrows<ArgumentException>(() => store.Update(patch => patch.StatusTopic = "resmon/+"));
        }),
        ("Metrics forwarder: one JSON line per interval from existing samples; off when 0 or disabled", () =>
        {
            AssertEqual(
                "{\"cpu\":12.3,\"mem\":45,\"mem_used_gb\":7.21,\"mem_total_gb\":15.86,\"mqtt\":\"connected\"}",
                LokiMetricsForwarder.FormatLine(new MetricsSample(12.34, 45, 7.2149, 15.8612, "connected")));
            AssertEqual(
                "{\"cpu\":100,\"mem\":0,\"mem_used_gb\":0,\"mem_total_gb\":0,\"mqtt\":\"paused\"}",
                LokiMetricsForwarder.FormatLine(new MetricsSample(130, -1, double.NaN, -2, "paused")));

            var now = DateTimeOffset.UnixEpoch;
            var sink = new LokiSink(new LokiEntryQueue(), NullLogger<LokiSink>.Instance, new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent)));
            var forwarder = new LokiMetricsForwarder(sink, () => now);
            var settings = new LokiSettings { Enabled = true, Url = "http://loki.test", MetricsIntervalSeconds = 10 };
            sink.Configure(settings);
            for (var i = 0; i < 25; i++)
            {
                forwarder.OnSample(new MetricsSample(1, 2, 3, 4, "connected"));
                now += TimeSpan.FromMilliseconds(i % 2 == 0 ? 999 : 1001);   // sampling jitter
            }

            var lines = sink.Queue.DequeueBatch(100);
            AssertEqual(3, lines.Count);
            AssertTrue(lines.All(l => l.Kind == LokiStreamKind.Metrics), "wrong stream");

            sink.Configure(settings with { MetricsIntervalSeconds = 0 });
            forwarder.OnSample(new MetricsSample(1, 2, 3, 4, "connected"));
            sink.Configure(settings with { Enabled = false });
            forwarder.OnSample(new MetricsSample(1, 2, 3, 4, "connected"));
            AssertEqual(0, sink.Queue.Count);
        }),

        // ---- Review fixes: batch limits, give-up, split, backoff, redaction, rate limit ----------
        ("Loki batches are capped at 1 MiB of line data and still deliver everything", () =>
        {
            var bodies = new List<(int Count, int Bytes)>();
            var sink = new LokiSink(new LokiEntryQueue(), NullLogger<LokiSink>.Instance, new FakeHandler(request =>
            {
                var body = request.Content!.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                bodies.Add((CountValues(Encoding.UTF8.GetString(body)), body.Length));
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }));
            sink.Configure(new LokiSettings { Enabled = true, Url = "http://loki.test", BatchMaxEntries = 500, Gzip = false });
            for (var i = 0; i < 300; i++)
            {
                sink.Queue.TryEnqueue(LokiStreamKind.Log, "info", i.ToString("D5") + new string('x', 10_000));
            }

            while (sink.FlushOnceAsync(CancellationToken.None).GetAwaiter().GetResult() != null)
            {
            }

            AssertEqual(300, bodies.Sum(b => b.Count));
            AssertTrue(bodies.Count >= 3, "expected several capped batches, got " + bodies.Count);
            AssertTrue(bodies.All(b => b.Bytes <= LokiSink.MaxBatchBytes + 4096), "batch over 1 MiB: " + string.Join(",", bodies.Select(b => b.Bytes)));

            var queue = new LokiEntryQueue(10);
            queue.TryEnqueue(LokiStreamKind.Log, "info", new string('y', 50_000));
            AssertTrue(queue.DequeueBatch(10, maxBytes: 100).Single().Line.Length <= LokiEntryQueue.MaxLineLength + 20, "oversized entry not truncated / not returned alone");
        }),
        ("A 15-minute network outage loses nothing that is still queued: 100% delivered after recovery", () =>
        {
            var now = DateTimeOffset.UnixEpoch;
            var outageEnds = now + TimeSpan.FromMinutes(15);
            var delivered = new List<string>();
            var failures = 0;
            var sink = new LokiSink(new LokiEntryQueue(), NullLogger<LokiSink>.Instance, new FakeHandler(request =>
            {
                if (now < outageEnds)
                {
                    failures++;
                    if (failures % 3 == 0)
                    {
                        return new HttpResponseMessage(HttpStatusCode.BadGateway);   // mix in 5xx
                    }

                    throw new HttpRequestException("No connection could be made because the target machine actively refused it.");
                }

                delivered.AddRange(Lines(request));
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }), () => now);
            sink.Configure(new LokiSettings { Enabled = true, Url = "http://loki.test", FlushIntervalMs = 2000, Gzip = false });

            // 6 entries per minute for 20 minutes, driven like the sender loop in 1 s steps.
            var produced = new List<string>();
            for (var second = 0; second < 20 * 60; second++)
            {
                if (second % 10 == 0)
                {
                    var line = "entry " + second;
                    produced.Add(line);
                    sink.Queue.TryEnqueue(LokiStreamKind.Log, "info", line);
                }

                while (sink.IsFlushDue(now))
                {
                    sink.FlushOnceAsync(CancellationToken.None).GetAwaiter().GetResult();
                }

                now += TimeSpan.FromSeconds(1);
            }

            while (sink.FlushOnceAsync(CancellationToken.None).GetAwaiter().GetResult() != null)
            {
            }

            AssertTrue(failures > 10, "outage not exercised: " + failures);
            AssertEqual(0L, sink.DroppedByServer);
            AssertEqual(0L, sink.Queue.TotalDropped);
            AssertEqual(string.Join(",", produced), string.Join(",", delivered));   // all, once, in order
        }),
        ("Only a single entry still throttled (429 without Retry-After) after splitting is given up", () =>
        {
            var now = DateTimeOffset.UnixEpoch;
            var logs = new CapturingLogs();
            using var factory = logs.Factory();
            var withRetryAfter = 0;
            var sink = new LokiSink(new LokiEntryQueue(), factory.CreateLogger<LokiSink>(), new FakeHandler(_ =>
                withRetryAfter-- > 0
                    ? new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Headers = { RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(5)) } }
                    : new HttpResponseMessage(HttpStatusCode.TooManyRequests)), () => now);
            sink.Configure(new LokiSettings { Enabled = true, Url = "http://loki.test" });

            // With Retry-After the server is just pacing us: retried, never given up.
            withRetryAfter = 20;
            sink.Queue.TryEnqueue(LokiStreamKind.Log, "info", "paced");
            for (var i = 0; i < 20; i++)
            {
                sink.FlushOnceAsync(CancellationToken.None).GetAwaiter().GetResult();
            }

            AssertEqual(1, sink.PendingCount);
            AssertEqual(0L, sink.DroppedByServer);

            // Without Retry-After: the single entry is given up after the third throttle.
            for (var i = 0; i < 3; i++)
            {
                sink.FlushOnceAsync(CancellationToken.None).GetAwaiter().GetResult();
            }

            AssertEqual(0, sink.PendingCount);
            AssertEqual(1L, sink.DroppedByServer);
            AssertEqual(1, logs.Entries.Count(e => e.Level == LogLevel.Warning && e.Message.Contains("gave up on 1 entry")));
        }),
        ("Batch byte cap holds for CJK text, which is sent as raw UTF-8 with valid escaping", () =>
        {
            var bodies = new List<byte[]>();
            var sink = new LokiSink(new LokiEntryQueue(), NullLogger<LokiSink>.Instance, new FakeHandler(request =>
            {
                bodies.Add(request.Content!.ReadAsByteArrayAsync().GetAwaiter().GetResult());
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }));
            sink.Configure(new LokiSettings { Enabled = true, Url = "http://loki.test", BatchMaxEntries = 5000, Gzip = false });
            var originals = new List<string>();
            for (var i = 0; i < 200; i++)
            {
                var line = $"{i:D3} 设备状态 \"quoted\" back\\slash \u0001\n" + string.Concat(Enumerable.Repeat("资源监视器日志", 700));
                originals.Add(line);
                sink.Queue.TryEnqueue(LokiStreamKind.Log, "info", line);
            }

            while (sink.FlushOnceAsync(CancellationToken.None).GetAwaiter().GetResult() != null)
            {
            }

            AssertTrue(bodies.Count >= 3, "expected several batches, got " + bodies.Count);
            AssertTrue(bodies.All(b => b.Length <= LokiSink.MaxBatchBytes + 4096), "CJK batch over the cap: " + string.Join(",", bodies.Select(b => b.Length)));
            var text = Encoding.UTF8.GetString(bodies[0]);
            AssertTrue(text.Contains("资源监视器日志") && !text.Contains("\\u8d44"), "CJK text not sent as raw UTF-8");
            AssertTrue(text.Contains("\\\"quoted\\\"") && text.Contains("\\u0001") && text.Contains("\\n"), "quotes/control characters not escaped");
            var received = bodies.SelectMany(b => JsonNode.Parse(b)!["streams"]!.AsArray().SelectMany(s => s!["values"]!.AsArray().Select(v => v![1]!.GetValue<string>()))).ToList();
            AssertEqual(string.Join("|", originals), string.Join("|", received));
        }),
        ("Loki splits a batch on 413 and after repeated 429, keeping entry order", () =>
        {
            var sizes = new List<int>();
            var delivered = new List<string>();
            var sink = new LokiSink(new LokiEntryQueue(), NullLogger<LokiSink>.Instance, new FakeHandler(request =>
            {
                var lines = Lines(request);
                sizes.Add(lines.Count);
                if (lines.Count > 1)
                {
                    return new HttpResponseMessage(HttpStatusCode.RequestEntityTooLarge);
                }

                delivered.AddRange(lines);
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }));
            sink.Configure(new LokiSettings { Enabled = true, Url = "http://loki.test", Gzip = false });
            foreach (var line in new[] { "1", "2", "3", "4" })
            {
                sink.Queue.TryEnqueue(LokiStreamKind.Log, "info", line);
            }

            while (sink.FlushOnceAsync(CancellationToken.None).GetAwaiter().GetResult() != null)
            {
            }

            AssertEqual("4,2,1,1,2,1,1", string.Join(",", sizes));
            AssertEqual("1,2,3,4", string.Join(",", delivered));
            AssertEqual(0L, sink.DroppedByServer);

            // 429 three times on the same batch: split in half, then both halves are delivered.
            var now = DateTimeOffset.UnixEpoch;
            var throttles = 3;
            sizes.Clear();
            var throttled = new LokiSink(new LokiEntryQueue(), NullLogger<LokiSink>.Instance, new FakeHandler(request =>
            {
                sizes.Add(Lines(request).Count);
                return throttles-- > 0 ? new HttpResponseMessage(HttpStatusCode.TooManyRequests) : new HttpResponseMessage(HttpStatusCode.NoContent);
            }), () => now);
            throttled.Configure(new LokiSettings { Enabled = true, Url = "http://loki.test", Gzip = false });
            foreach (var line in new[] { "1", "2", "3", "4" })
            {
                throttled.Queue.TryEnqueue(LokiStreamKind.Log, "info", line);
            }

            for (var i = 0; i < 5; i++)
            {
                now = throttled.RetryAt > now ? throttled.RetryAt : now;
                throttled.FlushOnceAsync(CancellationToken.None).GetAwaiter().GetResult();
            }

            AssertEqual("4,4,4,2,2", string.Join(",", sizes));
            AssertEqual(0, throttled.PendingCount);
        }),
        ("Loki backs off after 401/403/404 instead of retrying every flush interval", () =>
        {
            var now = DateTimeOffset.UnixEpoch;
            var statuses = new Queue<HttpStatusCode>(new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.NotFound });
            var sink = new LokiSink(new LokiEntryQueue(), NullLogger<LokiSink>.Instance,
                new FakeHandler(_ => new HttpResponseMessage(statuses.Dequeue())), () => now);
            sink.Configure(new LokiSettings { Enabled = true, Url = "http://loki.test", FlushIntervalMs = 100 });
            var delays = new List<double>();
            for (var i = 0; i < 3; i++)
            {
                sink.Queue.TryEnqueue(LokiStreamKind.Log, "info", "x" + i);
                AssertEqual(LokiPushOutcome.Drop, sink.FlushOnceAsync(CancellationToken.None).GetAwaiter().GetResult()!.Outcome);
                delays.Add((sink.RetryAt - now).TotalSeconds);
                sink.Queue.TryEnqueue(LokiStreamKind.Log, "info", "queued");
                AssertTrue(!sink.IsFlushDue(now + TimeSpan.FromMilliseconds(500)), "flush due during the backoff");
                now = sink.RetryAt;
                sink.Queue.DequeueBatch(10);
            }

            AssertEqual("1,2,4", string.Join(",", delays));
            AssertEqual(0, sink.PendingCount);
            AssertEqual(3L, sink.DroppedByServer);
        }),
        ("Log redactor replaces user folders, user name and (unless opted in) the PC name", () =>
        {
            var redactor = new LokiLogRedactor(
                new[]
                {
                    (@"C:\Users\Alice", "%USERPROFILE%"),
                    (@"C:\Users\Alice\AppData\Local\", "%LOCALAPPDATA%"),
                    (@"C:\Users\Alice\AppData\Roaming", "%APPDATA%"),
                },
                "Alice",
                "DESKTOP-42");
            AssertEqual(@"user=%LOCALAPPDATA%\ResourceMonitorDaemon\settings.json", redactor.Redact(@"user=c:\users\alice\appdata\local\ResourceMonitorDaemon\settings.json", keepHostName: false));
            AssertEqual(@"%APPDATA%\x and %USERPROFILE%\Desktop", redactor.Redact(@"C:\Users\Alice\AppData\Roaming\x and C:\Users\Alice\Desktop", false));
            AssertEqual("<user> logged in; Alicena and malice stay", redactor.Redact("ALICE logged in; Alicena and malice stay", false));
            AssertEqual("on <host>.", redactor.Redact("on desktop-42.", false));
            AssertEqual("on desktop-42.", redactor.Redact("on desktop-42.", keepHostName: true));
            AssertEqual("bo was here", new LokiLogRedactor(Array.Empty<(string, string)>(), "bo", null).Redact("bo was here", false));

            // 8.3 short forms: registered folders, plus the generic \Users\<name>\ fallback.
            var withShort = new LokiLogRedactor(new[] { (@"C:\Users\Alice Smith\AppData\Local", "%LOCALAPPDATA%"), (@"C:\Users\ALICES~1\AppData\Local", "%LOCALAPPDATA%") }, "Alice Smith", null);
            AssertEqual(@"temp=%LOCALAPPDATA%\Temp\x.tmp", withShort.Redact(@"temp=C:\Users\ALICES~1\AppData\Local\Temp\x.tmp", false));
            AssertEqual(@"C:\Users\<user>\Documents and /Users/<user>/logs", redactor.Redact(@"C:\Users\JDOEXA~1\Documents and /Users/someone/logs", false));

            // The real TEMP path (often reported in 8.3 form) keeps no part of the user folder name.
            var temp = LokiLogRedactor.ForCurrentUser().Redact(Path.GetTempPath(), false);
            AssertTrue(!System.Text.RegularExpressions.Regex.IsMatch(temp, @"[\\/]Users[\\/](?!<user>)", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "user folder leaked: " + temp);
        }),
        ("End to end: Loki never receives the Windows user name, user folders or (by default) the PC name", () =>
        {
            RedactionEndToEnd().GetAwaiter().GetResult();
        }),
        ("Device status: only contract fields are forwarded, re-serialized with capped strings", () =>
        {
            var payload = "{\"v\":1,\"build\":\"" + new string('b', 100) + "\",\"uptime_s\":5,\"heap_free\":\"lots\",\"heap_min\":-1," +
                "\"rssi\":-70,\"wifi\":\"connected\",\"prov\":\"" + new string('p', 50) + "\",\"dac\":\"yes\",\"secret\":\"" + new string('s', 500) + "\",\"nested\":{\"a\":1}}";
            AssertTrue(DeviceStatusValidator.TryValidate(Encoding.UTF8.GetBytes(payload), out var json, out _), "payload rejected");
            AssertEqual("{\"v\":1,\"build\":\"" + new string('b', 64) + "\",\"uptime_s\":5,\"rssi\":-70,\"wifi\":\"connected\",\"prov\":\"" + new string('p', 32) + "\"}", json);
        }),
        ("Device status is rate-limited (burst 3, then 1 per 5 s) and a flood cannot evict daemon logs", () =>
        {
            var now = DateTimeOffset.UnixEpoch;
            var logs = new CapturingLogs();
            using var factory = logs.Factory();
            var sink = new LokiSink(new LokiEntryQueue(capacity: 100), NullLogger<LokiSink>.Instance, new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent)));
            sink.Configure(new LokiSettings { Enabled = true, Url = "http://loki.test", QueueCapacity = 100 });
            for (var i = 0; i < 10; i++)
            {
                sink.Queue.TryEnqueue(LokiStreamKind.Log, "info", "daemon log " + i);
            }

            var forwarder = new DeviceStatusForwarder(sink, factory.CreateLogger<DeviceStatusForwarder>(), () => now);
            var status = Encoding.UTF8.GetBytes("{\"v\":1,\"pad\":\"" + new string('x', 900) + "\"}");
            for (var i = 0; i < 10_000; i++)
            {
                forwarder.Handle(status);
            }

            var queued = sink.Queue.DequeueBatch(1000);
            AssertEqual(10, queued.Count(e => e.Kind == LokiStreamKind.Log));
            AssertEqual(3, queued.Count(e => e.Kind == LokiStreamKind.Status));
            AssertEqual(9997L, forwarder.RateLimited);
            AssertEqual(1, logs.Entries.Count(e => e.Level == LogLevel.Warning));   // first excess reported once

            now += TimeSpan.FromSeconds(5);
            forwarder.Handle(status);
            forwarder.Handle(status);
            AssertEqual(1, sink.Queue.DequeueBatch(10).Count);
            now += TimeSpan.FromMinutes(1);
            forwarder.Handle(status);   // refilled: forwarded
            for (var i = 0; i < 5; i++)
            {
                forwarder.Handle(status);
            }

            AssertEqual(2, logs.Entries.Count(e => e.Level == LogLevel.Warning));
            AssertTrue(logs.Messages.Last(m => m.Contains("faster than expected")).Contains("9998 message(s)"), "excess count not reported: " + logs.Messages.Last());
        }),
        ("Saving the Loki section removes a hand-written plain Token key (any casing)", () =>
        {
            using var temp = new TempDir();
            var path = Path.Combine(temp.Path, "settings.json");
            File.WriteAllText(path, "{ \"observability\": { \"loki\": { \"token\": \"" + Secret + "\", \"Future\": 1 } } }");
            new UserSettingsStore(path).UpdateLoki(patch => patch.Enabled = true);
            var text = File.ReadAllText(path);
            AssertTrue(!text.Contains(Secret) && !text.Contains("\"token\"", StringComparison.OrdinalIgnoreCase), "plain token survived: " + text);
            AssertTrue(text.Contains("\"Future\"") && text.Contains("\"Enabled\""), "other keys lost: " + text);
        }),
        ("Sanitize warns about credentials over http and the Test path detects it", () =>
        {
            var insecure = new LokiSettings { Enabled = true, Url = "http://loki:3100", AuthMode = LokiAuthMode.Bearer, Token = Secret };
            var warnings = new List<string>();
            LokiSettingsValidator.Sanitize(insecure, warnings);
            AssertTrue(warnings.Count == 1 && warnings[0].Contains("unencrypted"), "warnings: " + string.Join(" | ", warnings));
            AssertTrue(LokiUrl.SendsCredentialsInsecurely(insecure), "insecure not detected");
            AssertTrue(!LokiUrl.SendsCredentialsInsecurely(insecure with { Url = "https://loki.example" }), "https flagged");
            AssertTrue(!LokiUrl.SendsCredentialsInsecurely(insecure with { AuthMode = LokiAuthMode.None }), "no-auth flagged");
            AssertTrue(!LokiUrl.SendsCredentialsInsecurely(insecure with { Token = "" }), "no-token flagged");
            warnings.Clear();
            LokiSettingsValidator.Sanitize(insecure with { Url = "https://loki.example" }, warnings);
            AssertEqual(0, warnings.Count);
        }),
        ("LokiSink.DisposeAsync releases its signals; later calls are harmless", () =>
        {
            var sink = new LokiSink(new LokiEntryQueue(), NullLogger<LokiSink>.Instance, new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent)));
            var settings = new LokiSettings { Enabled = true, Url = "http://loki.test", BatchMaxEntries = 1 };
            sink.Configure(settings);
            sink.Start();
            sink.DisposeAsync().AsTask().GetAwaiter().GetResult();
            AssertTrue(sink.Queue.ThresholdReached == null, "queue still signals the disposed sink");
            sink.Configure(settings);                                   // Wake on a disposed semaphore
            sink.Queue.TryEnqueue(LokiStreamKind.Log, "info", "late");
            sink.StopAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
            sink.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }),

        // ---- End to end -------------------------------------------------------------------
        ("End to end: fake Loki (HttpListener) receives logs, metrics and status, gzip-decoded, and the test push", () =>
        {
            FakeLokiEndToEnd().GetAwaiter().GetResult();
        }),
        ("Shutdown flush never exceeds its budget when Loki does not answer", () =>
        {
            ShutdownBudget().GetAwaiter().GetResult();
        }),
        ("MQTT resmon/status reaches the sink, survives a broker restart, and follows a topic change", () =>
        {
            StatusOverMqtt().GetAwaiter().GetResult();
        }),
    };

    private static async Task RedactionEndToEnd()
    {
        using var loki = new FakeLoki();
        var queue = new LokiEntryQueue();
        using var factory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Trace).AddProvider(new LokiLoggerProvider(queue)));
        var sink = new LokiSink(queue, NullLogger<LokiSink>.Instance);
        var settings = new LokiSettings { Enabled = true, Url = loki.BaseUrl, FlushIntervalMs = 100 };
        sink.Configure(settings);
        sink.Start();
        try
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var logger = factory.CreateLogger("ResourceMonitorDaemon.MonitorService");
            logger.LogInformation("Log file: {Path}", Path.Combine(local, "ResourceMonitorDaemon", "daemon.log"));
            logger.LogInformation("Running as {User} on {Host}", Environment.UserName, Environment.MachineName);
            await loki.WaitForAsync(l => l.Lines("log").Count() >= 2, "redacted lines");

            var lines = loki.Lines("log").ToList();
            AssertTrue(lines.Any(l => l.Contains(@"%LOCALAPPDATA%\ResourceMonitorDaemon\daemon.log")), "folder not replaced: " + string.Join(" | ", lines));
            AssertTrue(lines.All(l => !l.Contains(local, StringComparison.OrdinalIgnoreCase)), "local app data path leaked");
            if (Environment.UserName.Length >= LokiLogRedactor.MinNameLength)
            {
                AssertTrue(lines.All(l => !System.Text.RegularExpressions.Regex.IsMatch(l, @"\b" + System.Text.RegularExpressions.Regex.Escape(Environment.UserName) + @"\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)), "user name leaked");
                AssertTrue(lines.Any(l => l.Contains("Running as <user>")), "user placeholder missing");
            }

            AssertTrue(lines.Any(l => l.Contains("on <host>")) && lines.All(l => !l.Contains(Environment.MachineName, StringComparison.OrdinalIgnoreCase)), "PC name leaked");

            // Opting into the host label keeps the PC name in log lines too.
            sink.Configure(settings with { IncludeHostLabel = true });
            logger.LogInformation("Now on {Host}", Environment.MachineName);
            await loki.WaitForAsync(l => l.Lines("log").Any(line => line.Contains("Now on " + Environment.MachineName)), "host kept with IncludeHostLabel");
        }
        finally
        {
            await sink.StopAsync(TimeSpan.FromSeconds(1));
            await sink.DisposeAsync();
        }
    }

    private static List<string> Lines(HttpRequestMessage request) =>
        JsonNode.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult())!["streams"]!.AsArray()
            .SelectMany(s => s!["values"]!.AsArray().Select(v => v![1]!.GetValue<string>())).ToList();

    private static int CountValues(string json) =>
        JsonNode.Parse(json)!["streams"]!.AsArray().Sum(s => s!["values"]!.AsArray().Count);

    private static async Task FakeLokiEndToEnd()
    {
        using var loki = new FakeLoki();
        var logs = new CapturingLogs();
        var queue = new LokiEntryQueue();
        using var factory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Trace);
            builder.AddProvider(new LokiLoggerProvider(queue));
            builder.AddProvider(logs.Provider);
        });
        var sink = new LokiSink(queue, factory.CreateLogger<LokiSink>());
        var metrics = new LokiMetricsForwarder(sink);
        var status = new DeviceStatusForwarder(sink, factory.CreateLogger<DeviceStatusForwarder>());
        var settings = new LokiSettings
        {
            Enabled = true,
            Url = loki.BaseUrl,
            AuthMode = LokiAuthMode.Bearer,
            Token = Secret,
            TenantId = "tenant-e2e",
            IncludeHostLabel = true,
            FlushIntervalMs = 100,
            MetricsIntervalSeconds = 1,
            Gzip = true,
        };
        sink.Configure(settings);
        sink.Start();
        try
        {
            factory.CreateLogger("ResourceMonitorDaemon.MonitorService").LogInformation("e2e hello");
            metrics.OnSample(new MetricsSample(12.3, 45.6, 7.25, 15.9, "connected"));
            status.Handle(Encoding.UTF8.GetBytes("{\"v\":1,\"build\":\"e2e\",\"heap_free\":182344,\"rssi\":-61}"));
            status.Handle(Encoding.UTF8.GetBytes("{\"v\":2}"));   // rejected: must not arrive

            await loki.WaitForAsync(l => l.Lines("status").Any() && l.Lines("metrics").Any() && l.Lines("log").Any(line => line.Contains("e2e hello")), "all three streams");
            var requests = loki.Requests.ToList();
            AssertTrue(requests.All(r => r.ContentEncoding == "gzip"), "request not gzip-encoded");
            AssertTrue(requests.All(r => r.Authorization == "Bearer " + Secret), "auth header missing");
            AssertTrue(requests.All(r => r.Tenant == "tenant-e2e"), "tenant header missing");
            AssertTrue(requests.All(r => r.Path == "/loki/api/v1/push"), "wrong path: " + requests[0].Path);

            var streams = requests.SelectMany(r => r.Streams).ToList();
            AssertTrue(streams.All(s => s.Labels["host"] == Environment.MachineName), "host label missing");
            var statusStream = streams.First(s => s.Labels["kind"] == "status");
            AssertEqual("resmon-device", statusStream.Labels["app"]);
            AssertEqual("{\"v\":1,\"build\":\"e2e\",\"heap_free\":182344,\"rssi\":-61}", statusStream.Values.Single().Line);
            AssertTrue(streams.Where(s => s.Labels["kind"] == "status").Sum(s => s.Values.Count) == 1, "rejected status forwarded");
            var metricsLine = JsonNode.Parse(streams.First(s => s.Labels["kind"] == "metrics").Values[0].Line)!;
            AssertEqual(12.3, metricsLine["cpu"]!.GetValue<double>());
            AssertEqual("connected", metricsLine["mqtt"]!.GetValue<string>());
            AssertEqual("info", streams.First(s => s.Labels["kind"] == "log").Labels["level"]);
            foreach (var stream in streams)
            {
                for (var i = 1; i < stream.Values.Count; i++)
                {
                    AssertTrue(long.Parse(stream.Values[i].Ns) > long.Parse(stream.Values[i - 1].Ns), "timestamps not increasing");
                }
            }

            // Test button path: one line with the (unsaved) settings, HTTP result reported.
            var test = await LokiPushClient.TestAsync(settings with { Enabled = false }, CancellationToken.None);
            AssertEqual(LokiPushOutcome.Success, test.Outcome);
            AssertEqual("HTTP 204 No Content", test.ToString());
            await loki.WaitForAsync(l => l.Lines("log").Contains(LokiPushClient.TestLine), "test line");

            // Shutdown flushes what is still queued.
            factory.CreateLogger("ResourceMonitorDaemon.Tray").LogWarning("last words");
            var stopwatch = Stopwatch.StartNew();
            await sink.StopAsync(TimeSpan.FromSeconds(2));
            AssertTrue(stopwatch.ElapsedMilliseconds < 2500, $"stop took {stopwatch.ElapsedMilliseconds} ms");
            AssertTrue(loki.Requests.Any(r => r.Lines("log").Any(l => l.Contains("last words"))), "queued entry not flushed at shutdown");
            AssertTrue(logs.Messages.All(m => !m.Contains(Secret)), "token logged");
            AssertTrue(loki.Requests.SelectMany(r => r.Lines("log")).All(l => !l.Contains("Loki")), "sink messages were forwarded to Loki");
        }
        finally
        {
            await sink.DisposeAsync();
        }
    }

    private static async Task ShutdownBudget()
    {
        // Accepts TCP connections but never answers HTTP.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var accepted = new List<TcpClient>();
        var acceptLoop = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    accepted.Add(await listener.AcceptTcpClientAsync());
                }
            }
            catch (Exception)
            {
            }
        });

        var sink = new LokiSink(new LokiEntryQueue(), NullLogger<LokiSink>.Instance);
        sink.Configure(new LokiSettings { Enabled = true, Url = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}", FlushIntervalMs = 100 });
        sink.Start();
        sink.Queue.TryEnqueue(LokiStreamKind.Log, "info", "never acknowledged");
        await Task.Delay(300);   // the sender is now waiting for a response
        sink.Queue.TryEnqueue(LokiStreamKind.Log, "info", "still queued");
        var stopwatch = Stopwatch.StartNew();
        await sink.StopAsync(TimeSpan.FromMilliseconds(500));
        AssertTrue(stopwatch.ElapsedMilliseconds < 1200, $"stop took {stopwatch.ElapsedMilliseconds} ms with a 500 ms budget");
        AssertTrue(!sink.Queue.Accepting, "queue still accepting after stop");
        await sink.DisposeAsync();
        listener.Stop();
        await acceptLoop;
        accepted.ForEach(c => c.Dispose());
    }

    private static async Task StatusOverMqtt()
    {
        var now = DateTimeOffset.UnixEpoch;
        var port = FreePort();
        var settings = new DeviceSettings { Host = "127.0.0.1", Port = port, ConnectTimeoutMs = 1000, ReconnectInitialBackoffMs = 1000, ReconnectMaxBackoffMs = 2000 };
        var sink = new LokiSink(new LokiEntryQueue(), NullLogger<LokiSink>.Instance, new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent)));
        sink.Configure(new LokiSettings { Enabled = true, Url = "http://loki.test" });   // not started: entries stay queued
        var forwarder = new DeviceStatusForwarder(sink, NullLogger<DeviceStatusForwarder>.Instance);

        await using var connection = new MqttConnection(NullLogger<MqttConnection>.Instance, () => now);
        connection.StatusMessageReceived += forwarder.Handle;
        connection.Configure(settings);

        var server = await StartBrokerAsync(port);
        try
        {
            connection.MaintainConnection();
            await connection.PendingAttempt;
            AssertTrue(connection.IsConnected, "not connected");

            await PublishAsync(port, "resmon/status", "{\"v\":1,\"build\":\"one\"}");
            await PublishAsync(port, "resmon/status", "{\"v\":2,\"build\":\"future\"}");
            await PublishAsync(port, "resmon/other", "{\"v\":1,\"build\":\"other topic\"}");
            await WaitUntilAsync(() => forwarder.Received == 1 && sink.Queue.Count == 1, "first status");

            // Broker restart: clean session, so the daemon must subscribe again after reconnecting.
            await server.StopAsync();
            server.Dispose();
            await WaitUntilAsync(() => !connection.IsConnected, "connection loss");
            server = await StartBrokerAsync(port);
            now += TimeSpan.FromMinutes(1);
            connection.MaintainConnection();
            await connection.PendingAttempt;
            AssertTrue(connection.IsConnected, "did not reconnect after the broker restart");
            await PublishAsync(port, "resmon/status", "{\"v\":1,\"build\":\"two\"}");
            await WaitUntilAsync(() => forwarder.Received == 2, "status after resubscribe");

            // Topic change without endpoint change: resubscribed in the background.
            connection.Configure(settings with { StatusTopic = "lab/status" });
            await WaitUntilAsync(async () =>
            {
                await PublishAsync(port, "lab/status", "{\"v\":1,\"build\":\"three\"}");
                return forwarder.Received >= 3;
            }, "status on the new topic");
            await Task.Delay(300);   // let a duplicate from the polling publish above arrive first
            var before = forwarder.Received;
            await PublishAsync(port, "resmon/status", "{\"v\":1,\"build\":\"old topic\"}");
            await Task.Delay(300);
            AssertEqual(before, forwarder.Received);

            var lines = sink.Queue.DequeueBatch(100);
            AssertTrue(lines.All(l => l.Kind == LokiStreamKind.Status), "non-status entry");
            AssertTrue(lines[0].Line.Contains("\"one\"") && lines[1].Line.Contains("\"two\"") && lines[2].Line.Contains("\"three\""), "status lines: " + string.Join(" | ", lines.Select(l => l.Line)));
        }
        finally
        {
            await server.StopAsync();
            server.Dispose();
        }
    }

    // ---- helpers -----------------------------------------------------------------------------

    private static JsonObject FindStream(JsonArray streams, string kind, string? level = null) =>
        streams.Select(s => s!.AsObject()).First(s => s["stream"]!["kind"]!.GetValue<string>() == kind
            && (level == null || s["stream"]!["level"]?.GetValue<string>() == level));

    private static string Labels(JsonObject stream) =>
        string.Join(",", stream["stream"]!.AsObject().Select(p => $"{p.Key}={p.Value!.GetValue<string>()}"));

    private static string Gunzip(byte[] data)
    {
        using var input = new GZipStream(new MemoryStream(data), CompressionMode.Decompress);
        using var reader = new StreamReader(input, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static IConfiguration Config(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static LokiSettings LoadLoki(string appSettingsDirectory, string userPath, ISecretProtector protector) =>
        LokiSettingsLoader.Load(new ConfigurationBuilder().AddDaemonSources(appSettingsDirectory, userPath, reloadOnChange: false).Build(), new List<string>(), protector);

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task<MqttServer> StartBrokerAsync(int port)
    {
        var options = new MqttServerOptionsBuilder()
            .WithDefaultEndpoint()
            .WithDefaultEndpointPort(port)
            .WithDefaultEndpointBoundIPAddress(IPAddress.Loopback)
            .WithDefaultEndpointBoundIPV6Address(IPAddress.None)
            .Build();
        var server = new MqttFactory().CreateMqttServer(options);
        await server.StartAsync();
        return server;
    }

    /// <summary>Publishes like the ESP32 firmware would (QoS 0, not retained) from a separate client.</summary>
    private static async Task PublishAsync(int port, string topic, string payload)
    {
        using var client = new MqttFactory().CreateMqttClient();
        await client.ConnectAsync(new MqttClientOptionsBuilder().WithTcpServer("127.0.0.1", port).WithCleanSession().Build());
        await client.PublishAsync(new MqttApplicationMessageBuilder().WithTopic(topic).WithPayload(payload).Build());
        await client.DisconnectAsync();
    }

    private static Task WaitUntilAsync(Func<bool> condition, string what) => WaitUntilAsync(() => Task.FromResult(condition()), what);

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, string what)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds < 5000)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(50);
        }

        throw new InvalidOperationException("timed out waiting for " + what);
    }

    private static void AssertEqual<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"expected '{expected}', got '{actual}'");
        }
    }

    private static void AssertTrue(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void AssertThrows<TException>(Action action) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"expected {typeof(TException).Name}");
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    /// <summary>Sets an environment variable for the scope of a test and restores it afterwards.</summary>
    private sealed class EnvironmentVariable : IDisposable
    {
        private readonly string _name;
        private readonly string? _previous;

        public EnvironmentVariable(string name, string? value)
        {
            _name = name;
            _previous = Environment.GetEnvironmentVariable(name);
            Set(value);
        }

        public void Set(string? value) => Environment.SetEnvironmentVariable(_name, value);

        public void Dispose() => Environment.SetEnvironmentVariable(_name, _previous);
    }

    /// <summary>Captures level, message and exception text of every log call.</summary>
    private sealed class CapturingLogs
    {
        public ConcurrentQueue<(LogLevel Level, string Message)> Entries { get; } = new();

        public IEnumerable<string> Messages => Entries.Select(e => e.Message).ToList();

        public ILoggerProvider Provider => new CaptureProvider(this);

        public ILoggerFactory Factory() => LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Trace).AddProvider(Provider));

        private sealed class CaptureProvider(CapturingLogs owner) : ILoggerProvider, ILogger
        {
            public ILogger CreateLogger(string categoryName) => this;

            public void Dispose()
            {
            }

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                owner.Entries.Enqueue((logLevel, formatter(state, exception) + (exception == null ? "" : " " + exception)));
        }
    }

    private sealed record PushedValue(string Ns, string Line);

    private sealed record PushedStream(Dictionary<string, string> Labels, List<PushedValue> Values);

    private sealed record PushedRequest(string Path, string? ContentEncoding, string? Authorization, string? Tenant, List<PushedStream> Streams)
    {
        public IEnumerable<string> Lines(string kind) =>
            Streams.Where(s => s.Labels["kind"] == kind).SelectMany(s => s.Values.Select(v => v.Line));
    }

    /// <summary>A minimal Loki push endpoint on 127.0.0.1 (random free port) that records requests and answers 204.</summary>
    private sealed class FakeLoki : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Task _loop;

        public FakeLoki()
        {
            var port = FreePort();
            BaseUrl = $"http://127.0.0.1:{port}";
            _listener.Prefixes.Add(BaseUrl + "/");
            _listener.Start();
            _loop = Task.Run(LoopAsync);
        }

        public string BaseUrl { get; }

        public ConcurrentQueue<PushedRequest> Requests { get; } = new();

        public IEnumerable<string> Lines(string kind) => Requests.ToList().SelectMany(r => r.Lines(kind));

        public Task WaitForAsync(Func<FakeLoki, bool> condition, string what) => WaitUntilAsync(() => condition(this), what);

        public void Dispose()
        {
            _listener.Stop();
            _listener.Close();
            try
            {
                _loop.Wait(1000);
            }
            catch (Exception)
            {
            }
        }

        private async Task LoopAsync()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (Exception)
                {
                    return;
                }

                try
                {
                    var request = context.Request;
                    using var body = new MemoryStream();
                    await request.InputStream.CopyToAsync(body);
                    var bytes = body.ToArray();
                    var encoding = request.Headers["Content-Encoding"];
                    var json = encoding == "gzip" ? Gunzip(bytes) : Encoding.UTF8.GetString(bytes);
                    var streams = JsonNode.Parse(json)!["streams"]!.AsArray().Select(s => new PushedStream(
                        s!["stream"]!.AsObject().ToDictionary(p => p.Key, p => p.Value!.GetValue<string>()),
                        s["values"]!.AsArray().Select(v => new PushedValue(v![0]!.GetValue<string>(), v[1]!.GetValue<string>())).ToList())).ToList();
                    Requests.Enqueue(new PushedRequest(request.Url!.AbsolutePath, encoding, request.Headers["Authorization"], request.Headers["X-Scope-OrgID"], streams));
                    context.Response.StatusCode = 204;
                }
                catch (Exception)
                {
                    context.Response.StatusCode = 400;
                }
                finally
                {
                    context.Response.Close();
                }
            }
        }
    }
}
