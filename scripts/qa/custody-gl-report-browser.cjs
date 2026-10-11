// Read-only report smoke test against the local synthetic fixture.
const { chromium } = require('playwright');
const assert = require('assert/strict');
const { execFileSync } = require('child_process');
const fs = require('fs');
const baseUrl = process.env.PTG_CUSTODY_E2E_URL || 'http://127.0.0.1:5086';
const database = process.env.PTG_CUSTODY_E2E_DATABASE || 'ptg_oil_accounting_test_custody_browser_resume';
const container = process.env.PTG_CUSTODY_E2E_CONTAINER || 'mashal-custody-postgres';
const output = process.env.PTG_CUSTODY_E2E_OUTPUT || '/tmp';
assert(['127.0.0.1', 'localhost'].includes(new URL(baseUrl).hostname));
assert(database.startsWith('ptg_oil_accounting_test_'));
assert(process.env.PTG_CUSTODY_E2E_USERNAME && process.env.PTG_CUSTODY_E2E_PASSWORD);
const counts = () => execFileSync('docker', ['exec', container, 'psql', '-U', 'postgres', '-d', database, '-At', '-c',
    'SELECT (SELECT COUNT(*) FROM "JournalEntries"),(SELECT COUNT(*) FROM "LedgerEntries"),(SELECT COUNT(*) FROM "CustodyMovements");'], { encoding: 'utf8' }).trim();
(async () => {
    const browser = await chromium.launch({ executablePath: process.env.PTG_CUSTODY_E2E_CHROMIUM || '/usr/bin/chromium', headless: true, args: ['--no-sandbox'] });
    try {
        const page = await browser.newPage();
        await page.goto(baseUrl + '/Auth/Login?returnUrl=%2FCustody%2FLedgerReport');
        await page.locator('#Username').fill(process.env.PTG_CUSTODY_E2E_USERNAME);
        await page.locator('#Password').fill(process.env.PTG_CUSTODY_E2E_PASSWORD);
        const before = counts();
        await Promise.all([page.waitForURL('**/Custody/LedgerReport'), page.locator('.ptg-login-submit').click()]);
        await page.getByText(/دفتر کل خاموش است/).waitFor();
        await page.getByText(/تطبیق کامل نیست/).waitFor();
        const layouts = [];
        for (const width of [390, 768, 1440]) {
            await page.setViewportSize({ width, height: 1000 });
            const size = await page.evaluate(() => ({ client: document.documentElement.clientWidth, scroll: document.documentElement.scrollWidth }));
            assert(size.scroll <= size.client + 2, JSON.stringify(size));
            layouts.push({ width, ...size });
            await page.screenshot({ path: `${output}/mashal-gl-report-${width}.png`, fullPage: true });
        }
        assert.equal(counts(), before);
        fs.writeFileSync(`${output}/mashal-gl-report-e2e-result.json`, JSON.stringify({ passed: true, layouts, unchangedDocumentCounts: before }, null, 2));
        console.log('E2E passed: disabled GL warning, read-only report, mobile/tablet/desktop layouts');
    } finally { await browser.close(); }
})().catch(error => { console.error(error.stack); process.exit(1); });
