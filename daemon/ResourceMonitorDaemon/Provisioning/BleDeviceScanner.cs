using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;

namespace ResourceMonitorDaemon.Provisioning
{
    /// <summary>A device advertising the provisioning service name (PROV_RESMON_XXXXXX).</summary>
    public sealed record ProvisioningDeviceInfo(string Name, ulong BluetoothAddress, short Rssi)
    {
        /// <summary>Address as AA:BB:CC:DD:EE:FF.</summary>
        public string AddressText => FormatAddress(BluetoothAddress);

        public static string FormatAddress(ulong address) =>
            string.Join(":", Enumerable.Range(0, 6).Select(i => ((address >> (8 * (5 - i))) & 0xFF).ToString("X2")));
    }

    /// <summary>
    /// Merges advertisement and scan-response packets per address (the device name usually arrives
    /// only in the scan response) and de-duplicates devices. Pure logic, unit-tested.
    /// </summary>
    internal sealed class ScanResultAggregator
    {
        private readonly Dictionary<ulong, (string? Name, short Rssi)> _seen = new();

        /// <summary>
        /// Records one packet. Returns the device the first time it is known to carry a provisioning
        /// name; returns null for repeats and for unrelated devices.
        /// </summary>
        public ProvisioningDeviceInfo? Observe(ulong address, string? localName, short rssi)
        {
            var name = string.IsNullOrWhiteSpace(localName) ? null : localName.Trim();
            var known = _seen.TryGetValue(address, out var previous);
            var wasMatch = known && IsProvisioningName(previous.Name);
            var mergedName = name ?? (known ? previous.Name : null);
            _seen[address] = (mergedName, rssi);

            return !wasMatch && IsProvisioningName(mergedName)
                ? new ProvisioningDeviceInfo(mergedName!, address, rssi)
                : null;
        }

        /// <summary>All matching devices, strongest signal first.</summary>
        public IReadOnlyList<ProvisioningDeviceInfo> Snapshot() =>
            _seen.Where(kv => IsProvisioningName(kv.Value.Name))
                .Select(kv => new ProvisioningDeviceInfo(kv.Value.Name!, kv.Key, kv.Value.Rssi))
                .OrderByDescending(d => d.Rssi)
                .ThenBy(d => d.Name, StringComparer.Ordinal)
                .ToArray();

        public static bool IsProvisioningName(string? name) =>
            name is not null
            && name.Length > ProvisioningConstants.DeviceNamePrefix.Length
            && name.StartsWith(ProvisioningConstants.DeviceNamePrefix, StringComparison.Ordinal);
    }

    /// <summary>
    /// Active BLE scan for devices in provisioning mode. The local name is carried in the scan response
    /// (protocomm_ble.c), so the watcher must use active scanning.
    /// UNVERIFIED: not yet exercised against a physical ESP32.
    /// </summary>
    public sealed class BleDeviceScanner
    {
        /// <summary>
        /// Scans for <paramref name="duration"/> and returns the matching devices, strongest first.
        /// <paramref name="deviceFound"/> is reported once per device as soon as it is seen.
        /// Cancelling <paramref name="cancellationToken"/> stops the scan and throws.
        /// </summary>
        public async Task<IReadOnlyList<ProvisioningDeviceInfo>> ScanAsync(
            TimeSpan duration, IProgress<ProvisioningDeviceInfo>? deviceFound, CancellationToken cancellationToken)
        {
            if (duration <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(duration));
            }

            await BluetoothAvailability.EnsureAvailableAsync(cancellationToken).ConfigureAwait(false);

            var aggregator = new ScanResultAggregator();
            var sync = new object();
            var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };

            void OnReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
            {
                ProvisioningDeviceInfo? found;
                lock (sync)
                {
                    found = aggregator.Observe(args.BluetoothAddress, args.Advertisement?.LocalName, args.RawSignalStrengthInDBm);
                }

                if (found is not null)
                {
                    deviceFound?.Report(found);
                }
            }

            void OnStopped(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementWatcherStoppedEventArgs args)
            {
                if (args.Error == BluetoothError.Success)
                {
                    stopped.TrySetResult();
                }
                else
                {
                    stopped.TrySetException(MapWatcherError(args.Error));
                }
            }

            watcher.Received += OnReceived;
            watcher.Stopped += OnStopped;
            try
            {
                try
                {
                    watcher.Start();
                }
                catch (Exception ex)
                {
                    throw BluetoothAvailability.MapException(ex, "starting the Bluetooth scan");
                }

                if (watcher.Status == BluetoothLEAdvertisementWatcherStatus.Aborted)
                {
                    throw new ProvisioningTransportException(
                        ProvisioningTransportError.BluetoothUnavailable,
                        "The Bluetooth scan could not be started. Check that Bluetooth is turned on.");
                }

                var elapsed = Task.Delay(duration, cancellationToken);
                var first = await Task.WhenAny(elapsed, stopped.Task).ConfigureAwait(false);
                await first.ConfigureAwait(false); // propagates cancellation or a watcher error
            }
            finally
            {
                watcher.Received -= OnReceived;
                watcher.Stopped -= OnStopped;
                try
                {
                    watcher.Stop();
                }
                catch
                {
                    // Stopping an aborted watcher can throw; there is nothing left to clean up.
                }
            }

            lock (sync)
            {
                return aggregator.Snapshot();
            }
        }

        private static ProvisioningTransportException MapWatcherError(BluetoothError error) => error switch
        {
            BluetoothError.RadioNotAvailable => new ProvisioningTransportException(
                ProvisioningTransportError.BluetoothOff, "Bluetooth is turned off. Turn it on in Windows Settings and try again."),
            BluetoothError.DisabledByPolicy or BluetoothError.DisabledByUser => new ProvisioningTransportException(
                ProvisioningTransportError.BluetoothUnavailable, "Bluetooth is disabled on this PC."),
            BluetoothError.NotSupported => new ProvisioningTransportException(
                ProvisioningTransportError.BluetoothUnavailable, "This PC's Bluetooth adapter does not support Bluetooth Low Energy scanning."),
            BluetoothError.ResourceInUse => new ProvisioningTransportException(
                ProvisioningTransportError.BluetoothUnavailable, "The Bluetooth adapter is busy. Try again in a moment."),
            _ => new ProvisioningTransportException(
                ProvisioningTransportError.Other, $"The Bluetooth scan stopped unexpectedly ({error})."),
        };
    }
}
