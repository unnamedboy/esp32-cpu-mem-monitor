using System.Runtime.InteropServices;
using Windows.Devices.Bluetooth;
using Windows.Devices.Radios;

namespace ResourceMonitorDaemon.Provisioning
{
    /// <summary>Shared Bluetooth adapter checks and WinRT error mapping for the BLE scanner and transport.</summary>
    internal static class BluetoothAvailability
    {
        // HRESULTs seen from Windows.Devices.Bluetooth when the link or radio goes away.
        private const int HResultDeviceNotConnected = unchecked((int)0x8007048F); // ERROR_DEVICE_NOT_CONNECTED
        private const int HResultDeviceNotAvailable = unchecked((int)0x800710DF); // ERROR_DEVICE_NOT_AVAILABLE
        private const int HResultNotReady = unchecked((int)0x80070015);           // ERROR_NOT_READY
        private const int HResultAccessDenied = unchecked((int)0x80070005);       // E_ACCESSDENIED
        private const int HResultObjectClosed = unchecked((int)0x80000013);       // RO_E_CLOSED

        /// <summary>
        /// Throws <see cref="ProvisioningTransportException"/> with a user-presentable message when there
        /// is no usable Bluetooth LE adapter or the radio is switched off.
        /// </summary>
        public static async Task EnsureAvailableAsync(CancellationToken cancellationToken)
        {
            BluetoothAdapter? adapter;
            Radio? radio;
            try
            {
                adapter = await BluetoothAdapter.GetDefaultAsync().AsTask(cancellationToken).ConfigureAwait(false);
                radio = adapter is null ? null : await adapter.GetRadioAsync().AsTask(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new ProvisioningTransportException(
                    ProvisioningTransportError.BluetoothUnavailable,
                    "Bluetooth is not available on this PC.", ex);
            }

            if (adapter is null)
            {
                throw new ProvisioningTransportException(
                    ProvisioningTransportError.BluetoothUnavailable,
                    "No Bluetooth adapter was found on this PC.");
            }

            if (!adapter.IsLowEnergySupported || !adapter.IsCentralRoleSupported)
            {
                throw new ProvisioningTransportException(
                    ProvisioningTransportError.BluetoothUnavailable,
                    "This PC's Bluetooth adapter does not support Bluetooth Low Energy.");
            }

            if (radio is not null && radio.State != RadioState.On)
            {
                throw new ProvisioningTransportException(
                    ProvisioningTransportError.BluetoothOff,
                    "Bluetooth is turned off. Turn it on in Windows Settings and try again.");
            }
        }

        /// <summary>Maps an exception thrown by a WinRT Bluetooth call to a transport exception.</summary>
        public static ProvisioningTransportException MapException(Exception ex, string operation)
        {
            if (ex is ProvisioningTransportException transport)
            {
                return transport;
            }

            if (ex is ObjectDisposedException)
            {
                return new ProvisioningTransportException(
                    ProvisioningTransportError.DeviceDisconnected, $"The connection to the device was closed while {operation}.", ex);
            }

            return ex.HResult switch
            {
                HResultDeviceNotConnected or HResultObjectClosed => new ProvisioningTransportException(
                    ProvisioningTransportError.DeviceDisconnected, $"The device disconnected while {operation}.", ex),
                HResultDeviceNotAvailable or HResultNotReady => new ProvisioningTransportException(
                    ProvisioningTransportError.BluetoothOff, $"Bluetooth became unavailable while {operation}. Check that Bluetooth is turned on.", ex),
                HResultAccessDenied => new ProvisioningTransportException(
                    ProvisioningTransportError.GattError, $"Access was denied by the device while {operation}.", ex),
                _ when ex is COMException => new ProvisioningTransportException(
                    ProvisioningTransportError.Other, $"Bluetooth error while {operation} (0x{ex.HResult:X8}).", ex),
                _ => new ProvisioningTransportException(
                    ProvisioningTransportError.Other, $"Bluetooth error while {operation}: {ex.Message}", ex),
            };
        }
    }
}
