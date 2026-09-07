const state = {
  assets: [],
  selectedAssetId: "",
  schedules: [],
  solarPoints: [],
};

const refreshIntervalMs = 30000;
const $ = (id) => document.getElementById(id);

const apiState = $("api-state");
const assetSelect = $("asset");
const manualAsset = $("manual-asset");
const siteIdInput = $("site-id");
const balanceDateInput = $("balance-date");
const runIdInput = $("run-id");

function setApiState(ok) {
  apiState.className = ok ? "state state-ok" : "state state-failed";
  apiState.textContent = ok ? "API ok" : "API failed";
}

function fmt(value) {
  if (value === null || value === undefined || value === "") {
    return "-";
  }
  return String(value);
}

function fmtNumber(value, digits = 2) {
  if (value === null || value === undefined || Number.isNaN(Number(value))) {
    return "-";
  }
  return Number(value).toLocaleString(undefined, {
    maximumFractionDigits: digits,
  });
}

function fmtDate(value) {
  if (!value) {
    return "-";
  }
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) {
    return value;
  }
  return date.toLocaleString();
}

function currentLocalDateInputValue() {
  const now = new Date();
  const localMidnight = new Date(now.getTime() - now.getTimezoneOffset() * 60000);
  return localMidnight.toISOString().slice(0, 10);
}

function unit(value, suffix, digits = 2) {
  const formatted = fmtNumber(value, digits);
  return formatted === "-" ? "-" : `${formatted} ${suffix}`;
}

async function request(path, options = {}) {
  const response = await fetch(path, {
    headers: {
      "accept": "application/json",
      ...(options.headers || {}),
    },
    ...options,
  });
  const text = await response.text();
  const payload = text ? JSON.parse(text) : null;
  if (!response.ok) {
    const error = new Error(payload?.error || response.statusText);
    error.status = response.status;
    error.payload = payload;
    throw error;
  }
  return payload;
}

function setFacts(id, entries) {
  const node = $(id);
  node.replaceChildren();
  for (const [label, value] of entries) {
    const dt = document.createElement("dt");
    const dd = document.createElement("dd");
    dt.textContent = label;
    dd.textContent = fmt(value);
    node.append(dt, dd);
  }
}

function selectedAssetId() {
  return manualAsset.value.trim() || assetSelect.value || state.selectedAssetId;
}

function selectedSiteId() {
  return siteIdInput.value.trim();
}

function applyAsset(assetId) {
  state.selectedAssetId = assetId;
  $("asset-title").textContent = assetId || "-";
  const asset = state.assets.find((item) => item.asset_id === assetId);
  $("asset-capacity").textContent = asset
    ? `${unit(asset.capacity_kwh, "kWh")} / ${unit(asset.max_discharge_power_kw, "kW")}`
    : "-";
}

function setKpis(values = {}) {
  $("kpi-soc").textContent = values.soc ?? "-";
  $("kpi-battery-power").textContent = values.batteryPower ?? "-";
  $("kpi-load").textContent = values.load ?? "-";
  $("kpi-pv").textContent = values.pv ?? "-";
  $("kpi-grid").textContent = values.grid ?? "-";
}

async function loadAssets() {
  const body = await request("/assets");
  state.assets = body.assets || [];
  assetSelect.replaceChildren();

  if (state.assets.length === 0) {
    const option = document.createElement("option");
    option.value = "";
    option.textContent = "manual";
    assetSelect.append(option);
    applyAsset(manualAsset.value.trim());
    return;
  }

  for (const asset of state.assets) {
    const option = document.createElement("option");
    option.value = asset.asset_id;
    option.textContent = asset.asset_id;
    assetSelect.append(option);
  }

  const first = state.assets[0].asset_id;
  assetSelect.value = state.selectedAssetId || first;
  applyAsset(assetSelect.value);
}

async function loadHealth() {
  const health = await request("/health");
  $("health-status").textContent = health.status || "unknown";
  $("health-at").textContent = fmtDate(health.at);

  const regelleistung = await request("/health/regelleistung");
  $("regelleistung-status").textContent = regelleistung.production_gate || "unknown";
  $("regelleistung-at").textContent = fmtDate(regelleistung.at);
}

async function loadAssetStatus(assetId) {
  if (!assetId) {
    setFacts("status-facts", [["Asset", "not selected"]]);
    setFacts("command-facts", [["Command", "not selected"]]);
    return {};
  }

  try {
    const status = await request(`/battery/${encodeURIComponent(assetId)}/status`);
    const telemetry = status.telemetry;
    const quality = status.quality;
    setFacts("status-facts", [
      ["SOC", telemetry ? unit(telemetry.soc_percent, "%") : "-"],
      ["SOH", telemetry ? unit(telemetry.soh_percent, "%") : "-"],
      ["Power", telemetry ? unit(telemetry.active_power_kw, "kW") : "-"],
      ["Reactive", telemetry ? unit(telemetry.reactive_power_kvar, "kvar") : "-"],
      ["Available", telemetry ? telemetry.available : "-"],
      ["Fault", telemetry ? telemetry.fault_status : "-"],
      ["Quality", quality ? `${quality.flag} (${quality.reason})` : "-"],
      ["Observed", fmtDate(status.observed_at)],
    ]);
    const command = status.last_command;
    setFacts("command-facts", [
      ["Mode", command?.mode],
      ["Power", command ? unit(command.active_power_kw, "kW") : "-"],
      ["Reactive", command?.reactive_power_kvar === undefined ? "-" : unit(command.reactive_power_kvar, "kvar")],
      ["Reason", command?.reason],
      ["Valid until", fmtDate(command?.valid_until)],
      ["Source", command?.source],
    ]);
    return {
      soc: telemetry ? unit(telemetry.soc_percent, "%") : "-",
      batteryPower: telemetry ? unit(telemetry.active_power_kw, "kW") : "-",
    };
  } catch (error) {
    if (error.status === 404) {
      setFacts("status-facts", [["Status", "no telemetry"]]);
      setFacts("command-facts", [["Command", "none"]]);
      return {};
    }
    throw error;
  }
}

async function loadSiteStatus(assetId) {
  if (!assetId) {
    setFacts("site-facts", [["Asset", "not selected"]]);
    return {};
  }

  try {
    const status = await request(`/site/${encodeURIComponent(assetId)}/status`);
    const telemetry = status.telemetry;
    const quality = status.quality;
    setFacts("site-facts", [
      ["PV", telemetry ? unit(telemetry.pv_power_kw, "kW") : "-"],
      ["Load", telemetry ? unit(telemetry.load_power_kw, "kW") : "-"],
      ["Grid", telemetry ? unit(telemetry.grid_power_kw, "kW") : "-"],
      ["Irradiance", telemetry ? unit(telemetry.irradiance_w_per_square_meter, "W/m2") : "-"],
      ["Quality", quality ? `${quality.flag} (${quality.reason})` : "-"],
      ["Observed", fmtDate(status.observed_at)],
    ]);
    return {
      load: telemetry ? unit(telemetry.load_power_kw, "kW") : "-",
      pv: telemetry ? unit(telemetry.pv_power_kw, "kW") : "-",
      grid: telemetry ? unit(telemetry.grid_power_kw, "kW") : "-",
    };
  } catch (error) {
    if (error.status === 404) {
      setFacts("site-facts", [["Status", "no site telemetry"]]);
      return {};
    }
    throw error;
  }
}

async function loadSourceOverview(assetId, siteId) {
  const entries = [
    ["Deye Cloud", "shown here when Bess:TelemetrySource=deye_cloud"],
    ["FusionSolar", "shown in Site Status when Bess:SiteTelemetrySource=fusionsolar"],
    ["ASKUE", "shown in ASKUE Consumption when Bess:ConsumptionSource=askue"],
  ];

  if (!assetId && !siteId) {
    setFacts("source-facts", entries);
    return;
  }

  try {
    const [siteStatus, consumption] = await Promise.all([
      assetId
        ? request(`/site/${encodeURIComponent(assetId)}/status`).catch((error) => error)
        : Promise.resolve(new Error("asset-not-selected")),
      siteId
        ? request(`/site/${encodeURIComponent(siteId)}/consumption/recent?hours=48&take=1&source=askue`).catch((error) => error)
        : Promise.resolve(new Error("site-not-selected")),
    ]);

    const siteTelemetry = siteStatus instanceof Error ? null : siteStatus.telemetry;
    const askueCount = consumption instanceof Error ? 0 : (consumption.readings || []).length;
    setFacts("source-facts", [
      ["Deye Cloud", siteTelemetry ? "site telemetry present if Deye is the active battery source" : "not active"],
      ["FusionSolar", siteTelemetry?.pv_power_kw !== undefined && siteTelemetry?.pv_power_kw !== null ? "PV/site telemetry present" : "not active"],
      ["ASKUE", askueCount > 0 ? "recent consumption present" : "no recent readings"],
    ]);
  } catch {
    setFacts("source-facts", entries);
  }
}

async function loadSiteBalance(siteId) {
  const facts = $("site-balance-facts");
  const meterBody = $("site-balance-meters");
  const generationBody = $("site-balance-generation");
  meterBody.replaceChildren();
  generationBody.replaceChildren();
  if (!siteId) {
    setFacts("site-balance-facts", [["Site", "not selected"]]);
    appendTableEmpty(meterBody, "No site selected", 7);
    appendTableEmpty(generationBody, "No site selected", 4);
    return;
  }

  try {
    const date = balanceDateInput.value;
    const query = date ? `?date=${encodeURIComponent(date)}` : "";
    const balance = await request(`/site/${encodeURIComponent(siteId)}/balance${query}`);
    setFacts("site-balance-facts", [
      ["Quality", balance.data_quality_status],
      ["Window", `${fmtDate(balance.from)} - ${fmtDate(balance.to)}`],
      ["Main import", unit(balance.main_grid_import_kwh, "kWh")],
      ["Main export", unit(balance.main_grid_export_kwh, "kWh")],
      ["Subconsumers", unit(balance.subconsumer_consumption_kwh, "kWh")],
      ["Own consumption", unit(balance.own_consumption_kwh, "kWh")],
      ["Grid export", unit(balance.grid_export_kwh, "kWh")],
      ["Site net", unit(balance.site_net_balance_kwh, "kWh")],
    ]);

    const meters = balance.meter_totals || [];
    if (meters.length === 0) {
      appendTableEmpty(meterBody, "No meter totals", 7);
    } else {
      for (const meter of meters) {
        const tr = document.createElement("tr");
        for (const value of [
          `${meter.name} (${meter.meter_id})`,
          meter.role,
          fmtNumber(meter.apoz_raw, 4),
          fmtNumber(meter.aneg_raw, 4),
          fmtNumber(meter.apoz_kwh),
          fmtNumber(meter.aneg_kwh),
          meter.reading_count,
        ]) {
          const td = document.createElement("td");
          td.textContent = fmt(value);
          tr.append(td);
        }
        meterBody.append(tr);
      }
    }

    const generation = balance.generation || [];
    if (generation.length === 0) {
      appendTableEmpty(generationBody, "No generation meters", 4);
    } else {
      for (const item of generation) {
        const tr = document.createElement("tr");
        for (const value of [
          item.generation_type,
          fmtNumber(item.auxiliary_consumption_kwh),
          fmtNumber(item.export_kwh),
          fmtNumber(item.net_generation_kwh),
        ]) {
          const td = document.createElement("td");
          td.textContent = fmt(value);
          tr.append(td);
        }
        generationBody.append(tr);
      }
    }
  } catch (error) {
    setFacts("site-balance-facts", [["Error", error.message || "site balance load failed"]]);
    appendTableEmpty(meterBody, "Site balance load failed", 7);
    appendTableEmpty(generationBody, "Site balance load failed", 4);
  }
}

async function loadSchedules(assetId) {
  const tbody = $("schedules");
  tbody.replaceChildren();
  state.schedules = [];
  if (!assetId) {
    appendScheduleEmpty("No asset selected");
    drawScheduleChart();
    return;
  }

  const body = await request(`/markets/schedules/current?assetId=${encodeURIComponent(assetId)}`);
  state.schedules = body.schedules || [];
  if (state.schedules.length === 0) {
    appendScheduleEmpty("No active schedules");
    drawScheduleChart();
    return;
  }

  for (const schedule of state.schedules) {
    const tr = document.createElement("tr");
    for (const value of [
      schedule.type,
      schedule.version,
      `${fmtDate(schedule.horizon_start)} - ${fmtDate(schedule.horizon_end)}`,
      schedule.windows?.length ?? 0,
    ]) {
      const td = document.createElement("td");
      td.textContent = fmt(value);
      tr.append(td);
    }
    tbody.append(tr);
  }
  drawScheduleChart();
}

function appendScheduleEmpty(message) {
  const tr = document.createElement("tr");
  const td = document.createElement("td");
  td.colSpan = 4;
  td.className = "empty";
  td.textContent = message;
  tr.append(td);
  $("schedules").append(tr);
}

async function loadAskueConsumption(siteId) {
  const tbody = $("askue-consumption");
  tbody.replaceChildren();
  if (!siteId) {
    appendTableEmpty(tbody, "No site selected", 7);
    return;
  }

  try {
    const body = await request(`/site/${encodeURIComponent(siteId)}/consumption/recent?hours=72&take=24&source=askue`);
    const readings = body.readings || [];
    if (readings.length === 0) {
      appendTableEmpty(tbody, "No recent ASKUE readings", 7);
      return;
    }

    for (const reading of readings) {
      const tr = document.createElement("tr");
      for (const value of [
        fmtDate(reading.timestamp),
        `${reading.point_name} (${reading.point_id})`,
        reading.interval_seconds ? `${fmtNumber(reading.interval_seconds, 0)} s` : "-",
        fmtNumber(reading.apoz, 4),
        fmtNumber(reading.aneg, 4),
        fmtNumber(reading.ppoz, 4),
        fmtNumber(reading.pneg, 4),
      ]) {
        const td = document.createElement("td");
        td.textContent = fmt(value);
        tr.append(td);
      }
      tbody.append(tr);
    }
  } catch (error) {
    appendTableEmpty(tbody, error.message || "ASKUE load failed", 7);
  }
}

async function loadSiteMeasurements(siteId) {
  const tbody = $("site-measurements");
  tbody.replaceChildren();
  if (!siteId) {
    appendTableEmpty(tbody, "No site selected", 6);
    return;
  }

  try {
    const body = await request(`/site/${encodeURIComponent(siteId)}/measurements/recent?hours=72&take=40`);
    const measurements = body.measurements || [];
    if (measurements.length === 0) {
      appendTableEmpty(tbody, "No recent normalized measurements", 6);
      return;
    }

    for (const measurement of measurements) {
      const tr = document.createElement("tr");
      for (const value of [
        fmtDate(measurement.timestamp),
        measurement.source,
        `${measurement.instrument_name} (${measurement.instrument_id})`,
        measurement.metric,
        `${fmtNumber(measurement.value)} ${measurement.unit}`,
        measurement.quality,
      ]) {
        const td = document.createElement("td");
        td.textContent = fmt(value);
        tr.append(td);
      }
      tbody.append(tr);
    }
  } catch (error) {
    appendTableEmpty(tbody, error.message || "measurements load failed", 6);
  }
}

async function loadStop(assetId) {
  if (!assetId) {
    $("stop-state").textContent = "inactive";
    $("stop-detail").textContent = "-";
    return;
  }
  const body = await request(`/operator/stops/current?assetId=${encodeURIComponent(assetId)}`);
  if (!body.stop) {
    $("stop-state").textContent = "inactive";
    $("stop-detail").textContent = "-";
    return;
  }
  $("stop-state").textContent = "active";
  $("stop-detail").textContent = `${body.stop.reason} by ${body.stop.operator}`;
}

async function loadSolarForecast(assetId) {
  state.solarPoints = [];
  if (!assetId) {
    setFacts("solar-facts", [["Asset", "not selected"]]);
    drawSolarChart();
    return;
  }

  try {
    const forecast = await request(`/site/${encodeURIComponent(assetId)}/solar-forecast`);
    state.solarPoints = forecast.points || [];
    setFacts("solar-facts", [
      ["Source", forecast.source],
      ["Model", forecast.model],
      ["Generated", fmtDate(forecast.generated_at)],
      ["Horizon", `${fmtDate(forecast.horizon_start)} - ${fmtDate(forecast.horizon_end)}`],
      ["Installed DC", unit(forecast.installed_dc_kw, "kW")],
      ["Installed AC", unit(forecast.installed_ac_kw, "kW")],
      ["Points", state.solarPoints.length],
    ]);
    drawSolarChart();
  } catch (error) {
    if (error.status === 404) {
      setFacts("solar-facts", [["Forecast", "not available"]]);
      drawSolarChart();
      return;
    }
    throw error;
  }
}

async function loadPvProfiles(siteId) {
  const tbody = $("pv-profiles");
  tbody.replaceChildren();
  if (!siteId) {
    appendTableEmpty(tbody, "No site selected", 3);
    return;
  }

  try {
    const body = await request(`/site/${encodeURIComponent(siteId)}/pv-profiles`);
    const profiles = body.profiles || [];
    if (profiles.length === 0) {
      appendTableEmpty(tbody, "No PV profiles", 3);
      return;
    }
    for (const profile of profiles) {
      const tr = document.createElement("tr");
      for (const value of [
        profile.name,
        profile.enabled,
        unit(profile.inverter_ac_kw, "kW"),
      ]) {
        const td = document.createElement("td");
        td.textContent = fmt(value);
        tr.append(td);
      }
      tbody.append(tr);
    }
  } catch (error) {
    appendTableEmpty(tbody, error.status === 404 ? "No PV profiles" : error.message, 3);
  }
}

function appendTableEmpty(tbody, message, colSpan) {
  const tr = document.createElement("tr");
  const td = document.createElement("td");
  td.colSpan = colSpan;
  td.className = "empty";
  td.textContent = message;
  tr.append(td);
  tbody.append(tr);
}

async function loadRun() {
  const runId = runIdInput.value.trim();
  const tbody = $("run-breakdown");
  tbody.replaceChildren();
  if (!runId) {
    setFacts("run-facts", [["Run", "not selected"]]);
    appendTableEmpty(tbody, "No run selected", 3);
    return;
  }
  const run = await request(`/optimization/runs/${encodeURIComponent(runId)}`);
  setFacts("run-facts", [
    ["Status", run.status],
    ["Asset", run.asset_id],
    ["Solver", run.solver_name],
    ["Objective", fmtNumber(run.objective_value)],
    ["Reason", run.termination_reason],
    ["Runtime", `${fmtNumber(run.solver_runtime_seconds, 3)} s`],
    ["Horizon", `${fmtDate(run.horizon_start)} - ${fmtDate(run.horizon_end)}`],
    ["Created", fmtDate(run.created_at)],
    ["Produced schedule", run.produced_schedule ? `${run.produced_schedule.type} v${run.produced_schedule.version}` : "-"],
  ]);

  const components = run.objective_breakdown || [];
  if (components.length === 0) {
    appendTableEmpty(tbody, "No objective components", 3);
    return;
  }
  for (const component of components) {
    const tr = document.createElement("tr");
    for (const value of [component.name, fmtNumber(component.value), component.unit]) {
      const td = document.createElement("td");
      td.textContent = fmt(value);
      tr.append(td);
    }
    tbody.append(tr);
  }
}

function canvasContext(id) {
  const canvas = $(id);
  const rect = canvas.getBoundingClientRect();
  const scale = window.devicePixelRatio || 1;
  canvas.width = Math.max(1, Math.floor(rect.width * scale));
  canvas.height = Math.max(1, Math.floor(canvas.height * scale));
  const ctx = canvas.getContext("2d");
  ctx.setTransform(scale, 0, 0, scale, 0, 0);
  return { ctx, width: rect.width, height: canvas.height / scale };
}

function drawEmptyChart(id, message) {
  const { ctx, width, height } = canvasContext(id);
  ctx.clearRect(0, 0, width, height);
  ctx.fillStyle = "#5e6b78";
  ctx.font = "13px system-ui, sans-serif";
  ctx.fillText(message, 12, Math.max(24, height / 2));
}

function drawScheduleChart() {
  const windows = state.schedules.flatMap((schedule) =>
    (schedule.windows || []).map((window) => ({
      type: schedule.type,
      start: new Date(window.start).getTime(),
      end: new Date(window.end).getTime(),
      power: Number(window.target_power_kw),
    })));

  if (windows.length === 0) {
    drawEmptyChart("schedule-chart", "No active schedule data");
    return;
  }

  const minTime = Math.min(...windows.map((window) => window.start));
  const maxTime = Math.max(...windows.map((window) => window.end));
  const maxAbs = Math.max(1, ...windows.map((window) => Math.abs(window.power)));
  const { ctx, width, height } = canvasContext("schedule-chart");
  const pad = 18;
  const mid = height / 2;
  ctx.clearRect(0, 0, width, height);
  ctx.strokeStyle = "#d8dee5";
  ctx.beginPath();
  ctx.moveTo(pad, mid);
  ctx.lineTo(width - pad, mid);
  ctx.stroke();

  for (const window of windows) {
    const x1 = pad + ((window.start - minTime) / Math.max(1, maxTime - minTime)) * (width - pad * 2);
    const x2 = pad + ((window.end - minTime) / Math.max(1, maxTime - minTime)) * (width - pad * 2);
    const barHeight = (Math.abs(window.power) / maxAbs) * (height / 2 - pad);
    ctx.fillStyle = window.power >= 0 ? "#b54708" : "#146c63";
    ctx.fillRect(x1, window.power >= 0 ? mid - barHeight : mid, Math.max(2, x2 - x1), barHeight);
  }
}

function drawSolarChart() {
  const points = state.solarPoints
    .map((point) => ({
      time: new Date(point.timestamp).getTime(),
      power: Number(point.power_kw),
    }))
    .filter((point) => Number.isFinite(point.time) && Number.isFinite(point.power));

  if (points.length === 0) {
    drawEmptyChart("solar-chart", "No forecast data");
    return;
  }

  const minTime = Math.min(...points.map((point) => point.time));
  const maxTime = Math.max(...points.map((point) => point.time));
  const maxPower = Math.max(1, ...points.map((point) => point.power));
  const { ctx, width, height } = canvasContext("solar-chart");
  const pad = 18;
  ctx.clearRect(0, 0, width, height);
  ctx.strokeStyle = "#d8dee5";
  ctx.beginPath();
  ctx.moveTo(pad, height - pad);
  ctx.lineTo(width - pad, height - pad);
  ctx.stroke();
  ctx.strokeStyle = "#146c63";
  ctx.lineWidth = 2;
  ctx.beginPath();
  points.forEach((point, index) => {
    const x = pad + ((point.time - minTime) / Math.max(1, maxTime - minTime)) * (width - pad * 2);
    const y = height - pad - (point.power / maxPower) * (height - pad * 2);
    if (index === 0) {
      ctx.moveTo(x, y);
    } else {
      ctx.lineTo(x, y);
    }
  });
  ctx.stroke();
}

async function refresh() {
  try {
    await loadAssets();
    const assetId = selectedAssetId();
    const siteId = selectedSiteId();
    applyAsset(assetId);
    await loadHealth();
    const [batteryKpis, siteKpis] = await Promise.all([
      loadAssetStatus(assetId),
      loadSiteStatus(assetId),
      loadSourceOverview(assetId, siteId),
      loadSchedules(assetId),
      loadStop(assetId),
      loadSolarForecast(assetId),
      loadPvProfiles(siteId),
      loadSiteBalance(siteId),
      loadAskueConsumption(siteId),
      loadSiteMeasurements(siteId),
    ]).then(([battery, site]) => [battery, site]);
    setKpis({ ...batteryKpis, ...siteKpis });
    $("last-refresh").textContent = fmtDate(new Date().toISOString());
    setApiState(true);
  } catch (error) {
    setApiState(false);
    $("last-refresh").textContent = `failed: ${error.message || "request failed"}`;
  }
}

assetSelect.addEventListener("change", () => {
  manualAsset.value = "";
  applyAsset(assetSelect.value);
  refresh();
});

manualAsset.addEventListener("change", () => {
  applyAsset(manualAsset.value.trim());
  refresh();
});

siteIdInput.addEventListener("change", refresh);
balanceDateInput.addEventListener("change", refresh);

$("refresh").addEventListener("click", refresh);
$("load-run").addEventListener("click", () => {
  loadRun().catch((error) => {
    setApiState(false);
    setFacts("run-facts", [["Error", error.message]]);
    const tbody = $("run-breakdown");
    tbody.replaceChildren();
    appendTableEmpty(tbody, "Run load failed", 3);
  });
});

window.addEventListener("resize", () => {
  drawScheduleChart();
  drawSolarChart();
});

setKpis();
balanceDateInput.value = balanceDateInput.value || currentLocalDateInputValue();
setFacts("status-facts", [["Status", "loading"]]);
setFacts("command-facts", [["Command", "loading"]]);
setFacts("site-facts", [["Status", "loading"]]);
setFacts("source-facts", [["Sources", "loading"]]);
setFacts("solar-facts", [["Forecast", "loading"]]);
setFacts("site-balance-facts", [["Balance", "loading"]]);
setFacts("run-facts", [["Run", "not selected"]]);
appendTableEmpty($("site-balance-meters"), "No site balance loaded", 7);
appendTableEmpty($("site-balance-generation"), "No site balance loaded", 4);
appendTableEmpty($("run-breakdown"), "No run selected", 3);
refresh();
setInterval(refresh, refreshIntervalMs);
