# Saved server source, 2026-10-09

The current deployed server uses the extended source tree preserved at
`artifacts/telecontrol-20261008/source`. It includes the EMS planning/finance
modules, read-only prepared plan endpoints, broker and control gates, 23 database
migrations, multi-site catalog, FusionSolar device-power adapter, independent
ASKUE accounts, verified per-site transformer factors, Telecontrol CHP telemetry,
and the latest operator dashboard.

The main tree retains all accumulated source, tools, tests and documentation
changes. The extended source snapshot is saved separately to preserve the
current deployed version without replacing differences in the main tree.
Only source/configuration templates/documentation are committed from the
snapshot; generated binaries, environment files, private deployment settings,
raw provider exports and local logs are excluded.

## Rebuild

Install .NET SDK10.0.300 or a compatible10.0.x SDK and Node.js for UI tests.
From the repository root:

```powershell
dotnet build src/host/BatteryEms.Host/BatteryEms.Host.csproj -c Release
dotnet build artifacts/telecontrol-20261008/source/src/host/BatteryEms.Host/BatteryEms.Host.csproj -c Release
node --test artifacts/telecontrol-20261008/source/tests/operator/dashboard.test.cjs
```

The server application image is based on the extended tree. Its final web files
are read-only mounts from `readonly-data-20261008/new-site-balances-build` on the
server. Corresponding sources are saved in both trees under
`src/adapters/driving/BatteryEms.Api/wwwroot/operator`.
Credentials must be supplied from private configuration. Current server data
collection and planning remain active, with all12 command/control flags false.

Validation before saving: main Host Release build and extended Host Release
build both pass with0warnings/0errors;21 targeted main API tests pass;27 UI tests
pass. Transformer factors were verified against portal metadata for all new-site
meters, and the energy normalization was checked against portal interval energy.
