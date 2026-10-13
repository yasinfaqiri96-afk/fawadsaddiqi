// پیش‌نویس خودکار فرم بارگیری — End-to-End واقعی: Chromium + برنامهٔ واقعی مشعل + کپی موقت PostgreSQL.
//
// - دیتابیس اصلی فقط با pg_dump خوانده می‌شود؛ همه‌چیز روی کپی موقت با پیشوند ptg_oil_accounting_test_ اجرا،
//   Migrationها فقط روی همان کپی اعمال و در پایان کپی حذف می‌شود.
// - دو کاربر آزمایشی فقط در کپی ساخته می‌شوند (رمز اصلی هیچ کاربری خوانده یا استفاده نمی‌شود).
//
// اجرا (پس از build وب):
//   NODE_PATH=<node_modules دارای playwright>  PTG_CHROMIUM_PATH=<chrome.exe>
//   node tests/form-drafts-e2e/form-draft-real-e2e.cjs
// متغیرهای اختیاری: PTG_PG_BIN (پوشهٔ psql/pg_dump)، PTG_PG_SOURCE_DB (پیش‌فرض ptg_oil_system)، PTG_PG_HOST/PORT/USER/PASSWORD.

const fs = require('fs');
const os = require('os');
const path = require('path');
const crypto = require('crypto');
const { spawn, execFileSync } = require('child_process');
const { chromium } = require('playwright');

const repo = path.resolve(__dirname, '../..');
const webDir = path.join(repo, 'src', 'PTGOilSystem.Web');
const dll = path.join(webDir, 'bin', 'Debug', 'net8.0', 'PTGOilSystem.Web.dll');
const pgBin = process.env.PTG_PG_BIN || 'C:/Program Files/PostgreSQL/18/bin';
const pg = {
  host: process.env.PTG_PG_HOST || 'localhost',
  port: process.env.PTG_PG_PORT || '5432',
  user: process.env.PTG_PG_USER || 'postgres',
  password: process.env.PTG_PG_PASSWORD || 'postgres'
};
const SOURCE_DB = process.env.PTG_PG_SOURCE_DB || 'ptg_oil_system';
const PREFIX = 'ptg_oil_accounting_test_';
const dbName = `${PREFIX}drafts_e2e_${crypto.randomBytes(6).toString('hex')}`;
const port = 5600 + Math.floor(Math.random() * 90);
const ORIGIN = `http://localhost:${port}`;
const output = process.env.PTG_BROWSER_ARTIFACTS || path.join(os.tmpdir(), 'mashal-form-draft-e2e');
fs.mkdirSync(output, { recursive: true });

const USER_A = { username: 'draft_e2e_a', password: 'Draft-E2E-A-2026!', roleId: 1 };
const USER_B = { username: 'draft_e2e_b', password: 'Draft-E2E-B-2026!', roleId: 1 };
const pgEnv = { ...process.env, PGPASSWORD: pg.password };
const pgArgs = ['-h', pg.host, '-p', pg.port, '-U', pg.user];

function sql(query) {
  if (!dbName.startsWith(PREFIX)) throw new Error('refusing to touch a non-test database');
  return execFileSync(path.join(pgBin, 'psql.exe'), [...pgArgs, '-d', dbName, '-v', 'ON_ERROR_STOP=1', '-At', '-c', query],
    { env: pgEnv, encoding: 'utf8' }).trim();
}

function createCopy() {
  const dump = path.join(os.tmpdir(), `${dbName}.dump`);
  // فقط خواندن از دیتابیس اصلی.
  execFileSync(path.join(pgBin, 'pg_dump.exe'), [...pgArgs, '-d', SOURCE_DB, '-Fc', '-f', dump], { env: pgEnv });
  execFileSync(path.join(pgBin, 'createdb.exe'), [...pgArgs, dbName], { env: pgEnv });
  execFileSync(path.join(pgBin, 'pg_restore.exe'), [...pgArgs, '-d', dbName, '--no-owner', dump], { env: pgEnv });
  fs.rmSync(dump, { force: true });
}

function dropCopy() {
  if (!dbName.startsWith(PREFIX)) return;
  execFileSync(path.join(pgBin, 'psql.exe'), [...pgArgs, '-d', 'postgres', '-c', `DROP DATABASE IF EXISTS "${dbName}" WITH (FORCE)`], { env: pgEnv });
}

function hashPassword(password) {
  const salt = crypto.randomBytes(16);
  const hash = crypto.pbkdf2Sync(password, salt, 100000, 32, 'sha256');
  return `PBKDF2-SHA256$100000$${salt.toString('base64')}$${hash.toString('base64')}`;
}

function addUser(user) {
  sql(`INSERT INTO "Users" ("Username","FullName","PasswordHash","RoleId","IsActive","CreatedAtUtc")
       VALUES ('${user.username}','${user.username}','${hashPassword(user.password)}',${user.roleId},true,now() at time zone 'utc');`);
  return Number(sql(`SELECT "Id" FROM "Users" WHERE "Username"='${user.username}';`));
}

const results = [];
function check(id, name, ok, detail = '') {
  results.push({ id, name, ok });
  console.log(`${ok ? 'PASS' : 'FAIL'} ${id} ${name}${detail ? ' — ' + detail : ''}`);
  if (!ok) throw new Error(`${id} failed: ${name} ${detail}`);
}

async function waitHealthy(server, log) {
  const deadline = Date.now() + 8 * 60_000;
  while (Date.now() < deadline) {
    if (server.exitCode !== null) throw new Error('server exited:\n' + log.join(''));
    try {
      if ((await fetch(`${ORIGIN}/health`)).ok) return;
    } catch { /* starting */ }
    await new Promise((r) => setTimeout(r, 1000));
  }
  throw new Error('server not healthy:\n' + log.join(''));
}

async function newContext(browser) {
  return browser.newContext({ viewport: { width: 1600, height: 1000 }, locale: 'fa-AF' });
}

async function login(page, user) {
  await page.goto(`${ORIGIN}/Auth/Login`);
  await page.fill('#Username', user.username);
  await page.fill('#Password', user.password);
  await Promise.all([page.waitForNavigation(), page.click('.ptg-login-submit')]);
}

async function openLoading(page, url = '/Loading/Create') {
  await page.goto(`${ORIGIN}${url}`);
  await page.waitForSelector('[data-loading-create-form] input[data-ptg-draft]', { state: 'attached', timeout: 30000 });
}

async function waitStatus(page, text, timeout = 20000) {
  try {
    await page.waitForFunction((t) => {
      const el = document.querySelector('[data-ptg-draft-status]');
      return el && !el.hidden && el.textContent.includes(t);
    }, text, { timeout });
  } catch (error) {
    const state = await page.evaluate(() => {
      const el = document.querySelector('[data-ptg-draft-status]');
      const marker = document.querySelector('input[data-ptg-draft]');
      return { status: el ? `${el.hidden ? 'hidden' : 'shown'}:${el.textContent}:${el.title}` : 'none', key: marker && marker.value };
    }).catch(() => ({}));
    await page.screenshot({ path: path.join(output, 'wait-status-failed.png'), fullPage: false }).catch(() => {});
    throw new Error(`status "${text}" not reached at ${page.url()}: ${JSON.stringify(state)}`);
  }
}

function rowInput(page, index, field) {
  return page.locator('[data-loading-rows] tr[data-loading-row]').nth(index).locator(`input[name$=".${field}"]:not([type=hidden]):visible`).first();
}

async function rowValues(page) {
  return page.$$eval('[data-loading-rows] tr[data-loading-row]', (rows) => rows.map((row) => ({
    qty: row.querySelector('input[name$=".LoadedQuantityMt"]')?.value || '',
    consignee: row.querySelector('input[name$=".ConsigneeName"]')?.value || '',
    driver: row.querySelector('input[name$=".DriverName"]')?.value || '',
    destination: row.querySelector('input[name$=".DestinationName"]')?.value || ''
  })));
}

function businessCounts() {
  return sql(`SELECT concat_ws('|',
    (SELECT count(*) FROM "LoadingRegisters"),
    (SELECT count(*) FROM "InventoryMovements"),
    (SELECT count(*) FROM "JournalEntries"),
    (SELECT count(*) FROM "PaymentTransactions"),
    (SELECT count(*) FROM "LedgerEntries"));`);
}

function drafts(userId) {
  const raw = sql(`SELECT coalesce(json_agg(json_build_object('key',"DraftKey",'status',"Status",'rev',"Revision",'rows',"RowCount",'payload',"PayloadJson") ORDER BY "Id"), '[]') FROM "FormDrafts" WHERE "UserId"=${userId};`);
  return JSON.parse(raw);
}

function payloadValue(draft, name) {
  const pairs = JSON.parse(draft.payload);
  return pairs.filter((p) => p[0].endsWith(name)).map((p) => p[1]);
}

(async () => {
  if (!fs.existsSync(dll)) throw new Error(`Build the Web project first: ${dll}`);
  createCopy();
  const log = [];
  const server = spawn('dotnet', ['exec', dll, '--contentRoot', webDir], {
    cwd: webDir,
    env: {
      ...process.env,
      ASPNETCORE_ENVIRONMENT: 'Development',
      ASPNETCORE_URLS: ORIGIN,
      DATABASE_URL: `postgres://${pg.user}:${encodeURIComponent(pg.password)}@${pg.host}:${pg.port}/${dbName}`,
      PTG_AUTO_MIGRATE: 'true',
      PTG_DEV_AUTO_SIGNIN: 'false',
      PTG_UI_PICK: '0',
      Backup__SchedulerEnabled: 'false',
      Logging__LogLevel__Default: 'Warning'
    }
  });
  server.stdout.on('data', (d) => log.push(String(d)));
  server.stderr.on('data', (d) => log.push(String(d)));

  let browser;
  const errors = [];
  try {
    await waitHealthy(server, log);
    const userA = addUser(USER_A);
    const userB = addUser(USER_B);
    browser = await chromium.launch({ executablePath: process.env.PTG_CHROMIUM_PATH || undefined, headless: true });
    const baseline = businessCounts();

    // ---- 1. بازکردن فرم به‌تنهایی پیش‌نویس نمی‌سازد
    let context = await newContext(browser);
    let page = await context.newPage();
    page.on('pageerror', (e) => errors.push(e.message));
    await login(page, USER_A);
    await openLoading(page);
    await page.waitForTimeout(3500);
    check('T01', 'opening the form alone creates no draft', drafts(userA).length === 0);

    // ---- 2. چند سطر، ذخیرهٔ خودکار بدون دکمه و بدون درخواست برای هر کلید
    const saveRequests = [];
    page.on('request', (r) => { if (r.url().includes('/FormDrafts/Save')) saveRequests.push(Date.now()); });
    await page.click('[data-loading-contract-toggle]');
    await page.locator('[data-loading-contract-checkbox]').first().check();
    await page.click('[data-loading-contract-toggle]');
    await page.selectOption('[data-loading-transport-type]', '3');
    await rowInput(page, 0, 'LoadedQuantityMt').fill('');
    await rowInput(page, 0, 'LoadedQuantityMt').pressSequentially('25.5', { delay: 40 });
    await rowInput(page, 0, 'ConsigneeName').pressSequentially('Consignee Alpha', { delay: 40 });
    await page.click('[data-loading-row-add]');
    await rowInput(page, 1, 'LoadedQuantityMt').fill('31');
    await rowInput(page, 1, 'ConsigneeName').fill('Consignee Beta');
    await rowInput(page, 1, 'DriverName').fill('Driver Beta');
    await page.click('[data-loading-row-add]');
    await rowInput(page, 2, 'DestinationName').fill('Half row destination');
    await waitStatus(page, 'پیش‌نویس ذخیره شد');
    await page.waitForTimeout(2500);
    let list = drafts(userA);
    check('T02', 'rows auto-saved to the database without pressing save', list.length === 1 && list[0].rows === 3 && list[0].status === 1,
      JSON.stringify(list.map((d) => ({ rows: d.rows, status: d.status }))));
    check('T03', 'typing is debounced (no request per keystroke)', saveRequests.length <= 6, `${saveRequests.length} save requests for ~40 keystrokes`);
    check('T04', 'incomplete row is kept in the draft', payloadValue(list[0], '.DestinationName').includes('Half row destination'));
    const draftKey = list[0].key;
    const expectedRows = await rowValues(page);
    const selectedContract = await page.$eval('[data-loading-contract-checkbox]:checked', (c) => c.value);

    // ---- 3. Refresh و ادامهٔ پیش‌نویس
    await page.reload();
    await page.waitForSelector('[data-ptg-draft-notice]', { timeout: 20000 });
    check('T05', 'refresh offers to continue the saved draft', true);
    await page.click('[data-ptg-draft-notice] a.ak-btn:not(.ak-btn--ghost)');
    await page.waitForURL(/draft=/, { timeout: 20000 });
    await page.waitForSelector('[data-loading-rows] tr[data-loading-row]');
    let restored = await rowValues(page);
    const restoredContract = await page.$eval('[data-loading-contract-checkbox]:checked', (c) => c.value).catch(() => '');
    const restoredTransport = await page.$eval('[data-loading-transport-type]', (s) => s.value);
    check('T06', 'all rows, order and values restored after refresh',
      JSON.stringify(restored) === JSON.stringify(expectedRows), JSON.stringify(restored));
    check('T07', 'contract and transport type restored', restoredContract === selectedContract && restoredTransport === '3',
      `${restoredContract}/${restoredTransport}`);
    await waitStatus(page, 'ادامهٔ پیش‌نویس');

    // ---- 4. حذف سطر هم ذخیره می‌شود
    await page.locator('[data-loading-rows] tr[data-loading-row]').nth(1).locator('[data-loading-row-remove]').click();
    await page.waitForTimeout(300);
    await waitStatus(page, 'پیش‌نویس ذخیره شد');
    await page.locator('[data-loading-footer-actions]').screenshot({ path: path.join(output, 'loading-saved-status.png') });
    list = drafts(userA);
    check('T08', 'removing a row is saved', list.length === 1 && !payloadValue(list[0], '.ConsigneeName').includes('Consignee Beta')
      && list[0].rev >= 2, `rev=${list[0].rev}`);

    // ---- 5. بستن کامل مرورگر و ورود دوباره
    await context.close();
    context = await newContext(browser);
    page = await context.newPage();
    page.on('pageerror', (e) => errors.push(e.message));
    await login(page, USER_A);
    await openLoading(page, `/Loading/Create?draft=${draftKey}`);
    restored = await rowValues(page);
    check('T09', 'draft survives closing the browser and logging in again',
      restored.length === 2 && restored[0].qty === expectedRows[0].qty && restored[1].destination === 'Half row destination',
      JSON.stringify(restored));

    // ---- 6. دو پیش‌نویس مستقل
    const page2 = await context.newPage();
    page2.on('pageerror', (e) => errors.push(e.message));
    await openLoading(page2);
    await page2.waitForSelector('[data-ptg-draft-notice]', { timeout: 20000 });
    await page2.click('[data-ptg-draft-notice] button');
    await rowInput(page2, 0, 'ConsigneeName').fill('Second draft consignee');
    await waitStatus(page2, 'پیش‌نویس ذخیره شد');
    list = drafts(userA);
    check('T10', 'a second form becomes an independent draft', list.length === 2
      && payloadValue(list.find((d) => d.key === draftKey), '.ConsigneeName').includes('Consignee Alpha'));
    await page2.close();

    // ---- 7. دو پنجره روی یک پیش‌نویس: نسخهٔ تازه‌تر بازنویسی نمی‌شود
    const tab2 = await context.newPage();
    tab2.on('pageerror', (e) => errors.push(e.message));
    await openLoading(tab2, `/Loading/Create?draft=${draftKey}`);
    await rowInput(page, 0, 'ConsigneeName').fill('Tab one wins');
    await waitStatus(page, 'پیش‌نویس ذخیره شد');
    await rowInput(tab2, 0, 'ConsigneeName').fill('Tab two stale');
    await page.waitForTimeout(6000);
    list = drafts(userA);
    const original = list.find((d) => d.key === draftKey);
    check('T11', 'stale window does not overwrite the newer draft; its work is kept separately',
      payloadValue(original, '.ConsigneeName').includes('Tab one wins')
      && list.some((d) => d.key !== draftKey && payloadValue(d, '.ConsigneeName').includes('Tab two stale')),
      `${list.length} drafts`);
    await tab2.close();

    // ---- 8. قطع اینترنت: نسخهٔ محلی، پیام خطا و همگام‌سازی پس از اتصال
    await context.setOffline(true);
    await rowInput(page, 0, 'DriverName').fill('Offline driver');
    await waitStatus(page, 'ذخیره انجام نشد', 30000);
    const localCount = await page.evaluate(() => Object.keys(localStorage).filter((k) => k.startsWith('ptg:form-draft:v1:')).length);
    check('T12', 'offline edit is kept locally with a visible failure status', localCount >= 1);
    await context.setOffline(false);
    await page.evaluate(() => window.dispatchEvent(new Event('online')));
    await waitStatus(page, 'پیش‌نویس ذخیره شد', 30000);
    list = drafts(userA);
    check('T13', 'offline changes synced after reconnecting',
      payloadValue(list.find((d) => d.key === draftKey), '.DriverName').includes('Offline driver'));

    // ---- 9. قطع اینترنت و بستن صفحه؛ همگام‌سازی در بازدید بعدی
    await context.setOffline(true);
    await rowInput(page, 0, 'DestinationName').fill('Closed while offline');
    await page.waitForTimeout(500);
    await page.close();
    await context.setOffline(false);
    page = await context.newPage();
    page.on('pageerror', (e) => errors.push(e.message));
    await openLoading(page);
    await page.waitForTimeout(4000);
    list = drafts(userA);
    check('T14', 'work typed offline right before closing is synced on the next visit',
      payloadValue(list.find((d) => d.key === draftKey), '.DestinationName').includes('Closed while offline'));

    // ---- 10. جداسازی کاربران
    const contextB = await newContext(browser);
    const pageB = await contextB.newPage();
    await login(pageB, USER_B);
    await pageB.goto(`${ORIGIN}/FormDrafts`);
    const bListText = await pageB.innerText('main');
    check('T15', 'another user sees none of these drafts', bListText.includes('پیش‌نویسی وجود ندارد'));
    await openLoading(pageB, `/Loading/Create?draft=${draftKey}`);
    const bRows = await rowValues(pageB);
    check('T16', 'another user cannot open the draft by URL', !bRows.some((r) => r.consignee === 'Tab one wins'));
    const hijack = await pageB.evaluate(async (key) => {
      const token = document.querySelector("input[name='__RequestVerificationToken']").value;
      const res = await fetch('/FormDrafts/Save', {
        method: 'POST', credentials: 'same-origin',
        headers: { 'Content-Type': 'application/json', 'RequestVerificationToken': token },
        body: JSON.stringify({ key, formKey: 'Loading.Create', baseRevision: 99, fields: [['Notes', 'hijack']] })
      });
      return res.status;
    }, draftKey);
    list = drafts(userA);
    check('T17', 'another user cannot overwrite the draft', hijack === 409
      && !list.some((d) => d.payload.includes('hijack')), `status ${hijack}`);
    await contextB.close();

    check('T18', 'drafts changed nothing in loadings, stock, ledger, journals or payments', businessCounts() === baseline,
      `${baseline} -> ${businessCounts()}`);
    const auditSaves = sql(`SELECT count(*) FROM "AuditLogs" WHERE "RequestPath" ILIKE '/FormDrafts/Save%' AND "StatusCode" BETWEEN 200 AND 299;`);
    check('T19', 'successful autosaves do not flood the audit log (409s stay audited)', auditSaves === '0', auditSaves);

    // ---- 11. ثبت نهایی
    await openLoading(page, `/Loading/Create?draft=${draftKey}`);
    const resumeHtml = await page.content();
    const loadingsBefore = Number(sql('SELECT count(*) FROM "LoadingRegisters";'));
    // تکمیل هر فیلد الزامی سطرها با مقدار آزمایشی
    const rowCount = await page.locator('[data-loading-rows] tr[data-loading-row]').count();
    for (let i = 0; i < rowCount; i++) {
      await rowInput(page, i, 'LoadedQuantityMt').fill('10');
      const reference = page.locator('[data-loading-rows] tr[data-loading-row]').nth(i).locator('input[name$=".ImportedTransportReference"]:visible').first();
      if (await reference.count() && !(await reference.inputValue())) await reference.fill(`E2E-TRK-${i + 1}`);
      const price = rowInput(page, i, 'LoadingPriceUsd');
      if (await price.count() && !(await price.inputValue())) await price.fill('650');
    }
    const secondTab = await context.newPage();
    secondTab.on('pageerror', (e) => errors.push(e.message));
    await openLoading(secondTab, `/Loading/Create?draft=${draftKey}`);
    await page.waitForTimeout(2500);
    await Promise.all([page.waitForNavigation({ timeout: 120000 }), page.click('[data-loading-save]')]);
    const afterSubmitUrl = page.url();
    const submittedStatus = sql(`SELECT "Status" FROM "FormDrafts" WHERE "DraftKey"='${draftKey}';`);
    const loadingsAfter = Number(sql('SELECT count(*) FROM "LoadingRegisters";'));
    if (submittedStatus !== '3') {
      await page.screenshot({ path: path.join(output, 'submit-failed.png'), fullPage: true });
      const summary = await page.$eval('[data-loading-validation-summary]', (e) => e.innerText).catch(() => '');
      check('T20', 'final submit marks the draft submitted', false, `status=${submittedStatus} url=${afterSubmitUrl} ${summary}`);
    }
    check('T20', 'final submit creates the loadings and marks the draft submitted',
      submittedStatus === '3' && loadingsAfter === loadingsBefore + rowCount, `${loadingsBefore} -> ${loadingsAfter}, rows=${rowCount}`);

    // ---- 12. ثبت دوباره از پنجرهٔ دیگری که همان پیش‌نویس باز بود
    for (let i = 0; i < rowCount; i++) {
      await rowInput(secondTab, i, 'LoadedQuantityMt').fill('10');
      const reference = secondTab.locator('[data-loading-rows] tr[data-loading-row]').nth(i).locator('input[name$=".ImportedTransportReference"]:visible').first();
      if (await reference.count() && !(await reference.inputValue())) await reference.fill(`E2E-TRK-${i + 1}`);
      const price = rowInput(secondTab, i, 'LoadingPriceUsd');
      if (await price.count() && !(await price.inputValue())) await price.fill('650');
    }
    await Promise.all([secondTab.waitForNavigation({ timeout: 120000 }), secondTab.click('[data-loading-save]')]);
    const duplicateText = await secondTab.innerText('main');
    check('T21', 'the same draft cannot be submitted twice',
      Number(sql('SELECT count(*) FROM "LoadingRegisters";')) === loadingsAfter && duplicateText.includes('قبلاً ثبت نهایی شده'),
      secondTab.url());
    await openLoading(secondTab, `/Loading/Create?draft=${draftKey}`);
    const reopenedHtml = await secondTab.content();
    const reopenedKey = await secondTab.$eval('input[data-ptg-draft]', (i) => i.value);
    check('T22', 'a submitted draft cannot be reopened', reopenedHtml.includes('پیدا نشد یا قبلاً ثبت نهایی') && reopenedKey === '',
      `url=${secondTab.url()} key=${reopenedKey} alerts=${(await secondTab.$$eval('.boltz-alert', (a) => a.map((x) => x.innerText).join(' / ')))}`);

    // ---- 13. مدیریت و حذف پیش‌نویس‌های دیگر (فقط با تصمیم کاربر)
    await secondTab.goto(`${ORIGIN}/FormDrafts`);
    const before = drafts(userA).length;
    await secondTab.locator('form[action*="/FormDrafts/Delete"] button').first().click();
    await secondTab.click('[data-ptg-confirm-ok]').catch(() => {});
    await secondTab.waitForLoadState('load');
    await secondTab.waitForTimeout(1500);
    await secondTab.screenshot({ path: path.join(output, 'drafts-page.png'), fullPage: false });
    check('T23', 'user can delete a draft from the management page', drafts(userA).length === before - 1,
      `${before} -> ${drafts(userA).length}`);

    // ---- 14. فرم‌های تک‌رکوردی دیگر: قرارداد/خرید، فروش، دریافت/پرداخت
    const baselineOther = businessCounts();
    const otherForms = [
      { id: 'T24', name: 'contract / purchase', url: '/Contracts/Create', field: 'ContractName', value: 'E2E draft contract' },
      { id: 'T25', name: 'sales invoice', url: '/Sales/Create', field: 'QuantityMt', value: '17.25' },
      { id: 'T26', name: 'receipt / payment', url: '/Payments/Create', field: 'Amount', value: '4321' },
      { id: 'T26e', name: 'expense', url: '/Expenses/Create', field: 'Description', value: 'E2E draft expense' }
    ];
    for (const form of otherForms) {
      const p = await context.newPage();
      p.on('pageerror', (e) => errors.push(`${form.url}: ${e.message}`));
      await p.goto(`${ORIGIN}${form.url}`);
      await p.waitForSelector('input[data-ptg-draft]', { state: 'attached', timeout: 30000 });
      const input = p.locator(`form [name="${form.field}"]:visible`).first();
      await input.fill('');
      await input.pressSequentially(form.value, { delay: 30 });
      await waitStatus(p, 'پیش‌نویس ذخیره شد');
      if (form.id === 'T26') await p.screenshot({ path: path.join(output, 'payment-saved-status.png'), fullPage: false });
      await p.reload();
      await p.waitForSelector('[data-ptg-draft-notice]', { timeout: 20000 });
      if (form.id === 'T24') {
        await p.screenshot({ path: path.join(output, 'contract-resume-notice.png'), fullPage: false });
        await p.setViewportSize({ width: 390, height: 844 });
        await p.screenshot({ path: path.join(output, 'contract-resume-notice-mobile.png'), fullPage: false });
        const overflow = await p.evaluate(() => document.documentElement.scrollWidth > window.innerWidth + 1);
        check('T24m', 'resume notice fits a phone screen without horizontal scroll', !overflow);
        await p.setViewportSize({ width: 1600, height: 1000 });
      }
      await p.click('[data-ptg-draft-notice] a.ak-btn:not(.ak-btn--ghost)');
      await p.waitForURL(/draft=/, { timeout: 20000 });
      await p.waitForSelector('input[data-ptg-draft]', { state: 'attached', timeout: 30000 });
      const restoredValue = await p.locator(`form [name="${form.field}"]`).first().inputValue();
      const numeric = (v) => Number(String(v).replace(/[^0-9.]/g, ''));
      check(form.id, `${form.name} draft auto-saved and restored`,
        restoredValue === form.value || numeric(restoredValue) === numeric(form.value), restoredValue);
      await p.close();
    }
    check('T27', 'other drafts changed no business data either', businessCounts() === baselineOther,
      `${baselineOther} -> ${businessCounts()}`);

    await page.screenshot({ path: path.join(output, 'after-submit.png'), fullPage: false });
    fs.writeFileSync(path.join(output, 'resume.html'), resumeHtml);
    check('ERRORS', 'no uncaught page errors', errors.length === 0, errors.join(' | '));
    console.log(`\n${results.length} checks passed against the real server (copy ${dbName}).`);
  } finally {
    if (browser) await browser.close().catch(() => {});
    server.kill();
    await new Promise((r) => setTimeout(r, 2000));
    try { dropCopy(); } catch (e) { console.error('drop failed', e.message); }
    fs.writeFileSync(path.join(output, 'server.log'), log.join(''));
  }
})().catch((error) => {
  console.error(error);
  process.exit(1);
});
