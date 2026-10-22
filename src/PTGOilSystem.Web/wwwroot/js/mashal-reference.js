/* Presentation only: no amount, FX, stock or posting calculations. */
(function () {
    'use strict';
    const widgetIds = ['activities', 'tanks', 'alerts', 'summary', 'analytics', 'shipments'];
    let selected = widgetIds.slice();
    let pending = widgetIds.slice();
    function key() {
        const user = document.querySelector('meta[name="ptg-offline"]');
        try { return 'mashal-dashboard-widgets:' + JSON.parse(user?.content || '{}').u; }
        catch (_) { return 'mashal-dashboard-widgets'; }
    }
    function applyWidgets() {
        document.querySelectorAll('[data-mashal-widget]').forEach(function (el) {
            el.hidden = !selected.includes(el.dataset.mashalWidget);
        });
    }
    function syncChoices() {
        document.querySelectorAll('[data-mashal-widget-choice]').forEach(function (input) {
            input.checked = pending.includes(input.value);
        });
        const all = document.querySelector('[data-mashal-widgets-all]');
        if (all) { all.checked = pending.length === widgetIds.length; all.indeterminate = pending.length > 0 && pending.length < widgetIds.length; }
        const apply = document.querySelector('[data-mashal-widgets-apply]');
        if (apply) apply.disabled = widgetIds.every(id => pending.includes(id) === selected.includes(id));
    }
    function initReceipt() {
        const page = document.querySelector('.mashal-invoice-receipt');
        if (!page || page.dataset.mashalReceiptReady) return;
        const form = page.querySelector('#roznamchaForm');
        const party = form?.querySelector('.mashal-payment-party');
        const grid = party?.querySelector('.journal-primary-grid');
        if (!grid) return;
        page.dataset.mashalReceiptReady = 'true';
        // Move existing controls intact: names, values, validation and owning
        // cash/bank fieldset are preserved. No payment is created or recalculated.
        for (const name of ['Amount', 'Description']) {
            const field = form.querySelector('[name="' + name + '"]')?.closest('.ak-field');
            if (field) { field.classList.add('mashal-receipt-' + name.toLowerCase()); grid.append(field); }
        }
        const avatar = document.createElement('i');
        avatar.className = 'bi bi-person mashal-receipt-person';
        avatar.setAttribute('aria-hidden', 'true');
        const card = document.createElement('div');
        card.className = 'mashal-receipt-card';
        party.insertBefore(card, grid);
        card.append(avatar, grid);
        const options = document.createElement('details');
        options.className = 'mashal-receipt-options';
        const summary = document.createElement('summary');
        summary.textContent = 'نوع عملیات و روش دریافت / پرداخت';
        options.append(summary);
        for (const selector of ['.journal-direction-segmented', '.journal-mode-bar']) {
            const control = form.querySelector(selector);
            if (control) options.append(control);
        }
        const partyType = grid.querySelector('[name="CounterpartyType"]')?.closest('.ak-field');
        if (partyType) {
            const partyOptions = document.createElement('details');
            partyOptions.className = 'mashal-receipt-party-options';
            const label = document.createElement('summary');
            label.textContent = 'نوع طرف حساب';
            partyOptions.append(label, partyType);
            grid.append(partyOptions);
        }
        const usdNote = form.querySelector('#paymentUsdNote');
        if (usdNote) options.append(usdNote);
        form.querySelector('[data-more-toggle]').before(options);
        const total = form.querySelector('[data-journal-summary]');
        const bank = form.querySelector('.mashal-payment-bank');
        if (total && bank) {
            const totals = document.createElement('div');
            totals.className = 'mashal-receipt-totals';
            const balance = form.querySelector('#journalPartyBalance');
            bank.before(totals);
            if (balance) totals.append(balance);
            totals.append(total);
        }
    }
    function init() {
        initReceipt();
        if (!document.querySelector('.dashboard-page')) return;
        try {
            const saved = JSON.parse(localStorage.getItem(key()));
            selected = Array.isArray(saved) && saved.every(id => widgetIds.includes(id)) ? saved : widgetIds.slice();
        } catch (_) { selected = widgetIds.slice(); }
        pending = selected.slice(); applyWidgets(); syncChoices();
    }
    document.addEventListener('show.bs.modal', function (event) {
        if (event.target.id !== 'mashalWidgetPicker') return;
        pending = selected.slice(); syncChoices();
    });
    document.addEventListener('change', function (event) {
        if (event.target.matches('[data-mashal-widget-choice]')) {
            pending = Array.from(document.querySelectorAll('[data-mashal-widget-choice]:checked'), input => input.value); syncChoices();
        }
        if (event.target.matches('[data-mashal-widgets-all]')) {
            pending = event.target.checked ? widgetIds.slice() : []; syncChoices();
        }
    });
    function save() {
        try { localStorage.setItem(key(), JSON.stringify(selected)); return true; }
        catch (_) { return false; }
    }
    document.addEventListener('click', function (event) {
        if (event.target.closest('[data-mashal-widgets-apply]')) {
            selected = pending.slice(); applyWidgets(); save();
            bootstrap.Modal.getInstance(document.getElementById('mashalWidgetPicker'))?.hide();
        }
        if (event.target.closest('[data-mashal-save]')) {
            const status = document.querySelector('[data-mashal-save-status]');
            if (status) status.textContent = save() ? 'چیدمان داشبورد ذخیره شد.' : 'ذخیره چیدمان در مرورگر ممکن نیست.';
        }
    });
    document.addEventListener('input', function (event) {
        if (event.target.matches('[data-mashal-nav-search]')) {
            const query = event.target.value.trim();
            document.querySelectorAll('.boltz-sidebar-nav > ul > li').forEach(el => { el.hidden = !el.textContent.includes(query); });
        }
        if (event.target.matches('[data-mashal-settings-search]')) {
            const query = event.target.value.trim();
            document.querySelectorAll('.mashal-settings-group a').forEach(el => { el.hidden = !el.textContent.includes(query); });
        }
    });
    document.addEventListener('DOMContentLoaded', init);
    window.addEventListener('ptg:page-ready', init);
})();
