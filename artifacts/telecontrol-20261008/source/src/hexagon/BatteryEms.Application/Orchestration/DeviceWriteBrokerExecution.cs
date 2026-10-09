using BatteryEms.Application.Time;

namespace BatteryEms.Application.Orchestration;

// Only a trusted server-side saved-plan resolver implements this port. Neither
// caller-supplied JSON nor a hash alone is a device payload authorization.
public interface IDeviceWriteBrokerPlanResolver
{
    Task<ShadowPlanSnapshot?> ResolveAsync(DeviceWriteBrokerBeginRequest request, CancellationToken cancellationToken);
}

public sealed record DeviceWriteBrokerEnvelope(
    DeviceWriteBrokerBeginRequest Attempt,
    ShadowPlanSnapshot Plan,
    ShadowTouWindow Window)
{
    private static readonly string[] WindowIds = ["Z1", "Z2", "Z3", "Z4"];

    public DeviceWriteBrokerEnvelope EnsureValid(DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(Attempt);
        ArgumentNullException.ThrowIfNull(Plan);
        ArgumentNullException.ThrowIfNull(Window);
        Attempt.EnsureValid();
        Plan.EnsureValid();
        if (!Plan.PayloadReady || Plan.SiteId != Attempt.SiteId || Plan.DeliveryDate != Attempt.DeliveryDate
            || ActivationPayloadIntegrity.ComputeHash(Plan) != Attempt.PayloadHash
            || !Plan.Windows.Select(window => window.WindowId).Order(StringComparer.Ordinal)
                .SequenceEqual(WindowIds, StringComparer.Ordinal)
            || Window.WindowId != Attempt.WindowId
            || ActivationPayloadIntegrity.ComputeWindowHash(Window)
                != ActivationPayloadIntegrity.ComputeWindowHash(Plan.Windows.Single(window => window.WindowId == Attempt.WindowId)))
        {
            throw new ArgumentException("Resolved plan does not match the authorized attempt.");
        }
        if (Plan.Windows.SelectMany(window => window.Intervals).Any(interval =>
                interval.PowerWatts is < 0 or > 80000 || interval.SocPercent is < 30 or > 100 || interval.Voltage < 0))
        {
            throw new ArgumentException("Resolved plan exceeds the bounded device settings.");
        }
        var timing = ActivationWindowTimingPolicy.Evaluate(Attempt.DeliveryDate, Attempt.WindowId, nowUtc);
        if (!timing.CanStart) { throw new ArgumentException(timing.BlockingCode); }
        return this;
    }
}

public enum DeviceWriteBrokerReadback
{
    Unknown,
    Matched,
}

public interface IDeviceWriteBrokerDriver
{
    // Read-only: fresh topology, master telemetry, alarms and device capability.
    Task<bool> PreflightAsync(DeviceWriteBrokerEnvelope envelope, CancellationToken cancellationToken);

    // At most one mutation, then fresh read orders (never reuse a completed read).
    // Recheck freshness/timing at the actual network boundary. Never retry update,
    // never report Matched from order status alone, never accept agent evidence.
    Task<DeviceWriteBrokerReadback> WriteOnceAndVerifyAsync(DeviceWriteBrokerEnvelope envelope, CancellationToken cancellationToken);
}

// Revalidate the durable Initiated attempt and current authority immediately
// before the physical mutation, after the driver's fresh network preflight.
public interface IDeviceWriteBrokerMutationGate
{
    Task<bool> CanSendAsync(DeviceWriteBrokerBeginRequest request, CancellationToken cancellationToken);
}

public sealed class FailClosedDeviceWriteBrokerDriver : IDeviceWriteBrokerDriver
{
    public Task<bool> PreflightAsync(DeviceWriteBrokerEnvelope envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(false);
    }

    public Task<DeviceWriteBrokerReadback> WriteOnceAndVerifyAsync(DeviceWriteBrokerEnvelope envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(DeviceWriteBrokerReadback.Unknown);
    }
}

public sealed record DeviceWriteBrokerExecutionResult(
    bool Admitted,
    bool DriverInvoked,
    bool ObservationRecorded,
    DeviceWriteBrokerAttemptState? State,
    string OutcomeCode);

// Internal executor, not an HTTP/agent tool. The host supplies authenticated
// identities; the executor always replaces caller time with the server clock.
public sealed class DeviceWriteBrokerExecutionUseCase
{
    private readonly IDeviceWriteBrokerAttemptStore _store;
    private readonly IDeviceWriteBrokerPlanResolver _resolver;
    private readonly IDeviceWriteBrokerDriver _driver;
    private readonly IClock _clock;

    public DeviceWriteBrokerExecutionUseCase(IDeviceWriteBrokerAttemptStore store,
        IDeviceWriteBrokerPlanResolver resolver, IDeviceWriteBrokerDriver driver, IClock clock)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _driver = driver ?? throw new ArgumentNullException(nameof(driver));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031",
        Justification = "Prewrite failures are not sent; failures after durable initiation remain unknown and must never trigger a mutation retry.")]
    public async Task<DeviceWriteBrokerExecutionResult> ExecuteAsync(
        DeviceWriteBrokerBeginRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        request = (request with { NowUtc = _clock.UtcNow }).EnsureValid();
        var begin = await _store.BeginAsync(request, cancellationToken).ConfigureAwait(false);
        if (!begin.Accepted)
        {
            return new(false, false, false, begin.State, begin.BlockingCode ?? "device-write-broker-admission-rejected");
        }
        if (begin.IsReplay)
        {
            return new(true, false, false, begin.State, "device-write-broker-replay-no-execution");
        }

        DeviceWriteBrokerEnvelope? envelope = null;
        var preflightReady = false;
        try
        {
            var resolved = await _resolver.ResolveAsync(request, cancellationToken).ConfigureAwait(false);
            if (resolved is null) { throw new InvalidOperationException("Saved plan is unavailable."); }
            // Copy mutable lists into read-only collections before validation,
            // so neither resolver nor driver can change an approved interval.
            var plan = resolved with
            {
                BlockingCodes = Array.AsReadOnly(resolved.BlockingCodes.ToArray()),
                Windows = Array.AsReadOnly(resolved.Windows.Select(window => window with
                {
                    Intervals = Array.AsReadOnly(window.Intervals.ToArray()),
                }).ToArray()),
            };
            envelope = new DeviceWriteBrokerEnvelope(request, plan,
                plan.Windows.Single(window => window.WindowId == request.WindowId)).EnsureValid(_clock.UtcNow);
            preflightReady = await _driver.PreflightAsync(envelope, cancellationToken).ConfigureAwait(false);
            envelope.EnsureValid(_clock.UtcNow);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { preflightReady = false; }
        if (!preflightReady || envelope is null)
        {
            // Persistence failures propagate, rather than being mistaken for a
            // preflight failure and followed by a second observation attempt.
            return await ObserveAsync(request, false, DeviceWriteBrokerObservation.NotSent, cancellationToken).ConfigureAwait(false);
        }

        // A database exception/cancellation here may mean commit succeeded but
        // acknowledgement was lost. Never close it as NotSent, never call driver.
        var initiated = await _store.MarkInitiatedAsync(request.AttemptId, request.WriterOwnerId,
            _clock.UtcNow, cancellationToken).ConfigureAwait(false);
        if (initiated.IsReplay)
        {
            return new(true, false, false, initiated.State, "device-write-broker-replay-no-execution");
        }
        if (!initiated.Accepted)
        {
            return await ObserveAsync(request, false, DeviceWriteBrokerObservation.NotSent, cancellationToken).ConfigureAwait(false);
        }

        DeviceWriteBrokerObservation observation;
        var driverInvoked = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            envelope.EnsureValid(_clock.UtcNow);
            // Once initiated, even cancellation or a pre-network driver error
            // leaves the durable latch closed to other writers.
            driverInvoked = true;
            var readback = await _driver.WriteOnceAndVerifyAsync(envelope, cancellationToken).ConfigureAwait(false);
            observation = readback == DeviceWriteBrokerReadback.Matched
                ? DeviceWriteBrokerObservation.Verified : DeviceWriteBrokerObservation.Unknown;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { observation = DeviceWriteBrokerObservation.Unknown; }
        return await ObserveAsync(request, driverInvoked, observation, cancellationToken).ConfigureAwait(false);
    }

    private async Task<DeviceWriteBrokerExecutionResult> ObserveAsync(DeviceWriteBrokerBeginRequest request,
        bool driverInvoked, DeviceWriteBrokerObservation observation, CancellationToken cancellationToken)
    {
        var result = await _store.ObserveAsync(request.AttemptId, request.WriterOwnerId,
            observation, _clock.UtcNow, cancellationToken).ConfigureAwait(false);
        return new(true, driverInvoked, result.Accepted, result.State,
            result.Accepted ? observation switch
            {
                DeviceWriteBrokerObservation.Verified => "device-write-broker-readback-matched",
                DeviceWriteBrokerObservation.NotSent => "device-write-broker-not-sent",
                _ => "device-write-broker-outcome-unknown",
            } : result.BlockingCode ?? "device-write-broker-observation-unrecorded");
    }
}
