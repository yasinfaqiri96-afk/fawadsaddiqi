(async () => {
  'use strict';
  const catalog = await fetch('catalog.json').then(response => response.json());
  const nav = document.querySelector('#catalog-nav');
  const frame = document.querySelector('#live');
  const stage = document.querySelector('#stage');
  let active;
  let requestNumber = 0;
  for (const item of catalog.components) {
    const link = document.createElement('a');
    link.href = '#' + item.id;
    link.dataset.search = `${item.id} ${item.title} ${item.category}`;
    const number = document.createElement('code');
    number.textContent = item.id;
    const label = document.createElement('span');
    label.textContent = item.title;
    link.append(number, label);
    nav.append(link);
  }
  function fit() {
    if (!active) return;
    const available = document.querySelector('#live-canvas').clientWidth - 40;
    const scale = Math.min(1, Math.max(0.05, available / active.size[0]));
    frame.style.width = active.size[0] + 'px';
    frame.style.height = active.size[1] + 'px';
    frame.style.transform = `scale(${scale})`;
    stage.style.width = active.size[0] * scale + 'px';
    stage.style.height = active.size[1] * scale + 'px';
  }
  function renderFrame() {
    const params = new URLSearchParams({id:active.id,theme:document.querySelector('#theme').value,state:document.querySelector('#state').value});
    frame.src = 'specimen.html?' + params;
    document.querySelector('#native').href = frame.src;
    fit();
  }
  async function select() {
    const thisRequest = ++requestNumber;
    active = catalog.components.find(item => item.id === location.hash.slice(1)) || catalog.components[0];
    nav.querySelectorAll('a').forEach(link => link.setAttribute('aria-current', String(link.hash === '#' + active.id)));
    document.querySelector('#title').textContent = active.title;
    document.querySelector('#description').textContent = active.note || 'کامپوننت مستقل با اندازه‌ها و ساختار قابل استفاده در صفحات مختلف.';
    document.querySelector('#meta').replaceChildren(...[active.id,active.category,`${active.size[0]} × ${active.size[1]} px`,active.theme === 'blue' ? 'پوستهٔ آبی' : 'پوستهٔ سفید'].map(value => {const span=document.createElement('span');span.textContent=value;return span;}));
    document.querySelector('#ref-name').textContent = active.reference.slice(0,3);
    document.querySelector('#reference').src = active.image;
    document.querySelector('#theme').value = active.theme;
    document.querySelector('#state').value = 'default';
    const metrics = document.querySelector('#metrics');
    metrics.replaceChildren();
    const entries = {'مرجع':active.reference,'محدودهٔ برش در فایل اصلی':active.crop.join(', '),'مقیاس کالیبراسیون':active.scale,'فایل کامپوننت':active.template,...active.metrics};
    for (const [name,value] of Object.entries(entries)) {
      const row=document.createElement('tr');
      const label=document.createElement('th');label.scope='row';label.textContent=name;
      const cell=document.createElement('td');const code=document.createElement('code');code.textContent=String(value);cell.append(code);row.append(label,cell);metrics.append(row);
    }
    renderFrame();
    const markup = await fetch(active.template).then(response => response.text());
    if (thisRequest === requestNumber) document.querySelector('#code').textContent = markup;
  }
  document.querySelector('#filter').addEventListener('input', event => {
    const query = event.target.value.trim().toLocaleLowerCase();
    nav.querySelectorAll('a').forEach(link => {link.hidden = !link.dataset.search.toLocaleLowerCase().includes(query);});
  });
  document.querySelector('#theme').addEventListener('change',renderFrame);
  document.querySelector('#state').addEventListener('change',renderFrame);
  document.querySelector('#copy').addEventListener('click',async () => {
    const value = document.querySelector('#code').textContent;
    const status = document.querySelector('#copy-status');
    try {await navigator.clipboard.writeText(value);status.textContent='کپی شد.';}
    catch {
      const selection = window.getSelection();
      const range = document.createRange();range.selectNodeContents(document.querySelector('#code'));
      selection.removeAllRanges();selection.addRange(range);
      status.textContent='کد انتخاب شد؛ گزینهٔ کپی را بزنید.';
    }
  });
  window.addEventListener('hashchange',select);
  new ResizeObserver(fit).observe(document.querySelector('#live-canvas'));
  await select();
  window.galleryReady = true;
})().catch(error => {
  document.querySelector('#title').textContent='کتابخانه بارگذاری نشد؛ آن را از طریق HTTP اجرا کنید.';
  console.error(error);
});
