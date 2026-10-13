// مشعل — End-to-End «یک سیستم واحد» روی موبایل (Chromium با شبیه‌سازی Pixel 7) + برنامهٔ واقعی + PostgreSQL موقت.
//
// سناریو: فعال‌سازی کار بدون سرور در همان داشبورد؛ آماده‌سازی همهٔ صفحات منو؛ بازکردن برنامه بدون رمز محلی و آماده‌شدن
// صفحه با کلید عمومی؛ قطع واقعی سرور (پروسه کشته می‌شود)؛ بازشدن سرد از نشانی آیکن (/?source=pwa) با همان داشبورد و
// سایدبار کامل؛ ناوبری منو؛ صفحهٔ آماده‌نشده داخل همان پوسته؛ ثبت آفلاین در فورم اصلی؛ همگام‌سازی خودکار؛ به‌روزرسانی
// Service Worker بدون ازدست‌رفتن IndexedDB. دیتابیس موقت با پیشوند ptg_oil_accounting_test_ ساخته و در پایان حذف می‌شود.
//
// اجرا: PTG_TEST_POSTGRES_ADMIN="Host=...;Username=...;Password=...;Database=postgres" + NODE_PATH + PTG_CHROMIUM_PATH
//       node tests/offline-js/pwa-main-shell-e2e.cjs   (ابتدا Web را build کنید)

const fs = require('fs');
const os = require('os');
const path = require('path');
const crypto = require('crypto');
const { spawn, execFileSync } = require('child_process');
const { chromium, devices } = require('playwright');

const repo = path.resolve(__dirname, '../..');
const webDir = path.join(repo, 'src', 'PTGOilSystem.Web');
const dll = path.join(webDir, 'bin', 'Debug', 'net8.0', 'PTGOilSystem.Web.dll');
const swPath = path.join(webDir, 'wwwroot', 'sw.js');
const psql = process.env.PTG_PSQL || 'C:/Program Files/PostgreSQL/18/bin/psql.exe';
const output = process.env.PTG_BROWSER_ARTIFACTS || path.join(os.tmpdir(), 'mashal-pwa-shell-e2e');
const userDataDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mashal-pwa-shell-profile-'));
fs.mkdirSync(output, { recursive: true });

const admin = Object.fromEntries((process.env.PTG_TEST_POSTGRES_ADMIN || '').split(';').filter(Boolean)
  .map((kv) => { const i = kv.indexOf('='); return [kv.slice(0, i).trim().toLowerCase(), kv.slice(i + 1)]; }));
if (!admin.password) {
  console.error('PTG_TEST_POSTGRES_ADMIN is required.');
  process.exit(2);
}
const PREFIX = 'ptg_oil_accounting_test_';
const dbName = `${PREFIX}pwa_shell_${crypto.randomBytes(6).toString('hex')}`;
const port = 5900 + Math.floor(Math.random() * 90);
const ORIGIN = `http://localhost:${port}`;
const ADMIN_PASSWORD = 'Offline-E2E-Admin-2026!';
const PIN = 'kabul-e2e-2026';
const NOTICE = 'main > [data-offline-notice]';
const PANEL = 'main form [data-offline-panel]';

const pg = (database, query) => execFileSync(psql, ['-h', admin.host || 'localhost', '-p', admin.port || '5432',
  '-U', admin.username || 'postgres', '-d', database, '-v', 'ON_ERROR_STOP=1', '-At', '-c', query],
{ env: { ...process.env, PGPASSWORD: admin.password }, encoding: 'utf8' }).trim();

function sql(query) {
  if (!dbName.startsWith(PREFIX)) throw new Error('refusing to touch a non-test database');
  return pg(dbName, query);
}

const results = [];
function check(id, name, ok, detail = '') {
  results.push({ id, name, ok });
  console.log(`${ok ? 'PASS' : 'FAIL'} ${id} ${name}${detail ? ' — ' + detail : ''}`);
  if (!ok) throw new Error(`${id} failed: ${name} ${detail}`);
}

const log = [];
let server = null;

function startServer() {
  server = spawn('dotnet', ['exec', dll, '--contentRoot', webDir], {
    cwd: webDir,
    env: {
      ...process.env,
      ASPNETCORE_ENVIRONMENT: 'Development',
      ASPNETCORE_URLS: ORIGIN,
      DATABASE_URL: `postgres://${admin.username || 'postgres'}:${encodeURIComponent(admin.password)}@${admin.host || 'localhost'}:${admin.port || 5432}/${dbName}`,
      PTG_AUTO_MIGRATE: 'true',
      PTG_BOOTSTRAP_ADMIN_USERNAME: 'admin',
      PTG_BOOTSTRAP_ADMIN_PASSWORD: ADMIN_PASSWORD,
      PTG_DEV_AUTO_SIGNIN: 'false',
      PTG_UI_PICK: '0',
      Backup__SchedulerEnabled: 'false',
      Logging__LogLevel__Default: 'Warning'
    }
  });
  server.stdout.on('data', (d) => log.push(String(d)));
  server.stderr.on('data', (d) => log.push(String(d)));
  return waitHealthy();
}

async function stopServer() {
  if (!server) return;
  const exited = new Promise((resolve) => server.once('exit', resolve));
  server.kill();
  await exited;
  server = null;
  await new Promise((r) => setTimeout(r, 1000));
}

async function waitHealthy() {
  const deadline = Date.now() + 6 * 60_000;
  while (Date.now() < deadline) {
    if (server.exitCode !== null) throw new Error('server exited:\n' + log.join(''));
    try {
      if ((await fetch(`${ORIGIN}/health`)).ok) return;
    } catch { /* starting */ }
    await new Promise((r) => setTimeout(r, 1000));
  }
  throw new Error('server not healthy:\n' + log.join(''));
}

/** «گوشی»: Pixel 7 (عرض ۴۱۲، لمسی، UA اندروید)؛ هر launch یک بازکردن سرد برنامه است (sessionStorage خالی). */
async function launch() {
  return chromium.launchPersistentContext(userDataDir, {
    ...devices['Pixel 7'],
    executablePath: process.env.PTG_CHROMIUM_PATH || undefined,
    headless: true,
    locale: 'fa-AF',
    serviceWorkers: 'allow'
  });
}

const textOf = (page, selector) => page.$eval(selector, (el) => (el.hidden ? '' : el.innerText)).catch(() => '');

async function waitFor(page, selector, text, timeout = 30000) {
  await page.waitForFunction(([s, t]) => {
    const el = document.querySelector(s);
    return !!el && !el.hidden && el.innerText.includes(t);
  }, [selector, text], { timeout }).catch(async (e) => {
    await page.screenshot({ path: path.join(output, `timeout-${Date.now()}.png`), fullPage: true });
    throw new Error(`"${selector}" did not show "${text}": ${(await textOf(page, selector)).slice(0, 400)} | url=${page.url()} | ${e.message}`);
  });
}

async function login(page, returnUrl = '/') {
  await page.goto(`${ORIGIN}/Auth/Login?returnUrl=${encodeURIComponent(returnUrl)}`);
  await page.fill('#Username', 'admin');
  await page.fill('#Password', ADMIN_PASSWORD);
  await Promise.all([page.waitForNavigation(), page.click('.ptg-login-submit')]);
}

async function spaGo(page, url) {
  await page.evaluate((u) => window.PTG.spaNavigate(u), url);
  await page.waitForFunction((u) => (location.pathname + location.search).toLowerCase() === u.toLowerCase(), url, { timeout: 30000 });
  await page.waitForTimeout(400);
}

/** خواندن خام IndexedDB پروفایل (بدون کلید): meta و ردیف‌های رمزگذاری‌شدهٔ صفحات. */
async function idb(page, store, key) {
  return page.evaluate(async ([s, k]) => {
    const dbs = (await indexedDB.databases()).filter((d) => d.name && d.name.startsWith('mashal-offline-c'));
    if (!dbs.length) return null;
    return new Promise((resolve) => {
      const request = indexedDB.open(dbs[0].name);
      request.onsuccess = () => {
        const db = request.result;
        if (!db.objectStoreNames.contains(s)) { db.close(); resolve(null); return; }
        const os = db.transaction(s).objectStore(s);
        const get = k === undefined ? os.getAll() : os.get(k);
        get.onsuccess = () => { db.close(); resolve(get.result ?? null); };
        get.onerror = () => { db.close(); resolve(null); };
      };
      request.onerror = () => resolve(null);
    });
  }, [store, key]);
}

/** کلید ردیف صفحه (همان page-vault.js) برای نشانی‌ها. */
const pageKeys = (page, urls) => page.evaluate(async (list) => {
  const { PageVault } = await import('/offline/js/page-vault.js');
  return Promise.all(list.map((u) => PageVault.keyFor(new URL(u, location.href).href)));
}, urls);

const sidebarLinks = (page) => page.$$eval('#boltzSidebar .boltz-sidebar-nav a[href]', (links) => links.map((a) => a.getAttribute('href')));

async function pollSql(query, expected, timeout = 90000) {
  const deadline = Date.now() + timeout;
  let value = '';
  while (Date.now() < deadline) {
    value = sql(query);
    if (value === expected) return value;
    await new Promise((r) => setTimeout(r, 1500));
  }
  return value;
}

const swOriginal = fs.readFileSync(swPath, 'utf8');
const restoreSw = () => { if (fs.readFileSync(swPath, 'utf8') !== swOriginal) fs.writeFileSync(swPath, swOriginal); };
process.on('exit', restoreSw);

(async () => {
  if (!fs.existsSync(dll)) throw new Error(`Build the Web project first: ${dll}`);
  pg('postgres', `CREATE DATABASE "${dbName}"`);
  let context;
  try {
    await startServer();
    const now = "now() at time zone 'utc'";
    sql(`INSERT INTO "Companies" ("Code","Name","Country","IsActive","IsSystemOwner","CreatedAtUtc") VALUES ('MSH','Mashal E2E','AF',true,true,${now}) ON CONFLICT DO NOTHING;`);
    sql(`INSERT INTO "Currencies" ("Code","Name","IsActive","CreatedAtUtc") SELECT 'USD','US Dollar',true,${now} WHERE NOT EXISTS (SELECT 1 FROM "Currencies" WHERE "Code"='USD');`);
    sql(`INSERT INTO "Customers" ("Code","Name","IsActive","CreatedAtUtc") VALUES ('C-E2E','E2E Existing Customer',true,${now});`);
    console.log(`server ${ORIGIN} on test database ${dbName}`);

    context = await launch();
    let page = context.pages()[0] || await context.newPage();
    const errors = [];
    const watch = (p) => p.on('pageerror', (e) => errors.push(`${p.url()}: ${e.message}`));
    watch(page);

    // ---- ۱. ورود و فعال‌سازی در همان داشبورد مشعل (بدون رفتن به برنامهٔ جدا)
    await login(page, '/');
    await page.waitForFunction(() => !!navigator.serviceWorker.controller, null, { timeout: 30000 });
    await page.waitForSelector(`${NOTICE} [data-offline-activate]`, { timeout: 30000 });
    await page.screenshot({ path: path.join(output, '01-activation-in-dashboard.png'), fullPage: true });
    const [pin1, pin2] = await page.$$(`${NOTICE} [data-offline-new-pin]`);
    await pin1.fill(PIN);
    await pin2.fill(PIN);
    await page.click(`${NOTICE} [data-offline-activate]`);
    await waitFor(page, NOTICE, 'فعال شد');
    check('ACTIVATE-IN-MAIN', 'device activated from the main dashboard; the page stayed on the dashboard',
      new URL(page.url()).pathname === '/' && !!(await page.$('#boltzSidebar')));
    check('DEVICE', 'trusted device registered by the existing sync engine',
      await pollSql(`SELECT count(*) FROM "OfflineSyncDevices";`, '1', 60000) === '1');

    // ---- ۲. آماده‌سازی همهٔ گزینه‌های منو + داشبورد + فورم‌های مجاز (رمزگذاری‌شده)
    const onlineLinks = await sidebarLinks(page);
    const deadline = Date.now() + 300_000;
    while (Date.now() < deadline && !(await idb(page, 'meta', 'pageWarmup'))) await page.waitForTimeout(2000);
    const index = (await idb(page, 'meta', 'pagesIndex'))?.entries || [];
    const keys = new Set(index.map((e) => e.key));
    const wanted = onlineLinks.filter((href) => !/^\/(users|roles|backups|backuprestore|systemsettings|maintenance|auditlogs|offlinesync)(\/|$|\?)/i.test(href)
      && !/(export|download|pdf|excel|print|backup)/i.test(href.toLowerCase()));
    const missing = [];
    const wantedKeys = await pageKeys(page, wanted);
    wantedKeys.forEach((k, i) => { if (!keys.has(k)) missing.push(wanted[i]); });
    check('PREPARE-SIDEBAR', 'every permitted sidebar page (plus dashboard, forms, quick-create) is stored for offline use',
      missing.length === 0 && keys.has('shell'), `${wanted.length} sidebar pages, ${index.length} stored; missing: ${missing.join(', ')}`);

    // ---- ۳. بازکردن دوبارهٔ برنامه آنلاین بدون رمز محلی: صفحهٔ تازه باز هم آماده می‌شود (کلید عمومی)
    await context.close();
    context = await launch();
    page = context.pages()[0] || await context.newPage();
    watch(page);
    await login(page, '/');
    await page.waitForSelector('#boltzSidebar', { state: 'attached', timeout: 30000 });
    await page.waitForTimeout(2500);
    check('NO-PIN-ONLINE', 'online app start does not ask the local PIN', !(await page.$(`${NOTICE} [data-offline-pin]`)));
    await spaGo(page, '/Customers?e2e=locked');
    const [lockedKey] = await pageKeys(page, ['/Customers?e2e=locked']);
    let lockedRow = null;
    for (let i = 0; i < 20 && !lockedRow; i += 1) {
      lockedRow = await idb(page, 'pages', lockedKey);
      if (!lockedRow) await page.waitForTimeout(500);
    }
    check('LOCKED-PREPARE', 'page opened without the PIN is stored encrypted with the public page key (readable only after PIN)',
      !!lockedRow?.sealed?.wk && !JSON.stringify(lockedRow).includes('E2E Existing Customer'));

    // ---- ۴. سرور خاموش؛ بازشدن سرد از آیکن موبایل (start_url)
    await stopServer();
    await context.close();
    context = await launch();
    page = context.pages()[0] || await context.newPage();
    watch(page);
    await page.goto(`${ORIGIN}/?source=pwa`);
    await page.waitForSelector('#ox-pin', { timeout: 30000 });
    check('PIN-SCREEN', 'without the server the app icon opens the Mashal-styled local PIN screen (once per session)',
      !!(await page.$('.ptg-login-page #ox-pin')) && (await page.evaluate(() => navigator.onLine)));
    await page.screenshot({ path: path.join(output, '02-offline-pin.png'), fullPage: true });
    await page.fill('#ox-pin', PIN);
    await page.click('[data-loader-unlock] button[type="submit"]');
    await page.waitForSelector('#boltzSidebar', { state: 'attached', timeout: 30000 });
    await waitFor(page, NOTICE, 'نسخهٔ ذخیره‌شده');
    const offlineLinks = await sidebarLinks(page);
    check('SAME-SHELL', 'offline dashboard opens inside the same layout with the full, unchanged sidebar',
      JSON.stringify(offlineLinks) === JSON.stringify(onlineLinks) && !!(await page.$('main'))
      && new URL(page.url()).pathname === '/', `${offlineLinks.length}/${onlineLinks.length} links`);
    await page.screenshot({ path: path.join(output, '03-offline-dashboard.png'), fullPage: true });

    // سایدبار موبایل پشت دکمهٔ منو، با همهٔ گزینه‌ها
    await page.click('[data-shell-toggle]');
    await page.waitForTimeout(600);
    const sidebarOpen = await page.evaluate(() => {
      const r = document.getElementById('boltzSidebar').getBoundingClientRect();
      return document.querySelector('[data-shell-toggle]').getAttribute('aria-expanded') === 'true'
        && r.width > 100 && r.right > 0 && r.left < window.innerWidth;
    });
    await page.screenshot({ path: path.join(output, '04-offline-mobile-sidebar.png') });
    check('MOBILE-SIDEBAR', 'hamburger opens the same sidebar on the phone width while offline', sidebarOpen);

    // ناوبری منو بدون سرور (اولین گزینهٔ غیر داشبورد که فورم نیست)
    const target = onlineLinks.find((href) => href !== '/' && !/create/i.test(href) && wanted.includes(href));
    await page.click(`#boltzSidebar .boltz-sidebar-nav a[href="${target}"]`, { force: true });
    await page.waitForFunction((t) => (location.pathname + location.search).toLowerCase() === t.toLowerCase(), target, { timeout: 30000 });
    await waitFor(page, NOTICE, 'نسخهٔ ذخیره‌شده');
    check('OFFLINE-NAV', `sidebar navigation works without the server (${target})`,
      (await textOf(page, 'main')).length > 50 && !!(await page.$('#boltzSidebar')));
    await page.screenshot({ path: path.join(output, '05-offline-nav.png'), fullPage: true });

    // صفحهٔ آماده‌نشده: همان پوسته و پیام در بخش اصلی (نه محیط جدا)
    await page.goto(`${ORIGIN}/Customers?page=99&e2e=missing`);
    await waitFor(page, 'main', 'هنوز روی این دستگاه آماده نشده');
    check('MISSING-IN-SHELL', 'a page not prepared yet shows a message inside the same shell with the sidebar',
      !!(await page.$('#boltzSidebar')) && !page.url().includes('/offline/'));

    // آیکن ابر سربرگ فقط مدیریت عملیات (بدون داشبورد/فورم موازی)
    const chip = await page.getAttribute('[data-offline-chip]', 'href');
    await page.goto(`${ORIGIN}${chip}`);
    await page.waitForSelector('[data-tab="ops"][aria-current="page"]', { timeout: 30000 });
    check('OPS-ONLY', 'header cloud icon opens operations management only (no separate dashboard / new-entry tabs)',
      chip === '/offline/index.html#/ops' && !(await page.$('[data-tab="home"], [data-tab="new"]')));

    // ثبت آفلاین در همان فورم اصلی
    await page.goto(`${ORIGIN}/Customers/Create`);
    await page.waitForSelector('main form [name="Name"]', { timeout: 30000 });
    await page.fill('main form [name="Name"]', 'E2E Mobile Offline Customer');
    await page.click('main form .ak-save');
    await waitFor(page, PANEL, 'در دستگاه ذخیره شد');
    check('OFFLINE-SAVE', 'main customer form saved to the device outbox on the phone without the server', true);
    const outboxBefore = (await idb(page, 'outbox')) || [];

    // ---- ۵. به‌روزرسانی Service Worker (نشر تازه) بدون پاک‌شدن معلومات محلی
    await startServer();
    fs.writeFileSync(swPath, swOriginal.replace(/const VERSION = '([^']+)';/, "const VERSION = '$1-e2e';"));
    const newVersion = await page.evaluate(async () => {
      const registration = await navigator.serviceWorker.getRegistration();
      await registration.update();
      for (let i = 0; i < 60; i += 1) {
        const worker = navigator.serviceWorker.controller;
        const version = await new Promise((resolve) => {
          const timer = setTimeout(() => resolve(null), 500);
          navigator.serviceWorker.addEventListener('message', function on(event) {
            if (event.data?.type === 'mashal:version') { clearTimeout(timer); navigator.serviceWorker.removeEventListener('message', on); resolve(event.data.version); }
          });
          worker?.postMessage({ type: 'mashal:version' });
        });
        if (version && version.endsWith('-e2e')) return version;
        await new Promise((r) => setTimeout(r, 500));
      }
      return null;
    });
    restoreSw();
    const outboxAfter = (await idb(page, 'outbox')) || [];
    const indexAfter = (await idb(page, 'meta', 'pagesIndex'))?.entries || [];
    check('SW-UPDATE', 'new Service Worker version activated; outbox, encrypted pages and profile kept',
      !!newVersion && outboxAfter.length === outboxBefore.length && outboxBefore.length > 0 && indexAfter.length >= index.length,
      `${newVersion} outbox ${outboxBefore.length}→${outboxAfter.length}, pages ${indexAfter.length}`);

    // ---- ۶. اتصال برگشت: همگام‌سازی خودکار در همان صفحه
    const before = page.url();
    await page.evaluate(() => window.dispatchEvent(new Event('online')));
    const created = await pollSql(`SELECT count(*) FROM "Customers" WHERE "Name"='E2E Mobile Offline Customer';`, '1');
    check('AUTO-SYNC', 'the offline customer was posted by Customers/Create after reconnect; same page stayed open',
      created === '1' && page.url() === before);
    await page.screenshot({ path: path.join(output, '06-synced.png'), fullPage: true });

    check('NO-JS-ERRORS', 'no uncaught page errors', errors.length === 0, errors.slice(0, 5).join(' | '));
    console.log(`\n${results.filter((r) => r.ok).length}/${results.length} passed. Artifacts: ${output}`);
  } catch (error) {
    console.error(error);
    process.exitCode = 1;
  } finally {
    restoreSw();
    await context?.close().catch(() => {});
    await stopServer().catch(() => {});
    try { pg('postgres', `DROP DATABASE IF EXISTS "${dbName}" WITH (FORCE)`); } catch (e) { console.error('drop failed', e.message); }
    fs.rmSync(userDataDir, { recursive: true, force: true });
  }
})();
