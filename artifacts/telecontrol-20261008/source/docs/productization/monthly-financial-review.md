# Configurable site finance and monthly review

## Configuration

Set `Bess__FinanceConfigPath=/etc/bess/site-finance.json`. The host reads the
file and reloads parameter changes. `config/examples/site-finance.json` is an
unverified template: its zero rates are placeholders and `Confirmed=false`.
Never activate its values as real tariffs without verification.

`SiteFinance.Sites` contains one profile per site. All numerical monetary
inputs are configurable: distribution/transmission per kWh, release
coefficient, KGU/PV costs per generated kWh, battery degradation per discharged
kWh, fixed daily costs, tolerance and exchange rate. Monetary inputs/results
are excluding VAT. The configurable currency is a three-letter ISO code.
`MarketBidArea` must match the configured ENTSO-E domain; the price source is
always `entso-e`. Configure `Bess:PriceSeriesSource=entso-e`, token and domain
through the existing driver options, without storing tokens in this file.

Prices must have the declared `PriceCurrency/MWh` unit. `ExchangeRate` means
configured calculation currency per one source currency unit. When both
currencies match, use 1. The driver reads the response currency, including UAH;
EUR is not assumed. A different area, absent currency, mixed currencies,
wrong unit or incomplete price series cannot produce a financial result.
Hourly prices are cached for one hour in this query; monthly tariff reviews
are a separate cadence, not a restriction on daily market price updates.

## Monthly verification

The method is recovered from the energy servicies generation-report agent:

1. On the first day of the local month, open the official NERC distribution
   schedule whose effective period covers that month. Match the exact operator
   and voltage class, not merely the site name or newest visible table.
2. Record the rate excluding VAT, source URL, resolution, effective dates and
   date checked. Verify transmission independently.
3. Confirm the contract-specific release coefficient, currencies and FX basis
   separately. Operational cost estimates remain configurable and their exact
   snapshot must be included in the review.
4. Retain invoice/contract discrepancies and reconcile period, class, VAT and
   contractual basis. Do not silently choose one rate. Set `HasConflict=true`
   while unresolved; an unavailable official page is not verification.
5. Put the exact approved `Parameters`, currencies and rate into `Review`.
   `Month` is the first date of the checked month; `CheckedOn` cannot be after
   the delivery date; validity dates must cover it. `ParameterRevision`,
   operator/class, `MarketBidArea` and values must exactly match the current profile. Supply
   the statutory resolution references, coefficient and FX evidence.
6. Only after completion set `Confirmed=true`. Apply the updated JSON
   atomically. The retained prior config/review is the audit of old values.

One successful review covers the rest of its month within its effective
period; hourly dashboard refreshes do not call NERC. A new month, rate change,
operator/class change, FX change or unresolved conflict makes the result
`monthly_review_required`. Midmonth expiry of the tariff's effective period
also prevents using it outside that period.

`scripts/prepare-financial-review.py` prepares an idempotent review request
per site/month/parameter snapshot. Optional systemd service/timer templates
are in `deploy/finance/`: first day at 09:00 Europe/Kyiv, Persistent=true for a
missed run. Adapt paths and create the dedicated account before installation.
The request is a checklist for the energy servicies agent or operator. It does
not itself fetch NERC or certify numerical tariffs. No monthly automation has
been installed or enabled on the server by this change.

## Plan ingestion and output

The external planner must atomically supply
`{PlanDirectory}/{siteId}-YYYY-MM-DD.json` with snake_case fields:

```json
{
  "site_id": "site-khlibzavod-5",
  "delivery_date": "2026-10-07",
  "basis": "plan",
  "revision": "planner-revision-or-input-digest",
  "created_at": "2026-10-06T12:00:00Z",
  "intervals": [
    {
      "start": "2026-10-07T00:00:00+03:00",
      "kgu_generation_kwh": 0,
      "pv_generation_kwh": 0,
      "battery_charge_kwh": 0,
      "battery_discharge_kwh": 0,
      "grid_import_kwh": 0,
      "grid_export_kwh": 0,
      "subscribers_import_kwh": 0,
      "subscribers_export_kwh": 0
    }
  ]
}
```

The displayed single interval is a schema illustration. A real file must
contain every consecutive Kyiv-hour interval in the day: 23, 24 or 25 on DST
transition dates. Actual monthly reports, recovery-only partial plans and
battery-only plans are not a complete site plan. Keep measured/projected charge
separate from source-to-charge allocation, avoiding the known overwritten
`battery_charge_kwh` column in old reports.

`GET /site/{siteId}/financial-plan` selects today's Kyiv date independently of
the dashboard balance-date filter. It returns the parameters, review date,
plan revision, revenue, avoided purchase cost, battery charging cost and
operating costs. No hardware commands are issued. The dashboard uses
`planned_profit = own_use_saving + export_revenue - battery_charge_cost - operating_cost`.
Grid import cost is not subtracted again from source economic effect. Battery
legacy margin is not added a second time. This source-effect model conservatively
values all charging at the full purchase price, matching the existing battery
report; it is not a bank cash-flow statement.

Missing plan/review, mismatched identities/units, negative own-needs balances
and materially unallocated source energy produce an unavailable result. The
UI never substitutes yesterday's result or a historical monthly sum.
