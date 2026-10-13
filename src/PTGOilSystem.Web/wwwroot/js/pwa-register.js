// مشعل — ثبت Service Worker (نصب PWA و بازشدن آفلاین) و نشانگر وضعیت آفلاین در سربرگ.
// صفحات MVC را cache نمی‌کند؛ فقط پوستهٔ /offline برای زمان قطع اینترنت آماده می‌شود.
(function () {
    'use strict';

    if ('serviceWorker' in navigator && window.isSecureContext) {
        window.addEventListener('load', function () {
            navigator.serviceWorker.register('/sw.js', { scope: '/', updateViaCache: 'none' }).catch(function () { });
        });
    }

    var STATUS_KEY = 'mashal.offline.status';

    function readStatus() {
        try {
            return JSON.parse(localStorage.getItem(STATUS_KEY) || 'null') || {};
        } catch (e) {
            return {};
        }
    }

    function update() {
        var chip = document.querySelector('[data-offline-chip]');
        var icon = document.querySelector('[data-offline-chip-icon]');
        if (!chip || !icon) {
            return;
        }

        var status = readStatus();
        var pending = Number(status.pending) || 0;
        var problems = Number(status.conflicts) || 0;
        var label = 'مشعل آفلاین';
        var iconClass = 'bi-cloud-check';

        if (!navigator.onLine) {
            iconClass = 'bi-cloud-slash';
            label = 'آفلاین — مشعل آفلاین';
        } else if (pending > 0) {
            iconClass = 'bi-cloud-upload';
            label = pending.toLocaleString('fa-AF') + ' عملیات آفلاین ارسال‌نشده';
        } else if (problems > 0) {
            iconClass = 'bi-cloud-haze2';
            label = problems.toLocaleString('fa-AF') + ' عملیات آفلاین نیازمند بررسی';
        }

        icon.className = 'bi ' + iconClass;
        chip.setAttribute('title', label);
        chip.setAttribute('aria-label', label);
    }

    window.addEventListener('online', update);
    window.addEventListener('offline', update);
    window.addEventListener('storage', function (event) {
        if (event.key === STATUS_KEY) {
            update();
        }
    });
    document.addEventListener('DOMContentLoaded', update);
    document.addEventListener('ptg:page-loaded', update);
    update();
})();
