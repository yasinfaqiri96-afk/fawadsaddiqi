// فورم «پرداخت بین شرکا»: نرخ تبدیل فقط برای ارزِ غیر دالر دیده می‌شود، معادل دالر همان‌جا
// حساب می‌شود و پرداخت‌کننده و دریافت‌کنندهٔ یکسان پیش از ارسال رد می‌شود. محاسبهٔ رسمی
// همچنان سمت سرور است؛ اینجا فقط پیش‌نمایش است.
(function () {
    function init() {
        document.querySelectorAll('[data-partner-settlement-form]').forEach(function (root) {
            if (root.dataset.settlementReady === 'true') return;
            root.dataset.settlementReady = 'true';

            var form = root.closest('form');
            var from = root.querySelector('[data-settlement-from]');
            var to = root.querySelector('[data-settlement-to]');
            var amount = root.querySelector('[data-settlement-amount]');
            var currency = root.querySelector('[data-settlement-currency]');
            var fxField = root.querySelector('[data-settlement-fx-field]');
            var fx = root.querySelector('[data-settlement-fx]');
            var usd = root.querySelector('[data-settlement-usd]');
            var sameError = root.querySelector('[data-settlement-same-error]');
            if (!form || !from || !to || !amount || !currency || !fxField || !fx || !usd) return;

            function refresh() {
                var isUsd = (currency.value || 'USD').toUpperCase() === 'USD';
                fxField.hidden = isUsd;
                fx.required = !isUsd;
                if (isUsd) fx.value = '1';

                var value = parseFloat(amount.value) || 0;
                var rate = isUsd ? 1 : (parseFloat(fx.value) || 0);
                usd.textContent = value > 0 && rate > 0
                    ? (value * rate).toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 }) + ' USD'
                    : '—';

                var same = from.value !== '' && from.value === to.value;
                to.setCustomValidity(same ? 'same-partner' : '');
                if (sameError) sameError.hidden = !same;
            }

            form.addEventListener('input', refresh);
            form.addEventListener('change', refresh);
            refresh();
        });
    }

    document.addEventListener('DOMContentLoaded', init);
    document.addEventListener('ptg:page-ready', init);
    if (document.readyState !== 'loading') init();
})();
