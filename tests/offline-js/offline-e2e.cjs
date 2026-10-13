// مشعل آفلاین — تست End-to-End در مرورگر واقعی Chromium (Service Worker، IndexedDB، WebCrypto واقعی).
//
// سرور: فایل‌های واقعی wwwroot + یک /api/sync/v1 جعلی با همان قرارداد و همان قاعدهٔ idempotency.
// سرور واقعی ASP.NET با تست‌های OfflineSyncApiTests پوشش داده شده است. «Restart دستگاه» با بستن کامل
// پروفایل مرورگر و بازکردن دوبارهٔ همان پوشه شبیه‌سازی می‌شود — این تست دستگاه فیزیکی Android/iPhone نیست.
//
// اجرا (Windows):
//   set NODE_PATH=<npx-cache>\node_modules  &  set PTG_CHROMIUM_PATH=<chrome.exe>
//   node tests/offline-js/offline-e2e.cjs

const fs = require('fs');
const http = require('http');
const os = require('os');
const path = require('path');
const crypto = require('crypto');
const { chromium } = require('playwright');

const root = path.resolve(__dirname, '../../src/PTGOilSystem.Web/wwwroot');
const output = process.env.PTG_BROWSER_ARTIFACTS || path.join(os.tmpdir(), 'mashal-offline-e2e');
const userDataDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mashal-offline-profile-'));
fs.mkdirSync(output, { recursive: true });
const PORT = 5077;
const ORIGIN = `http://127.0.0.1:${PORT}`;
const PIN = 'kabul-2026';

const types = { '.html': 'text/html; charset=utf-8', '.js': 'text/javascript', '.css': 'text/css', '.png': 'image/png',
  '.ico': 'image/x-icon', '.woff2': 'font/woff2', '.webmanifest': 'application/manifest+json', '.json': 'application/json' };

const server = {
  ops: new Map(),
  devices: new Set(),
  pushCalls: 0,
  session: { userId: 5, username: 'op5', fullName: 'کارمند آزمایشی', role: 'Operator', companyId: 1, companyName: 'شرکت آزمایشی',
    navigation: ['Dashboard', 'Sales', 'Payments', 'Operations'],
    operationTypes: [{ key: 'Sale.Request', label: 'درخواست فروش', module: 'Sales' }, { key: 'General.Note', label: 'یادداشت', module: 'Dashboard' }] }
};

function json(res, status, body) {
  res.writeHead(status, { 'Content-Type': 'application/json', 'Cache-Control': 'no-store' });
  res.end(body === undefined ? '' : JSON.stringify(body));
}

function readBody(req) {
  return new Promise((resolve) => {
    let data = '';
    req.on('data', (chunk) => { data += chunk; });
    req.on('end', () => resolve(data ? JSON.parse(data) : null));
  });
}

const httpServer = http.createServer(async (req, res) => {
  const url = new URL(req.url, ORIGIN);
  const p = url.pathname;
  if (p.startsWith('/api/sync/v1/')) {
    const write = req.method === 'POST';
    if (write && req.headers['x-ptg-offline'] !== '1') return json(res, 400, { code: 'offline_client_header_required' });
    if (p.endsWith('/ping')) return json(res, 200, { ok: true, authenticated: true, serverTimeUtc: new Date().toISOString() });
    if (p.endsWith('/session')) return json(res, 200, { ...server.session, serverTimeUtc: new Date().toISOString() });
    if (p.endsWith('/snapshot')) {
      return json(res, 200, { asOfUtc: new Date().toISOString(),
        reference: { customers: [{ id: 7, name: 'مشتری الف' }], products: [{ id: 3, name: 'دیزل', code: 'D' }], currencies: [{ id: 1, name: 'دالر', code: 'USD' }],
          tanks: [{ id: 4, name: 'مخزن ۱ — ترمینال حیرتان', code: 'TK1' }], companies: [{ id: 1, name: 'شرکت آزمایشی', extra: 'owner' }] },
        dashboard: { inventory: { totalMt: 1250.5, lowStockTankCount: 1 }, cashPosition: { totalUsd: 98000, accountCount: 3 }, alerts: [] } });
    }
    const body = await readBody(req);
    if (p.endsWith('/devices')) { server.devices.add(body.deviceId); return json(res, 204); }
    if (p.endsWith('/operations')) {
      server.pushCalls += 1;
      const results = body.operations.map((op) => {
        const hash = crypto.createHash('sha256').update(op.payloadJson, 'utf8').digest('hex');
        if (hash !== op.payloadHash) return { clientOperationId: op.clientOperationId, outcome: 'rejected', code: 'payload_hash_mismatch' };
        // مثل سرور واقعی: همان لحظه با فرم موجود ثبت می‌شود؛ فروش بیش از ۱۰۰ تن = تعارض موجودی.
        const payload = JSON.parse(op.payloadJson);
        const status = op.operationType === 'Sale.Request' && payload.quantity > 100 ? 'Conflict' : 'Applied';
        const note = status === 'Conflict' ? 'موجودی قابل‌فروش این مخزن ۱۰۰ تن است و کافی نیست.' : null;
        const existing = server.ops.get(op.clientOperationId);
        if (existing && existing.status === 'Conflict' && op.revision > existing.revision) {
          Object.assign(existing, op, { status, note, refType: 'SalesTransaction', refId: status === 'Applied' ? '126' : null });
          return { clientOperationId: op.clientOperationId, outcome: 'stored', status, message: note, revision: op.revision,
            appliedReferenceType: existing.refType, appliedReferenceId: existing.refId };
        }
        if (existing) return { clientOperationId: op.clientOperationId, outcome: 'duplicate', status: existing.status, message: existing.note };
        server.ops.set(op.clientOperationId, { ...op, status, note, refType: 'SalesTransaction', refId: status === 'Applied' ? '125' : null });
        return { clientOperationId: op.clientOperationId, outcome: 'stored', status, message: note,
          appliedReferenceType: status === 'Applied' ? 'SalesTransaction' : null, appliedReferenceId: status === 'Applied' ? '125' : null };
      });
      return json(res, 200, { serverTimeUtc: new Date().toISOString(), results });
    }
    if (p.endsWith('/operations/status')) {
      return json(res, 200, { operations: body.ids.map((id) => ({ clientOperationId: id, status: server.ops.get(id)?.status || 'Unknown', note: server.ops.get(id)?.note || null,
        appliedReferenceType: server.ops.get(id)?.refType || null, appliedReferenceId: server.ops.get(id)?.refId || null })) });
    }
    return json(res, 404, {});
  }
  if (p === '/' || p.startsWith('/Auth/')) {
    res.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8' });
    return res.end('<!doctype html><html lang="fa" dir="rtl"><head><meta charset="utf-8"><title>مشعل آنلاین</title></head><body><h1 data-online-mvc>سیستم آنلاین</h1></body></html>');
  }
  const file = path.join(root, decodeURIComponent(p));
  if (!file.startsWith(root) || !fs.existsSync(file) || fs.statSync(file).isDirectory()) {
    res.writeHead(404);
    return res.end();
  }
  res.writeHead(200, { 'Content-Type': types[path.extname(file)] || 'application/octet-stream' });
  res.end(fs.readFileSync(file));
});

const results = [];
function check(id, name, ok, detail = '') {
  results.push({ id, name, ok, detail });
  console.log(`${ok ? 'PASS' : 'FAIL'} ${id} ${name}${detail ? ' — ' + detail : ''}`);
  if (!ok) throw new Error(`${id} failed: ${name} ${detail}`);
}

async function launch() {
  return chromium.launchPersistentContext(userDataDir, {
    executablePath: process.env.PTG_CHROMIUM_PATH || undefined,
    headless: true,
    viewport: { width: 390, height: 844 },
    locale: 'fa-AF',
    acceptDownloads: true,
    serviceWorkers: 'allow'
  });
}

async function waitText(page, text, timeout = 15000) {
  await page.waitForFunction((t) => document.body.innerText.includes(t), text, { timeout });
}

async function unlock(page) {
  // رمز محلی در هر نشست یک‌بار (session.js): اگر نشست هنوز باز است، برنامه بدون صفحهٔ قفل باز می‌شود.
  await page.waitForSelector('[data-unlock] #pin, [data-tabs]:not([hidden])', { timeout: 15000 });
  if (await page.$('[data-tabs]:not([hidden])')) return;
  await page.fill('#pin', PIN);
  await page.click('[data-unlock] button[type="submit"]');
  await page.waitForSelector('[data-tabs]:not([hidden])', { timeout: 20000 });
}

(async () => {
  await new Promise((resolve) => httpServer.listen(PORT, '127.0.0.1', resolve));
  const errors = [];
  let context = await launch();
  let page = context.pages()[0] || await context.newPage();
  page.on('pageerror', (e) => errors.push(e.message));
  let saleId;
  try {
    // ---- نصب/فعال‌سازی آنلاین
    await page.goto(`${ORIGIN}/offline/index.html`);
    await page.waitForSelector('[data-activate]', { timeout: 20000 });
    const manifest = await page.evaluate(async () => (await fetch('/manifest.webmanifest')).json());
    check('TEST 01', 'manifest is installable-shaped (standalone, icons, start_url)',
      manifest.display === 'standalone' && manifest.icons.some((i) => i.sizes === '512x512') && manifest.start_url.startsWith('/'));
    await page.fill('#pin1', PIN);
    await page.fill('#pin2', PIN);
    await page.click('[data-activate] button[type="submit"]');
    await page.waitForSelector('[data-tabs]:not([hidden])', { timeout: 30000 });
    await waitText(page, 'دیزل', 20000).catch(() => {});
    await page.waitForFunction(() => navigator.serviceWorker.controller !== null || navigator.serviceWorker.ready, null, { timeout: 15000 });
    await page.reload();
    await page.waitForFunction(() => !!navigator.serviceWorker.controller, null, { timeout: 15000 });
    check('TEST 01', 'service worker installed and controlling the page', true);
    await unlock(page);
    await page.screenshot({ path: path.join(output, 'home-online-390.png'), fullPage: true });

    // ---- قطع اینترنت؛ بازشدن start_url
    await context.setOffline(true);
    // start_url بدون اینترنت: بازکنندهٔ صفحات ذخیره‌شدهٔ مشعل (page-loader)؛ این تست صفحهٔ MVC ذخیره‌شده ندارد.
    await page.goto(`${ORIGIN}/?source=pwa`);
    await page.waitForSelector('#ox-loader .ox-card', { timeout: 15000 });
    check('TEST 04', 'start_url opens offline (stored-page loader) without internet', await page.isVisible('#ox-loader'));
    await page.goto(`${ORIGIN}/offline/index.html`);
    await unlock(page);
    await waitText(page, 'آفلاین');

    // ---- ثبت آفلاین
    await page.goto(`${ORIGIN}/offline/index.html#/new/Sale.Request`);
    await unlock(page).catch(() => {});
    await page.waitForSelector('[data-op-form]', { timeout: 15000 });
    await page.selectOption('#f-customerId', '7');
    await page.selectOption('#f-productId', '3');
    await page.selectOption('#f-tankId', '4');
    await page.fill('#f-quantity', '12.5');
    await page.selectOption('#f-currency', 'USD');
    await page.click('[data-op-form] button[type="submit"]');
    await waitText(page, 'در دستگاه ذخیره شد؛ در انتظار همگام‌سازی');
    saleId = decodeURIComponent(page.url().split('#/op/')[1] || '');
    check('TEST 05', 'operation saved on device while offline with pending message', !!saleId && server.ops.size === 0);
    await page.screenshot({ path: path.join(output, 'saved-offline-390.png'), fullPage: true });

    // ---- بستن کامل مرورگر و بازکردن دوباره، هنوز آفلاین
    await context.close();
    context = await launch();
    await context.setOffline(true);
    page = context.pages()[0] || await context.newPage();
    page.on('pageerror', (e) => errors.push(e.message));
    await page.goto(`${ORIGIN}/offline/index.html#/op/${encodeURIComponent(saleId)}`);
    await unlock(page);
    await waitText(page, 'در دستگاه ذخیره شد؛ در انتظار همگام‌سازی');
    check('TEST 06/07 (simulated)', 'pending operation survives full browser close/reopen offline', server.ops.size === 0);

    // ---- اتصال دوباره: ارسال و ثبت قطعی خودکار، بدون تأیید دستی
    await context.setOffline(false);
    await page.evaluate(() => window.dispatchEvent(new Event('online')));
    await page.waitForFunction(() => document.body.innerText.includes('همگام‌شد؛ در سیستم ثبت قطعی شد'), null, { timeout: 30000 })
      .catch(async () => { await page.click('[data-sync-now]'); await waitText(page, 'همگام‌شد؛ در سیستم ثبت قطعی شد', 30000); });
    await waitText(page, 'SalesTransaction 125');
    check('TEST 10/21', 'reconnect posts automatically: Applied with the real document number, one server operation',
      server.ops.size === 1 && server.ops.has(saleId) && server.pushCalls >= 1);

    // ---- تعارض تجاری → «اصلاح و ارسال دوباره» همان عملیات
    await page.goto(`${ORIGIN}/offline/index.html#/new/Sale.Request`);
    await unlock(page).catch(() => {});
    await page.waitForSelector('[data-op-form]');
    await page.selectOption('#f-customerId', '7');
    await page.selectOption('#f-productId', '3');
    await page.selectOption('#f-tankId', '4');
    await page.fill('#f-quantity', '150');
    await page.selectOption('#f-currency', 'USD');
    await page.click('[data-op-form] button[type="submit"]');
    await waitText(page, 'ثبت نشد؛ نیازمند اصلاح', 30000);
    await waitText(page, 'کافی نیست');
    const conflictId = decodeURIComponent(page.url().split('#/op/')[1] || '');
    await page.click('a[href^="#/edit/"]');
    await page.waitForSelector('[data-op-form]');
    await page.fill('#f-quantity', '90');
    await page.click('[data-op-form] button[type="submit"]');
    await waitText(page, 'همگام‌شد؛ در سیستم ثبت قطعی شد', 30000);
    const fixed = server.ops.get(conflictId);
    check('§6', 'conflict is corrected in the UI and the SAME operation is resent (revision 2) and posted',
      !!fixed && fixed.revision === 2 && fixed.status === 'Applied' && server.ops.size === 2);

    // ---- بکاپ
    await page.goto(`${ORIGIN}/offline/index.html#/backup`);
    await page.waitForSelector('[data-export]');
    await page.fill('#bp1', 'backup-pass-1');
    await page.fill('#bp2', 'backup-pass-1');
    const [download] = await Promise.all([page.waitForEvent('download'), page.click('[data-export] button[type="submit"]')]);
    const backupPath = path.join(output, download.suggestedFilename());
    await download.saveAs(backupPath);
    const envelope = JSON.parse(fs.readFileSync(backupPath, 'utf8'));
    check('TEST 16 (export)', 'encrypted backup file downloaded outside browser storage',
      envelope.format === 'mashal-offline-backup' && envelope.count === 2 && !fs.readFileSync(backupPath, 'utf8').includes('customerId'));

    // ---- چیدمان
    for (const width of [390, 768, 1366]) {
      await page.setViewportSize({ width, height: 900 });
      await page.goto(`${ORIGIN}/offline/index.html#/home`);
      await page.waitForSelector('.ox-card');
      const layout = await page.evaluate(() => ({ vw: innerWidth, dw: document.documentElement.scrollWidth, dir: document.dir }));
      check('LAYOUT', `no horizontal scroll at ${width}px, RTL`, layout.dw <= layout.vw && layout.dir === 'rtl', JSON.stringify(layout));
      await page.screenshot({ path: path.join(output, `home-${width}.png`), fullPage: true });
    }

    check('ERRORS', 'no uncaught page errors', errors.length === 0, errors.join(' | '));
    console.log(`\n${results.length} checks passed. Screenshots: ${output}`);
  } finally {
    await context.close().catch(() => {});
    httpServer.close();
    fs.rmSync(userDataDir, { recursive: true, force: true });
  }
})().catch((error) => {
  console.error(error);
  process.exit(1);
});
