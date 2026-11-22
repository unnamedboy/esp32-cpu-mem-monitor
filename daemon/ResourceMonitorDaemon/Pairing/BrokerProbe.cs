using System.Net.Sockets;

namespace ResourceMonitorDaemon.Pairing
{
    /// <summary>
    /// Checks whether the device's MQTT broker answers after the Bluetooth link was lost during
    /// provisioning (<see cref="Provisioning.WifiProvisioningResult.ConnectionLost"/>): the device may
    /// already have joined Wi-Fi and stopped BLE. A plain TCP connect to the broker port is enough to
    /// tell; nothing is sent.
    /// </summary>
    public static class BrokerProbe
    {
        private static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(1);

        /// <summary>
        /// Returns true as soon as <paramref name="alreadyConnected"/> reports the daemon's own MQTT
        /// connection is up, or a TCP connection to <paramref name="host"/>:<paramref name="port"/>
        /// succeeds; false when <paramref name="budget"/> runs out. Retries about once per second
        /// (mDNS can take a moment after the device joins Wi-Fi). Cancellation throws.
        /// </summary>
        public static async Task<bool> WaitForBrokerAsync(
            string host, int port, Func<bool>? alreadyConnected, TimeSpan budget, CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(host);
            using var overall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            overall.CancelAfter(budget);
            try
            {
                while (true)
                {
                    if (alreadyConnected?.Invoke() == true)
                    {
                        return true;
                    }

                    using (var attempt = CancellationTokenSource.CreateLinkedTokenSource(overall.Token))
                    {
                        attempt.CancelAfter(AttemptTimeout);
                        try
                        {
                            using var client = new TcpClient();
                            await client.ConnectAsync(host, port, attempt.Token).ConfigureAwait(false);
                            return true;
                        }
                        catch (SocketException)
                        {
                            // Not reachable (yet): name not resolved or connection refused. Try again.
                        }
                        catch (OperationCanceledException) when (!overall.IsCancellationRequested)
                        {
                            // This attempt timed out. Try again.
                        }
                    }

                    await Task.Delay(RetryDelay, overall.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return false; // budget used up
            }
        }
    }
}
