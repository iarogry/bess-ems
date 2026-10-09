const state = {
  assets: [],
  sites: [],
  selectedAssetId: "",
  schedules: [],
  solarPoints: [],
};

const refreshIntervalMs = 30000;
let refreshGeneration = 0;
let refreshController;
let runController;
let lastSuccessfulRefresh = 0;
const $ = (id) => document.getElementById(id);

const apiState = $("api-state");
const assetSelect = $("asset");
const manualAsset = $("manual-asset");
const siteIdInput = $("site-id");
const viewScope = $("view-scope");
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
    ...options,
    headers: { accept: "application/json", ...(options.headers || {}) },
  });
  const text = await response.text();
  const payload = text ? JSON.parse(text) : null;
  if (!response.ok) {
    const error = new Error(payload?.error || response.statusText);
    error.status = response.status;
    throw error;
  }
  return payload;
}

// Each refresh owns its reads. Even a transport that ignores abort cannot
// render an older response after the selection or generation has changed.
function scopedRead(controller, isCurrent) {
  const errors = [];
  const ensureCurrent = () => {
    if (controller.signal.aborted || !isCurrent()) {
      throw new DOMException("Superseded refresh", "AbortError");
    }
  };
  const read = async (path) => {
    ensureCurrent();
    try {
      const payload = await request(path, { signal: controller.signal });
      ensureCurrent();
      return payload;
    } catch (error) {
      ensureCurrent();
      if (error.status !== 404) errors.push(error);
      throw error;
    }
  };
  read.ensureCurrent = ensureCurrent;
  read.errors = errors;
  return read;
}

function clearLiveData(message) {
  setKpis();
  $("fleet-sites").replaceChildren();
  appendTableEmpty($("fleet-sites"), message, 5);
  for (const id of ["status-facts", "command-facts", "site-facts", "source-facts",
    "solar-facts", "site-balance-facts", "financial-facts"]) {
    setFacts(id, [["Status", message]]);
  }
  for (const [id, columns] of [["schedules", 5], ["pv-profiles", 3],
    ["site-balance-meters", 6], ["site-balance-generation", 4],
    ["site-measurements", 6]]) {
    const body = $(id);
    body.replaceChildren();
    appendTableEmpty(body, message, columns);
  }
  state.schedules = [];
  state.solarPoints = [];
  drawScheduleChart();
  drawSolarChart();
  $("stop-state").textContent = "unknown";
  $("stop-detail").textContent = message;
  $("health-status").textContent = "unknown";
  $("health-at").textContent = "-";
  $("regelleistung-status").textContent = "unknown";
  $("regelleistung-at").textContent = "-";
}

function currentTelemetry(status, maxAgeMs) {
  const at = Date.parse(status.observed_at);
  const age = Date.now() - at;
  const stale = !Number.isFinite(at) || age < 0 || age > maxAgeMs
    || String(status.quality?.flag || "").toLowerCase() !== "valid";
  if (stale) $("data-state").textContent = "Telemetry unavailable or stale";
  return stale ? null : status.telemetry;
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
  if (values.pvNote) $("generation-note").textContent = values.pvNote;
  $("kpi-grid").textContent = values.grid ?? "-";
  $("kpi-export").textContent = values.export ?? "—";
  $("export-note").textContent = values.exportNote ?? "Немає актуальних даних";
  $("kpi-profit").textContent = values.profit ?? "—";
  $("profit-note").textContent = values.profitNote ?? "Фінансовий план недоступний";
  $("profit-date").textContent = new Intl.DateTimeFormat("uk-UA", {
    timeZone: "Europe/Kyiv", dateStyle: "long",
  }).format(new Date());
}

function applyViewScope() {
  const all = viewScope.value === "all";
  document.body.classList.toggle("fleet-all", all);
  for (const id of ["export-card", "profit-card", "technical-details"]) $(id).hidden = all;
  $("fleet-panel").hidden = !all;
  for (const node of [assetSelect, manualAsset, siteIdInput, balanceDateInput, runIdInput, $("load-run"), $("grid-sign")]) node.disabled = all;
  $("askue-balance-panel").hidden = all;
  $("overview-scope").textContent = all ? "Усі площадки · поточна потужність" : "Площадка · поточна потужність";
  $("generation-title").textContent = all ? "Загальна генерація" : "Генерація на площадці";
  $("load-title").textContent = all ? "Загальне споживання" : "Власне споживання";
  $("generation-note").textContent = all ? "Завантаження складу джерел…" : "Склад джерел площадки потребує підтвердження";
  $("load-note").textContent = all ? "Завантаження споживання…" : "Навантаження за телеметрією; без окремого обліку субспоживачів";
  $("overview-note").textContent = all
    ? "PV + КГУ + додатний розряд УЗЕ. Заряд УЗЕ не віднімається. Неповні показники позначені окремо; відсутні дані не є нулем."
    : "Показники — для вибраного джерела телеметрії. Прочерк означає, що підтверджених даних немає. Нуль відображається лише за наявності вимірювання.";
}

function fleetMetricNote(metric) {
  const labels = { complete: "Повні дані", partial: "Часткова сума", estimated: "Час вимірювання частини джерел невідомий",
    unavailable: "Дані відсутні", membership_pending: "Потрібна відповідність спільних установок" };
  return `${labels[metric.status] || "Дані відсутні"} · джерел ${metric.available_sources}/${metric.expected_sources}`;
}

async function loadFleet(read) {
  const fleet = await read("/sites/overview");
  read.ensureCurrent?.();
  setKpis({ pv: unit(fleet.generation.power_kw, "kW"), load: unit(fleet.consumption.power_kw, "kW") });
  $("generation-note").textContent = fleetMetricNote(fleet.generation);
  $("load-note").textContent = fleetMetricNote(fleet.consumption);
  $("fleet-note").textContent = fleet.membership_confirmed
    ? `Площадок: ${fleet.sites.length}. У підсумку враховано лише доступні джерела; деталі нижче.`
    : "Площадкам потрібно призначити джерела й уточнити спільні установки. Загальну суму приховано, щоб уникнути подвійного обліку.";
  const body = $("fleet-sites");
  body.replaceChildren();
  for (const site of fleet.sites) {
    const row = document.createElement("tr");
    const missing = site.sources.filter(source => source.power_kw === null || source.power_kw === undefined)
      .map(source => source.physical_id);
    const times = site.sources.filter(source => source.power_kw !== null && source.power_kw !== undefined)
      .map(source => Date.parse(source.observed_at)).filter(Number.isFinite);
    for (const value of [site.name || site.site_id, fmtNumber(site.generation.power_kw), fmtNumber(site.consumption.power_kw),
      `${site.sources.length === 0 ? "Джерела не прив’язані" : fleetMetricNote(site.generation)}; споживання: ${fleetMetricNote(site.consumption)}${missing.length ? "; немає: " + missing.join(", ") : ""}`,
      times.length ? fmtDate(new Date(Math.min(...times)).toISOString()) : "—"]) {
      const cell = document.createElement("td"); cell.textContent = value; row.append(cell);
    }
    body.append(row);
  }
  if (!fleet.sites.length) appendTableEmpty(body, "Склад площадок ще не налаштовано", 5);
  $("data-state").textContent = !fleet.membership_confirmed ? "Очікується зіставлення спільних установок"
    : fleet.generation.status === "complete" && fleet.consumption.status === "complete" ? "Дані оновлено" : "Неповні або орієнтовні дані — див. повноту джерел";
}

async function loadSites(read = request) {
  const body = await read('/sites');
  state.sites = body.sites || [];
  if (!state.sites.length) return;
  const selected = siteIdInput.value || 'site-khlibzavod-5';
  siteIdInput.replaceChildren();
  for (const site of state.sites) {
    const option = document.createElement('option');
    option.value = site.site_id; option.textContent = site.name;
    siteIdInput.append(option);
  }
  siteIdInput.value = state.sites.some(site => site.site_id === selected) ? selected : state.sites[0].site_id;
}

async function loadAssets(read = request) {
  const body = await read("/assets");
  const site = state.sites.find(site => site.site_id === selectedSiteId());
  state.assets = (body.assets || []).filter(asset => !site || (site.asset_ids || []).includes(asset.asset_id));
  assetSelect.replaceChildren();

  if (state.assets.length === 0) {
    const option = document.createElement("option");
    option.value = "";
    option.textContent = "УЗЕ не прив’язано";
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

async function loadHealth(read = request) {
  const health = await read("/health");
  $("health-status").textContent = health.status || "unknown";
  $("health-at").textContent = fmtDate(health.at);

  const regelleistung = await read("/health/regelleistung");
  $("regelleistung-status").textContent = regelleistung.production_gate || "unknown";
  $("regelleistung-at").textContent = fmtDate(regelleistung.at);
}

async function loadAssetStatus(assetId, read = request) {
  if (!assetId) {
    setFacts("status-facts", [["Asset", "not selected"]]);
    setFacts("command-facts", [["Command", "not selected"]]);
    return {};
  }

  try {
    const status = await read(`/battery/${encodeURIComponent(assetId)}/status`);
    const telemetry = currentTelemetry(status, 600000);
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
    const command = status.last_command && Date.parse(status.last_command.valid_until) > Date.now()
      ? status.last_command : null;
    setFacts("command-facts", [
      ["Status", command ? "current" : status.last_command ? "expired or invalid" : "none"],
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
    read.ensureCurrent?.();
    if (error.status === 404) {
      setFacts("status-facts", [["Status", "no telemetry"]]);
      setFacts("command-facts", [["Command", "none"]]);
      return {};
    }
    throw error;
  }
}

function gridExport(telemetry, sign) {
  const power = telemetry?.grid_power_kw;
  if (typeof power !== "number" || !Number.isFinite(power)) {
    return { exportNote: "Немає актуальних даних" };
  }
  if (sign !== "import_pos" && sign !== "export_pos") {
    return { exportNote: "Потрібно визначити напрям потоку лічильника в налаштуваннях" };
  }
  const exported = sign === "import_pos" ? -power : power;
  return {
    export: unit(Math.max(0, exported), "kW"),
    exportNote: exported < 0 ? "Зараз площадка споживає енергію з мережі" : "Поточна віддача в мережу",
  };
}

async function loadSitePower(siteId, read = request) {
  const fleet = await read("/sites/overview");
  const site = (fleet.sites || []).find(item => item.site_id === siteId);
  if (!site) return {};
  return { pv: unit(site.generation.power_kw, "kW"), load: unit(site.consumption.power_kw, "kW"),
    pvNote: `Доступні джерела: ${site.generation.available_sources}/${site.generation.expected_sources}. Сума джерел, прив’язаних до цієї площадки.` };
}

async function loadSiteStatus(assetId, read = request, gridSign = $("grid-sign").value, siteId = "") {
  if (!assetId) {
    setFacts("site-facts", [["Asset", "not selected"]]);
    return {};
  }

  try {
    const status = await read(`/site/${encodeURIComponent(assetId)}/status`);
    const telemetry = currentTelemetry(status, 2400000);
    const pvPower = telemetry?.pv_power_kw;
    const quality = status.quality;
    setFacts("site-facts", [
      ["PV", pvPower !== undefined && pvPower !== null ? unit(pvPower, "kW") : "-"],
      ["Load", telemetry ? unit(telemetry.load_power_kw, "kW") : "-"],
      ["Grid", telemetry?.grid_power_kw == null ? "Джерело не передає потужність мережі" : unit(telemetry.grid_power_kw, "kW")],
      ["Irradiance", telemetry ? unit(telemetry.irradiance_w_per_square_meter, "W/m2") : "-"],
      ["Quality", quality ? `${quality.flag} (${quality.reason})` : "-"],
      ["Observed", fmtDate(status.observed_at)],
    ]);
    return {
      load: telemetry ? unit(telemetry.load_power_kw, "kW") : "-",
      // A source's PV is not a site total. Publish this KPI only after the
      // site generation model includes its explicitly assigned PV/CHP/BESS.
      pv: "—",
      grid: telemetry ? unit(telemetry.grid_power_kw, "kW") : "-",
      ...gridExport(telemetry, gridSign),
    };
  } catch (error) {
    read.ensureCurrent?.();
    if (error.status === 404) {
      setFacts("site-facts", [["Status", "no site telemetry"]]);
      return {};
    }
    throw error;
  }
}

function sourceTime(value) {
  const date = new Date(value);
  return value && Number.isFinite(date.getTime())
    ? new Intl.DateTimeFormat("uk-UA", { timeZone: "Europe/Kyiv", dateStyle: "short", timeStyle: "medium" }).format(date)
    : "час не передано";
}

const solarStationNames = {
  "158463133": "Жорновий млин",
  "133657926": "Запоріжмлин",
  "129469793": "Чернівціхлібінвест · ввід 2",
  "134735482": "Чернівціхлібінвест · ввід 1",
};

function sourcePowerDescription(source, label) {
  if (source.power_kw == null) return `${label}: немає доступних даних`;
  const quality = String(source.quality).toLowerCase() === "substituted"
    ? "Оцінка: точний час вимірювання не підтверджено. " : "";
  return `${label}: ${unit(source.power_kw, "кВт")}. ${quality}Отримано ${sourceTime(source.observed_at)} (Київ).`;
}

async function loadSourceOverview(assetId, siteId, read = request) {
  if (!siteId) {
    setFacts("source-facts", [["Площадка", "Оберіть площадку"]]);
    return;
  }
  const date = balanceDateInput.value || kyivDeliveryDate();
  const [fleet, accounts, balance] = await Promise.all([
    read("/sites/overview").catch(error => error),
    read("/sites/askue/status").catch(error => error),
    read(`/site/${encodeURIComponent(siteId)}/balance?date=${encodeURIComponent(date)}`).catch(error => error),
  ]);
  read.ensureCurrent?.();
  const selectedSite = fleet instanceof Error ? null : (fleet.sites || []).find(site => site.site_id === siteId);
  const sources = selectedSite?.sources || [];
  const entries = [];
  const deye = sources.filter(source => source.physical_id?.startsWith("deye-"));
  if (fleet instanceof Error) entries.push(["Джерела обладнання", "Не вдалося отримати стан джерел"]);
  else if (!deye.length) entries.push(["Deye Cloud", "Не прив’язано до цієї площадки"]);
  for (const source of deye) {
    const labels = { pv: "Генерація СЕС", battery: "УЗЕ — внесок у генерацію", load: "Навантаження" };
    entries.push([`Deye Cloud · ${labels[source.kind] || source.kind}`, sourcePowerDescription(source, "Активна потужність")]);
  }
  const solar = sources.filter(source => source.telemetry_id.startsWith("NE="));
  if (!(fleet instanceof Error) && !solar.length) entries.push(["FusionSolar", "Не прив’язано до цієї площадки"]);
  for (const source of solar) {
    const id = source.telemetry_id.replace("NE=", "");
    entries.push([`FusionSolar · ${solarStationNames[id] || "Сонячна станція"} (ID ${id})`,
      sourcePowerDescription(source, "Генерація СЕС — активна потужність")]);
  }
  for (const source of sources.filter(source => source.kind === "chp")) {
    entries.push([`КГУ · ${source.telemetry_id}`, sourcePowerDescription(source, "Генерація КГУ — активна потужність")]);
  }
  const account = accounts instanceof Error ? null : (accounts.accounts || []).find(item => item.site_id === siteId);
  if (account) {
    const status = account.state === "ok" ? "Дані отримуються" : account.state === "running" ? "Оновлення даних" : "Немає успішного актуального опитування";
    entries.push(["АСКУЕ · стан збору", `${status}. Останній успішний збір: ${sourceTime(account.last_success_at)} (Київ).`]);
  } else entries.push(["АСКУЕ · стан збору", accounts instanceof Error ? "Не вдалося перевірити збір даних" : "Обліковий запис не прив’язано"]);
  if (!(balance instanceof Error) && balance.meter_totals?.some(meter => meter.reading_count > 0)) {
    entries.push(["АСКУЕ · облікова доба", `${date} (Київ); ${balance.data_quality_status === "incomplete" ? "неповні дані" : balance.data_quality_status}`],
      ["АСКУЕ · імпорт із мережі", unit(balance.main_grid_import_kwh, "кВт·год")],
      ["АСКУЕ · експорт у мережу", unit(balance.main_grid_export_kwh, "кВт·год")],
      ["АСКУЕ · енергія субспоживачів", unit(balance.subconsumer_consumption_kwh, "кВт·год")]);
  } else if (account) entries.push(["АСКУЕ · енергія за добу", balance instanceof Error ? "Баланс недоступний" : "Немає отриманих інтервалів за цю добу"]);
  setFacts("source-facts", entries);
}

function kyivDeliveryDate() {
  const parts = new Intl.DateTimeFormat("en-CA", {
    timeZone: "Europe/Kyiv", year: "numeric", month: "2-digit", day: "2-digit",
  }).formatToParts(new Date());
  const part = (type) => parts.find((item) => item.type === type).value;
  return `${part("year")}-${part("month")}-${part("day")}`;
}

async function loadFinancialPlan(siteId, read = request) {
  if (!siteId) return {};
  const body = await read(`/site/${encodeURIComponent(siteId)}/financial-plan`);
  const parameters = body.parameters || {};
  setFacts("financial-facts", [
    ["Статус", body.status], ["Дата плану", body.delivery_date],
    ["Валюта", body.currency], ["Валюта ENTSO-E", body.price_currency],
    ["Курс перерахунку", fmtNumber(body.exchange_rate, 5)], ["Перевірено", body.checked_on],
    ["Версія параметрів", body.parameter_revision], ["Версія плану", body.plan_revision],
    ["Розподіл / кВт·год", fmtNumber(parameters.distribution_per_kwh, 5)],
    ["Передача / кВт·год", fmtNumber(parameters.transmission_per_kwh, 5)],
    ["Коефіцієнт продажу", fmtNumber(parameters.release_coefficient, 5)],
    ["Витрати КГУ / кВт·год", fmtNumber(parameters.kgu_cost_per_kwh, 5)],
    ["Витрати СЕС / кВт·год", fmtNumber(parameters.pv_cost_per_kwh, 5)],
    ["Деградація / кВт·год розряду", fmtNumber(parameters.battery_degradation_per_kwh, 5)],
    ["Постійні витрати / день", unit(parameters.fixed_daily_cost, body.currency)],
    ["Економія власного споживання", unit(body.economics?.own_use_saving, body.currency)],
    ["Дохід від продажу", unit(body.economics?.export_revenue, body.currency)],
    ["Вартість заряду", unit(body.economics?.battery_charge_cost, body.currency)],
    ["Операційні витрати", unit(body.economics?.operating_cost, body.currency)],
  ]);
  if (body.status === "monthly_review_required") {
    return { profitNote: "Потрібна щомісячна перевірка тарифів і коефіцієнтів" };
  }
  const profit = body.economics?.planned_profit;
  if (body.status !== "ready" || body.site_id !== siteId
    || body.delivery_date !== kyivDeliveryDate() || body.time_zone !== "Europe/Kyiv"
    || !/^[A-Z]{3}$/.test(body.currency || "") || typeof profit !== "number" || !Number.isFinite(profit)) {
    return { profitNote: "Немає підтвердженого повного плану на сьогодні" };
  }
  return { profit: unit(profit, body.currency), profitNote: "Економія + продаж − заряд − налаштовані витрати; без ПДВ" };
}

async function loadSiteBalance(siteId, date, read = request) {
  $("askue-balance-panel").hidden = false;
  const facts = $("site-balance-facts");
  const meterBody = $("site-balance-meters");
  const generationBody = $("site-balance-generation");
  meterBody.replaceChildren();
  generationBody.replaceChildren();
  if (!siteId) {
    setFacts("site-balance-facts", [["Site", "not selected"]]);
    appendTableEmpty(meterBody, "No site selected", 6);
    appendTableEmpty(generationBody, "No site selected", 4);
    return;
  }

  try {
    const query = date ? `?date=${encodeURIComponent(date)}` : "";
    const balance = await read(`/site/${encodeURIComponent(siteId)}/balance${query}`);
    setFacts("site-balance-facts", [
      ["Quality", balance.data_quality_status],
      ["Застереження", (balance.warnings || []).map(item => `${item.meter_id || "Баланс"}: ${item.code}`).join("; ") || "Немає"],
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
      appendTableEmpty(meterBody, "No meter totals", 6);
    } else {
      for (const meter of meters) {
        const tr = document.createElement("tr");
        for (const value of [
          `${meter.name} (${meter.meter_id})`,
          meter.role,
          "×" + fmtNumber(meter.value_multiplier),
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
    read.ensureCurrent?.();
    if (error.status === 404) {
      $("askue-balance-panel").hidden = true;
      return;
    }
    setFacts("site-balance-facts", [["Error", error.message || "site balance load failed"]]);
    appendTableEmpty(meterBody, "Site balance load failed", 6);
    appendTableEmpty(generationBody, "Site balance load failed", 4);
  }
}

async function loadSchedules(assetId, read = request) {
  const tbody = $("schedules");
  tbody.replaceChildren();
  state.schedules = [];
  if (!selectedSiteId()) {
    $("schedule-note").textContent = "На цій площадці УЗЕ не прив’язано; план недоступний.";
    appendScheduleEmpty("No asset selected");
    drawScheduleChart();
    return;
  }

  const site = state.sites.find(item => item.site_id === selectedSiteId());
  const assetIds = site?.asset_ids || (assetId ? [assetId] : []);
  const active = await Promise.all(assetIds.map(async id => {
    const body = await read(`/markets/schedules/current?assetId=${encodeURIComponent(id)}`);
    return (body.schedules || []).map(schedule => ({ ...schedule, asset_id: schedule.asset_id || id }));
  }));
  const prepared = await read('/site/'+encodeURIComponent(selectedSiteId())+'/prepared-plans?date='+encodeURIComponent(balanceDateInput.value || kyivDeliveryDate())).catch(error => {
    if (error.status === 404) return { plans: [] }; throw error;
  });
  state.schedules = [...active.flat(), ...(prepared.plans || []).map(plan => ({...plan.schedule, asset_id: plan.asset_id, equipment_kind: plan.equipment_kind, prepared: true}))];
  $('schedule-note').textContent = (prepared.plans || []).length ? 'План розраховано. Команди керування вимкнені; план не активовано.' : 'Підготовленого плану за обрану дату немає. Активовані графіки показуються окремо.';
  if (state.schedules.length === 0) {
    appendScheduleEmpty("No active schedules");
    drawScheduleChart();
    return;
  }

  for (const schedule of state.schedules) {
    const tr = document.createElement("tr");
    for (const value of [
      scheduleSource(schedule).label,
      schedule.prepared ? "Підготовлений план · " + schedule.type : "Активний графік · " + schedule.type,
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


async function loadSiteMeasurements(siteId, read = request) {
  const tbody = $("site-measurements");
  tbody.replaceChildren();
  if (!siteId) {
    appendTableEmpty(tbody, "No site selected", 6);
    return;
  }

  try {
    const body = await read(`/site/${encodeURIComponent(siteId)}/measurements/recent?hours=72&take=40`);
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
    read.ensureCurrent?.();
    appendTableEmpty(tbody, error.message || "measurements load failed", 6);
  }
}

async function loadStop(assetId, read = request) {
  if (!assetId) {
    $("stop-state").textContent = "unknown";
    $("stop-detail").textContent = "No asset selected";
    return;
  }
  const body = await read(`/operator/stops/current?assetId=${encodeURIComponent(assetId)}`);
  if (!body.stop) {
    $("stop-state").textContent = "inactive";
    $("stop-detail").textContent = "-";
    return;
  }
  $("stop-state").textContent = "active";
  $("stop-detail").textContent = `${body.stop.reason} by ${body.stop.operator}`;
}

async function loadSolarForecast(assetId, read = request) {
  state.solarPoints = [];
  if (!assetId) {
    setFacts("solar-facts", [["Asset", "not selected"]]);
    drawSolarChart();
    return;
  }

  try {
    const forecast = await read(`/site/${encodeURIComponent(assetId)}/solar-forecast`);
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
    read.ensureCurrent?.();
    if (error.status === 404) {
      setFacts("solar-facts", [["Forecast", "not available"]]);
      drawSolarChart();
      return;
    }
    throw error;
  }
}

async function loadPvProfiles(siteId, read = request) {
  const tbody = $("pv-profiles");
  $("pv-profiles-panel").hidden = true;
  tbody.replaceChildren();
  if (!siteId) {
    appendTableEmpty(tbody, "No site selected", 3);
    return;
  }

  try {
    const body = await read(`/site/${encodeURIComponent(siteId)}/pv-profiles`);
    const profiles = body.profiles || [];
    if (profiles.length === 0) {
      appendTableEmpty(tbody, "No PV profiles", 3);
      return;
    }
    $("pv-profiles-panel").hidden = false;
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
    read.ensureCurrent?.();
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

function clearRunData(message) {
  runController?.abort();
  setFacts("run-facts", [["Status", message]]);
  $("run-breakdown").replaceChildren();
  appendTableEmpty($("run-breakdown"), message, 3);
}

async function loadRun() {
  runController?.abort();
  const controller = new AbortController();
  runController = controller;
  const read = scopedRead(controller, () => runController === controller);
  setFacts("run-facts", [["Status", "Loading run"]]);
  const runId = runIdInput.value.trim();
  const tbody = $("run-breakdown");
  tbody.replaceChildren();
  if (!runId) {
    setFacts("run-facts", [["Run", "not selected"]]);
    appendTableEmpty(tbody, "No run selected", 3);
    return;
  }
  const run = await read(`/optimization/runs/${encodeURIComponent(runId)}`);
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

function canvasContext(id, cssHeight = 160) {
  const canvas = $(id);
  const rect = canvas.getBoundingClientRect();
  const scale = window.devicePixelRatio || 1;
  canvas.width = Math.max(1, Math.floor(rect.width * scale));
  canvas.height = Math.max(1, Math.floor(cssHeight * scale));
  canvas.style.height = `${cssHeight}px`;
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

function scheduleSource(schedule) {
  const id = schedule.asset_id || "Джерело не вказано";
  const kind = schedule.equipment_kind || (id === "single-bess-1" ? "battery" : "unknown");
  const types = { battery: ["УЗЕ", "#146c63"], chp: ["КГУ", "#b54708"],
    gas_cogeneration: ["КГУ", "#b54708"], solar: ["СЕС", "#7651b5"], pv: ["СЕС", "#7651b5"] };
  const [name, color] = types[kind] || ["Інше джерело", "#2863ae"];
  return { id, kind, color, label: `${name} · ${id}` };
}

function scheduleChartModel(schedules) {
  const series = schedules.map(schedule => ({ ...scheduleSource(schedule),
    status: schedule.prepared ? "Підготовлений план" : "Активний графік",
    windows: (schedule.windows || []).map(window => ({ start: Date.parse(window.start),
      end: Date.parse(window.end), power: window.target_power_kw }))
      .filter(window => Number.isFinite(window.start) && Number.isFinite(window.end)
        && window.end > window.start && typeof window.power === "number" && Number.isFinite(window.power))
  })).filter(item => item.windows.length);
  const windows = series.flatMap(item => item.windows);
  const max = Math.max(1, ...windows.map(window => Math.abs(window.power)));
  const magnitude = 10 ** Math.floor(Math.log10(max));
  const normalized = max / magnitude;
  const limit = (normalized <= 1 ? 1 : normalized <= 2 ? 2 : normalized <= 5 ? 5 : 10) * magnitude;
  return { series, limit, minTime: Math.min(...windows.map(window => window.start)),
    maxTime: Math.max(...windows.map(window => window.end)) };
}

function drawScheduleChart() {
  const model = scheduleChartModel(state.schedules);
  const legend = $("schedule-legend");
  legend.replaceChildren();
  if (!model.series.length) {
    drawEmptyChart("schedule-chart", "Немає плану або активного графіка");
    return;
  }
  for (const series of model.series) {
    const item = document.createElement("span");
    item.className = "schedule-legend-item";
    const swatch = document.createElement("span");
    swatch.className = "schedule-swatch";
    swatch.style.backgroundColor = series.color;
    const text = document.createElement("span");
    text.textContent = `${series.label} — ${series.status}`;
    item.append(swatch, text); legend.append(item);
  }
  const laneHeight = 210;
  const { ctx, width, height } = canvasContext("schedule-chart", model.series.length * laneHeight + 32);
  const left = 60, right = Math.max(left + 1, width - 20);
  const x = time => left + (time - model.minTime) / (model.maxTime - model.minTime) * (right - left);
  const hour = new Intl.DateTimeFormat("uk-UA", { timeZone: "Europe/Kyiv", hour: "2-digit", minute: "2-digit" });
  ctx.clearRect(0, 0, width, height);
  ctx.font = "12px system-ui, sans-serif";
  model.series.forEach((series, index) => {
    const top = index * laneHeight + 30, bottom = top + 150;
    const y = power => top + (model.limit - power) / (2 * model.limit) * (bottom - top);
    ctx.fillStyle = series.color;
    ctx.textAlign = "left";
    ctx.fillText(`${series.label} · ${series.status}`, left, top - 12);
    for (const value of [model.limit, model.limit / 2, 0, -model.limit / 2, -model.limit]) {
      ctx.strokeStyle = value === 0 ? "#697986" : "#d8dee5";
      ctx.lineWidth = value === 0 ? 1.5 : 1;
      ctx.beginPath(); ctx.moveTo(left, y(value)); ctx.lineTo(right, y(value)); ctx.stroke();
      ctx.fillStyle = "#495968"; ctx.textAlign = "right";
      ctx.fillText(fmtNumber(value), left - 8, y(value) + 4);
    }
    ctx.textAlign = "left"; ctx.fillText("кВт", 5, top - 12);
    for (const window of series.windows) {
      const x1 = x(window.start), x2 = x(window.end);
      ctx.fillStyle = series.color;
      ctx.globalAlpha = window.power < 0 ? 0.55 : 0.9;
      ctx.fillRect(x1 + 0.5, Math.min(y(0), y(window.power)), Math.max(0.5, x2 - x1 - 1), Math.abs(y(window.power) - y(0)));
      ctx.globalAlpha = 1;
    }
    const ticks = width < 500 ? 4 : 6;
    for (let tick = 0; tick <= ticks; tick++) {
      const time = model.minTime + (model.maxTime - model.minTime) * tick / ticks;
      ctx.fillStyle = "#495968";
      ctx.textAlign = tick === 0 ? "left" : tick === ticks ? "right" : "center";
      ctx.fillText(hour.format(new Date(time)), x(time), bottom + 20);
    }
  });
  ctx.textAlign = "right"; ctx.fillStyle = "#495968";
  ctx.fillText("Час Києва · однакова шкала для всіх джерел", right, height - 5);
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
  const generation = ++refreshGeneration;
  refreshController?.abort();
  const controller = new AbortController();
  refreshController = controller;
  const read = scopedRead(controller, () => generation === refreshGeneration);
  let siteId = selectedSiteId();
  const balanceDate = balanceDateInput.value;
  const gridSign = $("grid-sign").value;
  const timeout = setTimeout(() => {
    controller.abort();
    if (generation === refreshGeneration) {
      clearLiveData("Data unavailable");
      setApiState(false);
      $("data-state").textContent = "Refresh timed out";
      $("last-refresh").textContent = "failed: refresh timed out";
    }
  }, 15000);
  clearLiveData("Updating");
  applyViewScope();
  $("fleet-sites").replaceChildren();
  appendTableEmpty($("fleet-sites"), "Оновлення…", 5);
  $("data-state").textContent = "Updating data";
  $("api-state").className = "state state-unknown";
  $("api-state").textContent = "API loading";
  try {
    if (viewScope.value === "all") {
      const results = await Promise.allSettled([loadFleet(read), loadHealth(read)]);
      read.ensureCurrent();
      const failed = results.find(result => result.status === "rejected");
      if (failed) throw failed.reason;
      setApiState(true);
      $("last-refresh").textContent = fmtDate(new Date().toISOString());
      return;
    }
    await loadSites(read);
    siteId = selectedSiteId();
    await loadAssets(read);
    read.ensureCurrent();
    const assetId = selectedAssetId();
    applyAsset(assetId);
    const results = await Promise.allSettled([
      loadAssetStatus(assetId, read),
      loadSiteStatus(assetId, read, gridSign, siteId),
      loadHealth(read),
      loadSourceOverview(assetId, siteId, read),
      loadSchedules(assetId, read),
      loadStop(assetId, read),
      loadSolarForecast(assetId, read),
      loadPvProfiles(siteId, read),
      loadSiteBalance(siteId, balanceDate, read),
      Promise.resolve(),
      loadSiteMeasurements(siteId, read),
      loadFinancialPlan(siteId, read),
      loadSitePower(siteId, read),
    ]);
    read.ensureCurrent();
    if (results[0].status === "rejected") {
      setFacts("status-facts", [["Status", "Data unavailable"]]);
      setFacts("command-facts", [["Status", "Data unavailable"]]);
    }
    if (results[1].status === "rejected") setFacts("site-facts", [["Status", "Data unavailable"]]);
    if (results[4].status === "rejected") {
      $("schedules").replaceChildren();
      appendScheduleEmpty("Data unavailable");
    }
    if (results[5].status === "rejected") $("stop-detail").textContent = "Data unavailable";
    if (results[6].status === "rejected") setFacts("solar-facts", [["Status", "Data unavailable"]]);
    if (results[11].status === "rejected") setFacts("financial-facts", [["Статус", "Фінансовий план недоступний"]]);
    setKpis({
      ...(results[0].status === "fulfilled" ? results[0].value : {}),
      ...(results[1].status === "fulfilled" ? results[1].value : {}),
      ...(results[11].status === "fulfilled" ? results[11].value : {}),
      ...(results[12].status === "fulfilled" ? results[12].value : {}),
    });
    const failed = results.some((result) => result.status === "rejected") || read.errors.length > 0;
    setApiState(!failed);
    if (failed) {
      $("data-state").textContent = "Partial data — failed sections are unavailable";
      $("last-refresh").textContent = "Partial refresh: " + fmtDate(new Date().toISOString());
    } else {
      lastSuccessfulRefresh = Date.now();
      $("last-refresh").textContent = fmtDate(new Date().toISOString());
      if ($("data-state").textContent === "Updating data") $("data-state").textContent = "Data updated";
    }
  } catch (error) {
    if (generation !== refreshGeneration) return;
    clearLiveData("Data unavailable");
    $("fleet-sites").replaceChildren();
    appendTableEmpty($("fleet-sites"), "Не вдалося отримати дані", 5);
    if (viewScope.value === "all") {
      $("generation-note").textContent = "Дані недоступні";
      $("load-note").textContent = "Дані недоступні";
      $("fleet-note").textContent = "Не вдалося оновити огляд. Спробуйте ще раз.";
    }
    setApiState(false);
    $("data-state").textContent = controller.signal.aborted ? "Refresh timed out" : "Data unavailable";
    $("last-refresh").textContent = "failed: " + (error.message || "request failed");
  } finally {
    clearTimeout(timeout);
  }
}

assetSelect.addEventListener("change", () => {
  manualAsset.value = "";
  $("grid-sign").value = "";
  clearRunData("Selection changed");
  applyAsset(assetSelect.value);
  refresh();
});

viewScope.addEventListener("change", refresh);

manualAsset.addEventListener("change", () => {
  $("grid-sign").value = "";
  clearRunData("Selection changed");
  applyAsset(manualAsset.value.trim());
  refresh();
});

siteIdInput.addEventListener("change", () => {
  $("grid-sign").value = "";
  manualAsset.value = "";
  state.selectedAssetId = "";
  clearRunData("Selection changed");
  refresh();
});
$("grid-sign").addEventListener("change", refresh);
$("technical-details").addEventListener("toggle", () => {
  if ($("technical-details").open) {
    drawScheduleChart();
    drawSolarChart();
  }
});
balanceDateInput.addEventListener("change", refresh);

$("refresh").addEventListener("click", refresh);
$("load-run").addEventListener("click", () => {
  loadRun().catch((error) => {
    if (error.name === "AbortError") return;
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
appendTableEmpty($("site-balance-meters"), "No site balance loaded", 6);
appendTableEmpty($("site-balance-generation"), "No site balance loaded", 4);
appendTableEmpty($("run-breakdown"), "No run selected", 3);
refresh();
setInterval(() => {
  // A suspended browser may retain old values. Remove them before trying again.
  if (lastSuccessfulRefresh && Date.now() - lastSuccessfulRefresh > refreshIntervalMs * 2) {
    clearLiveData("Data expired");
    $("data-state").textContent = "Data expired";
  }
  refresh();
}, refreshIntervalMs);
document.addEventListener("visibilitychange", () => {
  if (!document.hidden) refresh();
});
