/*
 * PTG Oil System - Finance Forms Module
 * Handles finance-related form interactions and wizard forms.
 */

(function () {
    "use strict";

function initializeFinanceForms() {
initializeAccountStatementForm();
initializeExpenseCreateForm();
initializePaymentCreateForm();
initializeSalesCreateForm();
initializePreSaleCreateForm();
initializeOperationConditionalForms();
initializeOperationalAssetRentQuickForms();
initializeGenericCurrencyFxGroups();
}
function initializeAccountStatementForm() {
var currencySelect = document.getElementById("sourceCurrencyCode");
var fxGroup = document.getElementById("statementFxGroup");
var fxHint = document.getElementById("statementFxHint");
if (!currencySelect || !fxGroup || !fxHint || currencySelect.dataset.fxReady === "true") {
return;
}
function refreshFxVisibility() {
var currency = normalizeCurrency(currencySelect.value);
var needsFx = currency !== "USD";
fxGroup.hidden = !needsFx;
fxHint.hidden = !needsFx;
}
currencySelect.addEventListener("change", refreshFxVisibility);
refreshFxVisibility();
currencySelect.dataset.fxReady = "true";
}
function initializeExpenseCreateForm() {
var currencySelect = document.getElementById("Currency");
var fxRateGroup = document.getElementById("expenseFxRateGroup");
if (!currencySelect || !fxRateGroup || currencySelect.closest("[data-sales-create-form='true']") || currencySelect.dataset.fxReady === "true") {
return;
}
function refreshFxRateVisibility() {
fxRateGroup.style.display = normalizeCurrency(currencySelect.value) === "USD" ? "none" : "";
}
currencySelect.addEventListener("change", refreshFxRateVisibility);
refreshFxRateVisibility();
currencySelect.dataset.fxReady = "true";
}
function initializePaymentCreateForm() {
var cashAccountSelect = document.getElementById("cashAccountSelect");
var currencySelect = document.getElementById("paymentCurrency");
var fxRateGroup = document.getElementById("paymentFxGroup");
var fxRateHint = document.getElementById("paymentFxHint");
if (!cashAccountSelect || !currencySelect || !fxRateGroup || !fxRateHint || cashAccountSelect.dataset.paymentReady === "true") {
return;
}
function refreshFxVisibility() {
var needsFx = normalizeCurrency(currencySelect.value) !== "USD";
fxRateGroup.hidden = !needsFx;
fxRateHint.hidden = !needsFx;
}
function syncCurrencyFromAccount() {
var selectedOption = cashAccountSelect.options[cashAccountSelect.selectedIndex];
var isMixed = selectedOption && selectedOption.dataset && selectedOption.dataset.mixed === "true";
var accountCurrency = selectedOption && selectedOption.dataset ? selectedOption.dataset.currency : "";
// حساب مختلط همه ارزها را می‌پذیرد؛ انتخاب ارز را آزاد بگذار و قفل نکن.
currencySelect.disabled = false;
if (accountCurrency && !isMixed) {
currencySelect.value = normalizeCurrency(accountCurrency);
}
refreshFxVisibility();
}
cashAccountSelect.addEventListener("change", syncCurrencyFromAccount);
currencySelect.addEventListener("change", refreshFxVisibility);
refreshFxVisibility();
cashAccountSelect.dataset.paymentReady = "true";
}
function initializeSalesCreateForm() {
var form = document.querySelector("[data-sales-create-form='true']");
if (!form || form.dataset.salesReady === "true") return;

function isEnglishUi() {
var match = document.cookie.match(/(?:^|; )ptg-ui-lang=([^;]+)/);
return !!match && decodeURIComponent(match[1]).toLowerCase() === "en";
}
function text(fa, en) {
return isEnglishUi() ? en : fa;
}

var saleStages = {
terminal: "0",
presale: "1",
transit: "2",
border: "3",
customs: "4"
};
var baseCurrency = normalizeCurrency(form.getAttribute("data-sales-base-currency") || "USD");
var shipmentContractMap = parseJsonDataAttribute(form, "data-sales-shipment-contract-map");
var shipmentById = new Map(shipmentContractMap.map(function (item) {
return [String(item.shipmentId), item];
}));
// نگاشت مخزن ← ترمینال: فرم فروش فیلد ترمینال ندارد و مقدارش از مخزن گرفته می‌شود.
var tankTerminalMap = parseJsonDataAttribute(form, "data-sales-tank-terminal-map");
var terminalByTankId = new Map(tankTerminalMap.map(function (item) {
return [String(item.tankId), String(item.terminalId)];
}));

var stageSelect = form.querySelector("[data-sales-stage]");
var companySelect = form.querySelector("[data-sales-company]");
var productSelect = form.querySelector("[data-sales-product]");
var sourceContractSelect = form.querySelector("[data-sales-source-contract]");
var sourceTerminalSelect = form.querySelector("[data-sales-source-terminal]");
var sourceTankSelect = form.querySelector("[data-sales-source-tank]");
var shipmentSelect = form.querySelector("[data-sales-shipment]");
var destinationInput = form.querySelector("[data-sales-destination-id]");
var quantityInput = form.querySelector("[data-sales-quantity]");
var unitPriceInput = form.querySelector("[data-sales-unit-price]");
// «قیمت فروش هنوز مشخص نیست»: فیلد قیمت غیرفعال و مبلغ «نامشخص» نمایش داده می‌شود، نه صفر.
var pricePendingInput = form.querySelector("[data-sales-price-pending]");
var currencySelect = form.querySelector("[data-sales-currency]");
var fxRateField = form.querySelector("[data-sales-fx-rate-field]");
var fxRateInput = form.querySelector("[data-sales-fx-rate]");
var totalValue = form.querySelector("[data-sales-total-value]");
var saleDateInput = form.querySelector("[data-sales-date]");
var stockAlert = form.querySelector("[data-sales-stock-alert]");
var stockAlertValue = form.querySelector("[data-sales-stock-alert-value]");
var stockMessage = form.querySelector("[data-sales-stock-message]");
var summaryBaseRow = form.querySelector("[data-sales-summary-base-row]");
var saveSummaryList = form.querySelector("[data-sales-save-summary-list]");
var saveSummaryWarning = form.querySelector("[data-sales-save-summary-warning]");
var summaryQty = form.querySelector("[data-sales-summary-qty]");
var summaryAmount = form.querySelector("[data-sales-summary-amount]");
var summaryAmountUnit = form.querySelector("[data-sales-summary-amount-unit]");
var summaryBase = form.querySelector("[data-sales-summary-base]");
var ticketInput = form.querySelector("[data-sales-ticket]");
var stockSourceSelect = form.querySelector("[data-sales-stock-source]");
var hint = form.querySelector("#suggestedPriceHint");
var formula = form.querySelector("#suggestedPriceFormula");
var reason = form.querySelector("#suggestedPriceReason");
var fallback = form.querySelector("#suggestedPriceFallback");
var stageHelp = form.querySelector("#saleStageHelp");
var endpointBase = sourceContractSelect ? sourceContractSelect.getAttribute("data-suggested-price-url") : "";
var initialDestinationId = destinationInput ? destinationInput.value : "";
var stockRequestController = null;
var lastStageValue = stageSelect ? String(stageSelect.value || saleStages.terminal) : saleStages.terminal;

function normalizeDigits(value) {
return String(value || "")
.replace(/[۰-۹]/g, function (char) { return String("۰۱۲۳۴۵۶۷۸۹".indexOf(char)); })
.replace(/[٠-٩]/g, function (char) { return String("٠١٢٣٤٥٦٧٨٩".indexOf(char)); });
}
function parseDecimal(value) {
var normalized = normalizeDigits(value).replace(/,/g, "").trim();
var parsed = normalized ? Number(normalized) : 0;
return Number.isFinite(parsed) ? parsed : 0;
}
function formatMoney(value) {
return new Intl.NumberFormat("en-US", {
minimumFractionDigits: 2,
maximumFractionDigits: 4
}).format(value);
}
function formatQuantity(value) {
return new Intl.NumberFormat("en-US", {
minimumFractionDigits: 0,
maximumFractionDigits: 4
}).format(Number(value || 0));
}
function currentStageName() {
var value = stageSelect ? String(stageSelect.value || saleStages.terminal) : saleStages.terminal;
if (value === saleStages.terminal) return "terminal";
if (value === saleStages.transit) return "transit";
if (value === saleStages.border) return "border";
if (value === saleStages.customs) return "customs";
return "presale";
}
function stageNameFromValue(value) {
value = String(value || "");
if (value === saleStages.terminal) return "terminal";
if (value === saleStages.transit) return "transit";
if (value === saleStages.border) return "border";
if (value === saleStages.customs) return "customs";
return "presale";
}
function fieldWrapper(element) {
if (!element) return null;
if (element.matches(".ak-form-section[data-sales-stage-scope]")) return element;
return element.matches(".ak-field, .ak-col-full")
? element
: element.closest(".ak-field, .ak-col-full, .ak-form-section[data-sales-stage-scope]");
}
function selectedOptionText(select) {
// ترمینال مبدا در فرم فروش یک input مخفی است، نه select؛ پس نبودِ options باید بی‌خطر باشد.
if (!select || !select.options || select.selectedIndex < 0) return "";
return String(select.options[select.selectedIndex].text || "").trim();
}
function wrapperScopes(wrapper) {
var scopedElements = [];
if (wrapper.matches("[data-sales-stage-scope]")) scopedElements.push(wrapper);
wrapper.querySelectorAll("[data-sales-stage-scope]").forEach(function (element) {
scopedElements.push(element);
});
return scopedElements.flatMap(function (element) {
return splitTokens(element.getAttribute("data-sales-stage-scope"));
});
}
function clearWrapperControls(wrapper) {
wrapper.querySelectorAll("input, select, textarea").forEach(function (input) {
if (input.type === "hidden" || input.matches("[data-sales-stage]")) return;
input.value = "";
});
}
function wrappersWithPotentialDataLoss(previousStageName, nextStageName) {
var wrappers = new Set();
form.querySelectorAll("[data-sales-stage-scope]").forEach(function (element) {
var wrapper = fieldWrapper(element);
if (wrapper) wrappers.add(wrapper);
});
var losing = [];
wrappers.forEach(function (wrapper) {
var wasVisible = wrapperScopes(wrapper).some(function (scope) { return scope === previousStageName; });
var willBeVisible = wrapperScopes(wrapper).some(function (scope) { return scope === nextStageName; });
if (wasVisible && !willBeVisible && groupHasAnyValue(wrapper)) losing.push(wrapper);
});
return losing;
}
function refreshStageFields() {
var stageName = currentStageName();
var wrappers = new Set();
form.querySelectorAll("[data-sales-stage-scope]").forEach(function (element) {
var wrapper = fieldWrapper(element);
if (wrapper) wrappers.add(wrapper);
});
wrappers.forEach(function (wrapper) {
var visible = wrapperScopes(wrapper).some(function (scope) {
return scope === stageName;
});
setConditionalGroupVisible(wrapper, visible, true);
if (!visible) clearWrapperControls(wrapper);
});
}
function refreshStageHelp() {
if (!stageHelp) return;
var stageName = currentStageName();
if (stageName === "terminal") {
stageHelp.textContent = text(
"فروش از موجودی واقعی مخزن است. اگر قرارداد خرید منبع را نمی‌دانید، سیستم آن را از موجودی همین شرکت و مخزن محاسبه می‌کند.",
"This stage sells from real tank stock. If the source purchase contract is unknown, the system calculates it from this company and tank stock.");
return;
}
if (stageName === "presale") {
stageHelp.textContent = text(
"پیش‌فروش است. قرارداد فروش الزامی است و موجودی مخزن کم نمی‌شود.",
"Pre-sale requires a sales contract and does not reduce tank stock.");
return;
}
if (stageName === "transit") {
stageHelp.textContent = text(
"فروش در مسیر است. شیپمنت و سریال تکت را در صورت امکان وارد کنید.",
"Transit sale. Provide shipment and ticket serial when available.");
return;
}
if (stageName === "border") {
stageHelp.textContent = text(
"فروش مرزی است. شیپمنت، تکت و منبع ردیابی را در صورت امکان وارد کنید.",
"Border sale. Provide shipment, ticket and trace source when available.");
return;
}
stageHelp.textContent = text(
"فروش بعد از گمرک است. شیپمنت، تکت و منبع ردیابی را در صورت امکان وارد کنید.",
"After-customs sale. Provide shipment, ticket and trace source when available.");
}
function updateShipmentContext() {
if (!destinationInput) return;
var stageName = currentStageName();
if (stageName === "terminal" || stageName === "presale") {
destinationInput.value = initialDestinationId;
return;
}
var shipment = shipmentById.get(String((shipmentSelect && shipmentSelect.value) || ""));
destinationInput.value = shipment && shipment.destinationLocationId
? String(shipment.destinationLocationId)
: initialDestinationId;
}
function computeUsdTotal(quantity, unitPrice, currency, fxRate) {
currency = normalizeCurrency(currency) || baseCurrency;
if (currency === baseCurrency) return quantity * unitPrice;
var rate = parseDecimal(fxRate);
if (rate <= 0) return null;
return quantity * unitPrice * rate;
}
function isPricePending() {
return !!(pricePendingInput && pricePendingInput.checked);
}
function refreshPricePending() {
if (!pricePendingInput || !unitPriceInput) return;
var pending = isPricePending();
unitPriceInput.disabled = pending;
if (pending) unitPriceInput.value = "";
if (fxRateInput) fxRateInput.disabled = pending;
}
function refreshSaveSummary() {
// خلاصهٔ انتهای فرم فقط دو عدد دارد؛ فهرست خطی اختیاری است.
if (saveSummaryWarning) {
saveSummaryWarning.hidden = true;
saveSummaryWarning.textContent = "";
}
var stageName = currentStageName();
var quantity = parseDecimal(quantityInput && quantityInput.value);
var unitPrice = parseDecimal(unitPriceInput && unitPriceInput.value);
var currency = normalizeCurrency(currencySelect && currencySelect.value) || baseCurrency;
var total = quantity * unitPrice;
var usdTotal = computeUsdTotal(quantity, unitPrice, currency, fxRateInput && fxRateInput.value);
if (summaryQty) summaryQty.textContent = formatQuantity(quantity);
if (summaryAmount) summaryAmount.textContent = isPricePending() ? text("نامشخص", "Unknown") : formatMoney(total);
if (summaryAmountUnit) summaryAmountUnit.textContent = isPricePending() ? "" : currency;
if (summaryBase) {
var baseEquivalent = usdTotal !== null ? usdTotal : (currency === baseCurrency ? total : 0);
summaryBase.textContent = formatMoney(baseEquivalent);
}
var lines = [];
lines.push(selectedOptionText(stageSelect) || text("فروش", "Sale"));
if (stageName === "terminal") {
lines.push(text("فروش مستقیم از مخزن", "Direct tank sale"));
}
if (stageName === "terminal" && sourceTankSelect && sourceTankSelect.value) {
var sourceParts = [];
var sourceTerminalText = sourceTerminalSelect && sourceTerminalSelect.value ? selectedOptionText(sourceTerminalSelect) : "";
if (sourceTerminalText) sourceParts.push(sourceTerminalText);
sourceParts.push(selectedOptionText(sourceTankSelect));
lines.push(text("منبع: ", "Source: ") + sourceParts.join(" / "));
if (sourceContractSelect && sourceContractSelect.value) {
lines.push(text("قرارداد خرید: ", "Purchase contract: ") + selectedOptionText(sourceContractSelect));
} else {
lines.push(text(
"تخصیص قرارداد خرید: خودکار از موجودی مخزن",
"Purchase contract allocation: automatic from tank stock"));
}
} else if (stageName !== "terminal" && shipmentSelect && shipmentSelect.value) {
lines.push(text("محموله: ", "Shipment: ") + selectedOptionText(shipmentSelect));
}
if (stageName !== "terminal" && stageName !== "presale") {
var traceWarnings = [];
if (!shipmentSelect || !shipmentSelect.value) traceWarnings.push(text("شیپمنت وارد نشده", "Shipment missing"));
if (!ticketInput || !ticketInput.value.trim()) traceWarnings.push(text("سریال تکت وارد نشده", "Ticket serial missing"));
if (traceWarnings.length && saveSummaryWarning) {
saveSummaryWarning.hidden = false;
saveSummaryWarning.textContent = text(
"برای ردیابی بهتر: " + traceWarnings.join("، "),
"For better trace: " + traceWarnings.join(", "));
} else if (saveSummaryWarning) {
saveSummaryWarning.hidden = true;
saveSummaryWarning.textContent = "";
}
}
if (quantity > 0 && unitPrice > 0) {
var amountLine = formatQuantity(quantity) + " MT · " + formatMoney(total) + " " + currency;
if (usdTotal !== null && currency !== baseCurrency) {
amountLine += " · " + formatMoney(usdTotal) + " " + baseCurrency;
}
lines.push(amountLine);
}
if (saveSummaryList) {
saveSummaryList.replaceChildren();
lines.forEach(function (line) {
var item = document.createElement("li");
item.textContent = line;
saveSummaryList.appendChild(item);
});
}
}
function refreshFxRateVisibility() {
var needsFxRate = normalizeCurrency(currencySelect && currencySelect.value) !== baseCurrency;
if (fxRateField) setConditionalGroupVisible(fxRateField, needsFxRate, false);
// معادلِ ارز پایه فقط وقتی معنی دارد که ارز فاکتور با ارز پایه فرق کند.
if (summaryBaseRow) summaryBaseRow.hidden = !needsFxRate;
if (!needsFxRate && fxRateInput) fxRateInput.value = "";
}
function refreshTotal() {
if (!totalValue) return;
var quantity = parseDecimal(quantityInput && quantityInput.value);
var unitPrice = parseDecimal(unitPriceInput && unitPriceInput.value);
// واحد پول در سربرگ ستون آمده؛ داخل سلول تکرار نمی‌شود.
totalValue.textContent = isPricePending() ? text("نامشخص", "Unknown") : formatMoney(quantity * unitPrice);
refreshSaveSummary();
}
function hideStockAlert() {
if (stockAlert) stockAlert.hidden = true;
}
function stockChipItems() {
return stockAlert ? stockAlert.querySelectorAll(".ak-stock-chip__item") : [];
}
// یا پیام نشان داده می‌شود یا خلاصهٔ عددیِ مخزن؛ هر دو با هم نه.
function showStockMessage(message) {
if (stockMessage) {
stockMessage.hidden = false;
stockMessage.textContent = message;
}
stockChipItems().forEach(function (item) { item.hidden = true; });
}
function showStockNumbers(result) {
if (stockMessage) {
stockMessage.hidden = true;
stockMessage.textContent = "";
}
stockChipItems().forEach(function (item) { item.hidden = false; });
if (stockAlertValue) stockAlertValue.textContent = formatQuantity(result.availableMt) + " MT";
}
function refreshStockAlert() {
if (!stockAlert || !stockAlertValue || currentStageName() !== "terminal") {
hideStockAlert();
return;
}
var productId = productSelect && productSelect.value;
var companyId = companySelect && companySelect.value;
var sourcePurchaseContractId = sourceContractSelect && sourceContractSelect.value;
var sourceTerminalId = sourceTerminalSelect && sourceTerminalSelect.value;
var sourceStorageTankId = sourceTankSelect && sourceTankSelect.value;
if (!sourceStorageTankId) {
hideStockAlert();
return;
}
// مخزن انتخاب شده ولی هنوز چیزی برای پرس‌وجو کم است: به‌جای پنهان‌شدنِ بی‌صدا، دلیل را بگو.
var missing = [];
if (!companyId) missing.push(text("شرکت", "company"));
if (!productId) missing.push(text("محصول", "product"));
if (missing.length || !sourceTerminalId) {
stockAlert.hidden = false;
stockAlert.classList.remove("is-warning");
showStockMessage(missing.length
? text("برای نمایش موجودی، " + missing.join(" و ") + " را انتخاب کنید.",
"Select " + missing.join(" and ") + " to see the stock.")
: text("ترمینال این مخزن پیدا نشد.", "This tank has no terminal."));
return;
}
var balanceUrl = form.getAttribute("data-sales-stock-balance-url");
if (!balanceUrl) {
hideStockAlert();
return;
}
if (stockRequestController) stockRequestController.abort();
stockRequestController = new AbortController();
stockAlert.hidden = false;
stockAlert.classList.remove("is-warning");
showStockMessage(text("در حال بررسی موجودی...", "Checking stock..."));
var url = new URL(balanceUrl, window.location.origin);
url.searchParams.set("productId", productId);
url.searchParams.set("companyId", companyId);
if (sourcePurchaseContractId) url.searchParams.set("sourcePurchaseContractId", sourcePurchaseContractId);
url.searchParams.set("sourceTerminalId", sourceTerminalId);
url.searchParams.set("sourceStorageTankId", sourceStorageTankId);
if (saleDateInput && saleDateInput.value) url.searchParams.set("saleDate", saleDateInput.value);
fetch(url, {
headers: { "Accept": "application/json" },
signal: stockRequestController.signal
}).then(function (response) {
return response.json();
}).then(function (result) {
if (!result.ok) {
stockAlert.classList.add("is-warning");
showStockMessage(result.message || text("موجودی قابل نمایش نیست", "Stock is not available"));
return;
}
showStockNumbers(result);
}).catch(function (error) {
if (error.name === "AbortError") return;
stockAlert.classList.add("is-warning");
showStockMessage(text("موجودی دریافت نشد", "Could not load stock"));
});
}
function hideHint() {
if (!hint || !formula || !reason || !fallback) return;
hint.classList.add("d-none");
formula.textContent = "";
reason.textContent = "";
fallback.classList.add("d-none");
}
function loadSuggestedPrice() {
if (!sourceContractSelect || !unitPriceInput || !hint || !formula || !reason || !fallback || !endpointBase) return;
var contractId = sourceContractSelect.value;
if (!contractId) {
hideHint();
return;
}
fetch(endpointBase + "?sourcePurchaseContractId=" + encodeURIComponent(contractId), {
headers: { "X-Requested-With": "XMLHttpRequest" }
}).then(function (response) {
if (!response.ok) {
hideHint();
return null;
}
return response.json();
}).then(function (data) {
if (!data) return;
hint.classList.remove("d-none");
formula.textContent = data.formulaText || "";
reason.textContent = data.reason || "";
fallback.classList.toggle("d-none", !data.fallbackApplied);
if (data.ok && data.finalUnitPrice !== null && data.finalUnitPrice !== undefined && !isPricePending()) {
unitPriceInput.value = data.finalUnitPrice;
refreshTotal();
}
}).catch(function () {
hideHint();
});
}
function refreshStage() {
refreshStageFields();
updateShipmentContext();
refreshStockAlert();
refreshSaveSummary();
}
function handleStageChange() {
if (!stageSelect) return;
var nextValue = String(stageSelect.value || saleStages.terminal);
if (nextValue === lastStageValue) return;
var previousStageName = stageNameFromValue(lastStageValue);
var nextStageName = stageNameFromValue(nextValue);
var losingWrappers = wrappersWithPotentialDataLoss(previousStageName, nextStageName);
if (losingWrappers.length) {
var confirmed = window.confirm(text(
"با تغییر مرحله فروش، بعضی فیلدهای مرحله قبلی پاک می‌شود. ادامه بدهیم؟",
"Changing sale stage will clear some fields from previous stage. Continue?"));
if (!confirmed) {
stageSelect.value = lastStageValue;
return;
}
}
lastStageValue = nextValue;
refreshStage();
refreshStageHelp();
}
function syncSourceTerminalFromTank() {
if (!sourceTerminalSelect || !sourceTankSelect || !terminalByTankId.size) return;
var terminalId = terminalByTankId.get(String(sourceTankSelect.value || ""));
sourceTerminalSelect.value = terminalId || "";
}
if (stageSelect) stageSelect.addEventListener("change", handleStageChange);
if (shipmentSelect) shipmentSelect.addEventListener("change", updateShipmentContext);
// قبل از بقیهٔ شنونده‌های تغییر مخزن ثبت می‌شود تا موجودی با ترمینالِ درست خوانده شود.
if (sourceTankSelect) sourceTankSelect.addEventListener("change", syncSourceTerminalFromTank);
[companySelect, productSelect, sourceTerminalSelect, sourceTankSelect, saleDateInput].forEach(function (input) {
if (input) input.addEventListener("change", refreshStockAlert);
});
if (sourceContractSelect) sourceContractSelect.addEventListener("change", function () {
loadSuggestedPrice();
refreshStockAlert();
refreshSaveSummary();
});
[sourceTerminalSelect, sourceTankSelect, shipmentSelect, ticketInput, stockSourceSelect].forEach(function (input) {
if (input) input.addEventListener("change", refreshSaveSummary);
});
[quantityInput, unitPriceInput].forEach(function (input) {
if (input) input.addEventListener("input", refreshTotal);
});
if (fxRateInput) fxRateInput.addEventListener("input", refreshSaveSummary);
if (currencySelect) currencySelect.addEventListener("change", function () {
refreshFxRateVisibility();
refreshTotal();
});
if (pricePendingInput) pricePendingInput.addEventListener("change", function () {
refreshPricePending();
refreshTotal();
});
syncSourceTerminalFromTank();
refreshPricePending();
refreshStage();
refreshFxRateVisibility();
refreshTotal();
refreshStageHelp();
if (sourceContractSelect && sourceContractSelect.value) loadSuggestedPrice();
form.dataset.salesReady = "true";
}
function initializePreSaleCreateForm() {
// فرم «فروش با تحویل مرحله‌ای»: فقط جمعِ نمایشی؛ هیچ محاسبهٔ سروری اینجا نیست.
var form = document.querySelector("[data-presale-create-form]");
if (!form || form.dataset.presaleReady === "true") return;
var quantityInput = form.querySelector("[data-presale-quantity]");
var unitPriceInput = form.querySelector("[data-presale-unit-price]");
var currencySelect = form.querySelector("[data-presale-currency]");
var fxField = form.querySelector("[data-presale-fx-field]");
var totalValue = form.querySelector("[data-presale-total-value]");
var summaryQty = form.querySelector("[data-presale-summary-qty]");
var summaryAmount = form.querySelector("[data-presale-summary-amount]");
var summaryUnit = form.querySelector("[data-presale-summary-unit]");
function toNumber(value) {
var normalized = String(value || "")
.replace(/[۰-۹]/g, function (char) { return String("۰۱۲۳۴۵۶۷۸۹".indexOf(char)); })
.replace(/[٠-٩]/g, function (char) { return String("٠١٢٣٤٥٦٧٨٩".indexOf(char)); })
.replace(/,/g, "")
.trim();
var parsed = normalized ? Number(normalized) : 0;
return Number.isFinite(parsed) ? parsed : 0;
}
function refresh() {
var quantity = toNumber(quantityInput && quantityInput.value);
var unitPrice = toNumber(unitPriceInput && unitPriceInput.value);
var total = quantity * unitPrice;
var currency = normalizeCurrency(currencySelect && currencySelect.value) || "USD";
var money = new Intl.NumberFormat("en-US", { minimumFractionDigits: 2, maximumFractionDigits: 4 }).format(total);
if (totalValue) totalValue.textContent = money;
if (summaryQty) {
summaryQty.textContent = new Intl.NumberFormat("en-US", { minimumFractionDigits: 0, maximumFractionDigits: 4 }).format(quantity);
}
if (summaryAmount) summaryAmount.textContent = money;
if (summaryUnit) summaryUnit.textContent = currency;
if (fxField) fxField.hidden = currency === "USD";
}
[quantityInput, unitPriceInput].forEach(function (input) {
if (input) input.addEventListener("input", refresh);
});
if (currencySelect) currencySelect.addEventListener("change", refresh);
refresh();
form.dataset.presaleReady = "true";
}
function initializeOperationConditionalForms() {
document.querySelectorAll("form").forEach(function (form) {
initializeExpenseLinkScope(form);
initializeSalesConditionalFields(form);
initializeLossEventConditionalFields(form);
initializeDispatchConditionalFields(form);
initializeOperationalAssetConditionalFields(form);
initializeExclusiveFieldGroups(form);
});
}
function initializeGenericCurrencyFxGroups() {
document.querySelectorAll("[data-currency-fx-form]").forEach(function (form) {
if (form.dataset.genericFxReady === "true") return;
var currency = form.querySelector("[data-currency-field]");
var fxGroups = form.querySelectorAll("[data-fx-field]");
function refresh() {
var needsFx = normalizeCurrency(currency && currency.value) !== "USD";
fxGroups.forEach(function (group) {
setConditionalGroupVisible(group, needsFx, true);
});
}
if (currency) currency.addEventListener("change", refresh);
refresh();
form.dataset.genericFxReady = "true";
});
}
function setConditionalGroupVisible(group, visible, disableHiddenFields) {
if (!group) return;
var shouldDisable = disableHiddenFields !== false;
group.hidden = !visible;
group.classList.toggle("d-none", !visible);
var fields = group.matches && group.matches("input, select, textarea")
? [group]
: group.querySelectorAll("input, select, textarea");
Array.prototype.forEach.call(fields, function (field) {
if (field.type === "hidden" || field.hasAttribute("data-keep-enabled-when-hidden")) return;
field.disabled = shouldDisable && !visible;
});
}
function controlHasValue(control) {
if (!control) return false;
if (control.type === "checkbox" || control.type === "radio") return control.checked;
return String(control.value || "").trim() !== "";
}
function groupHasAnyValue(group) {
if (!group) return false;
var fields = group.matches && group.matches("input, select, textarea")
? [group]
: group.querySelectorAll("input, select, textarea");
return Array.prototype.some.call(fields, function (field) {
if (field.type === "hidden" || field.type === "checkbox" || field.type === "radio") return false;
return controlHasValue(field);
});
}
function splitTokens(value) {
return String(value || "").split(/[\s,]+/).filter(Boolean);
}
function initializeExclusiveFieldGroups(form) {
if (!form || form.dataset.exclusiveGroupsReady === "true") return;
var groups = {};
form.querySelectorAll("[data-exclusive-field-group]").forEach(function (wrapper) {
var groupName = wrapper.getAttribute("data-exclusive-field-group");
if (!groupName) return;
groups[groupName] = groups[groupName] || [];
groups[groupName].push(wrapper);
});
Object.keys(groups).forEach(function (groupName) {
var wrappers = groups[groupName];
function readActiveRole() {
var active = "";
wrappers.forEach(function (wrapper) {
if (wrapper.hidden || wrapper.classList.contains("d-none")) return;
var role = wrapper.getAttribute("data-exclusive-role") || "";
var field = wrapper.querySelector("select, input, textarea");
if (!active && controlHasValue(field)) active = role;
});
return active;
}
function refresh() {
var activeRole = readActiveRole();
wrappers.forEach(function (wrapper) {
var role = wrapper.getAttribute("data-exclusive-role") || "";
setConditionalGroupVisible(wrapper, !activeRole || role === activeRole, true);
});
}
wrappers.forEach(function (wrapper) {
wrapper.querySelectorAll("select, input, textarea").forEach(function (field) {
field.addEventListener("change", refresh);
field.addEventListener("input", refresh);
});
});
form.addEventListener("ptg:conditional-visibility-changed", refresh);
refresh();
});
form.dataset.exclusiveGroupsReady = "true";
}
function initializeExpenseLinkScope(form) {
var scopeSelect = form ? form.querySelector("[data-expense-link-scope]") : null;
if (!scopeSelect || scopeSelect.dataset.expenseScopeReady === "true") return;
var fields = form.querySelectorAll("[data-expense-link-field]");
function inferScope() {
var found = "";
fields.forEach(function (wrapper) {
var scope = wrapper.getAttribute("data-expense-link-field") || "";
var field = wrapper.querySelector("select, input, textarea");
if (!found && controlHasValue(field)) found = scope;
});
return found;
}
function refresh() {
var selected = scopeSelect.value || "";
var visibleScopes = selected === "transport" ? ["contract", "shipment", "transport", "service", "asset"]
: selected === "dispatch" ? ["contract", "dispatch", "service", "asset"]
: selected === "shipment" ? ["contract", "shipment", "service", "asset"]
: selected === "contract" ? ["contract", "service", "asset"]
: selected === "service" ? ["contract", "service"]
: selected === "asset" ? ["contract", "asset"]
: selected ? [selected]
: [];
fields.forEach(function (wrapper) {
var scope = wrapper.getAttribute("data-expense-link-field") || "";
setConditionalGroupVisible(wrapper, visibleScopes.indexOf(scope) !== -1, true);
});
form.dispatchEvent(new CustomEvent("ptg:conditional-visibility-changed"));
}
if (!scopeSelect.value) {
scopeSelect.value = inferScope();
}
scopeSelect.addEventListener("change", refresh);
refresh();
scopeSelect.dataset.expenseScopeReady = "true";
}
function initializeSalesConditionalFields(form) {
if (!form || form.dataset.salesConditionalReady === "true" || form.getAttribute("data-sales-create-form") !== "true") return;
var stageSelect = form.querySelector("#SaleStage");
var terminalStockValue = form.getAttribute("data-sale-stage-terminal-stock") || "0";
var stockFields = form.querySelectorAll("[data-sale-terminal-stock-field]");
var lossToggle = form.querySelector("[name='Loss.Enabled']");
var lossPanel = form.querySelector("[data-sales-loss-panel]");
function refresh() {
var usesTerminalStock = !stageSelect || String(stageSelect.value) === terminalStockValue;
stockFields.forEach(function (field) {
setConditionalGroupVisible(field, usesTerminalStock, true);
});
if (lossPanel) {
setConditionalGroupVisible(lossPanel, (lossToggle && lossToggle.checked) || groupHasAnyValue(lossPanel), true);
}
}
if (stageSelect) stageSelect.addEventListener("change", refresh);
if (lossToggle) lossToggle.addEventListener("change", refresh);
if (lossPanel) {
lossPanel.querySelectorAll("input, select, textarea").forEach(function (field) {
field.addEventListener("input", function () {
if (lossToggle && controlHasValue(field)) lossToggle.checked = true;
refresh();
});
});
}
refresh();
form.dataset.salesConditionalReady = "true";
}
function initializeLossEventConditionalFields(form) {
if (!form || form.dataset.lossEventReady === "true" || form.getAttribute("data-loss-event-form") !== "true") return;
var stageSelect = form.querySelector("#Stage");
var affectsInventory = form.querySelector("#AffectsInventory");
var stageFields = form.querySelectorAll("[data-loss-stage-fields]");
var inventoryFields = form.querySelectorAll("[data-loss-inventory-field]");
function refresh() {
var stage = stageSelect ? String(stageSelect.value || "") : "";
stageFields.forEach(function (wrapper) {
setConditionalGroupVisible(wrapper, splitTokens(wrapper.getAttribute("data-loss-stage-fields")).indexOf(stage) !== -1, true);
});
var showInventory = (affectsInventory && affectsInventory.checked) || stage === "4";
inventoryFields.forEach(function (wrapper) {
setConditionalGroupVisible(wrapper, showInventory, true);
});
}
if (stageSelect) stageSelect.addEventListener("change", refresh);
if (affectsInventory) affectsInventory.addEventListener("change", refresh);
refresh();
form.dataset.lossEventReady = "true";
}
function initializeDispatchConditionalFields(form) {
if (!form || form.dataset.dispatchConditionalReady === "true" || !form.hasAttribute("data-dispatch-conditional-form")) return;
var truckSelect = form.querySelector("#TruckId");
var truckField = form.querySelector("[data-dispatch-new-truck-field]");
var driverSelect = form.querySelector("#DriverId");
var driverField = form.querySelector("[data-dispatch-new-driver-field]");
var unloadToggle = form.querySelector("[data-dispatch-unload-toggle]");
var unloadFields = form.querySelector("[data-dispatch-unload-fields]");
function refresh() {
if (truckField) setConditionalGroupVisible(truckField, !controlHasValue(truckSelect), true);
if (driverField) setConditionalGroupVisible(driverField, !controlHasValue(driverSelect), true);
if (unloadFields) {
var open = (unloadToggle && unloadToggle.checked) || groupHasAnyValue(unloadFields);
if (unloadToggle && open) unloadToggle.checked = true;
setConditionalGroupVisible(unloadFields, open, true);
}
}
[truckSelect, driverSelect, unloadToggle].forEach(function (field) {
if (!field) return;
field.addEventListener("change", refresh);
field.addEventListener("input", refresh);
});
refresh();
form.dataset.dispatchConditionalReady = "true";
}
function initializeOperationalAssetConditionalFields(form) {
if (!form || form.dataset.assetConditionalReady === "true" || form.getAttribute("data-operational-asset-form") !== "true") return;
var typeSelect = form.querySelector("#AssetType");
var transportTypes = splitTokens(form.getAttribute("data-asset-transport-types"));
var storageTypes = splitTokens(form.getAttribute("data-asset-storage-types"));
var fields = form.querySelectorAll("[data-asset-type-field]");
function refresh() {
var value = typeSelect ? String(typeSelect.value || "") : "";
var isTransport = transportTypes.indexOf(value) !== -1;
var isStorage = storageTypes.indexOf(value) !== -1;
fields.forEach(function (wrapper) {
var roles = splitTokens(wrapper.getAttribute("data-asset-type-field"));
var visible = roles.length === 0 || roles.some(function (role) {
return role === "transport" ? isTransport
: role === "storage" ? isStorage
: role === "fixed" ? isStorage
: role === "flexible" ? !isTransport && !isStorage
: true;
});
setConditionalGroupVisible(wrapper, visible, true);
});
}
if (typeSelect) typeSelect.addEventListener("change", refresh);
refresh();
form.dataset.assetConditionalReady = "true";
}
function initializeOperationalAssetRentQuickForms() {
document.querySelectorAll("[data-oa-rent-form]").forEach(function (form) {
if (form.dataset.oaRentReady === "true") return;
var chargedToType = form.querySelector("[data-oa-charged-to-type]");
var usageInputs = Array.prototype.slice.call(form.querySelectorAll("[data-oa-rent-usage]"));
var externalPanel = form.querySelector("[data-oa-rent-external-party]");
var internalPanel = form.querySelector("[data-oa-rent-internal-party]");
var internalPartyType = form.querySelector("[data-oa-internal-party-type]");
var partyFields = Array.prototype.slice.call(form.querySelectorAll("[data-oa-rent-party-field]"));
var partyFieldsByType = {
"3": ["customer", "internal-customer"],
"4": ["company"],
"5": ["partner"],
"6": ["service-provider"]
};
function setPanelVisible(panel, visible) {
if (!panel) return;
panel.hidden = !visible;
panel.classList.toggle("is-hidden", !visible);
Array.prototype.slice.call(panel.querySelectorAll("input, select, textarea")).forEach(function (control) {
control.disabled = !visible;
if (!visible) {
control.value = "";
}
});
}
function setPartyFieldVisible(field, visible) {
if (!field) return;
field.hidden = !visible;
field.classList.toggle("is-hidden", !visible);
Array.prototype.slice.call(field.querySelectorAll("input, select, textarea")).forEach(function (control) {
control.disabled = !visible;
if (!visible) {
control.value = "";
}
});
}
function applyInternalPartyVisibility(value) {
var visibleFields = partyFieldsByType[String(value || "4")] || ["company"];
partyFields.forEach(function (field) {
var role = field.getAttribute("data-oa-rent-party-field");
var isExternalCustomerField = role === "customer";
setPartyFieldVisible(field, !isExternalCustomerField && visibleFields.indexOf(role) !== -1);
});
}
function applyExternalPartyVisibility() {
partyFields.forEach(function (field) {
var role = field.getAttribute("data-oa-rent-party-field");
setPartyFieldVisible(field, role === "customer");
});
}
function refresh() {
var selected = usageInputs.find(function (input) { return input.checked; });
var isInternal = selected && selected.value === "1";
var internalTypeValue = internalPartyType ? internalPartyType.value : "4";
if (chargedToType) {
chargedToType.value = isInternal ? internalTypeValue : "3";
}
setPanelVisible(externalPanel, !isInternal);
setPanelVisible(internalPanel, isInternal);
if (isInternal) {
applyInternalPartyVisibility(internalTypeValue);
} else {
applyExternalPartyVisibility();
}
}
usageInputs.forEach(function (input) {
input.addEventListener("change", refresh);
});
if (internalPartyType) {
internalPartyType.addEventListener("change", refresh);
}
refresh();
form.dataset.oaRentReady = "true";
});
}
function normalizeCurrency(value) {
return String(value || "USD").trim().toUpperCase();
}
function parseJsonDataAttribute(element, attributeName) {
try {
return JSON.parse(element.getAttribute(attributeName) || "[]");
} catch {
return [];
}
}

    window.initializeFinanceForms = initializeFinanceForms;
    window.PTG = window.PTG || {};
    window.PTG.initializeFinanceForms = initializeFinanceForms;

    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", initializeFinanceForms, { once: true });
    } else {
        initializeFinanceForms();
    }

})();
