// مشعل آفلاین — بازکردن صفحهٔ مشعل وقتی سرور در دسترس نیست (Service Worker این صفحه را به‌جای پاسخ سرور می‌دهد).
//
// همان آدرس درخواستی از نسخهٔ رمزگذاری‌شدهٔ همین کاربر ساخته می‌شود: پوستهٔ کامل Layout (منو، سربرگ) + محتوای صفحه
// با همان ناوبری SPA (PTG.spaApplyHtml). رمز محلی فقط اگر در این نشست هنوز وارد نشده باشد پرسیده می‌شود.

import { IdbStore, profileDbName } from './store.js';
import { Sealer, unlockProfile } from './vault.js';
import { loadProfiles, openRegistry, rememberSession, restoreSession } from './session.js';
import { PageVault } from './page-vault.js';
import { formatDateTime, relativeAge } from './util.js';

const ATTR_COPY = 'data-ptg-offline-copy';
const LOOP_KEY = 'mashal.offline.loader';
const root = document.getElementById('ox-loader');
const h = (value) => String(value ?? '').replace(/[&<>"']/g,
  (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));

let storedPage = null;

function show(html) {
  if (root) root.innerHTML = `<section class="ox-card">${html}</section>`;
}

async function boot() {
  let profiles;
  try {
    profiles = await loadProfiles();
  } catch (e) {
    show(`<p class="ox-error">ذخیره‌سازی محلی در این مرورگر در دسترس نیست (${h(e.message || '')}).</p>`);
    return;
  }
  if (!profiles.length) {
    show(`<h2>سرور در دسترس نیست</h2>
      <p>این دستگاه هنوز برای کار آفلاین فعال نشده است. یک‌بار با اینترنت وارد مشعل شوید و «مشعل آفلاین» را فعال کنید.</p>
      <div class="ox-actions"><a class="ox-btn" href="${h(location.href)}">تلاش دوباره</a></div>`);
    return;
  }
  // محافظ: اگر نمایش نسخهٔ ذخیره به ناوبری کامل دوباره برگشت، حلقه ساخته نشود.
  try {
    const last = JSON.parse(sessionStorage.getItem(LOOP_KEY) || 'null');
    if (last && last.url === location.href && Date.now() - last.at < 4000) {
      show(`<h2>نمایش نسخهٔ ذخیره‌شده ممکن نشد</h2><div class="ox-actions"><a class="ox-btn" href="${h(location.href)}">تلاش دوباره</a>
        <a class="ox-btn ox-btn--ghost" href="/offline/index.html">مشعل آفلاین</a></div>`);
      sessionStorage.removeItem(LOOP_KEY);
      return;
    }
    sessionStorage.setItem(LOOP_KEY, JSON.stringify({ url: location.href, at: Date.now() }));
  } catch {
    // ignore
  }
  const restored = await restoreSession();
  const profile = restored && profiles.find((p) => p.id === restored.profileId);
  if (profile) {
    await open(profile, restored.dataKey);
    return;
  }
  renderLock(profiles);
}

function renderLock(profiles, message = '') {
  show(`<h2>سرور در دسترس نیست — نسخهٔ ذخیره‌شدهٔ مشعل</h2>
    <p class="ox-hint">برای بازکردن صفحات و فورم‌هایی که پیش‌تر ذخیره شده‌اند، رمز محلی را وارد کنید (در این نشست فقط یک‌بار).</p>
    ${message ? `<p class="ox-error">${message}</p>` : ''}
    <form class="ox-form" data-loader-unlock>
      ${profiles.length > 1 ? `<div class="ox-field"><label for="ox-profile">کاربر</label>
        <select class="ox-input" id="ox-profile">${profiles.map((p) => `<option value="${h(p.id)}">${h(p.fullName || p.username)} — ${h(p.companyName || '')}</option>`).join('')}</select></div>` : ''}
      <div class="ox-field"><label for="ox-pin">رمز محلی${profiles.length === 1 ? ` — ${h(profiles[0].fullName || profiles[0].username)}` : ''}</label>
        <input class="ox-input" id="ox-pin" type="password" autocomplete="current-password" required /></div>
      <div class="ox-actions"><button class="ox-btn" type="submit">بازکردن</button>
        <a class="ox-btn ox-btn--ghost" href="${h(location.href)}">تلاش دوباره با سرور</a></div>
    </form>`);
  const form = root.querySelector('[data-loader-unlock]');
  root.querySelector('#ox-pin')?.focus();
  form.addEventListener('submit', async (event) => {
    event.preventDefault();
    const id = root.querySelector('#ox-profile')?.value || profiles[0].id;
    const profile = profiles.find((p) => p.id === id);
    const button = form.querySelector('button');
    button.disabled = true;
    try {
      const result = await unlockProfile(profile, root.querySelector('#ox-pin').value);
      await (await openRegistry()).put('profiles', result.profile);
      if (!result.ok) {
        renderLock(profiles, result.reason === 'locked'
          ? `به‌دلیل تلاش‌های ناموفق تا ${h(formatDateTime(result.lockedUntil))} قفل است.`
          : 'رمز محلی درست نیست.');
        return;
      }
      rememberSession(result.profile, result.rawKey);
      await open(result.profile, result.dataKey);
    } finally {
      button.disabled = false;
    }
  });
}

async function open(profile, dataKey) {
  const store = await IdbStore.open(profileDbName(profile.companyId, profile.userId));
  try {
    const vault = new PageVault(store, new Sealer(dataKey));
    const [page, shell] = await Promise.all([vault.get(location.href), vault.getShell()]);
    if (page?.full) {
      // صفحهٔ مودال/ثبت سریع: همان سند کامل خودش.
      writeDocument(page.html, page.savedAt);
      return;
    }
    if (page && shell) {
      render(shell.html, page);
      return;
    }
    const pages = (await vault.list()).sort((a, b) => (a.title || '').localeCompare(b.title || '', 'fa'));
    show(`<h2>این صفحه بدون سرور در دسترس نیست</h2>
      <p>این صفحه هنوز روی این دستگاه ذخیره نشده است. صفحات و فورم‌هایی که یک‌بار آنلاین باز شوند، بدون سرور هم باز می‌شوند.</p>
      ${pages.length ? `<ul class="ox-list">${pages.map((p) => `<li><a class="ox-item" href="${h(p.url)}"><div><strong>${h(p.title || p.url)}</strong>
        <span class="ox-muted">ذخیره: ${h(formatDateTime(p.savedAt))} (${h(relativeAge(p.savedAt))})</span></div></a></li>`).join('')}</ul>` : ''}
      <div class="ox-actions"><a class="ox-btn" href="${h(location.href)}">تلاش دوباره با سرور</a>
        <a class="ox-btn ox-btn--ghost" href="/offline/index.html#/ops">عملیات آفلاین</a></div>`);
  } finally {
    store.close();
  }
}

function writeDocument(html, savedAt) {
  const doc = new DOMParser().parseFromString(html, 'text/html');
  doc.documentElement.setAttribute(ATTR_COPY, savedAt);
  document.open();
  document.write(`<!DOCTYPE html>${'\n'}${doc.documentElement.outerHTML}`);
  document.close();
}

/**
 * پوستهٔ Layout (همان منو و سربرگ کاربر) با main خالی نوشته می‌شود؛ سپس page-apply.js محتوای صفحه را با همان
 * swapِ ناوبری SPA جای‌گذاری می‌کند تا CSS/JSِ مخصوص صفحه هم مثل حالت آنلاین بارگذاری شود.
 */
function render(shellHtml, page) {
  storedPage = page;
  const doc = new DOMParser().parseFromString(shellHtml, 'text/html');
  doc.documentElement.setAttribute(ATTR_COPY, page.savedAt);
  const main = doc.querySelector('main');
  if (main) main.innerHTML = '';
  const apply = doc.createElement('script');
  apply.type = 'module';
  apply.src = '/offline/js/page-apply.js';
  doc.body.appendChild(apply);
  document.open();
  document.write('<!DOCTYPE html>\n' + doc.documentElement.outerHTML);
  document.close();
}

export async function applyStoredPage() {
  const page = storedPage;
  if (!page) return;
  for (let i = 0; i < 100 && !(window.PTG && typeof window.PTG.spaApplyHtml === 'function'); i += 1) {
    await new Promise((resolve) => setTimeout(resolve, 50));
  }
  document.documentElement.setAttribute(ATTR_COPY, page.savedAt);
  if (window.PTG && typeof window.PTG.spaApplyHtml === 'function') {
    await window.PTG.spaApplyHtml(location.href, page.html, false);
    return;
  }
  const main = document.querySelector('main');
  const next = new DOMParser().parseFromString(page.html, 'text/html').querySelector('main');
  if (main && next) main.innerHTML = next.innerHTML;
}

if (root) boot();
