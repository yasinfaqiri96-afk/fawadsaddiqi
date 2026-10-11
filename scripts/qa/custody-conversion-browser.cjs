// Uses only synthetic local test data; see docs/custody-conversion-testing.md.
const {chromium}=require('playwright');const fs=require('fs'); const assert=require('assert/strict');
const baseUrl = process.env.PTG_CUSTODY_E2E_URL || 'http://127.0.0.1:5086';
const dbName = process.env.PTG_CUSTODY_E2E_DATABASE || 'ptg_oil_accounting_test_custody_browser_resume';
const container = process.env.PTG_CUSTODY_E2E_CONTAINER || 'mashal-custody-postgres';
const output = process.env.PTG_CUSTODY_E2E_OUTPUT || '/tmp';
assert(['127.0.0.1', 'localhost'].includes(new URL(baseUrl).hostname), 'E2E is restricted to a local test app');
assert(dbName.startsWith('ptg_oil_accounting_test_'), 'An integration-test database is required');
assert(process.env.PTG_CUSTODY_E2E_USERNAME && process.env.PTG_CUSTODY_E2E_PASSWORD, 'Explicit test credentials are required');
(async()=>{
const browser=await chromium.launch({executablePath:process.env.PTG_CUSTODY_E2E_CHROMIUM || '/usr/bin/chromium',headless:true,args:['--no-sandbox']});
const context=await browser.newContext({viewport:{width:1440,height:1000}});const page=await context.newPage();page.setDefaultTimeout(15000);
const c={username:process.env.PTG_CUSTODY_E2E_USERNAME,password:process.env.PTG_CUSTODY_E2E_PASSWORD};
await page.goto(baseUrl+'/Auth/Login?returnUrl=%2FCustody%2FReceive',{waitUntil:'domcontentloaded'});
await page.locator('#Username').fill(c.username);await page.locator('#Password').fill(c.password);
await Promise.all([page.waitForURL('**/Custody/Receive',{timeout:15000}),page.locator('.ptg-login-submit').click()]);
if (!process.env.PTG_CUSTODY_E2E_RESUME_LOT) {
await page.locator('#OwnerKey').selectOption('2:100001',{force:true});await page.locator('#ProductId').selectOption('100001',{force:true});await page.locator('#LocationKey').selectOption('100001:100001',{force:true});
await page.locator('#QuantityMt').fill('100');await page.locator('#DocumentNumber').fill('SYNTHETIC-BROWSER-CUSTODY');
await Promise.all([page.waitForURL(/Custody\/Details/),page.locator('form.ak-form button[type=submit]').click()]);
}
if (process.env.PTG_CUSTODY_E2E_RESUME_LOT) await page.goto(baseUrl+'/Custody/Details/'+process.env.PTG_CUSTODY_E2E_RESUME_LOT,{waitUntil:'domcontentloaded'});
const lotId=Number(page.url().match(/(?:Details\/|id=)(\d+)/)[1]);assert(lotId>0);
await page.locator('a[href*="ConvertPurchase"]').click();await page.waitForURL(/ConvertPurchase/);
const viewports=[{width:1440,height:1000},{width:390,height:844},{width:768,height:1024}];const layouts=[];
for(const viewport of viewports){await page.setViewportSize(viewport);await page.locator('#QuantityMt').waitFor({state:'visible'});const sizes=await page.evaluate(()=>({client:document.documentElement.clientWidth,scroll:document.documentElement.scrollWidth}));assert(sizes.scroll<=sizes.client+2,JSON.stringify(sizes));layouts.push({viewport,...sizes});await page.screenshot({path:`${output}/mashal-conversion-${viewport.width}.png`,fullPage:true});}
await page.setViewportSize(viewports[0]);await page.locator('#StorageTankId').selectOption('100001',{force:true});await page.locator('#QuantityMt').fill('40');await page.locator('#UnitPriceUsd').fill('500');await page.locator('#AgreementReference').fill('Signed synthetic CP browser test');
await Promise.all([page.waitForResponse(r=>r.request().method()==='POST'&&r.url().includes('/Custody/ConvertPurchase')),page.locator('form.ak-form button[type=submit]').click()]);await page.waitForLoadState('domcontentloaded');
await page.locator('form[action*="CancelConversion"]').waitFor();
const cancelForm=page.locator('form[action*="CancelConversion"]').first();const conversionId=Number((await cancelForm.getAttribute('action')).match(/(?:CancelConversion\/|id=)(\d+)/)[1]);
console.log(JSON.stringify({phase:'converted',lotId,conversionId,layouts}));
fs.writeFileSync(output+'/mashal-conversion-e2e-before-cancel.json',JSON.stringify({lotId,conversionId,layouts}));
// Verify persistence with PostgreSQL before the cancellation, rather than relying on a green message.
const cp=require('child_process'); const query=`SELECT (SELECT SUM(CASE WHEN "Direction"=1 THEN "QuantityMt" ELSE -"QuantityMt" END) FROM "CustodyMovements" WHERE "CustodyLotId"=${lotId}), (SELECT SUM(CASE WHEN "Direction" IN (1,4) THEN "QuantityMt" ELSE -"QuantityMt" END) FROM "InventoryMovements" WHERE "StorageTankId"=100001);`;
const balances=cp.execFileSync('docker',['exec',container,'psql','-U','postgres','-d',dbName,'-At','-c',query],{encoding:'utf8'}).trim();assert.equal(balances,'60.0000|40.0000');
await cancelForm.locator('input[name=reason]').fill('Synthetic agreed cancellation');await Promise.all([page.waitForResponse(r=>r.request().method()==='POST'&&r.url().includes('CancelConversion')),cancelForm.locator('button[type=submit]').click()]);await page.waitForLoadState('domcontentloaded');
await page.locator('form[action*="CancelConversion"]').waitFor({state:'detached'});
assert.equal(await page.locator('form[action*="CancelConversion"]').count(),0);
const after=cp.execFileSync('docker',['exec',container,'psql','-U','postgres','-d',dbName,'-At','-c',query],{encoding:'utf8'}).trim();assert.equal(after,'100.0000|0.0000');
fs.writeFileSync(output+'/mashal-conversion-e2e-result.json',JSON.stringify({passed:true,lotId,conversionId,before:balances,after,layouts},null,2));console.log('E2E passed: real login, receipt, conversion, database balances, cancellation, desktop/mobile/tablet layout');await browser.close();
})().catch(e=>{console.error(e.stack);process.exit(1)});
