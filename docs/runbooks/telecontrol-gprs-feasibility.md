# Telecontrol GPRS: investigation for CHP telemetry

Implementation follow-up: see [telecontrol-cloud.md](telecontrol-cloud.md). Direct cloud access and the new C# collector have since been verified; the investigation below records the earlier findings.

Investigated on 2026-10-08 through the running Windows application and read-only inspection of its installed files. No generator commands, settings changes, credential extraction, or authenticated API requests were performed.

## Finding

A direct cloud telemetry connector is technically plausible. The installed client contains concrete REST operation names and SignalR hub addresses. This establishes an integration path to investigate, not a verified public or supported API contract. Authentication, request envelopes, point identifiers, units, rate limits, and unattended access still require validation.

An alternative temporary bridge can read the already authenticated application's Windows accessibility tree. Actual numeric values and labels were successfully retrieved as text, so OCR is unnecessary for the observed screen. This approach requires an interactive Windows session and application availability; it is unsuitable as a dependable control input.

## Observed application and equipment

- Telecontrol GPRS 1.5.0.0, revision 336, Viessmann Kraft-Wärme-Kopplung GmbH; .NET WPF / ClickOnce application, `TcGprsWpfClient.exe`.
- One equipment row was visible: `Hlibodar-5 ZP`, ID `5552`, modem `10386`, equipment `62475997`, `Vitobloc 200 NG 530 SCR MT/LE`, ViNCI geb8, Zaporizhzhia.
- The user confirmed that this equipment is the CHP at Novobudov 6 on 2026-10-08.
- Live view showed off status, active power 0.0 kW, reactive power 0.0 kVAr, apparent power 0.0 kVA, 3133 operating hours and 953 starts. Temperatures changed between observations.
- The device timestamp advanced from 14:20:35 to 14:21:45 while the client clock showed approximately 15:21. Investigate the one-hour difference before applying stale-data checks; do not assume UTC or silently add an hour.
- The field labelled frequency displays `Upm` (rotations per minute). It must not be mapped to electrical frequency in Hz.
- Some fields display `---`: preserve these as missing values rather than zero.

## Installed-file evidence

Non-secret application configuration values:

| Setting | Value |
| --- | --- |
| `kwkAuthUrl` | `https://kwkac.azurewebsites.net/api` |
| `kwkGatewayUrl` | `https://kwk-gateway-prod.azurewebsites.net` |
| `dataLogsUrl` | `https://kwk.blob.core.windows.net/jsondatamergecontract` |

Client string evidence includes `/api?call=`, `GetDevicesByUser`, `GetDevicesStatsByUser`, `GetLastDatapoints`, `GetStatesChangedDataPacks`, `GetChppStatesReports`, `Authorization`, `Bearer `, and a token-expiry message. These names were extracted from the binary; HTTP verbs, parameters and response schemas were not established.

The binary also contains these exact SignalR URLs:

- `https://kwk-gateway-prod.azurewebsites.net/hub/device`
- `https://kwk-gateway-prod.azurewebsites.net/hub/user`

The installation includes RestSharp, Newtonsoft.Json and Microsoft.AspNetCore.SignalR client libraries. The UI offers Live data, reports, charts and modem status. An existing local XLSX state report is present, but its contents and suitability as an automated data feed were not evaluated.

## Recommended EMS integration

Prefer a read-only connector to the gateway if authenticated read requests can be reproduced and unattended use is permitted. Start with the latest datapoint operation; add SignalR only after confirming its subscription and reconnect behavior. Do not infer a polling interval from the UI or exceed provider limits.

Store the provider device ID, source timestamp, UTC-normalized timestamp once timezone semantics are verified, receipt timestamp, quality, running state, active power, operating hours, starts, available temperatures and warnings. Preserve raw provider values where needed for traceability. Report loss of connection or stale data explicitly; never replace unavailable power with a measured zero.

The current EMS fleet projection already supports source kind `chp`; see `docs/runbooks/all-sites-dashboard.md`. It reads normalized generation from `PvPowerKw`. Integrate through the existing site telemetry abstraction and explicitly bind the physical CHP to the confirmed site, without double counting ASKUE-derived generation. ASKUE remains the accounting and reconciliation source. Cloud and UI telemetry should initially support monitoring only.

If direct gateway access cannot be established promptly, an accessibility bridge can extract the live screen into a local read-only telemetry feed. Validate timestamp advancement, selected device identity, locale-dependent decimals and missing fields on every sample. Restarting, reconnecting and navigating the client require operational handling; the bridge must advertise unavailability when these fail.

Before deployment, verify one authenticated latest-data response for this device, map and compare its values with the live screen, resolve clock semantics, observe operation through a stop/start cycle and a connection interruption, and test the adapter's missing/stale handling. No connector has been implemented or deployed by this investigation.

For a permanent local interface, verify the exact installed controller's Modbus options and register map with its documentation or supplier. Other ViNCI-equipped Vitobloc models advertise Modbus TCP/RTU, but that does not prove this unit's enabled interfaces.
