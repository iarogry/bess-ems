# Existing financial calculations for the site dashboard

Checked 2026-10-07. This investigation uses local repository code and code/output
captured in accessible Codex chat history. The Windows directories are not
mounted in the current cloud workspace. Their current file contents and live
scenario availability have not been independently read from disk here.

## Sources recovered

- Chat `TOU BESS Deye — планування 15:00`, ID
  `01a080e4-8b7d-7ba2-b181-d6160a1d8f41`: the historical output contains
  `tmp/bess-deye-daily/PriorityOptimize/Program.cs`, the scenario builder,
  contract 1.3.0, and independent financial validation output.
  Operational checkout: `C:\Users\admin\temp\bess-ems`.
- Chat `Розрахунок Новобудов місяць КГУ СЕС УЗЕ`, ID
  `019f5fb2-b7cb-7a81-b165-35cd9b741fab`: the historical output contains
  the generation calculator and business rules.
  Project: `C:\Users\ya.grishin\Documents\energy servicies`.
  Shared calculator: `C:\Users\ya.grishin\.codex\skills\prepare-monthly-generation-report\scripts\calculate_monthly_effect.py`.
  Rules: corresponding `references\business-rules.md`.
  Battery report: project `tmp\2026-09-novobudov-battery-report\analyze_battery.py`.
- Current local BESS calculator:
  `src/hexagon/BatteryEms.Application/Optimization/ScheduleEconomics.cs`.

## Battery planning automation

For each hour, price is official OREE DAM/IPS in UAH/MWh; aggregate power
is in kW. For the one-hour interval:

    charge_cost = charge_kw * price_uah_mwh / 1000
    discharge_revenue = discharge_kw * price_uah_mwh / 1000
    final_margin = sum(discharge_revenue) - sum(charge_cost)

Efficiency (historically 0.95 each direction) enters the SOC dynamics:
SOC energy increases by charge * efficiency and decreases by discharge /
efficiency. It is not multiplied onto already grid-side monetary energy again.
The optimization objective has a tiny deterministic time tie-break. The saved
representable trajectory is recomputed after quantization and six-slot
projection; use its final financial result, not the raw solver objective.

Independent validation of the 2026-10-07 scenario recorded:

| Field | Value |
| --- | ---: |
| Charged energy, kWh | 1015.056 |
| Discharged energy, kWh | 916.088 |
| Charging cost, UAH | 1550.165160 |
| Discharge revenue, UAH | 9368.314232 |
| Final margin, UAH | 7818.149072 |
| Raw optimizer margin, UAH | 7818.17291070109 |

This is a historical planned result, not proof of execution or cash receipts.
The automation builds the next delivery date; the dashboard must select today's
Europe/Kyiv delivery date explicitly. Partial same-day recovery must not be
presented as a complete daily plan.
The financial formula excludes distribution/transmission tariffs, sale release
coefficient, degradation, and other operating expenses.

## Site reporting methodology

All hourly energy inputs are in kWh; DAM price here is in UAH/kWh.

    subscribers_net = subscribers_import - subscribers_export
    source_base = KGU_generation + PV_generation + battery_discharge
    own_needs = source_base + grid_import - subscribers_net - battery_charge - grid_export
    commercial_export = grid_export + subscribers_net

The calculator bounds source energy allocated to own needs, commercial export,
battery charging and unallocated destinations. It distributes the destination
volumes proportionally to hourly KGU/PV/battery source shares. Imported energy
is not included in these shares.

    full_purchase_price = DAM + distribution + transmission
    own_use_saving = source_own_kwh * full_purchase_price
    export_revenue = source_export_kwh * DAM * release_coefficient
    source_effect = own_use_saving + export_revenue
    battery_charge_cost = measured_battery_charge_kwh * full_purchase_price
    battery_net_effect = battery_own_saving + battery_export_revenue - battery_charge_cost

Historical September Novobudov parameters, excluding VAT:
distribution 2.89668 UAH/kWh, transmission 0.92845 UAH/kWh,
release coefficient 0.965. These are period/site parameters, not universal
constants: verify effective dates, operator and voltage class for today's plan.

Source economic effect includes avoided expenditure. It is not all cash
income and is not net profit. KGU fuel, maintenance, battery degradation and
other costs are not deducted by the above source-effect formula.
Keep grid cash balance separate from the source economic effect.
Do not add battery automation margin to a site report that already includes
the battery's discharge benefit; reconcile one common energy ledger first.

## Known source issue

The historical generation calculator wrote `battery_charge_kwh` first as
measured charging energy, then again as the battery source's allocation to
charging. This overwrote the input column in `hourly_result.csv`, yielding an
incorrect 0.017 kWh monthly summary instead of 13777.05 kWh.
The later battery report explicitly used unchanged `normalized_september.csv`
for measured charge and recomputed charge costs. Its reported net gross battery
effect was 98312.77 UAH excluding VAT. This is a September actual-report figure,
not today's forecast.
Any integration must keep measured `battery_charge_kwh` separate from source
allocation names such as `battery_to_charge_kwh`. For the specified ASKUE
interval format, kWh = raw * multiplier; do not apply another factor 0.5.

## Dashboard mapping

The calculation exists in the external automation/reporting projects. The
current change adds `/site/{siteId}/financial-plan` and a configurable,
read-only daily-plan artifact ingestion path. See `monthly-financial-review.md`.
No real plan exporter or server configuration has been connected yet.
A suitable contract must expose site ID, delivery date, timezone, plan/actual
basis, revision/source digest, calculation timestamp, tariff provenance,
currency/VAT basis, revenue, avoided cost, charging cost and gross effect.

For the requested site overview, distinguish:

- generation = KGU + PV (battery discharge shown separately as storage output);
- own consumption = own-needs balance after separating subconsumers;
- physical grid export = incomer export, separate from commercial export which
  includes downstream subconsumers;
- planned gross effect = consistently allocated projected own-use savings and
  sales, with battery charge costs treated once under an explicit cost policy.

A monthly actual report or next-day battery plan cannot populate today's
whole-site planned-profit card. Until a valid current-day site plan is ingested,
keep the card unavailable rather than hardcoding a recovered historical value.
