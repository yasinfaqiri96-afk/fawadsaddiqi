/* Presentation only. No requests, storage, arithmetic on money, or application field rewriting. */
(() => {
  'use strict';
  function announce(root, text) {
    let status = root.querySelector('[data-mr-status]');
    if (!status) {
      status = document.createElement('p');
      status.className = 'mr-demo-notice';
      status.dataset.mrStatus = '';
      status.setAttribute('role', 'status');
      root.append(status);
    }
    status.textContent = text;
  }
  function syncOptions(root) {
    const options = [...root.querySelectorAll('[data-mr-option]')];
    const all = root.querySelector('[data-mr-select-all]');
    if (!all || !options.length) return;
    const checked = options.filter(option => option.checked).length;
    all.checked = checked === options.length;
    all.indeterminate = checked > 0 && checked < options.length;
    const confirm = root.querySelector('[data-mr-confirm]');
    if (confirm) confirm.disabled = options.every(option => option.checked === option.defaultChecked);
  }
  document.addEventListener('change', event => {
    const target = event.target;
    if (!(target instanceof HTMLInputElement)) return;
    const root = target.closest('.mr-modal-panel') || target.closest('[data-mr-theme]');
    if (!root) return;
    if (target.matches('[data-mr-select-all]')) {
      root.querySelectorAll('[data-mr-option]').forEach(option => { option.checked = target.checked; });
    }
    if (target.matches('[data-mr-option],[data-mr-select-all]')) syncOptions(root);
  });
  document.addEventListener('click', event => {
    const target = event.target.closest('[data-mr-action]');
    if (!target) return;
    const root = target.closest('[data-mr-theme]');
    if (!root) return;
    const action = target.dataset.mrAction;
    if (action === 'step-up' || action === 'step-down') {
      const input = target.closest('.mr-stepper')?.querySelector('input[type="number"]');
      if (!input || input.disabled || input.readOnly) return;
      action === 'step-up' ? input.stepUp() : input.stepDown();
      input.dispatchEvent(new Event('input', { bubbles: true }));
      input.dispatchEvent(new Event('change', { bubbles: true }));
    } else if (action === 'open-modal') {
      const modal = root.querySelector('dialog');
      if (modal && !modal.open) modal.showModal();
    } else if (action === 'close-modal') {
      target.closest('dialog')?.close();
    } else if (action === 'confirm-widgets') {
      const panel = target.closest('.mr-modal-panel');
      const count = panel.querySelectorAll('[data-mr-option]:checked').length;
      target.closest('dialog')?.close();
      announce(root, `${count.toLocaleString('fa-IR')} ابزارک انتخاب شد.`);
    } else if (target.matches('[data-mr-demo]')) {
      // These sample actions intentionally cannot submit real transactions.
      if (action === 'add-card') {
        const group = target.closest('.mr-repeat-group');
        const card = group.querySelector('.mr-party-card');
        if (card && group.querySelectorAll('.mr-party-card').length < 4) {
          const copy = card.cloneNode(true);
          copy.style.marginTop = '16px';
          copy.querySelectorAll('input').forEach(input => { input.value = ''; });
          group.insertBefore(copy, target);
        }
      } else if (action === 'delete-card') {
        const card = target.closest('.mr-party-card');
        const group = target.closest('.mr-repeat-group');
        if (group?.querySelectorAll('.mr-party-card').length > 1) card.remove();
        else announce(root, 'حذف در نمونه نمایش داده شد؛ هیچ داده‌ای ثبت نمی‌شود.');
      } else if (action === 'toggle-password') {
        const section = root.querySelector('[data-mr-password]');
        if (section) {
          section.hidden = !section.hidden;
          target.setAttribute('aria-expanded', String(!section.hidden));
        }
      } else announce(root, 'این یک نمونهٔ کامپوننت است؛ هیچ اطلاعاتی در سیستم ثبت نشد.');
    }
  });
})();
