# Broker staging package: closed startup only

This is a framework-dependent **BrokerHost-only** staging package. It is not a
complete EMS release, a hardware pilot approval or evidence of shadow equivalence.
Use PowerShell 7 and the .NET 10 SDK for packaging; a compatible ASP.NET Core 10
runtime is required to run it. No service installer or automatic startup is included.

From the isolated repository root:

```powershell
./scripts/package-productization-broker.ps1 -DotnetPath <absolute-dotnet-path>
./scripts/test-productization-broker-package.ps1 -PackagePath <reported-package-directory> -DotnetPath <absolute-dotnet-path>
```

Packaging performs locked restore and strict Release publish. Each run creates a
new artifacts directory; it never replaces an existing package. The manifest
records source revision, dirty state, SDK and every payload file's size/SHA256.
The archive hash is printed. These hashes detect changes against a trusted
manifest; they are **not signatures** and do not establish publisher authenticity.
Do not promote a package built with `sourceDirty=true`.

The smoke check verifies payload hashes, refuses implicit `appsettings*.json`,
clears the child environment, and starts only its own process on loopback with
all three broker switches false. Expected: `/healthz` = 503 and POST
`/v1/device-writes` = 404. It stops its child and repeats startup once. Logs and
results are preserved outside the hashed payload. No operational `.env`, secret,
database or vendor endpoint is used. This is an abrupt disabled-process restart,
not a graceful shutdown or Initiated/Unknown recovery drill.

`broker-staging.example.json` is documentation, **not automatically loaded**.
Empty credentials and `vendor.invalid` intentionally make it unsuitable for
enabled deployment. Never copy operational secrets into this package.

Before any remote staging launch: agree host/OS, private-network access,
dedicated staging database, trusted TLS certificate and secret storage. Inspect
environment/config precedence and service permissions on that host. First start
disabled and loopback-only. Do not install an auto-restart service yet.

`Broker:Enabled=true` runs configured database migrations. This needs a separate
reviewed staging step even with the Deye switches false. Real driver composition
requires both `Broker:Deye:Enabled=true` and `Broker:Deye:WriteEnabled=true`;
these are not authorized by this runbook. Readiness currently remains 503.

Before hardware enablement: prove real shadow/legacy equivalence, device protocol
compatibility, physical sole-writer credential/egress restrictions and approved
pilot/kill-switch/rollback/reconciliation procedures. A lost response stays
Initiated/Unknown across restart; never unlock it merely to retry a command.
