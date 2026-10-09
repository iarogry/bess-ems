const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');

function dashboard(fetch) {
  class Element {
    constructor(id) { this.style = {}; this.id = id; this.textContent = ''; this.value = ''; this.children = []; this.width = 600; this.height = 160; }
    replaceChildren(...children) { this.children = children; }
    append(...children) { this.children.push(...children); }
    getContext() { return new Proxy({}, { get: () => () => {} }); }
    getBoundingClientRect() { return { width: 600, height: 160 }; }
  }
  const elements = new Map();
  const get = (id) => {
    if (!elements.has(id)) elements.set(id, new Element(id));
    return elements.get(id);
  };
  const timers = new Set();
  const context = vm.createContext({
    fetch, AbortController, DOMException, Date, console,
    setTimeout: (fn) => { timers.add(fn); return fn; },
    clearTimeout: (fn) => timers.delete(fn),
    document: { body: { classList: { toggle() {} } }, getElementById: get, createElement: (tag) => new Element(tag) },
    window: { devicePixelRatio: 1 },
  });
  const source = fs.readFileSync(path.join(__dirname, '../../src/adapters/driving/BatteryEms.Api/wwwroot/operator/app.js'), 'utf8');
  vm.runInContext(source.slice(0, source.indexOf('assetSelect.addEventListener')), context);
  get('manual-asset').value = 'old';
  get('site-id').value = 'site';
  return { get, timers, evaluate: code => vm.runInContext(code, context), refresh: () => vm.runInContext('refresh()', context), run: () => vm.runInContext('loadRun()', context) };
}

function response(body, status = 200) {
  return { ok: status < 400, status, statusText: 'failed', text: async () => JSON.stringify(body) };
}
function normal(url) {
  if (url === '/assets') return response({ assets: [] });
  if (url.includes('/battery/')) return response({ observed_at: new Date().toISOString(), quality: { flag: 'valid' }, telemetry: { soc_percent: url.includes('/new/') ? 75 : 25, active_power_kw: 12 } });
  if (url.endsWith('/status')) return response({ observed_at: new Date().toISOString(), quality: { flag: 'Valid' }, telemetry: { load_power_kw: 20 } });
  if (url.includes('/stops/')) return response({ stop: { reason: 'maintenance', operator: 'test' } });
  return response({ status: 'ok', production_gate: 'blocked' });
}
function gate() {
  let release;
  const promise = new Promise((resolve) => { release = resolve; });
  return { promise, release };
}
async function flush() { for (let i = 0; i < 20; i++) await Promise.resolve(); }

test('failed refresh clears prior KPI and stop; independent site data remains current', async () => {
  let fail = false;
  const ui = dashboard(async (url) => fail && (url.includes('/battery/') || url.includes('/stops/')) ? response({}, 503) : normal(url));
  await ui.refresh();
  assert.equal(ui.get('kpi-soc').textContent, '25 %');
  assert.equal(ui.get('stop-state').textContent, 'active');
  fail = true;
  await ui.refresh();
  assert.equal(ui.get('kpi-soc').textContent, '-');
  assert.equal(ui.get('stop-state').textContent, 'unknown');
  assert.equal(ui.get('kpi-load').textContent, '20 kW');
  assert.equal(ui.get('api-state').textContent, 'API failed');
});

test('late old-asset response cannot overwrite the new selection even when fetch ignores abort', async () => {
  const pending = gate();
  const ui = dashboard(async (url) => url.includes('/battery/old/') ? pending.promise : normal(url));
  const old = ui.refresh();
  await flush();
  ui.get('manual-asset').value = 'new';
  await ui.refresh();
  assert.equal(ui.get('kpi-soc').textContent, '75 %');
  pending.release(normal('/battery/old/status'));
  await old;
  assert.equal(ui.get('asset-title').textContent, 'new');
  assert.equal(ui.get('kpi-soc').textContent, '75 %');
  assert.equal(ui.get('api-state').textContent, 'API ok');
});

test('stale or missing observation timestamp cannot appear as a live SOC', async () => {
  const ui = dashboard(async (url) => url.includes('/battery/') ? response({ observed_at: '2020-01-01T00:00:00Z', telemetry: { soc_percent: 99 } }) : normal(url));
  await ui.refresh();
  assert.equal(ui.get('kpi-soc').textContent, '-');
  assert.match(ui.get('data-state').textContent, /stale/);
});

test('timeout removes partial readings instead of retaining an active stop', async () => {
  const pending = gate();
  const ui = dashboard(async (url) => url.includes('/battery/') ? pending.promise : normal(url));
  const work = ui.refresh();
  await flush();
  for (const expire of [...ui.timers]) expire();
  assert.equal(ui.get('stop-state').textContent, 'unknown');
  assert.equal(ui.get('data-state').textContent, 'Refresh timed out');
  pending.release(normal('/battery/old/status'));
  await work;
  assert.equal(ui.get('kpi-soc').textContent, '-');
  assert.equal(ui.get('stop-state').textContent, 'unknown');
  assert.equal(ui.get('data-state').textContent, 'Refresh timed out');
});

for (const flag of ['Stale', 'ProtocolError', 'Substituted']) {
  test(`API quality ${flag} cannot appear as a current SOC`, async () => {
    const ui = dashboard(async (url) => url.includes('/battery/')
      ? response({ observed_at: new Date().toISOString(), quality: { flag }, telemetry: { soc_percent: 99 } }) : normal(url));
    await ui.refresh();
    assert.equal(ui.get('kpi-soc').textContent, '-');
  });
}

test('expired command power is not shown as a current command', async () => {
  const ui = dashboard(async (url) => url.includes('/battery/')
    ? response({ observed_at: new Date().toISOString(), quality: { flag: 'Valid' },
      telemetry: { soc_percent: 25 }, last_command: { valid_until: '2020-01-01T00:00:00Z', active_power_kw: 88 } }) : normal(url));
  await ui.refresh();
  const values = ui.get('command-facts').children.map((child) => child.textContent);
  assert.ok(values.includes('expired or invalid'));
  assert.ok(!values.includes('88 kW'));
});

for (const [sign, power, expected] of [
  ['import_pos', -30, '30 kW'], ['import_pos', 30, '0 kW'],
  ['export_pos', 30, '30 kW'], ['export_pos', -30, '0 kW'],
  ['export_pos', 0, '0 kW'],
]) {
  test(`export uses explicit ${sign} convention for ${power} kW`, async () => {
    const ui = dashboard(async (url) => url.startsWith('/site/') && url.endsWith('/status')
      ? response({ observed_at: new Date().toISOString(), quality: { flag: 'Valid' },
        telemetry: { pv_power_kw: 80, load_power_kw: 50, grid_power_kw: power } }) : normal(url));
    ui.get('grid-sign').value = sign;
    await ui.refresh();
    assert.equal(ui.get('kpi-export').textContent, expected);
    assert.equal(ui.get('kpi-pv').textContent, '—');
    assert.equal(ui.get('kpi-load').textContent, '50 kW');
    assert.equal(ui.get('kpi-profit').textContent, '—');
  });
}

test('unknown direction never guesses export; stale site readings clear primary cards', async () => {
  let stale = false;
  const ui = dashboard(async (url) => url.startsWith('/site/') && url.endsWith('/status')
    ? response({ observed_at: stale ? '2020-01-01T00:00:00Z' : new Date().toISOString(),
      quality: { flag: 'Valid' }, telemetry: { pv_power_kw: 80, load_power_kw: 50, grid_power_kw: -30 } }) : normal(url));
  await ui.refresh();
  assert.equal(ui.get('kpi-export').textContent, '—');
  assert.match(ui.get('export-note').textContent, /напрям/);
  ui.get('grid-sign').value = 'import_pos';
  await ui.refresh();
  assert.equal(ui.get('kpi-export').textContent, '30 kW');
  stale = true;
  await ui.refresh();
  assert.equal(ui.get('kpi-export').textContent, '—');
  assert.equal(ui.get('kpi-pv').textContent, '—');
  assert.equal(ui.get('kpi-load').textContent, '-');
});

function financialPlan(overrides = {}) {
  const date = new Intl.DateTimeFormat('en-CA', { timeZone: 'Europe/Kyiv', year: 'numeric', month: '2-digit', day: '2-digit' }).format(new Date());
  return { site_id: 'site', delivery_date: date, time_zone: 'Europe/Kyiv', status: 'ready',
    currency: 'UAH', economics: { planned_profit: 100 }, ...overrides };
}

function fleet(confirmed = true) {
  const generation = { power_kw: confirmed ? 120 : null, status: confirmed ? 'estimated' : 'membership_pending', available_sources: 3, expected_sources: 3 };
  const consumption = { power_kw: confirmed ? 0 : null, status: confirmed ? 'partial' : 'membership_pending', available_sources: 1, expected_sources: 2 };
  return { membership_confirmed: confirmed, generation, consumption, sites: [{ site_id: 'site-a', name: '<Site A>',
    generation: { ...generation, power_kw: 120 }, consumption: { ...consumption, power_kw: 0 }, sources: [] }] };
}

test('all sites shows server totals with coverage and preserves measured zero consumption', async () => {
  const paths = [];
  const ui = dashboard(async url => { paths.push(url); return url === '/sites/overview' ? response(fleet()) : normal(url); });
  ui.get('view-scope').value = 'all';
  await ui.refresh();
  assert.equal(ui.get('kpi-pv').textContent, '120 kW');
  assert.equal(ui.get('kpi-load').textContent, '0 kW');
  assert.match(ui.get('load-note').textContent, /Часткова сума.*1\/2/);
  assert.match(ui.get('generation-note').textContent, /невідомий/);
  assert.equal(ui.get('fleet-sites').children[0].children[0].textContent, '<Site A>');
  assert.equal(ui.get('technical-details').hidden, true);
  assert.equal(ui.get('profit-card').hidden, true);
  assert.ok(paths.every(url => url === '/sites/overview' || url.startsWith('/health')));
});

test('shared installation mapping keeps overall totals hidden while individual rows remain visible', async () => {
  const ui = dashboard(async url => url === '/sites/overview' ? response(fleet(false)) : normal(url));
  ui.get('view-scope').value = 'all';
  await ui.refresh();
  assert.equal(ui.get('kpi-pv').textContent, '-');
  assert.match(ui.get('generation-note').textContent, /відповідність/);
  assert.match(ui.get('fleet-note').textContent, /подвійного обліку/);
  assert.equal(ui.get('fleet-sites').children.length, 1);
});

test('late fleet response cannot overwrite a later single-site selection', async () => {
  const pending = gate();
  let calls = 0;
  const ui = dashboard(async url => url === '/sites/overview' && calls++ === 0 ? pending.promise : normal(url));
  ui.get('view-scope').value = 'all';
  const old = ui.refresh();
  await flush();
  ui.get('view-scope').value = 'site';
  await ui.refresh();
  pending.release(response(fleet()));
  await old;
  assert.equal(ui.get('kpi-load').textContent, '20 kW');
  assert.equal(ui.get('fleet-panel').hidden, true);
  assert.equal(ui.get('profit-card').hidden, false);
});

test('today site profit appears and a failed subsequent request removes the prior amount', async () => {
  let fail = false;
  const ui = dashboard(async (url) => url.endsWith('/financial-plan')
    ? fail ? response({}, 503) : response(financialPlan()) : normal(url));
  await ui.refresh();
  assert.equal(ui.get('kpi-profit').textContent, '100 UAH');
  fail = true;
  await ui.refresh();
  assert.equal(ui.get('kpi-profit').textContent, '—');
});

for (const overrides of [
  { delivery_date: '2020-01-01' }, { site_id: 'another-site' },
  { status: 'monthly_review_required' }, { currency: 'unknown' },
]) {
  test(`unverified or mismatched financial plan is unavailable: ${JSON.stringify(overrides)}`, async () => {
    const ui = dashboard(async (url) => url.endsWith('/financial-plan') ? response(financialPlan(overrides)) : normal(url));
    await ui.refresh();
    assert.equal(ui.get('kpi-profit').textContent, '—');
  });
}

test('single-site generation uses assigned sources and does not require fleet membership approval', async () => {
  const ui = dashboard(async url => url === '/sites/overview' ? response({ membership_confirmed: false,
    sites: [{ site_id: 'site', generation: { power_kw: 147, available_sources: 3, expected_sources: 3 },
      consumption: { power_kw: 21 } }] }) : normal(url));
  await ui.refresh();
  assert.equal(ui.get('kpi-pv').textContent, '147 kW');
  assert.equal(ui.get('kpi-load').textContent, '21 kW');
  assert.match(ui.get('generation-note').textContent, /3\/3/);
});

test('schedule chart separates concurrent sources and shares a finite kW scale', () => {
  const ui = dashboard(async url => normal(url));
  const start = '2026-10-09T00:00:00Z', end = '2026-10-09T01:00:00Z';
  const schedules = [
    { asset_id: 'battery-a', equipment_kind: 'battery', prepared: true, windows: [{ start, end, target_power_kw: -160 }] },
    { asset_id: 'chp-a', equipment_kind: 'chp', windows: [{ start, end, target_power_kw: 450 }] },
    { asset_id: 'pv-a', equipment_kind: 'solar', windows: [{ start, end, target_power_kw: 90 }] }
  ];
  ui.evaluate('state.schedules = ' + JSON.stringify(schedules) + '; drawScheduleChart()');
  assert.equal(ui.evaluate('scheduleChartModel(state.schedules).series.length'), 3);
  assert.equal(ui.evaluate('scheduleChartModel(state.schedules).limit'), 500);
  assert.equal(ui.get('schedule-chart').style.height, '662px');
  assert.equal(ui.get('schedule-legend').children.length, 3);
  assert.match(ui.get('schedule-legend').children[0].children[1].textContent, /УЗЕ.*Підготовлений/);
  assert.match(ui.get('schedule-legend').children[1].children[1].textContent, /КГУ.*Активний/);
  assert.notEqual(ui.get('schedule-legend').children[0].children[0].style.backgroundColor,
    ui.get('schedule-legend').children[1].children[0].style.backgroundColor);
});

test('invalid power or reversed time windows cannot corrupt the chart scale', () => {
  const ui = dashboard(async url => normal(url));
  assert.equal(ui.evaluate(`scheduleChartModel([{ windows: [
    { start: '2026-10-09', end: '2026-10-08', target_power_kw: 80 },
    { start: '2026-10-09', end: '2026-10-10', target_power_kw: null }
  ] }]).series.length`), 0);
});


test('source overview lists only FusionSolar stations assigned to selected site and marks missing data', async () => {
  const ui = dashboard(async () => response({}));
  await ui.evaluate(`loadSourceOverview('', 'site-ukrainska-96', async url => url === '/sites/overview'
    ? { sites: [{ site_id: 'site-ukrainska-96', sources: [{ telemetry_id: 'NE=311287210', power_kw: null }] },
                { site_id: 'site-holovna-223', sources: [{ telemetry_id: 'NE=129469793', power_kw: 23 }] }] }
    : { readings: [{}] })`);
  const content = ui.get('source-facts').children.map(child => child.textContent).join(' ');
  assert.match(content, /311287210/);
  assert.match(content, /немає доступних даних/);
  assert.doesNotMatch(content, /129469793/);
});


test('source overview labels station power and ASKUE daily energy separately and preserves measured zero', async () => {
  const ui = dashboard(async () => response({}));
  await ui.evaluate(`loadSourceOverview('', 'site-zachyniaieva-113', async url => {
    if (url === '/sites/overview') return { sites: [{ site_id: 'site-zachyniaieva-113', sources: [
      { physical_id: 'fusionsolar-158463133-pv', telemetry_id: 'NE=158463133', power_kw: 0, quality: 'Substituted', observed_at: '2026-10-09T12:00:00Z' }
    ] }] };
    if (url === '/sites/askue/status') return { accounts: [{ site_id: 'site-zachyniaieva-113', state: 'ok', last_success_at: '2026-10-09T12:00:00Z' }] };
    return { meter_totals: [{ reading_count: 1 }], main_grid_import_kwh: 123, main_grid_export_kwh: 0, subconsumer_consumption_kwh: 0, data_quality_status: 'incomplete' };
  })`);
  const content = ui.get('source-facts').children.map(child => child.textContent).join(' ');
  assert.match(content, /Жорновий млин/);
  assert.match(content, /активна потужність: 0 кВт/);
  assert.match(content, /точний час вимірювання не підтверджено/);
  assert.match(content, /імпорт із мережі 123 кВт·год/);
  assert.match(content, /експорт у мережу 0 кВт·год/);
  assert.match(content, /Не прив’язано до цієї площадки/);
  assert.doesNotMatch(content, /recent consumption present/);
});
