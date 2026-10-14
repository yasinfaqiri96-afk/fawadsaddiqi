// مشعل آفلاین — End-to-End عملیات روی رکوردهای موجود (ویرایش، فعال/غیرفعال با دیالوگ تأیید) در همان صفحات اصلی،
// با Chromium واقعی + برنامهٔ واقعی + PostgreSQL موقت. قطع = کشتن واقعی پروسهٔ سرور.
// - صفحهٔ ویرایش رکوردی که هرگز دستی باز نشده، از آماده‌سازی خودکار («رکوردهای اخیر» هر فهرست) بدون سرور باز می‌شود.
// - آنلاین: همان مسیر عادی صفحه (هیچ عملیاتی در صف دستگاه نمی‌رود).
// - بدون سرور: پس از دیالوگ تأیید، درخواست در دستگاه می‌ماند، سطر فهرست نشان «در انتظار» می‌گیرد، پس از اتصال خودکار
//   با همان اکشن اجرا می‌شود؛ رکوردی که کاربر دیگری در این فاصله تغییر داده «نیازمند اصلاح» می‌ماند.
// اجرا: PTG_TEST_POSTGRES_ADMIN="Host=...;Username=...;Password=...;Database=postgres" + NODE_PATH + PTG_CHROMIUM_PATH
//       node tests/offline-js/record-actions-offline-e2e.cjs   (ابتدا Web را build کنید)

const fs = require('fs');
const os = require('os');
const path = require('path');
const crypto = require('crypto');
const { spawn, execFileSync } = require('child_process');
const { chromium } = require('playwright');

const repo = path.resolve(__dirname, '../..');
const webDir = path.join(repo, 'src', 'PTGOilSystem.Web');
const dll = path.join(webDir, 'bin', 'Debug', 'net8.0', 'PTGOilSystem.Web.dll');
const psql = process.env.PTG_PSQL || 'C:/Program Files/PostgreSQL/18/bin/psql.exe';
const output = process.env.PTG_BROWSER_ARTIFACTS || path.join(os.tmpdir(), 'mashal-record-actions-e2e');
const userDataDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mashal-record-actions-profile-'));
fs.mkdirSync(output, { recursive: true });

const admin = Object.fromEntries((process.env.PTG_TEST_POSTGRES_ADMIN || '').split(';').filter(Boolean)
  .map((kv) => { const i = kv.indexOf('='); return [kv.slice(0, i).trim().toLowerCase(), kv.slice(i + 1)]; }));
if (!admin.password) {
  console.error('PTG_TEST_POSTGRES_ADMIN is required.');
  process.exit(2);
}
const PREFIX = 'ptg_oil_accounting_test_';
const dbName = `${PREFIX}record_actions_${crypto.randomBytes(6).toString('hex')}`;
const port = 5900 + Math.floor(Math.random() * 90);
const ORIGIN = `http://localhost:${port}`;
const ADMIN_PASSWORD = 'Offline-E2E-Admin-2026!';
const PIN = 'kabul-e2e-2026';
const NOTICE = 'main > [data-offline-notice]';

function pg(args, database) {
  return execFileSync(psql, ['-h', admin.host || 'localhost', '-p', admin.port || '5432', '-U', admin.username || 'postgres',
    '-d', database, '-v', 'ON_ERROR_STOP=1', '-At', ...args], { env: { ...process.env, PGPASSWORD: admin.password }, encoding: 'utf8' }).trim();
}
function sql(query) {
  if (!dbName.startsWith(PREFIX)) throw new Error('refusing to touch a non-test database');
  return pg(['-c', query], dbName);
}
const dropDatabase = () => pg(['-c', `DROP DATABASE IF EXISTS "${dbName}" WITH (FORCE)`], 'postgres');

function check(id, name, ok, detail = '') {
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

const textOf = (page, selector) => page.$eval(selector, (el) => (el.hidden ? '' : el.innerText)).catch(() => '');

async function waitFor(page, selector, text, timeout = 30000) {
  await page.waitForFunction(([s, t]) => {
    const el = document.querySelector(s);
    return !!el && !el.hidden && el.innerText.includes(t);
  }, [selector, text], { timeout }).catch(async (e) => {
    await page.screenshot({ path: path.join(output, `timeout-${Date.now()}.png`), fullPage: true });
    throw new Error(`"${selector}" did not show "${text}": ${await textOf(page, selector)} | url=${page.url()} | ${e.message}`);
  });
}

async function spaGo(page, url) {
  await page.evaluate((u) => window.PTG.spaNavigate(u), url);
  await page.waitForFunction((u) => location.pathname.toLowerCase() === u.toLowerCase(), url, { timeout: 30000 });
  await page.waitForTimeout(400);
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
        get.onsuccess = () => { db.close(); resolve(get.result || null); };
        get.onerror = () => { db.close(); resolve(null); };
      };
      request.onerror = () => resolve(null);
    });
  }, key);
}

async function outboxCount(page) {
  return page.evaluate(async () => {
    const dbs = (await indexedDB.databases()).filter((d) => d.name && d.name.startsWith('mashal-offline-c'));
    if (!dbs.length) return 0;
    return new Promise((resolve) => {
      const request = indexedDB.open(dbs[0].name);
      request.onsuccess = () => {
        const db = request.result;
        const count = db.transaction('outbox').objectStore('outbox').count();
        count.onsuccess = () => { db.close(); resolve(count.result); };
        count.onerror = () => { db.close(); resolve(-1); };
      };
      request.onerror = () => resolve(-1);
    });
  });
}

async function pollSql(query, expected, timeout = 120000) {
  const deadline = Date.now() + timeout;
  let value = '';
  while (Date.now() < deadline) {
    value = sql(query);
    if (value === expected) return value;
    await new Promise((r) => setTimeout(r, 1500));
  }
  return value;
}

(async () => {
  if (!fs.existsSync(dll)) throw new Error(`Build the Web project first: ${dll}`);
  let context;
  try {
    await startServer();
    const now = "now() at time zone 'utc'";
    sql(`INSERT INTO "Companies" ("Code","Name","Country","IsActive","IsSystemOwner","CreatedAtUtc") VALUES ('MSH','Mashal E2E','AF',true,true,${now}) ON CONFLICT DO NOTHING;`);
    for (const code of ['A', 'B', 'C']) {
      sql(`INSERT INTO "Customers" ("Code","Name","IsActive","CreatedAtUtc","UpdatedAtUtc") VALUES ('REC-${code}','Record Customer ${code}',true,${now} - interval '1 day',${now} - interval '1 day');`);
    }
    const id = (code) => sql(`SELECT "Id" FROM "Customers" WHERE "Code"='REC-${code}';`);
    const [idA, idB, idC] = [id('A'), id('B'), id('C')];
    console.log(`server ${ORIGIN} on test database ${dbName}`);

    context = await chromium.launchPersistentContext(userDataDir, {
      executablePath: process.env.PTG_CHROMIUM_PATH || undefined, headless: true,
      viewport: { width: 1280, height: 900 }, locale: 'fa-AF', serviceWorkers: 'allow'
    });
    const page = context.pages()[0] || await context.newPage();
    const errors = [];
    page.on('pageerror', (e) => errors.push(`${page.url()}: ${e.message}`));

    // ---- ورود، فعال‌سازی دستگاه در همان داشبورد، آماده‌سازی خودکار
    await page.goto(`${ORIGIN}/Auth/Login`);
    await page.fill('#Username', 'admin');
    await page.fill('#Password', ADMIN_PASSWORD);
    await Promise.all([page.waitForNavigation(), page.click('.ptg-login-submit')]);
    await page.goto(`${ORIGIN}/`);
    await page.waitForSelector(`${NOTICE} [data-offline-activate]`, { timeout: 30000 });
    const [p1, p2] = await page.$$(`${NOTICE} [data-offline-new-pin]`);
    await p1.fill(PIN);
    await p2.fill(PIN);
    await page.click(`${NOTICE} [data-offline-activate]`);
    await waitFor(page, NOTICE, 'فعال شد', 60000);
    await page.waitForFunction(() => !!navigator.serviceWorker.controller, null, { timeout: 30000 }).catch(() => {});
    const deadline = Date.now() + 300_000;
    while (Date.now() < deadline && !(await idbMeta(page, 'pageWarmup')) && !(await idbMeta(page, 'prepare'))?.doneAt) await page.waitForTimeout(2000);
    const prepared = await idbMeta(page, 'prepare');
    check('PREPARE-RECORDS', 'details/edit pages of the most recent records of menu lists were queued for preparation',
      (prepared?.summary?.records || 0) > 0, `${prepared?.summary?.records || 0} record pages`);

    // ---- آنلاین: همان مسیر عادی صفحه؛ هیچ چیزی در صف دستگاه نمی‌رود
    const before = await outboxCount(page);
    await spaGo(page, `/Customers/Edit/${idA}`);
    await page.fill('main form [name="Phone"]', '0700111111');
    await Promise.all([page.waitForURL(/\/Customers(\?|$)/i, { timeout: 30000 }), page.click('main form button[type="submit"]')]);
    check('ONLINE-UNTOUCHED', 'online edit is the normal page flow (saved on the server, nothing queued on the device)',
      sql(`SELECT "Phone" FROM "Customers" WHERE "Id"=${idA};`) === '0700111111' && (await outboxCount(page)) === before);

    // نسخهٔ دستگاه از رکورد B (بعداً کاربر دیگری آن را تغییر می‌دهد)
    await spaGo(page, `/Customers/Edit/${idB}`);
    await spaGo(page, '/Customers');

    // ---- قطع واقعی سرور
    await stopServer();

    // ویرایش رکوردی که هرگز دستی باز نشده (C) از نسخهٔ آماده‌شده
    await spaGo(page, `/Customers/Edit/${idC}`);
    await page.waitForSelector('main form [name="Phone"]', { timeout: 30000 });
    await page.fill('main form [name="Phone"]', '0700333333');
    await page.click('main form button[type="submit"]');
    await waitFor(page, NOTICE, 'نگه داشته شد', 60000);
    check('OFFLINE-EDIT', 'edit of a never-opened record is kept on the device (pending, not final) without the server', true);

    // ویرایش B از نسخهٔ دستگاه
    await spaGo(page, `/Customers/Edit/${idB}`);
    await page.fill('main form [name="Phone"]', '0700222222');
    await page.click('main form button[type="submit"]');
    await waitFor(page, NOTICE, 'نگه داشته شد', 60000);

    // غیرفعال‌سازی A از فهرست: اول دیالوگ تأیید مشعل، سپس نگه‌داشتن در دستگاه
    await spaGo(page, '/Customers');
    await page.evaluate((rowId) => {
      const form = [...document.querySelectorAll('main form')].find((f) => /\/customers\/toggleactive\/(\d+)/i.exec(f.getAttribute('action') || '')?.[1] === rowId);
      form.requestSubmit();
    }, idA);
    await page.waitForSelector('[data-ptg-confirm-dialog]:not([hidden]), [data-ptg-confirm-dialog].show, [data-ptg-confirm-dialog][open]', { timeout: 15000 });
    check('CONFIRM-FIRST', 'the same confirmation dialog appears before anything is kept', true);
    await page.click('[data-ptg-confirm-dialog] [data-ptg-confirm-ok]');
    await waitFor(page, NOTICE, 'نگه داشته شد', 60000);
    const pendingBadges = await page.$$eval('main [data-offline-pending-mark]', (els) => els.map((e) => e.innerText));
    check('PENDING-MARK', 'list rows of records with unsynced changes show “در انتظار همگام‌سازی” (never shown as final)',
      pendingBadges.length >= 2 && pendingBadges.every((t) => t.includes('در انتظار')), `${pendingBadges.length} marks`);
    check('NOTHING-SENT', 'nothing reached the server while it was down',
      sql(`SELECT count(*) FROM "OfflineSyncOperations";`) === '0' && sql(`SELECT "IsActive" FROM "Customers" WHERE "Id"=${idA};`) === 't');

    // کاربر دیگری در همین فاصله B را تغییر داد (مستقیم در دیتابیس، بدون کاربر جاری).
    sql(`UPDATE "Customers" SET "Notes"='تغییر کاربر دیگر', "UpdatedAtUtc"=now(), "UpdatedByUserId"=NULL WHERE "Id"=${idB};`);

    // ---- بازگشت سرور: همگام‌سازی خودکار با همان اکشن‌ها
    await startServer();
    await page.evaluate(() => window.dispatchEvent(new Event('online')));
    const synced = await pollSql(`SELECT count(*) FROM "OfflineSyncOperations" WHERE "Status" IN (2,3);`, '3', 150000);
    const statuses = sql(`SELECT string_agg("Status"::text || ':' || coalesce("StatusNote",''), ' | ') FROM "OfflineSyncOperations";`);
    console.log('   server statuses:', statuses);
    check('AUTO-SYNC', 'queued record actions were sent automatically after reconnect',
      sql(`SELECT "Phone" FROM "Customers" WHERE "Id"=${idC};`) === '0700333333'
      && sql(`SELECT "IsActive" FROM "Customers" WHERE "Id"=${idA};`) === 'f', `final ops ${synced}`);
    check('STALE-CONFLICT', 'record changed by another user meanwhile is not overwritten and is kept for review',
      sql(`SELECT coalesce("Phone",'') FROM "Customers" WHERE "Id"=${idB};`) !== '0700222222'
      && /کاربر دیگری/.test(sql(`SELECT coalesce(string_agg("StatusNote", ' '),'') FROM "OfflineSyncOperations" WHERE "EntityId"='${idB}';`)));
    check('ONCE', 'each action applied exactly once',
      sql(`SELECT count(*) FROM "ProcessedFormTokens" WHERE "Token" LIKE 'offline:%';`) === '2');
    check('NO-JS-ERRORS', 'no uncaught page errors', errors.length === 0, errors.join(' | '));
    console.log('\nrecord actions E2E passed.');
  } catch (error) {
    console.error(error);
    console.error(log.join('').slice(-3000));
    process.exitCode = 1;
  } finally {
    try { if (context) await context.close(); } catch { /* ignore */ }
    await stopServer().catch(() => {});
    try { dropDatabase(); } catch (e) { console.error('drop failed', e.message); }
    fs.rmSync(userDataDir, { recursive: true, force: true });
  }
})();
