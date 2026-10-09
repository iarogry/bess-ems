using BatteryEms.Adapters.Persistence;
using Xunit;

namespace BatteryEms.Adapters.Persistence.Tests;

// RM-M2-MIG-06: pin the embedded-resource layout. Drafts under
// Migrations/Drafts/ are deliberately Build-Action None so DbUp
// never sees them; the only way they can leak into the script set
// is if someone adds a stray <EmbeddedResource Include=
// "Migrations/Drafts/**/*.sql" /> to the csproj. This test catches
// that mistake at unit-test time, before it reaches a database.
public sealed class MigrationResourceSetTests
{
    private const string RunOnceMarker = ".Migrations.RunOnce.";
    private const string DraftsMarker = ".Migrations.Drafts.";

    [Fact]
    public void Only_RunOnce_scripts_are_embedded()
    {
        var resources = typeof(BessDbMigrator).Assembly.GetManifestResourceNames();

        var runOnce = resources
            .Where(r => r.Contains(RunOnceMarker, StringComparison.Ordinal))
            .ToArray();
        Assert.NotEmpty(runOnce);
        Assert.Contains(runOnce, r => r.EndsWith(".0001_initial.sql", StringComparison.Ordinal));
        Assert.Contains(runOnce, r => r.EndsWith(".0006_price_series.sql", StringComparison.Ordinal));
        Assert.Contains(runOnce, r => r.EndsWith(".0008_site_measurements.sql", StringComparison.Ordinal));
        Assert.Contains(runOnce, r => r.EndsWith(".0009_site_pv_profiles.sql", StringComparison.Ordinal));
        Assert.Contains(runOnce, r => r.EndsWith(".0010_solar_forecasts.sql", StringComparison.Ordinal));
        Assert.Contains(runOnce, r => r.EndsWith(".0011_orchestration.sql", StringComparison.Ordinal));
        Assert.Contains(runOnce, r => r.EndsWith(".0012_site_measurement_status_rows.sql", StringComparison.Ordinal));
        Assert.Contains(runOnce, r => r.EndsWith(".0013_site_consumption_interval_seconds.sql", StringComparison.Ordinal));
        Assert.Contains(runOnce, r => r.EndsWith(".0014_shadow_plan_comparisons.sql", StringComparison.Ordinal));
        Assert.Contains(runOnce, r => r.EndsWith(".0015_activation_proposals_outbox.sql", StringComparison.Ordinal));
        Assert.Contains(runOnce, r => r.EndsWith(".0016_activation_writer_safety.sql", StringComparison.Ordinal));
        Assert.Contains(runOnce, r => r.EndsWith(".0017_activation_cutover_controls.sql", StringComparison.Ordinal));
        Assert.Contains(runOnce, r => r.EndsWith(".0018_activation_pilot_sessions.sql", StringComparison.Ordinal));
        Assert.Contains(runOnce, r => r.EndsWith(".0019_activation_prewrite_claims.sql", StringComparison.Ordinal));
        Assert.Contains(runOnce, r => r.EndsWith(".0020_activation_pilot_window_scope.sql", StringComparison.Ordinal));
        Assert.Contains(runOnce, r => r.EndsWith(".0021_activation_completion_evidence.sql", StringComparison.Ordinal));
        Assert.Contains(runOnce, r => r.EndsWith(".0022_device_write_broker_attempts.sql", StringComparison.Ordinal));
    }

    [Fact]
    public void Shared_broker_migration_latches_unresolved_sites_and_consumes_claim_and_window_once()
    {
        var sql = ReadRunOnceScript("0022_device_write_broker_attempts.sql");
        Assert.Contains("WHERE state IN ('Prepared', 'Initiated', 'Unknown')", sql, StringComparison.Ordinal);
        Assert.Contains("ux_device_write_broker_activation_claim", sql, StringComparison.Ordinal);
        Assert.Contains("ux_device_write_broker_verified_window", sql, StringComparison.Ordinal);
        Assert.Contains("device-write-broker-outcome-unknown", sql, StringComparison.Ordinal);
        Assert.Contains("IS TRUE", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("expires_at", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("DELETE", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Completion_evidence_migration_bounds_codes_and_rejects_backdated_completion()
    {
        var sql = ReadRunOnceScript("0021_activation_completion_evidence.sql");
        Assert.Contains("ck_activation_outbox_completion_evidence", sql, StringComparison.Ordinal);
        Assert.Contains("{0,94}", sql, StringComparison.Ordinal);
        Assert.Contains("claim_completed_at_utc >= claimed_at_utc", sql, StringComparison.Ordinal);
        Assert.Contains("IS TRUE", sql, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0018_activation_pilot_sessions.sql")]
    [InlineData("0020_activation_pilot_window_scope.sql")]
    public void Activation_upgrade_guards_use_DbUp_safe_dollar_quoting(string script)
    {
        var sql = ReadRunOnceScript(script);
        Assert.Contains("DO $$", sql, StringComparison.Ordinal);
        Assert.Contains("RAISE EXCEPTION", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("$activation_", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Activation_outbox_migration_is_held_idempotent_and_secret_free()
    {
        var sql = ReadRunOnceScript("0015_activation_proposals_outbox.sql");

        Assert.Contains("activation_proposals", sql, StringComparison.Ordinal);
        Assert.Contains("UNIQUE (comparison_id, payload_hash)", sql, StringComparison.Ordinal);
        Assert.Contains("activation_outbox", sql, StringComparison.Ordinal);
        Assert.Contains("idempotency_key TEXT NOT NULL UNIQUE", sql, StringComparison.Ordinal);
        Assert.Contains("'Held'", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("device_sn", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("credential", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Activation_writer_safety_migration_defaults_closed_and_has_fencing_state()
    {
        var sql = ReadRunOnceScript("0016_activation_writer_safety.sql");

        Assert.Contains("kill_switch_engaged BOOLEAN NOT NULL DEFAULT TRUE", sql, StringComparison.Ordinal);
        Assert.Contains("writer_authority TEXT NOT NULL DEFAULT 'LegacyRunner'", sql, StringComparison.Ordinal);
        Assert.Contains("activation_writer_fence_sequences", sql, StringComparison.Ordinal);
        Assert.Contains("activation_writer_leases", sql, StringComparison.Ordinal);
        Assert.Contains("fencing_token BIGINT NOT NULL", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("password", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token TEXT", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Activation_cutover_migration_binds_release_and_idempotent_rollback_metadata()
    {
        var sql = ReadRunOnceScript("0017_activation_cutover_controls.sql");

        Assert.Contains("release_safety_revision BIGINT", sql, StringComparison.Ordinal);
        Assert.Contains("release_fencing_token BIGINT", sql, StringComparison.Ordinal);
        Assert.Contains("ck_activation_outbox_release_metadata", sql, StringComparison.Ordinal);
        Assert.Contains("last_rollback_operation_id UUID", sql, StringComparison.Ordinal);
        Assert.Contains("last_rollback_cancelled_count INTEGER", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("password", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Activation_pilot_migration_is_bounded_exclusive_and_release_bound()
    {
        var sql = ReadRunOnceScript("0018_activation_pilot_sessions.sql");

        Assert.Contains("activation_pilot_sessions", sql, StringComparison.Ordinal);
        Assert.Contains("INTERVAL '15 minutes'", sql, StringComparison.Ordinal);
        Assert.Contains("ux_activation_pilot_sessions_active_site", sql, StringComparison.Ordinal);
        Assert.Contains("ux_activation_pilot_sessions_active_outbox", sql, StringComparison.Ordinal);
        Assert.Contains("WHERE status = 'Armed'", sql, StringComparison.Ordinal);
        Assert.Contains("release_pilot_session_id UUID", sql, StringComparison.Ordinal);
        Assert.Contains("migration blocked", sql, StringComparison.Ordinal);
        Assert.Contains("status IN ('Ready', 'Claimed')", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("password", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Activation_prewrite_claim_migration_is_bounded_and_completion_auditable()
    {
        var sql = ReadRunOnceScript("0019_activation_prewrite_claims.sql");

        Assert.Contains("claim_id UUID NULL UNIQUE", sql, StringComparison.Ordinal);
        Assert.Contains("INTERVAL '30 seconds'", sql, StringComparison.Ordinal);
        Assert.Contains("claim_safety_revision", sql, StringComparison.Ordinal);
        Assert.Contains("claim_fencing_token", sql, StringComparison.Ordinal);
        Assert.Contains("claim_pilot_session_id", sql, StringComparison.Ordinal);
        Assert.Contains("claim_outcome_code", sql, StringComparison.Ordinal);
        Assert.Contains("claim_completed_at_utc", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("password", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Activation_window_scope_migration_binds_pilot_release_and_claim_to_one_window()
    {
        var sql = ReadRunOnceScript("0020_activation_pilot_window_scope.sql");

        Assert.Contains("window_id TEXT NOT NULL", sql, StringComparison.Ordinal);
        Assert.Contains("release_window_id", sql, StringComparison.Ordinal);
        Assert.Contains("claim_window_id", sql, StringComparison.Ordinal);
        Assert.Contains("claim_window_payload_hash", sql, StringComparison.Ordinal);
        Assert.Contains("IN ('Z1', 'Z2', 'Z3', 'Z4')", sql, StringComparison.Ordinal);
        Assert.Contains("migration blocked", sql, StringComparison.Ordinal);
        Assert.Contains("IF EXISTS (SELECT 1 FROM activation_pilot_sessions)", sql, StringComparison.Ordinal);
        Assert.Contains(") IS TRUE)", sql, StringComparison.Ordinal);
        Assert.Contains("preserving audit records", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("device_sn", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Shadow_plan_migration_is_append_only_and_run_bound()
    {
        var sql = ReadRunOnceScript("0014_shadow_plan_comparisons.sql");

        Assert.Contains("shadow_plan_snapshots", sql, StringComparison.Ordinal);
        Assert.Contains("UNIQUE (site_id, delivery_date, side, snapshot_hash)", sql, StringComparison.Ordinal);
        Assert.Contains("shadow_plan_comparisons", sql, StringComparison.Ordinal);
        Assert.Contains("run_id UUID NOT NULL UNIQUE REFERENCES orchestration_runs", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("device_sn", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("credential", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Timescale_migration_is_guarded_for_plain_postgres()
    {
        var sql = ReadRunOnceScript("0005_timescale_telemetry_hypertable.sql");

        Assert.Contains("pg_available_extensions", sql, StringComparison.Ordinal);
        Assert.Contains("TimescaleDB extension is not available", sql, StringComparison.Ordinal);
        Assert.Contains("shared_preload_libraries", sql, StringComparison.Ordinal);
        Assert.Contains("TimescaleDB extension is available but is not preloaded", sql, StringComparison.Ordinal);
        Assert.Contains("CREATE EXTENSION IF NOT EXISTS timescaledb", sql, StringComparison.Ordinal);
        Assert.Contains("insufficient_privilege", sql, StringComparison.Ordinal);
        Assert.Contains("create_hypertable", sql, StringComparison.Ordinal);
        Assert.Contains("PRIMARY KEY (\"id\", \"recorded_at\")", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("$bess_timescale$", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void No_draft_migration_is_embedded()
    {
        var resources = typeof(BessDbMigrator).Assembly.GetManifestResourceNames();

        var drafts = resources
            .Where(r => r.Contains(DraftsMarker, StringComparison.Ordinal))
            .ToArray();
        Assert.Empty(drafts);
    }

    [Fact]
    public void Embedded_resource_set_contains_only_RunOnce_migration_scripts()
    {
        // Carve-out Mn4: belt-and-suspenders allowlist. The two
        // tests above check positive (RunOnce contains the
        // expected file) and one negative (no Drafts/* leak), but
        // neither catches a wholly different leak — e.g. someone
        // adds <EmbeddedResource Include="**/*.json" /> and ships
        // an unintended payload through DbUp's filter (it would
        // skip the json, but the resource set would still grow
        // silently). This test fences the manifest: every assembly
        // resource must either match the RunOnce/????_*.sql
        // pattern or live on the explicit allowlist.
        var assembly = typeof(BessDbMigrator).Assembly;
        var resources = assembly.GetManifestResourceNames();

        var allowedNonScript = new[]
        {
            // .NET adds these automatically for resx-style strings;
            // they would never appear here today but are reserved
            // by the framework if a future class adds a .resx file.
            ".g.resources",
        };

        var unexpected = resources
            .Where(r => !r.Contains(RunOnceMarker, StringComparison.Ordinal))
            .Where(r => !allowedNonScript.Any(a => r.EndsWith(a, StringComparison.Ordinal)))
            .ToArray();

        Assert.True(
            unexpected.Length == 0,
            "Unexpected manifest resources detected (every embedded resource must "
            + "be a RunOnce/????_*.sql script or appear on the allowlist): "
            + string.Join(", ", unexpected));
    }

    private static string ReadRunOnceScript(string fileName)
    {
        var assembly = typeof(BessDbMigrator).Assembly;
        var resource = assembly.GetManifestResourceNames()
            .Single(name => name.EndsWith("." + fileName, StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Embedded migration not found: {fileName}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
