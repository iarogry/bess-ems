using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BatteryEms.Application.Planning;

namespace BatteryEms.Host.Planning;

// Immutable date-keyed files on a persistent volume. Atomic rename publishes
// schedule and API actions together; interrupted temporary files are ignored.
public sealed class FileEquipmentDayPlanStore(string directory) : IEquipmentDayPlanStore
{
    private static readonly JsonSerializerOptions JsonOptions = new();

    public async Task<EquipmentDayPlan?> FindAsync(string assetId, DateOnly deliveryDate, CancellationToken cancellationToken)
    {
        var path = PathFor(assetId, deliveryDate);
        if (!File.Exists(path)) { return null; }
        using var stream = File.OpenRead(path);
        var plan = await JsonSerializer.DeserializeAsync<EquipmentDayPlan>(stream, JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Saved equipment plan is empty.");
        if (plan.Target.AssetId != assetId || plan.DeliveryDate != deliveryDate)
        { throw new InvalidDataException("Saved equipment plan identity mismatch."); }
        return plan;
    }

    public async Task<bool> TrySaveAsync(EquipmentDayPlan plan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        Directory.CreateDirectory(directory);
        var path = PathFor(plan.Target.AssetId, plan.DeliveryDate);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, plan, JsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                FlushToDisk(stream);
            }
            try { File.Move(temporary, path, overwrite: false); }
            catch (IOException) when (File.Exists(path)) { return false; }
            return true;
        }
        finally { if (File.Exists(temporary)) { File.Delete(temporary); } }
    }

    private static void FlushToDisk(FileStream stream) => stream.Flush(flushToDisk: true);

    private string PathFor(string assetId, DateOnly date) => Path.Combine(directory,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(assetId))) + $"-{date:yyyy-MM-dd}.json");
}
