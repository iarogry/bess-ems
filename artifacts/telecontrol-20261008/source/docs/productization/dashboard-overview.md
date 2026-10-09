# Site dashboard overview (2026-10-07)

The operator overview now prioritizes generation, own consumption, grid export,
and today's planned site profit. All technical panels, schedules, measurements,
and optimization run details are inside a native `details` disclosure, closed
by default. Advanced filters are also collapsed. Charts redraw when the
technical disclosure opens, with a stable canvas height across redraws.

## Source limitations

- Generation currently means `pv_power_kw` for the selected telemetry asset;
  it is not an aggregate of all site generators.
- Consumption uses `load_power_kw`; the live API cannot isolate the site's
  own consumption from subconsumers. The daily meter balance is not an
  instantaneous power reading and is not used as a substitute.
- Export uses `grid_power_kw` only when the meter sign convention has been
  explicitly selected. Positive import and positive export conventions are
  supported. Import shows zero export; absent/stale data shows a dash.
  This setting resets on asset or site selection changes.
- Planned profit now reads `/site/{siteId}/financial-plan`, using the configured
  energy servicies calculation and ENTSO-E prices. It requires a complete
  current-day site plan and a valid review for the local calendar month.
  Changes in reviewed parameters invalidate the review. Details and setup are
  in `monthly-financial-review.md`; recovered external sources are documented
  in `dashboard-financial-sources.md`.

## Verification and deployment

19 JavaScript tests pass, including both export sign conventions, missing
convention, measured zero, financial date/site/review checks, stale readings, refresh races and timeout cleanup.
Chromium checks passed for desktop (1440px) and mobile (390px), collapsed and
expanded technical panels, chart sizing and absence of JavaScript errors.
Preview screenshots in `/workspace/onboarding-results/dashboard-overview-*.png`
use synthetic telemetry, not live server readings.

These changes are local. They have not been deployed to 10.10.70.66; the current
execution environment has no configured VPN access to that server.
