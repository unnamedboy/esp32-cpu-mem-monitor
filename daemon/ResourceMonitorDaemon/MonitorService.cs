using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;

namespace ResourceMonitorDaemon
{
    public class MonitorService : BackgroundService
    {
        private readonly ILogger<MonitorService> _logger;
        private PerformanceCounter? _cpuCounter;
        private IMqttClient? _mqttClient;
        private bool _isPaused;
        private bool _mqttConnected;

        public Action<string, string, ToolTipIcon>? OnNotification { get; set; }

        private const string MQTT_BROKER_HOST = "resmon.local";
        private const int MQTT_BROKER_PORT = 1883;
        private const string CPU_TOPIC = "resmon/cpu";
        private const string MEM_TOPIC = "resmon/mem";

        private const int MAX_RETRIES = 10;
        private const double INITIAL_BACKOFF_MS = 100;
        private const double MAX_BACKOFF_MS = 10000;

        public MonitorService(ILogger<MonitorService> logger)
        {
            _logger = logger;
        }

        public override Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("ResourceMonitorDaemon started. Monitoring system resources...");

            try
            {
                _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
                _cpuCounter.NextValue();

                var factory = new MqttFactory();
                _mqttClient = factory.CreateMqttClient();

                _logger.LogInformation($"MQTT Broker: {MQTT_BROKER_HOST}:{MQTT_BROKER_PORT}");
                _logger.LogInformation($"CPU Topic: {CPU_TOPIC}");
                _logger.LogInformation($"MEM Topic: {MEM_TOPIC}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unable to initialize resource monitor.");
            }

            return base.StartAsync(cancellationToken);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await ConnectToMqttBroker();

            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));

            while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    float cpuUsage = _cpuCounter?.NextValue() ?? 0;
                    var memInfo = NativeMemory.GetMemoryStatus();

                    string metrics = $"[Metrics] CPU: {cpuUsage:F1}% | RAM: {memInfo.UsedGB:F2}/{memInfo.TotalGB:F2} GB ({memInfo.MemoryLoad}%)";
                    _logger.LogDebug(metrics);

                    if (_isPaused)
                    {
                        continue;
                    }

                    if (!_mqttConnected || _mqttClient?.IsConnected != true)
                    {
                        await ConnectToMqttBroker();
                    }

                    if (_mqttConnected)
                    {
                        await PublishMetricDataToMqtt(CPU_TOPIC, cpuUsage);
                        await PublishMetricDataToMqtt(MEM_TOPIC, memInfo.MemoryLoad);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "An error occurred while sampling resources.");
                }
            }
        }

        private async Task<bool> ConnectToMqttBroker()
        {
            int retryCount = 0;
            double backoffMs = INITIAL_BACKOFF_MS;

            while (retryCount < MAX_RETRIES)
            {
                try
                {
                    if (_mqttClient == null)
                    {
                        return false;
                    }

                    if (_mqttClient.IsConnected)
                    {
                        _mqttConnected = true;
                        return true;
                    }

                    var options = new MqttClientOptionsBuilder()
                        .WithTcpServer(MQTT_BROKER_HOST, MQTT_BROKER_PORT)
                        .WithCleanSession()
                        .Build();

                    await _mqttClient.ConnectAsync(options);
                    _mqttConnected = true;
                    _logger.LogInformation("Connected to ESP32 MQTT broker");
                    return true;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"[Retry {retryCount + 1}/{MAX_RETRIES}] MQTT broker connection failed: {ex.Message}");
                    retryCount++;

                    if (retryCount < MAX_RETRIES)
                    {
                        await Task.Delay((int)backoffMs);
                        backoffMs = Math.Min(backoffMs * 2, MAX_BACKOFF_MS);
                    }
                }
            }

            _mqttConnected = false;
            _logger.LogError($"Unable to connect to ESP32 MQTT broker ({MQTT_BROKER_HOST}:{MQTT_BROKER_PORT}) after {MAX_RETRIES} retries");
            OnNotification?.Invoke(
                "MQTT connection failed",
                $"Unable to connect to ESP32 MQTT broker ({MQTT_BROKER_HOST}:{MQTT_BROKER_PORT}).\nCheck that the ESP32 is powered on and connected to the same Wi-Fi.",
                ToolTipIcon.Error);
            return false;
        }

        private async Task PublishMetricDataToMqtt(string topic, double percent)
        {
            try
            {
                if (_mqttClient == null || !_mqttClient.IsConnected)
                {
                    _mqttConnected = false;
                    return;
                }

                var payload = MetricPayloadFormatter.FormatPercentPayload(percent);

                var message = new MqttApplicationMessageBuilder()
                    .WithTopic(topic)
                    .WithPayload(payload)
                    .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtMostOnce)
                    .Build();

                await _mqttClient.PublishAsync(message);
                _logger.LogDebug($"{percent:F1}% published to MQTT topic {topic}");
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"MQTT publish failed: {ex.Message}");
                _mqttConnected = false;
            }
        }

        public void Pause()
        {
            _isPaused = true;
            _logger.LogInformation("Monitoring send paused");
        }

        public void Resume()
        {
            _isPaused = false;
            _logger.LogInformation("Monitoring send resumed");
        }

        public bool IsPaused => _isPaused;

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("ResourceMonitorDaemon stopping...");
            _cpuCounter?.Dispose();

            if (_mqttClient?.IsConnected == true)
            {
                await _mqttClient.DisconnectAsync();
            }

            _mqttClient?.Dispose();
            await base.StopAsync(cancellationToken);
        }
    }
}
