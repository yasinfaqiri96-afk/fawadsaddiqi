// مشعل — اندازه‌گیری واقعی سرعت (آنلاین، هنگام آماده‌سازی آفلاین، و بدون سرور) روی موبایل شبیه‌سازی‌شده.
//
// - دیتابیس: کپی تازه از یک دیتابیس آزمایشی (پیش‌فرض ptg_oil_accounting_test_pwa_local؛ فقط pg_dump خوانده می‌شود)
//   در دیتابیس موقت با پیشوند ptg_oil_accounting_test_perf_ که در پایان حذف می‌شود. دیتابیس اصلی هرگز لمس نمی‌شود.
// - مرورگر: Chromium با Pixel 7، CPU چهار برابر کندتر و شبکهٔ 4G (۴۰ms، ۱۲Mbps).
// - شمارش درخواست‌های سرور از لاگ Kestrel (Hosting.Diagnostics) — شامل درخواست‌های Service Worker.
// - ورود با PTG_DEV_AUTO_SIGNIN (فقط Development، کاربر admin همان کپی).
//
// اجرا: NODE_PATH + PTG_CHROMIUM_PATH، سپس  node tests/offline-js/offline-perf-measure.cjs [label]
// نتیجه: JSON در PTG_PERF_OUT (پیش‌فرض tmp/mashal-perf-<label>.json).

const fs = require('fs');
const os = require('os');
const path = require('path');
const crypto = require('crypto');
const { spawn, execFileSync } = require('child_process');
const { chromium, devices } = require('playwright');

const label = process.argv[2] || 'run';
const repo = path.resolve(__dirname, '../..');
const webDir = path.join(repo, 'src', 'PTGOilSystem.Web');
const dll = path.join(webDir, 'bin', 'Debug', 'net8.0', 'PTGOilSystem.Web.dll');
const pgBin = process.env.PTG_PG_BIN || 'C:/Program Files/PostgreSQL/18/bin';
const source = process.env.PTG_PERF_SOURCE_DB || 'ptg_oil_accounting_test_pwa_local';
const PREFIX = 'ptg_oil_accounting_test_';
const dbName = `${PREFIX}perf_${crypto.randomBytes(5).toString('hex')}`;
const port = 6100 + Math.floor(Math.random() * 80);
const ORIGIN = `http://localhost:${port}`;
const PIN = 'kabul-perf-2026';
const out = process.env.PTG_PERF_OUT || path.join(os.tmpdir(), `mashal-perf-${label}.json`);
const userDataDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mashal-perf-profile-'));
const NOTICE = 'main > [data-offline-notice]';

if (!source.startsWith(PREFIX)) throw new Error('source must be a test copy');
const pg = (exe, args) => execFileSync(path.join(pgBin, exe), ['-h', 'localhost', '-U', 'postgres', '-w', ...args], { encoding: 'utf8' });

let server = null;
const requests = []; // { at, method, path }
const serverLog = [];
function startServer() {
  const baseEnv = { ...process.env };
  delete baseEnv.DATABASE_URL;
  server = spawn('dotnet', ['exec', dll, '--contentRoot', webDir], {
    cwd: webDir,
    env: {
      ...baseEnv,
      ASPNETCORE_ENVIRONMENT: 'Development',
      ASPNETCORE_URLS: ORIGIN,
      ConnectionStrings__DefaultConnection: `Host=localhost;Port=5432;Username=postgres;Database=${dbName}`,
      PTG_AUTO_MIGRATE: 'true',
      PTG_DEV_AUTO_SIGNIN: 'true',
      PTG_BOOTSTRAP_ADMIN_USERNAME: 'admin',
      PTG_UI_PICK: '0',
      Backup__SchedulerEnabled: 'false',
      Logging__LogLevel__Default: 'Warning',
      'Logging__LogLevel__Microsoft.AspNetCore.Hosting.Diagnostics': 'Information',
      Logging__Console__FormatterName: 'simple'
    }
  });
  const onData = (d) => {
    serverLog.push(String(d)); if (serverLog.length > 400) serverLog.shift();
    for (const line of String(d).split(/\r?\n/)) {
      const m = /Request starting HTTP\/[\d.]+ (GET|POST|HEAD) http:\/\/[^/]+(\/\S*)/.exec(line);
      if (m) requests.push({ at: Date.now(), method: m[1], path: m[2] });
    }
  };
  server.stdout.on('data', onData);
  server.stderr.on('data', onData);
  return (async () => {
    const deadline = Date.now() + 6 * 60_000;
    while (Date.now() < deadline) {
      if (server.exitCode !== null) throw new Error('server exited:\n' + serverLog.join('').slice(-4000));
      try { if ((await fetch(`${ORIGIN}/health`)).ok) return; } catch { /* starting */ }
      await new Promise((r) => setTimeout(r, 1000));
    }
    throw new Error('server not healthy');
  })();
}
async function stopServer() {
  if (!server) return;
  const exited = new Promise((r) => server.once('exit', r));
  server.kill();
  await exited;
  server = null;
  await new Promise((r) => setTimeout(r, 800));
}

/** درخواست‌های سرور در بازه (بدون فایل ثابت). */
function htmlRequests(from, to = Date.now()) {
  return requests.filter((r) => r.at >= from && r.at <= to && !/\.(css|js|mjs|woff2?|png|ico|svg|webmanifest|json|map)(\?|$)/i.test(r.path)
    && !r.path.startsWith('/health'));
}
function summarize(list) {
  const byPath = {};
  for (const r of list) {
    const key = `${r.method} ${r.path.split('?')[0].toLowerCase()}`;
    byPath[key] = (byPath[key] || 0) + 1;
  }
  return byPath;
}

async function launch() {
  const context = await chromium.launchPersistentContext(userDataDir, {
    ...devices['Pixel 7'],
    executablePath: process.env.PTG_CHROMIUM_PATH || undefined,
    headless: true,
    locale: 'fa-AF',
    serviceWorkers: 'allow'
  });
  await context.addInitScript(() => {
    window.__lt = { total: 0, count: 0, max: 0 };
    try {
      new PerformanceObserver((list) => {
        for (const e of list.getEntries()) {
          window.__lt.total += e.duration;
          window.__lt.count += 1;
          window.__lt.max = Math.max(window.__lt.max, e.duration);
        }
      }).observe({ type: 'longtask', buffered: true });
    } catch { /* unsupported */ }
  });
  return context;
}

async function throttle(page) {
  const cdp = await page.context().newCDPSession(page);
  await cdp.send('Emulation.setCPUThrottlingRate', { rate: 4 });
  await cdp.send('Network.enable');
  await cdp.send('Network.emulateNetworkConditions', {
    offline: false, latency: 40, downloadThroughput: 12 * 1024 * 1024 / 8, uploadThroughput: 4 * 1024 * 1024 / 8
  });
  return cdp;
}

/** بارگذاری کامل یک آدرس: زمان‌های Navigation Timing + آماده‌شدن سایدبار. */
async function fullLoad(page, url) {
  const t0 = Date.now();
  await page.goto(`${ORIGIN}${url}`, { waitUntil: 'load', timeout: 120000 });
  const wall = Date.now() - t0;
  const timing = await page.evaluate(() => {
    const n = performance.getEntriesByType('navigation')[0];
    const fcp = performance.getEntriesByName('first-contentful-paint')[0];
    return n ? { ttfb: Math.round(n.responseStart), dcl: Math.round(n.domContentLoadedEventEnd), load: Math.round(n.loadEventEnd), fcp: fcp ? Math.round(fcp.startTime) : null } : null;
  });
  return { url, wall, ...timing };
}

/** ناوبری SPA: از کلیک تا ptg:page-ready. */
async function spaNav(page, url) {
  const before = Date.now();
  const ms = await page.evaluate((u) => new Promise((resolve) => {
    const t0 = performance.now();
    const timer = setTimeout(() => resolve(-1), 60000);
    window.addEventListener('ptg:page-ready', () => { clearTimeout(timer); resolve(Math.round(performance.now() - t0)); }, { once: true });
    window.PTG.spaNavigate(u);
  }), url);
  await page.waitForTimeout(1500); // درخواست‌های پس‌زمینهٔ همان صفحه (captureCurrent و …)
  const extra = htmlRequests(before).filter((r) => r.method === 'GET' && r.path.split('?')[0].toLowerCase() === url.split('?')[0].toLowerCase());
  return { url, ms, serverHits: extra.length };
}

async function longTasks(page) {
  return page.evaluate(() => ({ ...window.__lt, total: Math.round(window.__lt.total), max: Math.round(window.__lt.max) }));
}

async function idbMeta(page, key) {
  return page.evaluate(async (k) => {
    const dbs = (await indexedDB.databases()).filter((d) => d.name && d.name.startsWith('mashal-offline-c'));
    if (!dbs.length) return null;
    return new Promise((resolve) => {
      const request = indexedDB.open(dbs[0].name);
      request.onsuccess = () => {
        const db = request.result;
        if (!db.objectStoreNames.contains('meta')) { db.close(); resolve(null); return; }
        const get = db.transaction('meta').objectStore('meta').get(k);
        get.onsuccess = () => { db.close(); resolve(get.result ?? null); };
        get.onerror = () => { db.close(); resolve(null); };
      };
      request.onerror = () => resolve(null);
    });
  }, key);
}

const median = (xs) => { const s = xs.filter((x) => x >= 0).sort((a, b) => a - b); return s.length ? s[Math.floor(s.length / 2)] : null; };

(async () => {
  if (!fs.existsSync(dll)) throw new Error(`Build Web first: ${dll}`);
  const dump = path.join(os.tmpdir(), `${dbName}.dump`);
  process.env.PGOPTIONS = '-c default_transaction_read_only=on';
  pg('pg_dump.exe', ['-Fc', '-f', dump, source]);
  delete process.env.PGOPTIONS;
  pg('psql.exe', ['-d', 'postgres', '-c', `CREATE DATABASE "${dbName}"`]);
  try { pg('pg_restore.exe', ['-d', dbName, '--no-owner', '--no-privileges', dump]); } catch { /* warnings */ }
  fs.rmSync(dump, { force: true });

  const result = { label, at: new Date().toISOString(), source };
  let context;
  try {
    await startServer();
    context = await launch();
    let page = context.pages()[0] || await context.newPage();
    await throttle(page);

    // ---- ۱. آنلاین، بدون فعال‌سازی آفلاین: بازشدن اول و دوم داشبورد
    const firstStart = Date.now();
    result.coldDashboard = await fullLoad(page, '/');
    await page.waitForFunction(() => !!navigator.serviceWorker.controller, null, { timeout: 30000 }).catch(() => {});
    await page.waitForTimeout(4000);
    result.coldDashboardServer = summarize(htmlRequests(firstStart));
    result.warmDashboard = await fullLoad(page, '/');
    const links = await page.$$eval('#boltzSidebar .boltz-sidebar-nav a[href]', (a) => a.map((x) => x.getAttribute('href')));
    result.sidebarLinks = links.length;
    const sample = [...new Set(links.filter((h) => h && h.startsWith('/') && h !== '/' && !/^\/(users|roles|backups|systemsettings|maintenance|auditlogs|offlinesync)/i.test(h)))].slice(0, 8);
    result.sample = sample;
    const navBefore = [];
    for (const url of sample) navBefore.push(await spaNav(page, url));
    result.spaOnlineNoProfile = { median: median(navBefore.map((n) => n.ms)), serverHitsPerNav: navBefore.reduce((s, n) => s + n.serverHits, 0) / navBefore.length, items: navBefore };
    result.longTasksOnline = await longTasks(page);

    // ---- ۲. فعال‌سازی و آماده‌سازی: داشبورد و ناوبری هم‌زمان با آماده‌سازی پس‌زمینه
    await fullLoad(page, '/');
    await page.waitForSelector(`${NOTICE} [data-offline-activate]`, { timeout: 30000 });
    const [p1, p2] = await page.$$(`${NOTICE} [data-offline-new-pin]`);
    await p1.fill(PIN);
    await p2.fill(PIN);
    const activateAt = Date.now();
    await page.click(`${NOTICE} [data-offline-activate]`);
    await page.waitForFunction(() => document.querySelector('main > [data-offline-notice]')?.innerText.includes('فعال شد'), null, { timeout: 60000 });
    result.activationMs = Date.now() - activateAt;
    await page.waitForTimeout(1500);
    const navDuring = [];
    for (const url of sample.slice(0, 5)) navDuring.push(await spaNav(page, url));
    result.spaDuringPrepare = { median: median(navDuring.map((n) => n.ms)), items: navDuring };
    // تا پایان آماده‌سازی (حداکثر ۶ دقیقه)
    const deadline = Date.now() + 6 * 60_000;
    let warm = null;
    while (Date.now() < deadline) {
      warm = (await idbMeta(page, 'pageWarmup')) || (await idbMeta(page, 'prepare'));
      if (warm && (warm.at || warm.doneAt)) break;
      await page.waitForTimeout(2000);
    }
    result.prepareMs = warm ? Date.now() - activateAt : null;
    result.prepareServer = summarize(htmlRequests(activateAt));
    result.prepareServerTotal = htmlRequests(activateAt).length;
    const index = (await idbMeta(page, 'pagesIndex'))?.entries || [];
    result.storedEntries = index.length;
    result.storedBytes = index.reduce((s, e) => s + (e.size || 0), 0);
    result.storageEstimate = await page.evaluate(async () => { const e = await navigator.storage?.estimate?.(); return e ? { usage: e.usage, quota: e.quota } : null; });
    result.longTasksAfterPrepare = await longTasks(page);

    // ---- ۳. آنلاین پس از آماده‌سازی: بازشدن داشبورد و ناوبری عادی
    result.dashboardAfterPrepare = await fullLoad(page, '/');
    await page.waitForTimeout(3000);
    const navAfter = [];
    for (const url of sample) navAfter.push(await spaNav(page, url));
    result.spaOnlineWithProfile = { median: median(navAfter.map((n) => n.ms)), serverHitsPerNav: navAfter.reduce((s, n) => s + n.serverHits, 0) / navAfter.length, items: navAfter };

    // ---- ۴. بدون سرور
    await stopServer();
    await context.close();
    context = await launch();
    page = context.pages()[0] || await context.newPage();
    await throttle(page);
    const offStart = Date.now();
    await page.goto(`${ORIGIN}/`, { timeout: 60000 });
    await page.waitForSelector('#ox-pin', { timeout: 30000 });
    result.offlinePinScreenMs = Date.now() - offStart;
    await page.fill('#ox-pin', PIN);
    const unlockAt = Date.now();
    await page.click('[data-loader-unlock] button[type="submit"]');
    await page.waitForSelector('#boltzSidebar', { state: 'attached', timeout: 30000 });
    await page.waitForFunction(() => !!document.querySelector('main') && document.querySelector('main').innerText.length > 40, null, { timeout: 30000 });
    result.offlineDashboardAfterPinMs = Date.now() - unlockAt;
    const navOff = [];
    for (const url of sample.slice(0, 5)) {
      const t = Date.now();
      await page.evaluate((u) => window.PTG.spaNavigate(u), url);
      await page.waitForFunction((u) => (location.pathname + location.search).toLowerCase() === u.toLowerCase(), url, { timeout: 30000 }).catch(() => {});
      await page.waitForTimeout(100);
      const text = await page.$eval('main', (m) => m.innerText.slice(0, 200)).catch(() => '');
      navOff.push({ url, ms: Date.now() - t, missing: text.includes('آماده نشده') || text.includes('ذخیره نشده') });
    }
    result.spaOffline = { median: median(navOff.map((n) => n.ms)), items: navOff };
    // آدرس‌های هرگز بازنشده: فیلتر/صفحه‌بندی و فورم با پارامتر
    const probes = [`${sample[0]}?page=2`, `${sample[0]}?search=a`, '/Customers/Create?returnUrl=%2F'];
    result.offlineUnvisited = [];
    for (const url of probes) {
      await page.goto(`${ORIGIN}${url}`, { timeout: 60000 }).catch(() => {});
      await page.waitForSelector('main', { timeout: 30000 }).catch(() => {});
      await page.waitForTimeout(1500);
      const text = await page.$eval('main', (m) => m.innerText.slice(0, 300)).catch(() => '');
      result.offlineUnvisited.push({ url, works: !!text && !text.includes('آماده نشده') && !text.includes('ذخیره نشده'), text: text.slice(0, 120) });
    }
    result.longTasksOffline = await longTasks(page);
  } catch (error) {
    result.error = String(error && error.stack || error);
    console.error(error);
    process.exitCode = 1;
  } finally {
    fs.writeFileSync(out, JSON.stringify(result, null, 2));
    console.log(JSON.stringify(result, null, 2));
    console.log(`\nwritten ${out}`);
    await context?.close().catch(() => {});
    await stopServer().catch(() => {});
    try { pg('psql.exe', ['-d', 'postgres', '-c', `DROP DATABASE IF EXISTS "${dbName}" WITH (FORCE)`]); } catch (e) { console.error('drop failed', e.message); }
    fs.rmSync(userDataDir, { recursive: true, force: true });
  }
})();
