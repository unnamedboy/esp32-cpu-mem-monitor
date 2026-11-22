namespace ResourceMonitorDaemon.Provisioning
{
    /// <summary>
    /// Base type for provisioning failures. Messages are user-presentable and must never contain the
    /// Wi-Fi passphrase or the pairing code.
    /// </summary>
    public class ProvisioningException : Exception
    {
        public ProvisioningException(string message) : base(message) { }
        public ProvisioningException(string message, Exception? innerException) : base(message, innerException) { }
    }

    /// <summary>The device answered with something that does not follow the provisioning protocol.</summary>
    public class ProvisioningProtocolException : ProvisioningException
    {
        public ProvisioningProtocolException(string message) : base(message) { }
        public ProvisioningProtocolException(string message, Exception? innerException) : base(message, innerException) { }
    }

    /// <summary>
    /// After a failed Wi-Fi attempt the device answered a new SetConfig/ApplyConfig with InternalError:
    /// its provisioning state machine is stuck in the failed state and needs a restart.
    /// </summary>
    public sealed class ProvisioningRetryRejectedException : ProvisioningProtocolException
    {
        public ProvisioningRetryRejectedException()
            : base("The device did not accept another attempt after the previous one failed. " +
                   "Power-cycle the device or hold its BOOT button for 5 seconds to restart provisioning, then try again.")
        {
        }
    }

    public enum PairingCodeRejectionReason
    {
        /// <summary>
        /// The device closed the connection while checking the client's verifier. This is how a real
        /// ESP-IDF v4.4 device reacts to a wrong pairing code (security1.c returns ESP_FAIL and
        /// protocomm_ble.c calls esp_ble_gatts_close without a response).
        /// </summary>
        DeviceClosedConnection,

        /// <summary>The device's verify data did not decrypt to our public key.</summary>
        VerificationMismatch,
    }

    /// <summary>The device did not accept the pairing code (Security1 proof of possession).</summary>
    public sealed class PairingCodeRejectedException : ProvisioningException
    {
        public PairingCodeRejectedException(PairingCodeRejectionReason reason, Exception? innerException = null)
            : base(BuildMessage(reason), innerException)
        {
            Reason = reason;
        }

        public PairingCodeRejectionReason Reason { get; }

        private static string BuildMessage(PairingCodeRejectionReason reason) => reason switch
        {
            PairingCodeRejectionReason.VerificationMismatch =>
                "Pairing code rejected: the device could not prove it knows the same pairing code.",
            _ =>
                "Pairing code rejected: the device closed the connection while checking the pairing code. " +
                "The pairing code is most likely wrong.",
        };
    }

    public enum ProvisioningTransportError
    {
        /// <summary>No Bluetooth LE capable adapter is present or the stack is unavailable.</summary>
        BluetoothUnavailable,
        /// <summary>The Bluetooth radio is switched off.</summary>
        BluetoothOff,
        /// <summary>The device could not be found or reached.</summary>
        DeviceNotFound,
        /// <summary>The device disconnected (or the session was lost) during the exchange.</summary>
        DeviceDisconnected,
        /// <summary>The operation did not complete in time.</summary>
        Timeout,
        /// <summary>The provisioning GATT service is missing on the device.</summary>
        ServiceNotFound,
        /// <summary>The requested endpoint (characteristic) does not exist on the device.</summary>
        EndpointNotFound,
        /// <summary>The device returned a GATT protocol error or access was denied.</summary>
        GattError,
        /// <summary>Any other transport failure.</summary>
        Other,
    }

    /// <summary>A failure of the underlying link (BLE), as opposed to a protocol-level failure.</summary>
    public sealed class ProvisioningTransportException : ProvisioningException
    {
        public ProvisioningTransportException(ProvisioningTransportError error, string message, Exception? innerException = null)
            : base(message, innerException)
        {
            Error = error;
        }

        public ProvisioningTransportError Error { get; }
    }
}
