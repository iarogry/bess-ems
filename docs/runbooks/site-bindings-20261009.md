# Site bindings verified 2026-10-09

| Site | FusionSolar station codes | ASKUE account key | Point IDs |
|---|---|---|---|
| Зачиняєва 113 | NE=158463133, NE=133657926 | zachyniaieva113 | 761,762 |
| Українська 96 | NE=311287210 (access pending) | ukrainska96 | 550,551 |
| Верхня 1 | none | verkhnia1 | 756,757,758,759,760 |
| Головна 223 | NE=129469793, NE=134735482 | none | none |

The provider station registry verified addresses for four accessible stations.
311287210 is absent from the current FusionSolar API account (total four stations).
Its site binding is recorded, but it is excluded from provider batch polling until
access is granted, to avoid breaking collection for accessible stations.

ASKUE accounts are independent. Khlebzavod3 can also access agroservis meters
761/762 and points 552,553,798; these are excluded from Ukrainian96 pending the
owner's clarification. Duplicate provider point IDs are deduplicated by adapter.
All three accounts successfully imported data after point-scope correction.
120 accidentally imported Ukrainian96 rows from other points were backed up
on the server before removal; corrected queries contain only 550/551.

New ASKUE data retains raw interval energy. Novobudov6 transformer factors are
not reused for other sites. Per-site normalized consumption/balance requires
its own verified meter ratios and meter roles.

Dashboard membership is confirmed for configured physical sources. Missing or
unconfigured sources are reflected as partial coverage; displayed fleet power
is the sum of available readings. The two unmapped sites are left unchanged.
FusionSolar uses device active_power, with substituted timestamp quality when
the provider omits a sample timestamp.

Verification: 26 UI tests pass; health/database OK; 3 ASKUE account states OK;
60 / 120 / 209 imported rows respectively; exact station-to-site sets verified;
12 command flags remain false and gateway write requests return HTTP401.
Evidence: artifacts/dashboard-export-20261008/site-bindings-verification.json.
Passwords are only in root0600 private server configuration and are absent here.


## Follow-up: new ASKUE site balances

Root cause: balance configuration lookup supported only Novobudov6. Collection
succeeded, but new sites returned site-balance-configuration-not-found (404).
The API now reads per-site meter definitions with explicit Name, Role, Kt, Kn.
Unknown/unverified sites remain unavailable; there is no default multiplier.
All supported sites use the Kyiv accounting day. Source configuration:
config/examples/site-balance.new-sites.json.

Portal metadata verified for all nine meters: Ukrainian96 550/551=600;
Zachyniaieva113 761=900 and 762=1500; Verkhnia1 756/757=200,
758/759/760=1. Interval energy does not get multiplied by duration again.
Meter756's first two 15-minute API intervals produce52.68kWh, exactly matching
portal30-minute energy52.6800. Main inputs are distinguished from independent
mobile-provider subconsumer meters.

The daily ASKUE balance panel is now in the main single-site overview, outside
collapsed technical details. Real-time kW cards remain sourced from power
telemetry; interval kWh is not substituted for live power. ASKUE balance covers
assigned ASKUE meters; unmetered FusionSolar generation is not added to it.
7 API regression tests cover normalization, site isolation and Kyiv day start;
26 existing UI tests pass.

Final UI deployment uses two read-only file bind mounts from
readonly-data-20261008/new-site-balances-build/app.js and index.html into
/app/wwwroot/operator/. Docker classic image export failed with a missing
cache layer, including a no-cache retry. API image remains
bess-ems:new-site-balances-20261009; both UI mounts are verified read-only.
Final receipt: artifacts/dashboard-export-20261008/new-site-balances-verification.json.
Browser confirmed the visible Zachyniaieva113 panel with4578kWh grid import.
