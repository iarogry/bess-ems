using BatteryEms.Adapters.DeyeCloud;
using BatteryEms.Application.Orchestration;
using BatteryEms.Application.Time;

namespace BatteryEms.BrokerHost;

// Prepare before BeginAsync. No token is changed underneath an executing driver.
public interface IBrokerDriverSessionFactory
{
    bool SupportsSite(string siteId);
    Task<BrokerDriverSession?> OpenAsync(CancellationToken cancellationToken);
}

public sealed class BrokerDriverSession : IDisposable
{
    private readonly HttpClient? _ownedClient;

    public BrokerDriverSession(IDeviceWriteBrokerDriver driver, HttpClient? ownedClient = null)
    {
        Driver = driver ?? throw new ArgumentNullException(nameof(driver));
        _ownedClient = ownedClient;
    }

    public IDeviceWriteBrokerDriver Driver { get; }
    public void Dispose() => _ownedClient?.Dispose();
}

internal sealed class DefaultBrokerDriverSessionFactory(IDeviceWriteBrokerDriver driver) : IBrokerDriverSessionFactory
{
    public bool SupportsSite(string siteId) => driver is not FailClosedDeviceWriteBrokerDriver;
    public Task<BrokerDriverSession?> OpenAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<BrokerDriverSession?>(driver is FailClosedDeviceWriteBrokerDriver ? null : new(driver));
    }
}

public sealed class DeyeBrokerDriverSessionFactory : IBrokerDriverSessionFactory
{
    private readonly Uri _baseAddress;
    private readonly DeyeBrokerDeviceOptions _device;
    private readonly DeyeBrokerTokenProvider _tokens;
    private readonly IClock _clock;
    private readonly IDeviceWriteBrokerMutationGate _gate;

    public DeyeBrokerDriverSessionFactory(Uri baseAddress, DeyeBrokerDeviceOptions device,
        DeyeBrokerTokenProvider tokens, IClock clock, IDeviceWriteBrokerMutationGate gate)
    {
        _baseAddress = baseAddress ?? throw new ArgumentNullException(nameof(baseAddress));
        _device = (device ?? throw new ArgumentNullException(nameof(device))).EnsureValid();
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
    }

    public bool SupportsSite(string siteId) => _device.WriteEnabled && siteId == _device.SiteId;

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000",
        Justification = "Ownership of the dedicated client transfers to the returned session; construction failures dispose it here.")]
    public async Task<BrokerDriverSession?> OpenAsync(CancellationToken cancellationToken)
    {
        if (!_device.WriteEnabled) { return null; }
        var access = await _tokens.AcquireAsync(cancellationToken).ConfigureAwait(false);
        if (access.ExpiresAtUtc - _clock.UtcNow <= TimeSpan.FromSeconds(180))
        { throw new InvalidOperationException("deye-broker-auth-unavailable"); }
        var client = DeyeCloudDeviceWriteBrokerDriver.CreateAuthenticatedClient(_baseAddress, access.Value);
        try
        {
            return new BrokerDriverSession(new TokenBoundBrokerDriver(
                new DeyeCloudDeviceWriteBrokerDriver(client, _device, _clock, _gate), access.ExpiresAtUtc, _clock), client);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }
}

// Admission/database delays must not consume the token budget silently.
// Expiry never causes refresh inside a prepared/executing session.
public sealed class TokenBoundBrokerDriver : IDeviceWriteBrokerDriver
{
    private readonly IDeviceWriteBrokerDriver _driver;
    private readonly DateTimeOffset _expiresAt;
    private readonly IClock _clock;

    public TokenBoundBrokerDriver(IDeviceWriteBrokerDriver driver, DateTimeOffset expiresAt, IClock clock)
    {
        _driver = driver ?? throw new ArgumentNullException(nameof(driver));
        _expiresAt = expiresAt;
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public Task<bool> PreflightAsync(DeviceWriteBrokerEnvelope envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        cancellationToken.ThrowIfCancellationRequested();
        return _expiresAt - _clock.UtcNow > TimeSpan.FromSeconds(150)
            ? _driver.PreflightAsync(envelope, cancellationToken) : Task.FromResult(false);
    }

    public Task<DeviceWriteBrokerReadback> WriteOnceAndVerifyAsync(DeviceWriteBrokerEnvelope envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        cancellationToken.ThrowIfCancellationRequested();
        return _expiresAt - _clock.UtcNow > TimeSpan.FromSeconds(120)
            ? _driver.WriteOnceAndVerifyAsync(envelope, cancellationToken) : Task.FromResult(DeviceWriteBrokerReadback.Unknown);
    }
}

// Credentials are server configuration, never request fields. Both switches
// default false. Enabling the shell alone still cannot create device attempts.
public sealed class BrokerDeyeOptions
{
    public bool Enabled { get; set; }
    public bool WriteEnabled { get; set; }
    public Uri? BaseAddress { get; set; }
    public string SiteId { get; set; } = string.Empty;
    public string StationId { get; set; } = string.Empty;
    public string MasterSerial { get; set; } = string.Empty;
    public string SlaveSerial { get; set; } = string.Empty;
    public string AppId { get; set; } = string.Empty;
    public string AppSecret { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public long? CompanyId { get; set; }

    public BrokerDeyeOptions EnsureValid()
    {
        if (WriteEnabled && !Enabled) { throw new ArgumentException("Deye write requires explicit driver enablement."); }
        if (!Enabled) { return this; }
        if (BaseAddress is not { IsAbsoluteUri: true, Scheme: "https" } address
            || !address.AbsolutePath.EndsWith('/') || !string.IsNullOrEmpty(address.UserInfo)
            || !string.IsNullOrEmpty(address.Query) || !string.IsNullOrEmpty(address.Fragment))
        { throw new ArgumentException("An explicit HTTPS vendor base is required."); }
        Device().EnsureValid();
        Token().EnsureValid();
        return this;
    }

    internal DeyeBrokerDeviceOptions Device() => new(SiteId, StationId, MasterSerial, SlaveSerial, WriteEnabled);
    internal DeyeBrokerTokenOptions Token() => new()
    { AppId = AppId, AppSecret = AppSecret, Email = Email, Password = Password, CompanyId = CompanyId };
}
