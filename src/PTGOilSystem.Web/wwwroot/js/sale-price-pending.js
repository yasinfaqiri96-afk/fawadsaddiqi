/*
 * PTG Oil System - فروش با قیمت باز
 * گزینهٔ «قیمت فروش هنوز مشخص نیست» در فرم‌های فروش (گروهی، از محموله، موتر، رسید بارگیری و
 * رسید حمل). با روشن‌شدن، ورودی‌های قیمت/نرخ همان فرم غیرفعال و خالی می‌شوند (غیرفعال یعنی
 * نه ارسال می‌شوند نه اعتبارسنجی)، و راهنمای «نامشخص» دیده می‌شود. هیچ محاسبه‌ای این‌جا نیست.
 *
 *   [data-sale-price-pending-toggle]   checkbox
 *   [data-sale-price-pending-target]   ورودی قیمت/نرخی که باید غیرفعال شود
 *   [data-sale-price-pending-hint]     متنی که فقط در حالت قیمت باز دیده می‌شود
 */
(function () {
    "use strict";

    function initialize() {
        document.querySelectorAll("[data-sale-price-pending-toggle]").forEach(function (toggle) {
            if (toggle.dataset.pricePendingReady === "true") return;
            var form = toggle.closest("form");
            if (!form) return;

            function sync() {
                var pending = toggle.checked;
                form.querySelectorAll("[data-sale-price-pending-target]").forEach(function (input) {
                    input.disabled = pending;
                    if (pending) input.value = "";
                });
                form.querySelectorAll("[data-sale-price-pending-hint]").forEach(function (element) {
                    element.hidden = !pending;
                });
            }

            toggle.addEventListener("change", sync);
            sync();
            toggle.dataset.pricePendingReady = "true";
        });
    }

    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", initialize, { once: true });
    } else {
        initialize();
    }
})();
