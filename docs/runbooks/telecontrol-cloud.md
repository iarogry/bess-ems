# Telecontrol cloud: Novobudov 6 CHP

Implemented on 2026-10-08. User-confirmed equipment: Viessmann Vitobloc 200 NG 530 SCR MT/LE, `Hlibodar-5 ZP`, device ID `5552`, equipment `62475997`.

## Verified protocol

- `POST https://kwkac.azurewebsites.net/api/auth/token` with JSON `userName`, `password`, `appType=wpf`, `appVersion=1.5.0.0`, `deploymentVersion=1.5.0.0`.
- `POST https://kwk-gateway-prod.azurewebsites.net/api?call=GetLastDatapoints` with JSON `deviceId=5552` and `Authorization: Bearer …`.
- **A User-Agent is mandatory.** Without it the gateway returns HTTP 200 with a `properties.Requester` validation error rather than telemetry. The connector identifies itself as `bess-ems-telecontrol/1.0`.
- Response fields verified live: `data.packageDateTime`, `data.dataPoints`, `data.messages`. `data.id` identifies a sample, not the generator.
- The C# adapter and its hosted polling service were verified live: 71 parameters, 6 message codes, active power 0 kW, operating hours 3133. Sample time `2026-10-08T14:48:07` normalized provisionally to `2026-10-08T12:48:07Z`; receipt was `12:48:15Z`.

This is the installed desktop client's cloud protocol, not a documented vendor-supported public API. It is suitable as an interim monitoring integration with explicit failure and freshness handling.

## Configuration

Use `config/examples/telecontrol.novobudov6.json` plus runtime secrets:

```text
Telecontrol__Enabled=true
Telecontrol__DeviceId=5552
Telecontrol__AssetId=telecontrol-5552
Telecontrol__SourceTimeZoneId=Europe/Berlin
Telecontrol__ClockConfirmed=false
Telecontrol__PollIntervalSeconds=30
Telecontrol__MaxMeasurementAgeSeconds=120
Telecontrol__Username=<existing Telecontrol username>
Telecontrol__Password=<existing Telecontrol password>
```

The host reads ASP.NET configuration/environment variables; it does not automatically import `.env`. Compose can use an env file. The diagnostic console imports only `Telecontrol__*` entries from the local ignored `.env`.

`Telecontrol:Enabled` registers an independent collector alongside existing FusionSolar/ASKUE services. It does not replace `Bess:SiteTelemetrySource`. No generator control operations exist in this adapter.

Endpoints:

- `GET /chp/telecontrol-5552/status`: parameters with original strings, numeric values, units and availability, message codes, power, source/receipt times and quality. Missing source returns 404; failed polls publish unavailable power with protocol-error quality.
- Fleet views can bind `kind=chp`, `TelemetryId=telecontrol-5552`, and one canonical physical ID. Replace an existing binding for this same physical CHP rather than adding it twice. The equipment's placement at Novobudov 6 is confirmed; its relationship to the provisional Deye/Huawei fleet rows still requires explicit site membership.

## Semantics

Generation is only the unique point whose `name` and `dataBlockName` are both `Leistung`, with unit `kW` and scale 0. Values in the observed payload are already scaled. Setpoints, energy and reactive/apparent power are never substituted. A measured zero remains zero; missing, negative, nonfinite or duplicate power is unavailable.

Each parameter retains the provider name and unit. Temperatures below absolute zero (including observed -437/-438 sentinel values) are unavailable. `Drehzahl` is RPM; actual electrical frequency is `GridFrequency` in Hz. Message codes are retained without inventing descriptions or severity.

The provider returns a timezone-free wall clock. Its offset matches Europe/Berlin during this observation, but the vendor's clock convention has not been independently confirmed. `ClockConfirmed=false` therefore marks otherwise fresh readings `Substituted`, which existing EMS control-quality rules reject. Explicit offsets are honored; missing, invalid, ambiguous DST or future timestamps are rejected, and old readings become stale. The original timestamp and receipt time remain inspectable. Do not set `ClockConfirmed=true` solely from this one-hour comparison.

Bearer tokens remain in process memory. Expiry is respected; a telemetry 401 triggers at most one reauthentication/retry. Failed logins have a five-minute cooldown. HTTP redirects are disabled. Provider bodies, passwords and token values are not logged.

## Validation and release

Run the adapter tests and `ChpTelemetryEndpointTests` / `FleetOverviewTests`. `tools/TelecontrolProbe` starts the same hosted collector with in-memory stores and prints a single summary, then stops. It never issues equipment commands.

An isolated candidate source is prepared under `artifacts/telecontrol-20261008/source` from the deployed dashboard source snapshot, with additive Telecontrol changes. This preserves server-only dashboard/planning features absent from the workspace's base host. Do not deploy the workspace's base Host DLL over that server snapshot.

Deployed on 2026-10-08 after explicit user authorization to `jar@10.10.70.66`. Image `bess-ems:telecontrol-20261008` runs in `bess-readonly-data-20261008`. The existing multi-account ASKUE environment and dashboard features were preserved. Following explicit user confirmation, source `telecontrol-5552`, physical ID `viessmann-62475997-generation`, was moved into `site-khlibzavod-5`. The standalone `site-novobudov6` entry was removed. Other existing source bindings, including `kgu-929-realtime`, were retained; no identity equivalence with that source has been established. Fleet membership outside this confirmed binding remains provisional.

Server verification at 14:25 UTC (17:25 Kyiv): health/database OK; 71 parameters, power 0 kW; measurement 14:24:39 UTC, receipt 14:24:46 UTC; receipt advanced over a 35-second check. ASKUE account `khlibzavod5` was OK and imported 340 readings. Twelve existing equipment/control switches remained false, gateway POST returned 401, container restart count was zero. Quality remains `Substituted` because the source timezone convention is provisional. Full evidence: `artifacts/telecontrol-20261008/verification.json`.

Read-only URLs: `http://10.10.70.66:3000/chp/telecontrol-5552/status` and `http://10.10.70.66:3000/sites/overview`. Previous backend and gateway containers were retained with suffix `-before-telecontrol`; private environment/inspect backups are mode 0600 in the server release directory. The first gateway check used loopback despite the gateway binding to the LAN address, triggered rollback, and was corrected before the successful deployment.

The site membership change uses the same image, preserves all other environment settings, and retains the immediately preceding backend and gateway with suffix `-before-chp-site`. Configuration-only deployment script: `artifacts/telecontrol-20261008/move-chp-site.py`.
