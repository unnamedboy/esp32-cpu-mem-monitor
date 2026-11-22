using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace ResourceMonitorDaemon.Provisioning
{
    /// <summary>
    /// protocomm BLE transport for Windows (WinRT GATT client), following esp_prov ble_cli.py
    /// (ESP-IDF v4.4.7, https://github.com/espressif/esp-idf/blob/v4.4.7/tools/esp_prov/transport/ble_cli.py):
    /// <list type="bullet">
    /// <item>find the provisioning service by UUID;</item>
    /// <item>map endpoint names to characteristics through each characteristic's 0x2901
    /// Characteristic User Description text (case-insensitive), never by hard-coded UUIDs;</item>
    /// <item>a request is a write-with-response to the endpoint characteristic, the response is a
    /// subsequent read of the same characteristic (the device stores it with
    /// esp_ble_gatts_set_attr_value in its write handler).</item>
    /// </list>
    /// The device ties the protocomm session to the BLE connection and ends it on disconnect, so the
    /// link is held open with <see cref="GattSession.MaintainConnection"/> and any disconnect after
    /// discovery fails all later requests.
    /// UNVERIFIED: not yet exercised against a physical ESP32.
    /// </summary>
    public sealed class BleProvisioningTransport : IProvisioningTransport
    {
        public static readonly TimeSpan DefaultOperationTimeout = TimeSpan.FromSeconds(10);

        private readonly BluetoothLEDevice _device;
        private readonly GattSession? _gattSession;
        private readonly GattDeviceService _service;
        private readonly IReadOnlyDictionary<string, GattCharacteristic> _endpoints;
        private readonly TimeSpan _operationTimeout;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private volatile bool _seenConnected;
        private volatile bool _linkLost;
        private bool _disposed;

        private BleProvisioningTransport(
            BluetoothLEDevice device,
            GattSession? gattSession,
            GattDeviceService service,
            IReadOnlyDictionary<string, GattCharacteristic> endpoints,
            TimeSpan operationTimeout)
        {
            _device = device;
            _gattSession = gattSession;
            _service = service;
            _endpoints = endpoints;
            _operationTimeout = operationTimeout;
            _seenConnected = device.ConnectionStatus == BluetoothConnectionStatus.Connected;
            _device.ConnectionStatusChanged += OnConnectionStatusChanged;
        }

        /// <summary>Endpoint names discovered on the device (lower case).</summary>
        public IReadOnlyCollection<string> EndpointNames => _endpoints.Keys.ToArray();

        /// <summary>
        /// Connects to the device, discovers the provisioning service and maps its endpoints.
        /// <paramref name="operationTimeout"/> bounds each individual GATT operation.
        /// </summary>
        public static async Task<BleProvisioningTransport> ConnectAsync(
            ulong bluetoothAddress, CancellationToken cancellationToken, TimeSpan? operationTimeout = null)
        {
            var timeout = operationTimeout ?? DefaultOperationTimeout;
            await BluetoothAvailability.EnsureAvailableAsync(cancellationToken).ConfigureAwait(false);

            var device = await WithTimeoutAsync(
                ct => BluetoothLEDevice.FromBluetoothAddressAsync(bluetoothAddress).AsTask(ct),
                timeout, "looking up the device", cancellationToken).ConfigureAwait(false);
            if (device is null)
            {
                throw new ProvisioningTransportException(
                    ProvisioningTransportError.DeviceNotFound,
                    "The device was not found. Make sure it is powered on and in provisioning mode, then scan again.");
            }

            GattSession? gattSession = null;
            GattDeviceService? service = null;
            try
            {
                gattSession = await WithTimeoutAsync(
                    ct => GattSession.FromDeviceIdAsync(device.BluetoothDeviceId).AsTask(ct),
                    timeout, "opening a GATT session", cancellationToken).ConfigureAwait(false);
                if (gattSession is not null)
                {
                    gattSession.MaintainConnection = true;
                }

                // Uncached: the device's GATT table differs between provisioning and normal mode.
                var servicesResult = await WithTimeoutAsync(
                    ct => device.GetGattServicesForUuidAsync(ProvisioningConstants.ServiceUuid, BluetoothCacheMode.Uncached).AsTask(ct),
                    timeout, "connecting to the device", cancellationToken).ConfigureAwait(false);
                EnsureSuccess(servicesResult.Status, servicesResult.ProtocolError, "connecting to the device");
                if (servicesResult.Services.Count == 0)
                {
                    throw new ProvisioningTransportException(
                        ProvisioningTransportError.ServiceNotFound,
                        "The device does not offer the Wi-Fi provisioning service. It may already be configured; " +
                        ProvisioningConstants.ReprovisionInstruction + " to re-enter provisioning mode.");
                }

                service = servicesResult.Services[0];
                for (var i = 1; i < servicesResult.Services.Count; i++)
                {
                    servicesResult.Services[i].Dispose();
                }

                var endpoints = await DiscoverEndpointsAsync(service, timeout, cancellationToken).ConfigureAwait(false);
                foreach (var required in new[] { ProvisioningConstants.SessionEndpoint, ProvisioningConstants.ConfigEndpoint })
                {
                    if (!endpoints.ContainsKey(required))
                    {
                        throw new ProvisioningTransportException(
                            ProvisioningTransportError.EndpointNotFound,
                            $"The device's provisioning service has no '{required}' endpoint " +
                            $"(found: {string.Join(", ", endpoints.Keys.OrderBy(k => k, StringComparer.Ordinal))}).");
                    }
                }

                return new BleProvisioningTransport(device, gattSession, service, endpoints, timeout);
            }
            catch
            {
                service?.Dispose();
                gattSession?.Dispose();
                device.Dispose();
                throw;
            }
        }

        public async Task<byte[]> SendAsync(string endpoint, byte[] data, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(endpoint);
            ArgumentNullException.ThrowIfNull(data);
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (!_endpoints.TryGetValue(endpoint.ToLowerInvariant(), out var characteristic))
            {
                throw new ProvisioningTransportException(
                    ProvisioningTransportError.EndpointNotFound, $"The device has no '{endpoint}' endpoint.");
            }

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfLinkLost();

                var write = await WithTimeoutAsync(
                    ct => characteristic.WriteValueWithResultAsync(data.AsBuffer(), GattWriteOption.WriteWithResponse).AsTask(ct),
                    _operationTimeout, $"sending to '{endpoint}'", cancellationToken).ConfigureAwait(false);
                EnsureSuccess(write.Status, write.ProtocolError, $"sending to '{endpoint}'");

                var read = await WithTimeoutAsync(
                    ct => characteristic.ReadValueAsync(BluetoothCacheMode.Uncached).AsTask(ct),
                    _operationTimeout, $"reading the response from '{endpoint}'", cancellationToken).ConfigureAwait(false);
                EnsureSuccess(read.Status, read.ProtocolError, $"reading the response from '{endpoint}'");

                // No link check here: a completed read is valid data even if the device drops the link
                // right afterwards, which it does on purpose after reporting Connected.
                return read.Value is null ? Array.Empty<byte>() : read.Value.ToArray();
            }
            finally
            {
                _gate.Release();
            }
        }

        public ValueTask DisposeAsync()
        {
            if (!_disposed)
            {
                _disposed = true;
                // The device may already be gone (it stops BLE after a successful provisioning); releasing
                // the WinRT objects must never turn a finished provisioning run into an error.
                TryRelease(() => _device.ConnectionStatusChanged -= OnConnectionStatusChanged);
                TryRelease(_service.Dispose);
                TryRelease(() => _gattSession?.Dispose());
                TryRelease(_device.Dispose);
                // _gate is intentionally not disposed: an in-flight SendAsync still releases it.
            }

            return ValueTask.CompletedTask;
        }

        private static async Task<Dictionary<string, GattCharacteristic>> DiscoverEndpointsAsync(
            GattDeviceService service, TimeSpan timeout, CancellationToken cancellationToken)
        {
            var characteristicsResult = await WithTimeoutAsync(
                ct => service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached).AsTask(ct),
                timeout, "listing provisioning endpoints", cancellationToken).ConfigureAwait(false);
            EnsureSuccess(characteristicsResult.Status, characteristicsResult.ProtocolError, "listing provisioning endpoints");

            var endpoints = new Dictionary<string, GattCharacteristic>(StringComparer.Ordinal);
            foreach (var characteristic in characteristicsResult.Characteristics)
            {
                var descriptorsResult = await WithTimeoutAsync(
                    ct => characteristic.GetDescriptorsForUuidAsync(
                        GattDescriptorUuids.CharacteristicUserDescription, BluetoothCacheMode.Uncached).AsTask(ct),
                    timeout, "reading endpoint names", cancellationToken).ConfigureAwait(false);
                if (descriptorsResult.Status != GattCommunicationStatus.Success || descriptorsResult.Descriptors.Count == 0)
                {
                    continue;
                }

                var value = await WithTimeoutAsync(
                    ct => descriptorsResult.Descriptors[0].ReadValueAsync(BluetoothCacheMode.Uncached).AsTask(ct),
                    timeout, "reading endpoint names", cancellationToken).ConfigureAwait(false);
                if (value.Status != GattCommunicationStatus.Success || value.Value is null)
                {
                    continue;
                }

                var name = ParseUserDescription(value.Value.ToArray());
                if (name.Length > 0)
                {
                    endpoints[name] = characteristic;
                }
            }

            return endpoints;
        }

        /// <summary>0x2901 text -> endpoint key: UTF-8, trailing NULs/whitespace removed, lower case (as ble_cli.py).</summary>
        internal static string ParseUserDescription(byte[] raw) =>
            Encoding.UTF8.GetString(raw).TrimEnd('\0').Trim().ToLowerInvariant();

        private static void TryRelease(Action release)
        {
            try
            {
                release();
            }
            catch (Exception)
            {
                // Best effort cleanup of WinRT objects whose device has disconnected.
            }
        }

        private void OnConnectionStatusChanged(BluetoothLEDevice sender, object args)
        {
            if (sender.ConnectionStatus == BluetoothConnectionStatus.Connected)
            {
                _seenConnected = true;
            }
            else if (_seenConnected)
            {
                _linkLost = true;
            }
        }

        private void ThrowIfLinkLost()
        {
            if (_linkLost)
            {
                throw new ProvisioningTransportException(
                    ProvisioningTransportError.DeviceDisconnected,
                    "The Bluetooth connection to the device was lost, which ends its provisioning session. Reconnect and try again.");
            }
        }

        private static void EnsureSuccess(GattCommunicationStatus status, byte? protocolError, string operation)
        {
            switch (status)
            {
                case GattCommunicationStatus.Success:
                    return;
                case GattCommunicationStatus.Unreachable:
                    throw new ProvisioningTransportException(
                        ProvisioningTransportError.DeviceDisconnected,
                        $"The device could not be reached while {operation}. Move it closer and make sure it is still in provisioning mode.");
                case GattCommunicationStatus.ProtocolError:
                    throw new ProvisioningTransportException(
                        ProvisioningTransportError.GattError,
                        $"The device returned GATT error 0x{protocolError ?? 0:X2} while {operation}.");
                case GattCommunicationStatus.AccessDenied:
                    throw new ProvisioningTransportException(
                        ProvisioningTransportError.GattError, $"The device denied access while {operation}.");
                default:
                    throw new ProvisioningTransportException(
                        ProvisioningTransportError.Other, $"Bluetooth operation failed ({status}) while {operation}.");
            }
        }

        private static async Task<T> WithTimeoutAsync<T>(
            Func<CancellationToken, Task<T>> operation, TimeSpan timeout, string description, CancellationToken cancellationToken)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);
            try
            {
                return await operation(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new ProvisioningTransportException(
                    ProvisioningTransportError.Timeout, $"Timed out while {description}.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not ProvisioningException)
            {
                throw BluetoothAvailability.MapException(ex, description);
            }
        }
    }
}
