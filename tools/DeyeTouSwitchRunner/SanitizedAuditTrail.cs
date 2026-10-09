using System.Diagnostics;
using System.Text;
using System.Text.Json;

internal sealed class SanitizedAuditTrail
{
    private static readonly SemaphoreSlim WriteLock = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _path;
    private readonly string _runId;
    private readonly string _scenarioDate;
    private readonly string _window;
    private string _payloadHash;

    public SanitizedAuditTrail(string path, string runId, string scenarioDate, string window, string payloadHash)
    {
        _path = Path.GetFullPath(path);
        _runId = runId;
        _scenarioDate = scenarioDate;
        _window = window;
        _payloadHash = payloadHash;
    }

    public void SetPayloadHash(string payloadHash) => _payloadHash = payloadHash;

    public async Task AppendAsync(
        string action,
        string phase,
        string outcome,
        string? endpoint = null,
        int? httpStatus = null,
        long? elapsedMs = null,
        object? details = null,
        CancellationToken cancellationToken = default)
    {
        var entry = new
        {
            schema_version = 1,
            timestamp_utc = DateTimeOffset.UtcNow,
            run_id = _runId,
            scenario_date = _scenarioDate,
            window = _window,
            payload_sha256 = _payloadHash,
            action,
            phase,
            outcome,
            endpoint,
            http_status = httpStatus,
            elapsed_ms = elapsedMs,
            details
        };

        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(entry, JsonOptions) + "\n");
        await WriteLock.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            await using var stream = new FileStream(
                _path,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 4096,
                options: FileOptions.Asynchronous | FileOptions.WriteThrough);
            await stream.WriteAsync(bytes, cancellationToken);
            await stream.FlushAsync(cancellationToken);
            stream.Flush(flushToDisk: true);
        }
        finally
        {
            WriteLock.Release();
        }
    }
}

internal sealed class AuditedHttpHandler(SanitizedAuditTrail auditTrail) : DelegatingHandler(new HttpClientHandler())
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Keep only the API path. Never persist query strings, headers, bodies, serials, or credentials.
        var endpoint = request.RequestUri?.AbsolutePath.TrimStart('/') ?? "unknown";
        var started = Stopwatch.StartNew();
        await auditTrail.AppendAsync("http_request", "started", "pending", endpoint, cancellationToken: cancellationToken);
        try
        {
            var response = await base.SendAsync(request, cancellationToken);
            started.Stop();
            await auditTrail.AppendAsync(
                "http_request",
                "completed",
                response.IsSuccessStatusCode ? "http_success" : "http_failure",
                endpoint,
                (int)response.StatusCode,
                started.ElapsedMilliseconds,
                cancellationToken: cancellationToken);
            return response;
        }
        catch (Exception exception)
        {
            started.Stop();
            await auditTrail.AppendAsync(
                "http_request",
                "failed",
                "transport_error",
                endpoint,
                elapsedMs: started.ElapsedMilliseconds,
                details: new { exception_type = exception.GetType().Name },
                cancellationToken: cancellationToken);
            throw;
        }
    }
}
