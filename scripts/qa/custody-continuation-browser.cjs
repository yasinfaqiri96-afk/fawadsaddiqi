// E2E for the Claude continuation of custody: multi-lot sale, partial return, unpaid provider
// charge with partial payment and liter-based delivery. Runs only against localhost and a
// database whose name starts with ptg_oil_accounting_test_. Fixture data must be synthetic
// (see docs/custody-claude-continuation.md). Credentials come from the environment only.
const { chromium } = require('playwright');
const assert = require('assert/strict');
const { execFileSync } = require('child_process');
const fs = require('fs');

const baseUrl = process.env.PTG_CUSTODY_E2E_URL || 'http://127.0.0.1:5086';
const database = process.env.PTG_CUSTODY_E2E_DATABASE || 'ptg_oil_accounting_test_claude_e2e';
const container = process.env.PTG_CUSTODY_E2E_CONTAINER || '';
const output = process.env.PTG_CUSTODY_E2E_OUTPUT || '/tmp';
const id = Number(process.env.PTG_CUSTODY_E2E_FIXTURE_ID || 200001);
assert(['127.0.0.1', 'localhost'].includes(new URL(baseUrl).hostname));
assert(database.startsWith('ptg_oil_accounting_test_'));
assert(process.env.PTG_CUSTODY_E2E_USERNAME && process.env.PTG_CUSTODY_E2E_PASSWORD);

const sql = (query) => {
    const args = ['-U', 'postgres', '-d', database, '-At', '-c', query];
    return (container
        ? execFileSync('docker', ['exec', container, 'psql', ...args], { encoding: 'utf8' })
        : execFileSync('psql', ['-h', '127.0.0.1', ...args], { encoding: 'utf8', env: { ...process.env, PGPASSWORD: process.env.PGPASSWORD || '' } })).trim();
};
const ownerKey = `2:${id}`;
const tankA = `${id}:${id}`;
const tankB = `${id}:${id + 1}`;

async function noHorizontalOverflow(page, name, layouts) {
    for (const width of [390, 768, 1440]) {
        await page.setViewportSize({ width, height: 1000 });
        const size = await page.evaluate(() => ({ client: document.documentElement.clientWidth, scroll: document.documentElement.scrollWidth }));
        assert(size.scroll <= size.client + 2, `${name} @${width}: ${JSON.stringify(size)}`);
        layouts.push({ page: name, width, ...size });
        if (width === 390) await page.screenshot({ path: `${output}/mashal-custody-${name}-390.png`, fullPage: true });
    }
    await page.setViewportSize({ width: 1440, height: 1000 });
}

async function submit(page, urlGlob) {
    await Promise.all([page.waitForURL(urlGlob), page.locator('form.ak-form button[type=submit]').last().click()]);
}

(async () => {
    const browser = await chromium.launch({ executablePath: process.env.PTG_CUSTODY_E2E_CHROMIUM || undefined, headless: true, args: ['--no-sandbox'] });
    const layouts = [];
    try {
        const page = await browser.newPage({ viewport: { width: 1440, height: 1000 } });
        await page.goto(baseUrl + '/Auth/Login?returnUrl=%2FCustody');
        await page.locator('#Username').fill(process.env.PTG_CUSTODY_E2E_USERNAME);
        await page.locator('#Password').fill(process.env.PTG_CUSTODY_E2E_PASSWORD);
        await Promise.all([page.waitForURL('**/Custody'), page.locator('.ptg-login-submit').click()]);

        // دو بار برای یک مالک: ۴۰ تُن در مخزن A و ۶۲٬۵۰۰ لیتر × ۸۰۰ = ۵۰ تُن در مخزن B.
        await page.goto(baseUrl + '/Custody/Receive');
        await page.locator('#OwnerKey').selectOption(ownerKey);
        await page.locator('#ProductId').selectOption(String(id));
        await page.locator('#LocationKey').selectOption(tankA);
        await page.locator('#QuantityMt').fill('40');
        await submit(page, '**/Custody/Details/**');
        const lotA = Number(new URL(page.url()).pathname.split('/').pop());

        await page.goto(baseUrl + '/Custody/Receive');
        await page.locator('#OwnerKey').selectOption(ownerKey);
        await page.locator('#ProductId').selectOption(String(id));
        await page.locator('#LocationKey').selectOption(tankB);
        await page.locator('#QuantityUnit').selectOption('L');
        await page.locator('#QuantityLiters').fill('62500');
        await page.locator('#DensityKgPerM3').fill('800');
        await page.locator('#VolumeTemperatureC').fill('15');
        await page.locator('#DensityTemperatureC').fill('15');
        await page.locator('#MeasurementReference').fill('Synthetic lab certificate E2E-1');
        await submit(page, '**/Custody/Details/**');
        const lotB = Number(new URL(page.url()).pathname.split('/').pop());
        assert.equal(sql(`SELECT SUM("QuantityMt") FROM "CustodyMovements" WHERE "CustodyLotId"=${lotB}`), '50.0000');

        // فروش از دو بار: ۲۰ تُن از A و ۲۵ تُن از B؛ یک سند، کمیشن ۳٪ روی کل.
        await page.goto(`${baseUrl}/Custody/Sale/${lotA}`);
        await page.locator('#CustomerId').selectOption(String(id));
        await page.locator('#QuantityMt').fill('20');
        await page.locator('#UnitPriceUsd').fill('500');
        await page.locator('#CommissionRate').fill('3');
        await page.locator('details.ak-advanced summary').first().click();
        await page.locator(`input[name="Extra[0].Key"][value^="${lotB}:"]`).waitFor({ state: 'attached' });
        await page.locator('#Extra_0_QuantityMt').fill('25');
        await noHorizontalOverflow(page, 'sale', layouts);
        await submit(page, '**/Custody/Owner**');
        const sale = sql(`SELECT "Id","QuantityMt","AmountUsd","CommissionUsd","StockOperationKey" FROM "CustodySales" WHERE "CustodyLotId"=${lotA} ORDER BY "Id" DESC LIMIT 1`).split('|');
        assert.deepEqual(sale.slice(1, 4), ['45.0000', '22500.0000', '675.0000']);
        assert.equal(sql(`SELECT COUNT(*) FROM "CustodyMovements" WHERE "OperationKey"='${sale[4]}'`), '2');

        // برگشت قسمی: ۵ تُن به بار A.
        await page.goto(`${baseUrl}/Custody/SaleReturn/${sale[0]}?key=${encodeURIComponent(ownerKey)}`);
        const lineIndex = await page.locator('input[name^="Lines["][name$="].Key"]').evaluateAll(
            (inputs, lot) => inputs.findIndex(i => i.value.startsWith(`${lot}:`)), lotA);
        await page.locator(`#Lines_${lineIndex}_QuantityMt`).fill('5');
        await noHorizontalOverflow(page, 'sale-return', layouts);
        await submit(page, '**/Custody/Owner**');
        assert.equal(sql(`SELECT "QuantityMt"||'|'||"AmountUsd"||'|'||"CommissionUsd" FROM "CustodySaleReturns" WHERE "CustodySaleId"=${sale[0]}`), '5.0000|2500.0000|75.0000');

        // مصرف نسیهٔ ۳۰۰ دالر به ترانسپورت و پرداخت قسمی ۱۰۰.
        await page.goto(`${baseUrl}/Custody/Finance?key=${encodeURIComponent(ownerKey)}&kind=ProviderCharge`);
        await page.locator('#ServiceProviderId').selectOption(String(id));
        await page.locator('#ExpenseTypeId').selectOption(String(id));
        await page.locator('#AmountUsd').fill('300');
        await noHorizontalOverflow(page, 'provider-charge', layouts);
        await submit(page, '**/Custody/Owner**');
        const charge = sql(`SELECT "Id" FROM "CustodyFinanceEntries" WHERE "Kind"=6 AND "OwnerPartyId"=${id} ORDER BY "Id" DESC LIMIT 1`);
        await page.goto(`${baseUrl}/Custody/Finance?key=${encodeURIComponent(ownerKey)}&kind=ProviderPayment&chargeId=${charge}`);
        assert.equal(await page.locator('#AmountUsd').inputValue(), '300.0000');
        await page.locator('#AmountUsd').fill('100');
        await page.locator('#CashAccountId').selectOption(String(id));
        await submit(page, '**/Custody/Owner**');
        assert.equal(sql(`SELECT SUM("AmountUsd") FROM "CustodyFinanceEntries" WHERE "SettlesFinanceEntryId"=${charge} AND "Status"=1`), '100.0000');
        await noHorizontalOverflow(page, 'owner', layouts);

        // تحویل به لیتر: ۲٬۵۰۰ لیتر × ۸۰۰ = ۲ تُن؛ فقط فیلدهای لیتر فعال.
        await page.goto(`${baseUrl}/Custody/Operation/${lotA}?op=Deliver`);
        await page.locator('#Measure_Unit').selectOption('L');
        assert.equal(await page.locator('#QuantityMt').isDisabled(), true);
        await page.locator('#Measure_Liters').fill('2500');
        await page.locator('#Measure_DensityKgPerM3').fill('800');
        await page.locator('#Measure_VolumeTemperatureC').fill('15');
        await page.locator('#Measure_DensityTemperatureC').fill('15');
        await page.locator('#Measure_Reference').fill('Synthetic lab certificate E2E-2');
        await page.locator('#CounterpartyName').fill('Synthetic receiver');
        await noHorizontalOverflow(page, 'deliver-liters', layouts);
        await submit(page, `**/Custody/Details/${lotA}`);
        assert.equal(sql(`SELECT "QuantityMt"||'|'||"SourceLiters" FROM "CustodyMovements" WHERE "CustodyLotId"=${lotA} AND "Kind"=3`), '2.0000|2500.0000');

        // مانده‌ها: A = ۴۰ − ۲۰ + ۵ − ۲ = ۲۳، B = ۵۰ − ۲۵ = ۲۵؛ هیچ موجودی ملکی ساخته نشد.
        assert.equal(sql(`SELECT SUM(CASE WHEN "Direction"=1 THEN "QuantityMt" ELSE -"QuantityMt" END) FROM "CustodyMovements" WHERE "CustodyLotId"=${lotA}`), '23.0000');
        assert.equal(sql(`SELECT SUM(CASE WHEN "Direction"=1 THEN "QuantityMt" ELSE -"QuantityMt" END) FROM "CustodyMovements" WHERE "CustodyLotId"=${lotB}`), '25.0000');
        assert.equal(sql(`SELECT COUNT(*) FROM "InventoryMovements" WHERE "ProductId"=${id}`), '0');

        fs.writeFileSync(`${output}/mashal-custody-continuation-e2e.json`, JSON.stringify({ passed: true, lotA, lotB, sale: sale[0], charge, layouts }, null, 2));
        console.log('E2E passed: multi-lot sale, partial return, provider charge + partial payment, liter delivery; no horizontal overflow at 390/768/1440');
    } finally {
        await browser.close();
    }
})().catch(error => { console.error(error.stack); process.exit(1); });
