// مشعل آفلاین — End-to-End کامل: Chromium واقعی + برنامهٔ واقعی مشعل (همان Program.cs) + PostgreSQL موقت.
//
// - دیتابیس موقت با پیشوند محافظت‌شدهٔ ptg_oil_accounting_test_ ساخته، Migration فقط روی همان اجرا و در پایان حذف می‌شود.
// - ورود، فعال‌سازی دستگاه، قطع اینترنت، ثبت آفلاین، بستن کامل مرورگر، اتصال و ثبت قطعی خودکار همه واقعی‌اند.
// - سند واقعی (روزنامچه، مشتری) و پیام واقعی کنترل موجودی فروش از دیتابیس/سرور خوانده می‌شود.
//
// اجرا: PTG_TEST_POSTGRES_ADMIN="Host=...;Username=...;Password=...;Database=postgres" + NODE_PATH + PTG_CHROMIUM_PATH
//       node tests/offline-js/offline-real-e2e.cjs   (ابتدا Web را build کنید)

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
const output = process.env.PTG_BROWSER_ARTIFACTS || path.join(os.tmpdir(), 'mashal-offline-real-e2e');
const userDataDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mashal-offline-real-profile-'));
fs.mkdirSync(output, { recursive: true });

const admin = Object.fromEntries((process.env.PTG_TEST_POSTGRES_ADMIN || '').split(';').filter(Boolean)
  .map((kv) => { const i = kv.indexOf('='); return [kv.slice(0, i).trim().toLowerCase(), kv.slice(i + 1)]; }));
if (!admin.password) {
  console.error('PTG_TEST_POSTGRES_ADMIN is required.');
  process.exit(2);
}
const PREFIX = 'ptg_oil_accounting_test_';
const dbName = `${PREFIX}offline_e2e_${crypto.randomBytes(6).toString('hex')}`;
const port = 5900 + Math.floor(Math.random() * 90);
const ORIGIN = `http://localhost:${port}`;
const ADMIN_PASSWORD = 'Offline-E2E-Admin-2026!';
const PIN = 'kabul-e2e-2026';

function sql(query, database = dbName) {
  if (!database.startsWith(PREFIX)) throw new Error('refusing to touch a non-test database');
  return execFileSync(psql, ['-h', admin.host || 'localhost', '-p', admin.port || '5432', '-U', admin.username || 'postgres',
    '-d', database, '-v', 'ON_ERROR_STOP=1', '-At', '-c', query],
  { env: { ...process.env, PGPASSWORD: admin.password }, encoding: 'utf8' }).trim();
}

function dropDatabase() {
  execFileSync(psql, ['-h', admin.host || 'localhost', '-p', admin.port || '5432', '-U', admin.username || 'postgres',
    '-d', 'postgres', '-c', `DROP DATABASE IF EXISTS "${dbName}" WITH (FORCE)`],
  { env: { ...process.env, PGPASSWORD: admin.password }, encoding: 'utf8' });
}

const results = [];
function check(id, name, ok, detail = '') {
  results.push({ id, name, ok });
  console.log(`${ok ? 'PASS' : 'FAIL'} ${id} ${name}${detail ? ' — ' + detail : ''}`);
  if (!ok) throw new Error(`${id} failed: ${name} ${detail}`);
}

async function waitHealthy(server, log) {
  const deadline = Date.now() + 6 * 60_000;
  while (Date.now() < deadline) {
    if (server.exitCode !== null) throw new Error('server exited:\n' + log.join(''));
    try {
      const response = await fetch(`${ORIGIN}/health`);
      if (response.ok) return;
    } catch { /* starting */ }
    await new Promise((r) => setTimeout(r, 1000));
  }
  throw new Error('server not healthy:\n' + log.join(''));
}

function seed() {
  const now = "now() at time zone 'utc'";
  sql(`INSERT INTO "Companies" ("Code","Name","Country","IsActive","IsSystemOwner","CreatedAtUtc") VALUES ('MSH','Mashal E2E','AF',true,true,${now}) ON CONFLICT DO NOTHING;`);
  const companyId = sql(`SELECT "Id" FROM "Companies" WHERE "IsSystemOwner" LIMIT 1;`);
  sql(`INSERT INTO "Currencies" ("Code","Name","IsActive","CreatedAtUtc") SELECT 'USD','US Dollar',true,${now} WHERE NOT EXISTS (SELECT 1 FROM "Currencies" WHERE "Code"='USD');`);
  sql(`INSERT INTO "Customers" ("Code","Name","IsActive","CreatedAtUtc") VALUES ('C-E2E','E2E Customer',true,${now});`);
  sql(`INSERT INTO "Products" ("Code","Name","UnitOfMeasure","IsActive","CreatedAtUtc") VALUES ('DSL-E2E','Diesel E2E','MT',true,${now});`);
  sql(`INSERT INTO "Terminals" ("Code","Name","IsActive","CreatedAtUtc") VALUES ('T-E2E','E2E Terminal',true,${now});`);
  sql(`INSERT INTO "StorageTanks" ("TerminalId","TankCode","ProductId","CapacityMt","IsActive","CreatedAtUtc")
       SELECT t."Id",'TK-E2E',p."Id",5000,true,${now} FROM "Terminals" t, "Products" p WHERE t."Code"='T-E2E' AND p."Code"='DSL-E2E';`);
  sql(`INSERT INTO "CashAccounts" ("Code","Name","CompanyId","AccountType","Currency","IsActive","CreatedAtUtc") VALUES ('CA-E2E','E2E Cash',${companyId},1,'USD',true,${now});`);
}

async function launch() {
  return chromium.launchPersistentContext(userDataDir, {
    executablePath: process.env.PTG_CHROMIUM_PATH || undefined,
    headless: true,
    viewport: { width: 390, height: 844 },
    locale: 'fa-AF',
    serviceWorkers: 'allow'
  });
}

async function waitText(page, text, timeout = 20000) {
  await page.waitForFunction((t) => document.body.innerText.includes(t), text, { timeout });
}

async function unlock(page) {
  await page.waitForSelector('[data-unlock] #pin', { timeout: 20000 });
  await page.fill('#pin', PIN);
  await page.click('[data-unlock] button[type="submit"]');
  await page.waitForSelector('[data-tabs]:not([hidden])', { timeout: 30000 });
}

async function newOperation(page, type, fill) {
  await page.goto(`${ORIGIN}/offline/index.html#/new/${type}`);
  if (await page.$('[data-unlock] #pin')) await unlock(page);
  await page.waitForSelector('[data-op-form]', { timeout: 20000 });
  await fill();
  await page.click('[data-op-form] button[type="submit"]');
  await waitText(page, 'در دستگاه ذخیره شد؛ در انتظار همگام‌سازی');
  return decodeURIComponent(page.url().split('#/op/')[1] || '');
}

(async () => {
  if (!fs.existsSync(dll)) throw new Error(`Build the Web project first: ${dll}`);
  const log = [];
  const server = spawn('dotnet', ['exec', dll, '--contentRoot', webDir], {
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

  let context;
  try {
    await waitHealthy(server, log);
    seed();
    console.log(`server ${ORIGIN} on test database ${dbName}`);

    context = await launch();
    let page = context.pages()[0] || await context.newPage();
    const errors = [];
    page.on('pageerror', (e) => errors.push(e.message));

    // ---- ورود واقعی به MVC
    await page.goto(`${ORIGIN}/Auth/Login`);
    await page.fill('#Username', 'admin');
    await page.fill('#Password', ADMIN_PASSWORD);
    await Promise.all([page.waitForNavigation(), page.click('.ptg-login-submit')]);
    const shell = await page.evaluate(() => ({
      manifest: !!document.querySelector('link[rel="manifest"]'),
      chip: !!document.querySelector('[data-offline-chip]')
    }));
    check('MVC', 'real MVC page after login has manifest link and offline chip', shell.manifest && shell.chip);

    // ---- فعال‌سازی دستگاه
    await page.goto(`${ORIGIN}/offline/index.html`);
    await page.waitForSelector('[data-activate]', { timeout: 30000 });
    await page.fill('#pin1', PIN);
    await page.fill('#pin2', PIN);
    await page.click('[data-activate] button[type="submit"]');
    await page.waitForSelector('[data-tabs]:not([hidden])', { timeout: 30000 }).catch(async (e) => {
      await page.screenshot({ path: path.join(output, 'activation-failed.png'), fullPage: true });
      throw new Error('activation failed: ' + (await page.evaluate(() => document.body.innerText)).slice(0, 600) + ' | ' + errors.join(' | '));
    });
    await page.waitForFunction(() => !!navigator.serviceWorker.controller || true);
    await page.reload();
    await page.waitForFunction(() => !!navigator.serviceWorker.controller, null, { timeout: 20000 });
    await unlock(page);
    await page.click('[data-sync-now]');
    await page.waitForTimeout(3000);
    check('ACTIVATE', 'device activated against the real server; service worker controls the page', true);

    // ---- قطع اینترنت و ثبت آفلاین
    await context.setOffline(true);
    await page.goto(`${ORIGIN}/?source=pwa`);
    await unlock(page);
    await waitText(page, 'آفلاین');

    const receiptId = await newOperation(page, 'CustomerReceipt.Request', async () => {
      await page.selectOption('#f-customerId', { index: 1 });
      await page.selectOption('#f-cashAccountId', { index: 1 });
      await page.fill('#f-amount', '250');
      await page.selectOption('#f-currency', 'USD');
      await page.fill('#f-reference', 'E2E-R1');
    });
    const customerOpId = await newOperation(page, 'Customer.Draft', async () => {
      await page.fill('#f-name', 'E2E Offline Customer');
    });
    const saleId = await newOperation(page, 'Sale.Request', async () => {
      await page.selectOption('#f-customerId', { index: 1 });
      await page.selectOption('#f-productId', { index: 1 });
      await page.selectOption('#f-tankId', { index: 1 });
      await page.fill('#f-quantity', '5');
      await page.fill('#f-unitPrice', '700');
      await page.selectOption('#f-currency', 'USD');
      await page.fill('#f-reference', 'E2E-S1');
    });
    check('TEST 05', 'three operations saved offline; nothing reached the server',
      sql(`SELECT count(*) FROM "OfflineSyncOperations";`) === '0');

    // ---- بستن کامل مرورگر و بازکردن دوباره (هنوز آفلاین)
    await context.close();
    context = await launch();
    await context.setOffline(true);
    page = context.pages()[0] || await context.newPage();
    page.on('pageerror', (e) => errors.push(e.message));
    await page.goto(`${ORIGIN}/offline/index.html#/ops`);
    await unlock(page);
    await waitText(page, 'در انتظار ارسال');
    check('TEST 06/07 (simulated)', 'pending operations survive full browser close/reopen offline', true);

    // ---- اتصال: ارسال و ثبت قطعی خودکار با اکشن‌های واقعی
    await context.setOffline(false);
    await page.evaluate(() => window.dispatchEvent(new Event('online')));
    const deadline = Date.now() + 60_000;
    let statuses = '';
    while (Date.now() < deadline) {
      statuses = sql(`SELECT string_agg("OperationType" || '=' || "Status", ',' ORDER BY "Id") FROM "OfflineSyncOperations";`);
      if (statuses && statuses.split(',').length === 3 && !statuses.includes('=1') && !statuses.includes('=5')) break;
      await page.waitForTimeout(1500);
    }
    console.log('server statuses:', statuses);
    const payments = sql(`SELECT count(*) FROM "PaymentTransactions" WHERE "Reference"='E2E-R1';`);
    const customers = sql(`SELECT count(*) FROM "Customers" WHERE "Name"='E2E Offline Customer';`);
    const sales = sql(`SELECT count(*) FROM "SalesTransactions";`);
    check('TEST 10', 'customer receipt posted automatically by Payments/Create (exactly one document)', payments === '1');
    check('TEST 10', 'new customer created automatically by Customers/Create', customers === '1');
    check('TEST 14/20', 'sale without tank stock was NOT posted (Sales/Create stock check)', sales === '0');

    await page.goto(`${ORIGIN}/offline/index.html#/op/${encodeURIComponent(receiptId)}`);
    await waitText(page, 'همگام‌شد؛ در سیستم ثبت قطعی شد', 30000);
    await page.goto(`${ORIGIN}/offline/index.html#/op/${encodeURIComponent(saleId)}`);
    await waitText(page, 'ثبت نشد؛ نیازمند اصلاح', 30000);
    await waitText(page, 'موجودی');
    await page.screenshot({ path: path.join(output, 'real-conflict-390.png'), fullPage: true });
    check('§6', 'stock conflict message from the real Sales controller is shown on the device', true);

    // ---- ارسال تکراری: Sync دوباره سند دوم نمی‌سازد
    await page.click('[data-sync-now]');
    await page.waitForTimeout(3000);
    check('TEST 12', 'repeated sync creates no second payment',
      sql(`SELECT count(*) FROM "PaymentTransactions" WHERE "Reference"='E2E-R1';`) === '1'
      && sql(`SELECT count(*) FROM "ProcessedFormTokens" WHERE "Token" LIKE 'offline:%';`) === '2');

    check('ERRORS', 'no uncaught page errors', errors.length === 0, errors.join(' | '));
    console.log(`\n${results.length} checks passed against the real server. customerOp=${customerOpId}`);
  } finally {
    if (context) await context.close().catch(() => {});
    server.kill();
    await new Promise((r) => setTimeout(r, 1500));
    try { dropDatabase(); } catch (e) { console.error('drop failed', e.message); }
    fs.rmSync(userDataDir, { recursive: true, force: true });
    fs.writeFileSync(path.join(output, 'server.log'), log.join(''));
  }
})().catch((error) => {
  console.error(error);
  process.exit(1);
});
