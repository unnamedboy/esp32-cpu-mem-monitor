namespace ResourceMonitorDaemon.Provisioning
{
    /// <summary>
    /// A protocomm transport: sends one request to a named endpoint (prov-session, prov-config,
    /// proto-ver, ...) and returns the device's response. Requests on one transport must be issued
    /// sequentially because the Security1 keystream is order-dependent.
    /// Implementations throw <see cref="ProvisioningTransportException"/> for link failures.
    /// </summary>
    public interface IProvisioningTransport : IAsyncDisposable
    {
        Task<byte[]> SendAsync(string endpoint, byte[] data, CancellationToken cancellationToken);
    }
}
