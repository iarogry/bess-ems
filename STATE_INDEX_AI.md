# AI State Index

One line per decision or handoff. Search this file first; open only the
referenced handoff. Newest entries go at the top under `Entries`.

Format:

```text
timestamp | area | status | handoff | summary | verification | base_commit
```

Status values: `verified`, `partial`, `blocked`, `superseded`.

## Entries

```text
2026-10-06T12:56+03:00 | mvp-audit | verified | HANDOFF_2026-10-06T12-56.md | Independent MVP-readiness audit: ~85% ready; local build broken (12x NU1004 stale lock files + 7 CA errors in committed Deye/OREE code), local main diverged from origin/main 110/9, uncommitted Deye TOU WIP has 3 red tests + 1 khlibzavod config-drift failure; 1318/1322 non-integration tests pass; audit was non-mutating (lock files reverted, no commits/pushes); Deye TOU live runner untouched | full-solution build attempted, 15 non-integration test projects executed, clean-worktree committed-baseline build, git fetch origin | 2b197db54872184dd3b890c9401cee037d97f8ae
2026-07-20T09:55+03:00 | askue-archive | verified | HANDOFF_2026-07-18T18-29.md | Established clean ASKUE archive contract: source of truth is askue.net server, export 2026-01-01..2026-07-19 day-by-day into one final CSV, deduplicate by point_id+interval_start_local or point_id+timestamp; local tmp CSVs are artifacts, DB store upserts canonical intervals | live cookie-based ASKUE export for 2026-05-01 succeeded with 48 readings; persistence key and UPSERT path confirmed in site_consumption_readings | ac08e912975ec63d631f8d268385f84fc0225378
2026-07-18T19:00+03:00 | verification | verified | HANDOFF_2026-07-18T18-29.md | Installed .NET SDK 10.0.302 x64; removed Deye TLS analyzer finding; made ASKUE and ENTSO-E live tests explicit opt-in; promoted all indexed passports to verified | 1335/1335 tests passed across all 19 non-integration test projects | ac08e912975ec63d631f8d268385f84fc0225378
2026-07-18T18:29+03:00 | ai-navigation | verified | HANDOFF_2026-07-18T18-29.md | Added function passports, architecture snapshot, compact state index, and freshness protocol using base commit plus live file SHA-256 | document cross-check completed; no runtime code changed | ac08e912975ec63d631f8d268385f84fc0225378
2026-07-17T19:47+03:00 | fusionsolar | verified | HANDOFF_2026-07-18T18-29.md | Replaced getKpiStationHour with one batched getStationRealKpi request for four stations every 300s; aggregate active_power into site PV telemetry | FusionSolar tests 7/7; production Updated stations=4; site status Valid; no 407 observed | ac08e912975ec63d631f8d268385f84fc0225378
2026-06-10 | askue-units | partial | memory.md#askue-unit-correction | Corrected interval energy conversion to source_value*multiplier*interval_seconds/3600 | focused Application 14/14; ASKUE 2/2 at that time | ac08e912975ec63d631f8d268385f84fc0225378
2026-06-06 | orchestration | partial | memory.md#data-attributes-and-internal-orchestrator-planning-handoff | Added first Application/persistence orchestration slice; production scheduler and approval/control path remain follow-up | use case 5/5; migration 4/4; persistence roundtrip 3/3 at that time | ac08e912975ec63d631f8d268385f84fc0225378
```

## Update Rules

- Never paste long session prose here.
- A superseding decision gets a new line; do not rewrite history silently.
- A handoff filename must exist before its index line is considered valid.
- Never include credentials, tokens, cookies, private URLs containing secrets,
  or connection strings.
- If code changed after a handoff, its conclusions are navigation hints until
  live code and hashes are checked.
