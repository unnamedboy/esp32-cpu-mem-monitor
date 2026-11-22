using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using MQTTnet;
using MQTTnet.Protocol;
using MQTTnet.Server;
using ResourceMonitorDaemon;

/// <summary>
/// End-to-end check of <see cref="MqttConnection"/> against an in-process MQTTnet broker bound
/// to loopback only. Uses a fake clock so backoff deadlines are deterministic.
/// </summary>
internal static class MqttConnectionScenario
{
    private const string Failed = "MQTT connection failed";
    private const string Reconnected = "MQTT reconnected";

    public static async Task Run()
    {
        var now = DateTimeOffset.UnixEpoch;
        var port = FreeLoopbackPort();
        var notifications = new ConcurrentQueue<string>();
        var received = new ConcurrentQueue<MqttApplicationMessage>();
        var settings = new DeviceSettings
        {
            Host = "127.0.0.1",
            Port = port,
            ConnectTimeoutMs = 1000,
            ReconnectInitialBackoffMs = 1000,
            ReconnectMaxBackoffMs = 4000,
        };

        await using var connection = new MqttConnection(NullLogger<MqttConnection>.Instance, () => now);
        connection.OnNotification = (title, _, _) => notifications.Enqueue(title);
        connection.Configure(settings);

        // 1. No broker: ticks return immediately; one notification for the whole outage.
        for (var i = 1; i <= 3; i++)
        {
            var stopwatch = Stopwatch.StartNew();
            connection.MaintainConnection();
            var published = await connection.PublishAsync(settings.CpuTopic, "1.0", CancellationToken.None);
            Check(stopwatch.ElapsedMilliseconds < 250, $"tick {i} blocked for {stopwatch.ElapsedMilliseconds} ms");
            Check(!published, "publish reported success without a broker");

            await connection.PendingAttempt;
            Check(connection.FailedAttemptsInOutage == i, $"expected {i} failures, got {connection.FailedAttemptsInOutage}");

            connection.MaintainConnection(); // backoff not elapsed: must not attempt again
            await connection.PendingAttempt;
            Check(connection.FailedAttemptsInOutage == i, "attempted again before the backoff deadline");
            now += TimeSpan.FromMilliseconds(settings.ReconnectMaxBackoffMs);
        }

        CheckNotifications(notifications, Failed);

        // 2. Broker up: reconnects, "reconnected" notice, unchanged wire format.
        var server = await StartBrokerAsync(port, received);
        try
        {
            connection.MaintainConnection();
            await connection.PendingAttempt;
            Check(connection.IsConnected, "did not connect once the broker was up");
            Check(connection.FailedAttemptsInOutage == 0, "outage failure count not reset after success");
            CheckNotifications(notifications, Failed, Reconnected);

            Check(await connection.PublishAsync(settings.CpuTopic, MetricPayloadFormatter.FormatPercentPayload(42.26), CancellationToken.None), "publish failed");
            var message = await WaitForMessageAsync(received);
            Check(message.Topic == "resmon/cpu", $"topic {message.Topic}");
            Check(Encoding.UTF8.GetString(message.PayloadSegment) == "42.3", "payload changed");
            Check(message.QualityOfServiceLevel == MqttQualityOfServiceLevel.AtMostOnce, "QoS changed");
            Check(!message.Retain, "message is retained");

            // 3. Endpoint change to an unresolvable host with a fallback address.
            settings = settings with { Host = "resmon-test.invalid", FallbackAddress = "127.0.0.1" };
            connection.Configure(settings);
            Check(!connection.IsConnected, "endpoint change did not drop the old connection");
            connection.MaintainConnection();
            await connection.PendingAttempt;
            Check(connection.IsConnected, "fallback address was not used");
            CheckNotifications(notifications, Failed, Reconnected);
        }
        finally
        {
            await server.StopAsync();
            server.Dispose();
        }

        // 4. Broker goes away: loss detected, one new failure notification.
        var deadline = Stopwatch.StartNew();
        while (connection.IsConnected && deadline.ElapsedMilliseconds < 5000)
        {
            await Task.Delay(50);
        }

        Check(!connection.IsConnected, "broker shutdown not detected");

        // The connection was short-lived (fake clock did not advance), so the drop counts as a
        // failure: no immediate reconnect attempt.
        connection.MaintainConnection();
        Check(connection.PendingAttempt.IsCompleted && connection.FailedAttemptsInOutage == 0, "reconnected immediately after a short-lived connection");
        for (var i = 1; i <= 2; i++)
        {
            now += TimeSpan.FromMilliseconds(settings.ReconnectMaxBackoffMs);
            connection.MaintainConnection();
            await connection.PendingAttempt;
        }

        Check(connection.FailedAttemptsInOutage == 2, $"expected 2 failures, got {connection.FailedAttemptsInOutage}");
        CheckNotifications(notifications, Failed, Reconnected, Failed);
    }

    /// <summary>Dispose must not wait for connect timeouts to unreachable Host and fallback.</summary>
    public static async Task DisposeWhileConnecting()
    {
        var connection = new MqttConnection(NullLogger<MqttConnection>.Instance);
        connection.Configure(new DeviceSettings
        {
            Host = "192.0.2.1",              // TEST-NET-1: never answers
            FallbackAddress = "192.0.2.2",
            ConnectTimeoutMs = 10000,
        });
        connection.MaintainConnection();
        await Task.Delay(200);
        var attempt = connection.PendingAttempt;
        Check(!attempt.IsCompleted, "attempt finished before dispose; cannot exercise cancellation here");

        var stopwatch = Stopwatch.StartNew();
        await connection.DisposeAsync();
        Check(stopwatch.ElapsedMilliseconds < 1000, $"dispose took {stopwatch.ElapsedMilliseconds} ms");
        Check(attempt.IsCompleted, "connect attempt still running after dispose");
    }

    /// <summary>An outage noticed before the tray attached its handler is still notified once.</summary>
    public static async Task LateNotificationHandler()
    {
        var now = DateTimeOffset.UnixEpoch;
        var notifications = new ConcurrentQueue<string>();
        await using var connection = new MqttConnection(NullLogger<MqttConnection>.Instance, () => now);
        connection.Configure(new DeviceSettings { Host = "127.0.0.1", Port = FreeLoopbackPort(), ConnectTimeoutMs = 500 });

        connection.MaintainConnection();
        await connection.PendingAttempt;
        Check(connection.FailedAttemptsInOutage == 1, "first attempt did not fail");

        connection.OnNotification = (title, _, _) => notifications.Enqueue(title);
        for (var i = 0; i < 2; i++)
        {
            now += TimeSpan.FromMinutes(1);
            connection.MaintainConnection();
            await connection.PendingAttempt;
        }

        CheckNotifications(notifications, Failed);
    }

    private static async Task<MqttServer> StartBrokerAsync(int port, ConcurrentQueue<MqttApplicationMessage> received)
    {
        var options = new MqttServerOptionsBuilder()
            .WithDefaultEndpoint()
            .WithDefaultEndpointPort(port)
            .WithDefaultEndpointBoundIPAddress(IPAddress.Loopback)
            .WithDefaultEndpointBoundIPV6Address(IPAddress.None)
            .Build();
        var server = new MqttFactory().CreateMqttServer(options);
        server.InterceptingPublishAsync += args =>
        {
            received.Enqueue(args.ApplicationMessage);
            return Task.CompletedTask;
        };
        await server.StartAsync();
        return server;
    }

    private static async Task<MqttApplicationMessage> WaitForMessageAsync(ConcurrentQueue<MqttApplicationMessage> received)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds < 3000)
        {
            if (received.TryDequeue(out var message))
            {
                return message;
            }

            await Task.Delay(20);
        }

        throw new InvalidOperationException("broker did not receive the message");
    }

    private static int FreeLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static void CheckNotifications(IEnumerable<string> actual, params string[] expected) =>
        Check(actual.SequenceEqual(expected), $"notifications [{string.Join(", ", actual)}], expected [{string.Join(", ", expected)}]");

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
