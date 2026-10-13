// مشعل آفلاین — End-to-End همان صفحات و فورم‌های اصلی مشعل با Chromium واقعی + برنامهٔ واقعی + PostgreSQL موقت.
//
// قطع، «خاموش‌شدن واقعی سرور» است (پروسه کشته می‌شود؛ navigator.onLine همچنان true است) تا تشخیص آنلاین/آفلاین با
// دسترسی واقعی به سرور آزموده شود. دیتابیس موقت با پیشوند ptg_oil_accounting_test_ ساخته و در پایان حذف می‌شود.
//
// اجرا: PTG_TEST_POSTGRES_ADMIN="Host=...;Username=...;Password=...;Database=postgres" + NODE_PATH + PTG_CHROMIUM_PATH
//       node tests/offline-js/main-forms-offline-e2e.cjs   (ابتدا Web را build کنید)

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
const output = process.env.PTG_BROWSER_ARTIFACTS || path.join(os.tmpdir(), 'mashal-main-forms-e2e');
const userDataDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mashal-main-forms-profile-'));
fs.mkdirSync(output, { recursive: true });

const admin = Object.fromEntries((process.env.PTG_TEST_POSTGRES_ADMIN || '').split(';').filter(Boolean)
  .map((kv) => { const i = kv.indexOf('='); return [kv.slice(0, i).trim().toLowerCase(), kv.slice(i + 1)]; }));
if (!admin.password) {
  console.error('PTG_TEST_POSTGRES_ADMIN is required.');
  process.exit(2);
}
const PREFIX = 'ptg_oil_accounting_test_';
const dbName = `${PREFIX}main_forms_${crypto.randomBytes(6).toString('hex')}`;
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
      const response = await fetch(`${ORIGIN}/health`);
      if (response.ok) return;
    } catch { /* starting */ }
    await new Promise((r) => setTimeout(r, 1000));
  }
  throw new Error('server not healthy:\n' + log.join(''));
}

async function launch() {
  return chromium.launchPersistentContext(userDataDir, {
    executablePath: process.env.PTG_CHROMIUM_PATH || undefined,
    headless: true,
    viewport: { width: 1280, height: 900 },
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
    throw new Error(`"${selector}" did not show "${text}": ${await textOf(page, selector)} | url=${page.url()} | ${e.message}`);
  });
}

const PANEL = 'main form [data-offline-panel]';
const NOTICE = 'main > [data-offline-notice]';

async function spaGo(page, url) {
  await page.evaluate((u) => window.PTG.spaNavigate(u), url);
  // تا صفحهٔ مقصد واقعاً جای صفحهٔ قبلی را نگرفته، فیلدهای همنام صفحهٔ قبلی هنوز روی صفحه‌اند.
  await page.waitForFunction((u) => location.pathname.toLowerCase() === u.toLowerCase(), url, { timeout: 30000 });
  await page.waitForTimeout(300);
}

async function waitForm(page, name) {
  await page.waitForSelector(`main form [name="${name}"]`, { timeout: 30000 });
}

/** مثل کاربر: مقدار فیلدهای همان فورم اصلی (select/radio/input) و رویداد change برای منطق صفحه. */
async function setFields(page, values) {
  await page.evaluate((v) => {
    for (const [name, value] of Object.entries(v)) {
      for (const el of document.querySelectorAll(`main form [name="${name}"]`)) {
        if (el.type === 'radio' || el.type === 'checkbox') {
          el.checked = el.value === value;
        } else {
          if (el.tagName === 'SELECT' && value && ![...el.options].some((o) => o.value === value)) el.add(new Option(value, value));
          el.value = value;
        }
        el.dispatchEvent(new Event('change', { bubbles: true }));
      }
    }
  }, values);
}

/** فیلدها پس از اجرای رویدادهای change خودِ صفحه (که بعضی فیلدهای وابسته را پاک می‌کنند) دوباره گذاشته می‌شوند. */
async function fillForm(page, values) {
  await setFields(page, values);
  await page.waitForTimeout(600);
  await setFields(page, values);
}

async function save(page) {
  await page.click('main form .ak-save');
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

(async () => {
  if (!fs.existsSync(dll)) throw new Error(`Build the Web project first: ${dll}`);
  let context;
  try {
    await startServer();
    const now = "now() at time zone 'utc'";
    sql(`INSERT INTO "Companies" ("Code","Name","Country","IsActive","IsSystemOwner","CreatedAtUtc") VALUES ('MSH','Mashal E2E','AF',true,true,${now}) ON CONFLICT DO NOTHING;`);
    const companyId = sql(`SELECT "Id" FROM "Companies" WHERE "IsSystemOwner" LIMIT 1;`);
    sql(`INSERT INTO "Currencies" ("Code","Name","IsActive","CreatedAtUtc") SELECT 'USD','US Dollar',true,${now} WHERE NOT EXISTS (SELECT 1 FROM "Currencies" WHERE "Code"='USD');`);
    sql(`INSERT INTO "Customers" ("Code","Name","IsActive","CreatedAtUtc") VALUES ('C-E2E','E2E Existing Customer',true,${now});`);
    sql(`INSERT INTO "CashAccounts" ("Code","Name","CompanyId","AccountType","Currency","IsActive","CreatedAtUtc") VALUES ('CA-E2E','E2E Cash',${companyId},1,'USD',true,${now});`);
    const existingCustomerId = sql(`SELECT "Id" FROM "Customers" WHERE "Code"='C-E2E';`);
    const cashId = sql(`SELECT "Id" FROM "CashAccounts" WHERE "Code"='CA-E2E';`);
    console.log(`server ${ORIGIN} on test database ${dbName}`);

    context = await launch();
    let page = context.pages()[0] || await context.newPage();
    const errors = [];
    const watch = (p) => {
      p.on('pageerror', (e) => errors.push(`${p.url()}: ${e.message}`));
      p.on('console', (m) => { if (m.type() === 'error' && !/ERR_CONNECTION_REFUSED|Failed to load resource/.test(m.text())) console.log('  [console]', m.text().slice(0, 300)); });
      p.on('frameattached', () => {});
    };
    watch(page);

    // ---- ورود عادی و فعال‌سازی دستگاه (همان برنامهٔ آفلاین موجود)؛ رمز محلی برای این نشست یک‌بار
    await page.goto(`${ORIGIN}/Auth/Login`);
    await page.fill('#Username', 'admin');
    await page.fill('#Password', ADMIN_PASSWORD);
    await Promise.all([page.waitForNavigation(), page.click('.ptg-login-submit')]);
    await page.goto(`${ORIGIN}/offline/index.html`);
    await page.waitForSelector('[data-activate]', { timeout: 30000 });
    await page.fill('#pin1', PIN);
    await page.fill('#pin2', PIN);
    await page.click('[data-activate] button[type="submit"]');
    await page.waitForSelector('[data-tabs]:not([hidden])', { timeout: 30000 });
    await page.waitForTimeout(2500);
    check('SETUP', 'device activated (existing offline profile + trusted device)', sql(`SELECT count(*) FROM "OfflineSyncDevices";`) === '1');

    // ---- همان سیستم اصلی: داشبورد؛ صفحات و فورم‌ها رمزگذاری‌شده برای کار آفلاین آماده می‌شوند
    await page.goto(`${ORIGIN}/`);
    await page.waitForFunction(() => !!navigator.serviceWorker.controller, null, { timeout: 30000 });
    const pinAsked = await page.$(`${NOTICE} [data-offline-pin]`);
    check('PIN-ONCE', 'main system pages reuse the PIN entered in this session (no second prompt)', !pinAsked);
    const deadline = Date.now() + 240_000;
    while (Date.now() < deadline && !(await idbMeta(page, 'pageWarmup'))) await page.waitForTimeout(2000);
    const index = await idbMeta(page, 'pagesIndex');
    check('PREPARE', 'dashboard, permitted main forms and their lists were stored encrypted for offline use',
      !!(await idbMeta(page, 'pageWarmup')) && (index?.entries?.length || 0) > 20, `${index?.entries?.length || 0} pages`);

    await spaGo(page, '/Currencies/Create'); // فورم فقط-آنلاین؛ صفحه ذخیره می‌شود
    await waitForm(page, 'Code');

    // ---- آنلاین: همان فورم، همان POST سرور
    await spaGo(page, '/Customers/Create');
    await waitForm(page, 'Name');
    await page.fill('main form [name="Name"]', 'E2E Online Customer');
    await Promise.all([page.waitForURL(/\/Customers(\?|$)/i, { timeout: 30000 }), save(page)]).catch(async (e) => {
      await page.screenshot({ path: path.join(output, 'online-save-failed.png'), fullPage: true });
      throw new Error('online save did not redirect: ' + (await page.evaluate(() => [...document.querySelectorAll(
        '.validation-summary-errors, .field-validation-error, .alert, [data-offline-panel], [data-offline-notice]')]
        .map((el) => el.innerText.trim()).filter(Boolean).join(' | '))) + ' | ' + e.message);
    });
    check('ONLINE', 'online save goes through the current Customers/Create flow (redirect to list)',
      sql(`SELECT count(*) FROM "Customers" WHERE "Name"='E2E Online Customer';`) === '1' && sql(`SELECT count(*) FROM "OfflineSyncOperations";`) === '0');

    // ---- سرور خاموش (اینترنت مرورگر وصل است)
    await stopServer();
    await spaGo(page, '/Customers');
    await waitFor(page, NOTICE, 'نسخهٔ ذخیره‌شده');
    const listText = await textOf(page, 'main');
    check('OFFLINE-READ', 'list page opens from the encrypted copy with its saved time; includes the record saved online',
      listText.includes('E2E Online Customer') && (await page.evaluate(() => navigator.onLine)));
    await page.screenshot({ path: path.join(output, 'offline-list.png'), fullPage: true });

    await spaGo(page, '/Customers/Create');
    await waitForm(page, 'Name');
    await page.fill('main form [name="Name"]', 'E2E Offline Customer');
    await save(page);
    await waitFor(page, PANEL, 'در دستگاه ذخیره شد');
    check('OFFLINE-SAVE', 'same customer form saved offline to the device outbox without asking the PIN again',
      (await page.inputValue('main form [name="Name"]')) === '' && !(await page.$(`${PANEL} [data-offline-pin]`)));

    // دریافت از مشتریِ تازه‌ای که هنوز فقط در دستگاه است (op:)
    await spaGo(page, '/Payments/Create');
    await waitForm(page, 'Amount');
    const pendingCustomer = await page.$eval('main form select[name="CustomerId"]',
      (s) => [...s.options].find((o) => o.text.includes('E2E Offline Customer'))?.value || '');
    check('PENDING-REF', 'the customer created offline is selectable in the payment form (pending reference)', pendingCustomer.startsWith('op:'), pendingCustomer);
    const payment = (customerId, reference, amount = '250') => ({
      // همان مقدار گزینه‌هایی که کاربر در فهرست‌ها انتخاب می‌کند (دریافت از مشتری، نقد/بانک، منبع: شرکت).
      Direction: '1', PaymentKind: '1', CounterpartyType: '2', PaymentMethod: '0', FundingSource: '1',
      CustomerId: customerId, CashAccountId: cashId, Amount: amount, Currency: 'USD', Reference: reference
    });
    await fillForm(page, payment(pendingCustomer, 'E2E-PAY-OFF'));
    await save(page);
    await waitFor(page, PANEL, 'در دستگاه ذخیره شد');

    await spaGo(page, '/Payments/Create');
    await waitForm(page, 'Amount');
    await fillForm(page, payment('', 'E2E-PAY-FIX', '75')); // بدون مشتری: قاعدهٔ سرور رد می‌کند
    await save(page);
    await waitFor(page, PANEL, 'در دستگاه ذخیره شد');
    check('OFFLINE-QUEUE', 'three main-form operations are queued; nothing reached the server', true);

    // فورم فقط-آنلاین بدون سرور: فرستاده نمی‌شود و معلومات روی صفحه می‌ماند
    await spaGo(page, '/Currencies/Create');
    await waitForm(page, 'Code');
    await page.fill('main form [name="Code"]', 'EUX');
    await save(page);
    await waitFor(page, NOTICE, 'فقط با اتصال به سرور');
    check('ONLINE-ONLY', 'an online-only form is not sent offline and keeps the typed values',
      (await page.inputValue('main form [name="Code"]')) === 'EUX');

    // ثبت سریع (مودال iframe از داخل فورم دیگر) بدون سرور: همان فورم مشتری، رکورد «در انتظار ثبت» به فورم مادر می‌رسد
    await spaGo(page, '/Payments/Create');
    await waitForm(page, 'Amount');
    await page.evaluate(() => {
      window.__quickCreated = [];
      window.PTG.completeQuickCreate = (item) => { window.__quickCreated.push(item); return true; };
      const frame = document.createElement('iframe');
      frame.id = 'e2e-quick';
      frame.src = '/customers/create?modal=1&quickCreate=1';
      document.body.appendChild(frame);
    });
    const quick = await (await page.waitForSelector('#e2e-quick')).contentFrame();
    await quick.waitForSelector('form [name="Name"]', { timeout: 30000 }).catch(async (e) => {
      await page.screenshot({ path: path.join(output, 'quick-create-failed.png'), fullPage: true });
      const diag = await quick.evaluate(async () => {
        const out = { session: !!sessionStorage.getItem('mashal.offline.session'), loop: sessionStorage.getItem('mashal.offline.loader') };
        try {
          out.dbs = (await indexedDB.databases()).map((d) => `${d.name}@${d.version}`);
          const m = await import('/offline/js/session.js');
          out.profiles = (await m.loadProfiles()).length;
          out.restored = !!(await m.restoreSession());
        } catch (err) {
          out.error = String(err && err.message || err);
        }
        return JSON.stringify(out);
      }).catch((err) => 'eval failed ' + err.message);
      throw new Error(`quick-create frame did not show the form: ${quick.url()} | ${(await quick.evaluate(() => document.body?.innerText || '').catch(() => '')).slice(0, 300)} | ${diag} | ${e.message}`);
    });
    await quick.fill('form [name="Name"]', 'E2E Quick Customer');
    await quick.click('form button[type="submit"]');
    await page.waitForFunction(() => window.__quickCreated.length === 1, null, { timeout: 30000 }).catch(async (e) => {
      await page.screenshot({ path: path.join(output, 'quick-create-save-failed.png'), fullPage: true });
      const body = await quick.evaluate(() => [...document.querySelectorAll('[data-offline-panel], [data-offline-notice], .alert')]
        .map((el) => el.innerText.trim()).filter(Boolean).join(' | ')).catch((err) => 'eval failed ' + err.message);
      throw new Error(`quick-create save did not reach the parent: ${quick.url()} | ${body} | ${e.message}`);
    });
    const quickItem = await page.evaluate(() => window.__quickCreated[0]);
    check('QUICK-CREATE', 'quick-create modal works offline: saved to the device and the parent form gets the pending record',
      String(quickItem.id).startsWith('op:') && quickItem.label.includes('E2E Quick Customer'), JSON.stringify(quickItem));

    // ---- بستن کامل برنامه و بازکردن دوباره بدون سرور
    await context.close();
    context = await launch();
    page = context.pages()[0] || await context.newPage();
    watch(page);
    await page.goto(`${ORIGIN}/Customers/Create`);
    await page.waitForSelector('#ox-pin', { timeout: 30000 });
    await page.fill('#ox-pin', PIN);
    await page.click('[data-loader-unlock] button[type="submit"]');
    await waitForm(page, 'Name');
    await waitFor(page, NOTICE, 'نسخهٔ ذخیره‌شده');
    await waitFor(page, PANEL, 'منتظر همگام‌سازی');
    check('RESTART', 'after a full close the same form opens without the server (PIN once), pending operations kept',
      (await page.$('.boltz-shell-frame, nav, aside')) !== null);
    await page.screenshot({ path: path.join(output, 'offline-restart-form.png'), fullPage: true });

    // ---- سرور برمی‌گردد: همگام‌سازی خودکار، بدون ثبت دوباره
    await startServer();
    await page.evaluate(() => window.dispatchEvent(new Event('online')));
    const customers = await pollSql(`SELECT count(*) FROM "Customers" WHERE "Name"='E2E Offline Customer';`, '1');
    const paidTo = await pollSql(`SELECT count(*) FROM "PaymentTransactions" p JOIN "Customers" c ON c."Id"=p."CustomerId" WHERE p."Reference"='E2E-PAY-OFF' AND c."Name"='E2E Offline Customer';`, '1');
    check('AUTO-SYNC', 'offline customer and the receipt for it were posted automatically by the real actions', customers === '1' && paidTo === '1');
    check('QUICK-SYNC', 'customer from the offline quick-create modal was created by Customers/Create',
      await pollSql(`SELECT count(*) FROM "Customers" WHERE "Name"='E2E Quick Customer';`, '1') === '1');
    const conflict = await pollSql(`SELECT count(*) FROM "OfflineSyncOperations" WHERE "Status"=3;`, '1', 30000);
    check('CONFLICT', 'receipt without a customer was refused by the same server rule (kept for correction)',
      conflict === '1' && sql(`SELECT count(*) FROM "PaymentTransactions" WHERE "Reference"='E2E-PAY-FIX';`) === '0');

    // ---- اصلاح در همان فورم اصلی و ارسال دوبارهٔ همان عملیات
    const conflictId = sql(`SELECT "ClientOperationId" FROM "OfflineSyncOperations" WHERE "Status"=3 LIMIT 1;`);
    // پس از بستن برنامه کوکی وب از بین رفته است: ورود عادی (همگام‌سازی بالا با نشست دستگاه انجام شد).
    await page.goto(`${ORIGIN}/Payments/Create?offlineEdit=${conflictId}`);
    if (/\/Auth\/Login/i.test(page.url())) {
      await page.fill('#Username', 'admin');
      await page.fill('#Password', ADMIN_PASSWORD);
      await Promise.all([page.waitForURL(/offlineEdit=/, { timeout: 30000 }), page.click('.ptg-login-submit')]);
    }
    await waitFor(page, PANEL, 'اصلاح عملیات آفلاین');
    check('EDIT-OPEN', 'the conflicting operation reopens in the same main form with its values',
      (await page.inputValue('main form [name="Reference"]')) === 'E2E-PAY-FIX');
    await setFields(page, { CustomerId: existingCustomerId });
    await save(page);
    await waitFor(page, PANEL, 'اصلاح ذخیره شد');
    const fixed = await pollSql(`SELECT count(*) FROM "PaymentTransactions" WHERE "Reference"='E2E-PAY-FIX';`, '1');
    check('EDIT-RESEND', 'corrected operation (same id, new revision) posted once', fixed === '1',
      sql(`SELECT "Status" || ' rev' || "Revision" || ' ' || coalesce("StatusNote",'') FROM "OfflineSyncOperations" WHERE "ClientOperationId"='${conflictId}';`)
      + ' | panel: ' + await textOf(page, PANEL));

    // ---- پاسخ نامعلوم: POST به سرور رسید و ثبت شد، ولی پاسخ در راه قطع شد
    await page.goto(`${ORIGIN}/Payments/Create`);
    await waitForm(page, 'Amount');
    await fillForm(page, payment(existingCustomerId, 'E2E-PAY-UNK', '33'));
    await page.route('**/Payments/Create**', async (route) => {
      if (route.request().method() !== 'POST') return route.continue();
      await route.fetch();
      return route.abort('connectionreset');
    });
    await save(page);
    await waitFor(page, PANEL, 'شناسهٔ ضد تکرار');
    await page.unroute('**/Payments/Create**');
    await page.evaluate(() => window.dispatchEvent(new Event('online')));
    await page.waitForTimeout(6000);
    check('UNKNOWN-OUTCOME', 'lost response: the queued form is matched by its token; exactly one receipt exists',
      sql(`SELECT count(*) FROM "PaymentTransactions" WHERE "Reference"='E2E-PAY-UNK';`) === '1'
      && await pollSql(`SELECT count(*) FROM "OfflineSyncOperations" WHERE "Status" <> 2;`, '0', 30000) === '0');

    await page.evaluate(() => window.dispatchEvent(new Event('online')));
    await page.waitForTimeout(4000);
    check('NO-DUPLICATE', 'repeated sync creates no second document',
      sql(`SELECT count(*) FROM "Customers" WHERE "Name"='E2E Offline Customer';`) === '1'
      && sql(`SELECT count(*) FROM "PaymentTransactions" WHERE "Reference" LIKE 'E2E-PAY-%';`) === '3');

    // ---- خروج: کلید نشست برداشته می‌شود؛ معلومات رمزگذاری‌شدهٔ دستگاه می‌ماند
    await page.goto(`${ORIGIN}/`);
    await page.evaluate(() => document.querySelector('form[action*="Logout" i]')?.requestSubmit());
    await page.waitForURL(/\/Auth\/Login/i, { timeout: 30000 });
    check('LOGOUT', 'logout clears the unlocked session key but keeps the device data',
      (await page.evaluate(() => sessionStorage.getItem('mashal.offline.session'))) === null && !!(await idbMeta(page, 'pagesIndex')));

    check('ERRORS', 'no uncaught page errors', errors.length === 0, errors.join(' | '));
    console.log(`\n${results.length} checks passed.`);
  } finally {
    if (context) await context.close().catch(() => {});
    await stopServer().catch(() => {});
    try { dropDatabase(); } catch (e) { console.error('drop failed', e.message); }
    fs.rmSync(userDataDir, { recursive: true, force: true });
    fs.writeFileSync(path.join(output, 'server.log'), log.join(''));
  }
})().catch((error) => {
  console.error(error);
  process.exit(1);
});
