/* Specimen rendering only. Application integration does not load this file. */
(async () => {
  const params = new URLSearchParams(location.search);
  const catalog = await fetch('catalog.json').then(response => response.json());
  const component = catalog.components.find(item => item.id === params.get('id')) || catalog.components[0];
  const mount = document.querySelector('#mount');
  if (params.get('responsive') !== '1') mount.dataset.mrNative = '';
  mount.dataset.mrTheme = params.get('theme') === 'blue' ? 'blue' : params.get('theme') === 'light' ? 'light' : component.theme;
  mount.innerHTML = await fetch(component.template).then(response => response.text());
  document.title = component.title + ' — مشعل';
  if (mount.querySelector('[data-mr-action="open-modal"]')) {
    const modalTemplate = catalog.components.find(item => item.id === 'C32').template;
    const dialog = document.createElement('dialog');
    dialog.className = 'mr-modal';
    dialog.setAttribute('aria-label', 'انتخاب ابزارک‌ها');
    dialog.innerHTML = await fetch(modalTemplate).then(response => response.text());
    mount.append(dialog);
  }
  if (params.get('state') === 'disabled') mount.querySelectorAll('input,select,textarea,button').forEach(control => { control.disabled = true; });
  if (params.get('state') === 'error') {
    mount.querySelectorAll('.mr-input').forEach((control, index) => {
      control.setAttribute('aria-invalid', 'true');
      const error = document.createElement('span');
      error.className = 'mr-error';
      error.id = 'kit-error-' + index;
      error.textContent = 'این مقدار را بررسی کنید.';
      control.setAttribute('aria-describedby', error.id);
      control.parentElement.append(error);
    });
  }
  await document.fonts.ready;
  window.kitReady = true;
})().catch(error => {
  document.querySelector('#mount').textContent = 'نمونه بارگذاری نشد.';
  console.error(error);
});
