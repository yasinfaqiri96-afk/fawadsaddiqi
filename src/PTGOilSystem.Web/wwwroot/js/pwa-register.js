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

        // دسترسی واقعی به سرور (form-bridge.js) بر navigator.onLine مقدم است.
        if (!navigator.onLine || status.reachable === false) {
            iconClass = 'bi-cloud-slash';
            label = pending > 0
                ? 'سرور در دسترس نیست — ' + pending.toLocaleString('fa-AF') + ' عملیات در دستگاه منتظر ارسال'
                : 'سرور در دسترس نیست — کار آفلاین';
        } else if (status.syncing) {
            iconClass = 'bi-arrow-repeat';
            label = 'در حال همگام‌سازی عملیات آفلاین';
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

    // خروج: کلید بازشدهٔ مشعل آفلاین در این نشست (offline/js/session.js) برداشته می‌شود تا کاربر بعدی بدون رمز
    // محلی به آن دسترسی نداشته باشد. معلومات رمزگذاری‌شده و عملیات Pending در IndexedDB دست نمی‌خورند.
    document.addEventListener('submit', function (event) {
        var form = event.target;
        var action = form && form.getAttribute ? form.getAttribute('action') || '' : '';
        if (/\/auth\/logout/i.test(action)) {
            try { sessionStorage.removeItem('mashal.offline.session'); } catch (e) { }
        }
    }, true);

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
