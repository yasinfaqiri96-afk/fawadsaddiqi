// E2E for custody multi-currency (AFN sale, USD owner obligation, FX responsibility) and commodity
// loans (lend + partial in-kind return). Runs only against localhost and a database whose name
// starts with ptg_oil_accounting_test_. Fixture data is synthetic (ids 300001…, see
// docs/custody-claude-continuation.md). Credentials come from the environment only.
const { chromium } = require('playwright');
const assert = require('assert/strict');
const { execFileSync } = require('child_process');
const fs = require('fs');

const baseUrl = process.env.PTG_CUSTODY_E2E_URL || 'http://127.0.0.1:5087';
const database = process.env.PTG_CUSTODY_E2E_DATABASE || 'ptg_oil_accounting_test_claude_e2e_fx';
const output = process.env.PTG_CUSTODY_E2E_OUTPUT || '/tmp';
const id = Number(process.env.PTG_CUSTODY_E2E_FIXTURE_ID || 300001);
assert(['127.0.0.1', 'localhost'].includes(new URL(baseUrl).hostname));
assert(database.startsWith('ptg_oil_accounting_test_'));
assert(process.env.PTG_CUSTODY_E2E_USERNAME && process.env.PTG_CUSTODY_E2E_PASSWORD);

const sql = (query) => execFileSync('psql', ['-h', '127.0.0.1', '-U', 'postgres', '-d', database, '-At', '-c', query],
    { encoding: 'utf8', env: { ...process.env, PGPASSWORD: process.env.PGPASSWORD || '' } }).trim();
const ownerKey = `2:${id}`;
const tankA = `${id}:${id}`;
const tankB = `${id}:${id + 1}`;
const afnTill = id + 1;

async function noHorizontalOverflow(page, name, layouts) {
    for (const width of [390, 768, 1440]) {
        await page.setViewportSize({ width, height: 1000 });
        const size = await page.evaluate(() => ({ client: document.documentElement.clientWidth, scroll: document.documentElement.scrollWidth }));
        assert(size.scroll <= size.client + 2, `${name} @${width}: ${JSON.stringify(size)}`);
        layouts.push({ page: name, width, ...size });
        if (width === 390) await page.screenshot({ path: `${output}/mashal-${name}-390.png`, fullPage: true });
        if (width === 1440) await page.screenshot({ path: `${output}/mashal-${name}-1440.png`, fullPage: true });
    }
    await page.setViewportSize({ width: 1440, height: 1000 });
}

async function submit(page, urlGlob) {
    await Promise.all([page.waitForURL(urlGlob), page.locator('form.ak-form button[type=submit]').last().click()]);
}

async function openDetails(page, text) {
    const details = page.locator('details.ak-advanced', { hasText: text }).first();
    if (!(await details.evaluate(d => d.open))) await details.locator('summary').first().click();
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

        // ---------------- امانی دوارزی: فروش افغانی، تعهد دالری به مالک
        await page.goto(baseUrl + '/Custody/Receive');
        await page.locator('#OwnerKey').selectOption(ownerKey);
        await page.locator('#ProductId').selectOption(String(id));
        await page.locator('#LocationKey').selectOption(tankA);
        await page.locator('#QuantityMt').fill('50');
        await submit(page, '**/Custody/Details/**');
        const lot = Number(new URL(page.url()).pathname.split('/').pop());

        await page.goto(`${baseUrl}/Custody/Sale/${lot}`);
        await page.locator('#CustomerId').selectOption(String(id));
        await page.locator('#QuantityMt').fill('10');
        await page.locator('#Currency').selectOption('AFN');
        await page.locator('#UnitPriceUsd').fill('35000');
        await page.locator('#CommissionRate').fill('3');
        await openDetails(page, 'ارز تعهد به مالک');
        await page.locator('#OwnerCurrency').selectOption('USD');
        await page.locator('#OwnerAgreedPerUsd').fill('70');
        await page.locator('#OwnerRateReference').fill('Synthetic owner deal E2E-FX');
        await noHorizontalOverflow(page, 'custody-sale-fx', layouts);
        await submit(page, '**/Custody/Owner**');
        const sale = sql(`SELECT "Id"||'|'||"Currency"||'|'||"OwnerCurrency"||'|'||"SaleAmount"||'|'||"AmountUsd"||'|'||COALESCE("FxResponsibility"::text,'none') FROM "CustodySales" WHERE "CustodyLotId"=${lot} ORDER BY "Id" DESC LIMIT 1`).split('|');
        assert.deepEqual(sale.slice(1), ['AFN', 'USD', '350000.0000', '5000.0000', 'none']);
        assert.equal(await page.locator(`a[href*="/Custody/FxAgreement/${sale[0]}"]`).count(), 1);

        // وصول افغانی به نرخ توافقیِ ۷۲ (تفاوت دارد) پیش از ثبت توافق مسئولیت ← رد.
        const collectUrl = `${baseUrl}/Custody/Finance?key=${encodeURIComponent(ownerKey)}&kind=Collection&saleId=${sale[0]}`;
        const collect = async () => {
            await page.goto(collectUrl);
            assert.equal(await page.locator('input[name="Currency"]').inputValue(), 'AFN');
            await page.locator('#AmountUsd').fill('140000');
            await page.locator('#CashAccountId').selectOption(String(afnTill));
            await openDetails(page, 'نرخ توافقی');
            await page.locator('#AgreedCurrencyPerUsd').fill('72');
            await page.locator('#RateReference').fill('Synthetic exchange slip E2E-72');
        };
        await collect();
        await noHorizontalOverflow(page, 'custody-collection-afn', layouts);
        await page.locator('form.ak-form button[type=submit]').last().click();
        await page.locator('.alert-danger', { hasText: 'مسئول تفاوت نرخ' }).waitFor();
        assert.equal(sql(`SELECT COUNT(*) FROM "CustodyFinanceEntries" WHERE "CustodySaleId"=${sale[0]}`), '0');

        await page.goto(`${baseUrl}/Custody/FxAgreement/${sale[0]}?key=${encodeURIComponent(ownerKey)}`);
        await page.locator('#Responsibility').selectOption('Company');
        await page.locator('#Reference').fill('Synthetic owner FX agreement E2E');
        await noHorizontalOverflow(page, 'custody-fx-agreement', layouts);
        await submit(page, '**/Custody/Owner**');
        await collect();
        await submit(page, '**/Custody/Owner**');
        assert.equal(sql(`SELECT "AmountUsd"||'|'||"OwnerAmount"||'|'||"FxCompanyUsd" FROM "CustodyFinanceEntries" WHERE "CustodySaleId"=${sale[0]} AND "Kind"=1`),
            '1944.4444|2000.0000|-55.5556');
        await page.locator('section', { hasText: 'خلاصهٔ حساب — USD' }).first().waitFor();
        await noHorizontalOverflow(page, 'custody-owner-fx', layouts);

        // ---------------- قرض جنسی: قرض‌دادن ۳۰ تُن از موجودی ملکی و برگشت قسمی ۱۰ تُن
        await page.goto(`${baseUrl}/CommodityLoans/Create?direction=Lend`);
        await page.locator('#CounterpartyKey').selectOption(`1:${id}`);
        await page.locator('#ProductId').selectOption(String(id));
        await page.locator('#QualitySpec').fill('EN590 10ppm');
        await page.locator('#QuantityMt').fill('30');
        await page.locator('#LocationKey').selectOption(tankB);
        await page.locator('#ReturnObligation').fill('همان جنس و کیفیت در همین ترمینال');
        await page.locator('#AgreementReference').fill('Synthetic loan agreement E2E-L1');
        await page.locator('#QuantityTolerancePercent').fill('2');
        await noHorizontalOverflow(page, 'loan-create', layouts);
        await submit(page, '**/CommodityLoans/Details/**');
        const loan = Number(new URL(page.url()).pathname.split('/').pop());
        assert.equal(sql(`SELECT "QuantityMt"||'|'||"Direction" FROM "CommodityLoans" WHERE "Id"=${loan}`), '30.0000|1');
        await noHorizontalOverflow(page, 'loan-details', layouts);

        await page.goto(`${baseUrl}/CommodityLoans/Settle/${loan}`);
        await page.locator('#QuantityMt').fill('10');
        await noHorizontalOverflow(page, 'loan-settle', layouts);
        await submit(page, `**/CommodityLoans/Details/${loan}`);
        assert.equal(sql(`SELECT SUM("QuantityMt") FROM "CommodityLoanSettlements" WHERE "CommodityLoanId"=${loan} AND "Status"=1`), '10.0000');
        assert.equal(sql(`SELECT SUM(CASE WHEN "Direction"=1 THEN "QuantityMt" ELSE -"QuantityMt" END) FROM "InventoryMovements" WHERE "ProductId"=${id}`), '80.0000');

        await page.goto(`${baseUrl}/CommodityLoans`);
        await page.locator(`a[href$="/CommodityLoans/Details/${loan}"]`).waitFor();
        await noHorizontalOverflow(page, 'loan-index', layouts);
        await page.goto(`${baseUrl}/CommodityLoans/Report`);
        await page.locator('td', { hasText: 'خریدار آزمایشی' }).first().waitFor();
        await noHorizontalOverflow(page, 'loan-report', layouts);
        await page.goto(`${baseUrl}/Custody/LedgerReport`);
        await noHorizontalOverflow(page, 'custody-ledger-report', layouts);

        fs.writeFileSync(`${output}/mashal-custody-fx-loans-e2e.json`, JSON.stringify({ passed: true, lot, sale: sale[0], loan, layouts }, null, 2));
        console.log('E2E passed: AFN sale with USD owner, FX block until agreement, company-borne FX; lend + partial return; no horizontal overflow at 390/768/1440');
    } finally {
        await browser.close();
    }
})().catch(error => { console.error(error.stack); process.exit(1); });
