// Local synthetic fixture only; see docs/custody-measurement-testing.md.
const { chromium } = require('playwright');
const assert = require('assert/strict');
const { execFileSync } = require('child_process');
const fs = require('fs');
const baseUrl = process.env.PTG_CUSTODY_E2E_URL || 'http://127.0.0.1:5086';
const dbName = process.env.PTG_CUSTODY_E2E_DATABASE || 'ptg_oil_accounting_test_custody_browser_resume';
const container = process.env.PTG_CUSTODY_E2E_CONTAINER || 'mashal-custody-postgres';
const output = process.env.PTG_CUSTODY_E2E_OUTPUT || '/tmp';
const tankId = Number(process.env.PTG_CUSTODY_E2E_MEASUREMENT_TANK || 100002);
assert(['127.0.0.1', 'localhost'].includes(new URL(baseUrl).hostname));
assert(dbName.startsWith('ptg_oil_accounting_test_'));
assert(process.env.PTG_CUSTODY_E2E_USERNAME && process.env.PTG_CUSTODY_E2E_PASSWORD);
assert(Number.isSafeInteger(tankId) && tankId > 0);
const sql = query => execFileSync('docker', ['exec', container, 'psql', '-U', 'postgres', '-d', dbName, '-At', '-c', query], { encoding: 'utf8' }).trim();
(async () => {
    const browser = await chromium.launch({ executablePath: process.env.PTG_CUSTODY_E2E_CHROMIUM || '/usr/bin/chromium', headless: true, args: ['--no-sandbox'] });
    try {
        const page = await browser.newPage({ viewport: { width: 390, height: 844 } });
        page.setDefaultTimeout(15000);
        await page.goto(baseUrl + '/Auth/Login?returnUrl=%2FCustody%2FReceive', { waitUntil: 'domcontentloaded' });
        await page.locator('#Username').fill(process.env.PTG_CUSTODY_E2E_USERNAME);
        await page.locator('#Password').fill(process.env.PTG_CUSTODY_E2E_PASSWORD);
        await Promise.all([page.waitForURL('**/Custody/Receive'), page.locator('.ptg-login-submit').click()]);
        await page.locator('#OwnerKey').selectOption('2:100001', { force: true });
        await page.locator('#ProductId').selectOption('100001', { force: true });
        await page.locator('#LocationKey').selectOption(`100001:${tankId}`, { force: true });
        await page.locator('#QuantityUnit').selectOption('L', { force: true });
        assert.equal(await page.locator('#QuantityMt').isVisible(), false);
        await page.locator('#QuantityLiters').fill('100000');
        await page.locator('#DensityKgPerM3').fill('800');
        await page.locator('#VolumeTemperatureC').fill('15');
        await page.locator('#DensityTemperatureC').fill('20');
        await page.locator('#MeasurementReference').fill('SYNTHETIC-CERTIFICATE-800-15');
        const before = sql(`SELECT COUNT(*) FROM "CustodyMovements" WHERE "StorageTankId"=${tankId};`);
        await Promise.all([page.waitForResponse(r => r.request().method() === 'POST' && r.url().includes('/Custody/Receive')), page.locator('form.ak-form button[type=submit]').click()]);
        await page.getByText(/دمای حجم و چگالی یکسان نیست/).waitFor();
        assert.equal(sql(`SELECT COUNT(*) FROM "CustodyMovements" WHERE "StorageTankId"=${tankId};`), before);
        await page.locator('#DensityTemperatureC').fill('15');
        const layouts = [];
        for (const width of [390, 1440]) {
            await page.setViewportSize({ width, height: 1000 });
            const size = await page.evaluate(() => ({ client: document.documentElement.clientWidth, scroll: document.documentElement.scrollWidth }));
            assert(size.scroll <= size.client + 2, JSON.stringify(size));
            layouts.push({ width, ...size });
            await page.screenshot({ path: `${output}/mashal-liter-receipt-${width}.png`, fullPage: true });
        }
        await Promise.all([page.waitForURL(/Custody\/Details/), page.locator('form.ak-form button[type=submit]').click()]);
        const lotId = Number(page.url().match(/(?:Details\/|id=)(\d+)/)[1]);
        await page.getByText(/SYNTHETIC-CERTIFICATE-800-15/).waitFor();
        const recorded = sql(`SELECT "QuantityMt","SourceLiters","DensityKgPerM3","MeasurementTemperatureC","DensityTemperatureC" FROM "CustodyMovements" WHERE "CustodyLotId"=${lotId};`);
        assert.equal(recorded, '80.0000|100000.0000|800.000000|15.000|15.000');
        fs.writeFileSync(`${output}/mashal-measurement-e2e-result.json`, JSON.stringify({ passed: true, lotId, recorded, layouts, mismatchedTemperatureRejected: true }, null, 2));
        console.log('E2E passed: liter form, temperature rejection, certified mass, persisted source, mobile/desktop layout');
    } finally { await browser.close(); }
})().catch(error => { console.error(error.stack); process.exit(1); });
