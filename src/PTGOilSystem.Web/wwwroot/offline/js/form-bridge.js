// مشعل آفلاین — اتصال همان صفحات و فورم‌های اصلی مشعل به زیرساخت آفلاین موجود
// (Outbox رمزگذاری‌شده، SyncEngine، نشست دستگاه، بکاپ). هیچ فورم یا محیط جداگانه‌ای ساخته نمی‌شود.
//
// فورم‌ها (فهرست مجاز از سرور: OfflineFormRegistry، در <meta name="ptg-offline">):
//  - سرور در دسترس (پاسخ واقعی /api/sync/v1/ping): همان POST فعلی فورم؛ اعتبارسنجی و نتیجهٔ سرور بی‌تغییر.
//  - سرور در دسترس نیست: همان فیلدهای فورم (نام/مقدار) به‌شکل عملیات «Form.Submit» در Outbox همین کاربر ذخیره و پس از
//    اتصال، خودکار با همان اکشن سرور ثبت قطعی می‌شود (همهٔ قواعد تجاری، موجودی، حسابداری و صلاحیت همان‌جا).
//  - فرستاده شد ولی پاسخ نیامد: فورم دارای توکن ضد تکرار → با همان توکن در صف (سرور سند دوم نمی‌سازد)؛
//    فورم بدون توکن → پیش‌نویس دستگاه تا کاربر پس از بررسی بفرستد.
//  - فورمی که آفلاین مجاز نیست: بدون سرور فرستاده نمی‌شود و معلومات روی صفحه می‌ماند.
// صفحات: هر صفحه‌ای که آنلاین باز شود، رمزگذاری‌شده نگه داشته می‌شود و بدون سرور با همان ظاهر و تاریخ ذخیره باز می‌شود.
// رمز محلی در هر نشست فقط یک‌بار (session.js).

import { IdbStore, isQuotaError, profileDbName } from './store.js';
import { PageSealer, Sealer, createProfile, unlockProfile, validatePin } from './vault.js';
import { ApiClient } from './api.js';
import { ConnectivityMonitor } from './connectivity.js';
import { SyncEngine } from './sync-engine.js';
import { DeviceAuth } from './device-auth.js';
import { FORM_SUBMIT, OutboxRepo, Status, canResubmit, typeLabel } from './operations.js';
import { formatDateTime, formatNumber, relativeAge, todayIso, uuid } from './util.js';
import { SESSION_IDLE_MS, ensurePageKeys, forgetSession, hasSession, loadProfiles, openRegistry, rememberSession, rememberShellStyles, restoreSession, touchSession } from './session.js';
import { PageVault } from './page-vault.js';

const STATUS_KEY = 'mashal.offline.status';
const CHECK_MS = 30_000;
const PAGE_REFRESH_MS = 10 * 60_000;
const SHELL_REFRESH_MS = 6 * 60 * 60_000;
const WARM_EVERY_MS = 6 * 60 * 60_000;
const OPS_LINK = '<a href="/offline/index.html#/ops">عملیات آفلاین</a>';
const OFFER_KEY = 'mashal.offline.offer.';
const OFFER_SNOOZE_MS = 30 * 24 * 60 * 60_000;
const TONES = ['alert-info', 'alert-warning', 'alert-success', 'alert-danger'];
const ATTR_COPY = 'data-ptg-offline-copy';

/** فیلد انتخاب طرف‌حساب/وسیله → فورم ساخت همان رکورد (برای انتخاب رکوردی که آفلاین ساخته شد). */
const REFERENCE_FORMS = Object.freeze({
  customerid: '/customers/create',
  supplierid: '/suppliers/create',
  partnerid: '/partners/create',
  sarrafid: '/sarrafs/create',
  serviceproviderid: '/serviceproviders/create',
  truckid: '/trucks/create',
  driverid: '/drivers/create',
  wagonid: '/wagons/create',
  vesselid: '/vessels/create',
  locationid: '/locations/create'
});
const NAME_FIELDS = ['Name', 'FullName', 'PlateNumber', 'Title', 'NamePersian', 'Code'];

const NOTE = Object.freeze({
  offline: 'سرور در دسترس نیست.',
  gateway: 'سرور پاسخ نداد.',
  auth: 'نشست ورود یا توکن فورمِ سرور معتبر نبود و فورم ثبت نشد.',
  unknown: 'فورم به سرور فرستاده شد ولی پاسخ نیامد؛ معلوم نیست ثبت شد یا نه.',
  pendingRef: 'این فورم به رکوردی اشاره دارد که هنوز فقط در دستگاه است (در انتظار ثبت)؛ پس از ثبت قطعی آن رکورد، همین فورم خودکار فرستاده می‌شود.',
  unknownToken: 'فورم به سرور فرستاده شد ولی پاسخ نیامد. همان فورم با همان شناسهٔ ضد تکرار در صف ماند؛ اگر پیش‌تر ثبت شده باشد، سرور سند دوم نمی‌سازد.'
});

const CONFIG = readConfig();
const api = new ApiClient();
const monitor = new ConnectivityMonitor(api);
const state = {
  session: null, // { profile, store, repo, engine, vault }
  pageMessage: null, // { tone, html }
  pendingNav: null, // { url, push } — بازکردن نسخهٔ ذخیره پس از رمز
  edit: null, // { id, form }
  messages: new WeakMap(), // panel → { tone, html }
  intents: new WeakMap(), // form → { status, note, payload }
  counts: null,
  warming: false,
  lastActivity: Date.now()
};

const h = (value) => String(value ?? '').replace(/[&<>"']/g,
  (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));

function readConfig() {
  try {
    const config = JSON.parse(document.querySelector('meta[name="ptg-offline"]')?.content || '{}');
    return { userId: Number(config.u) || 0, forms: new Set((config.f || []).map((p) => String(p).toLowerCase())) };
  } catch {
    return { userId: 0, forms: new Set() };
  }
}

const isCopy = () => document.documentElement.hasAttribute(ATTR_COPY);

/** صفحهٔ داخل مودال (iframe) یا ثبت سریع: سند کاملِ خودش، بدون پوستهٔ منو. */
const isModalUrl = (raw) => {
  try {
    return new URL(raw, location.href).searchParams.get('modal') === '1';
  } catch {
    return false;
  }
};
const isQuickCreatePage = () => window.top !== window && new URLSearchParams(location.search).get('quickCreate') === '1';
/** فورم به رکوردی اشاره دارد که هنوز فقط در دستگاه است (op:)؛ باید پس از ثبت آن رکورد فرستاده شود. */
const hasPendingReference = (form) => [...new FormData(form).values()].some((v) => typeof v === 'string' && v.startsWith('op:'));

function isLoginUrl(url) {
  try {
    return new URL(url, location.href).pathname.toLowerCase().startsWith('/auth/login');
  } catch {
    return false;
  }
}

/** صفحه‌ای که برای خواندن آفلاین نگه داشته می‌شود (نه API، ورود، فایل خروجی یا برنامهٔ آفلاین). */
function cacheable(raw) {
  try {
    const url = new URL(raw, location.href);
    const path = url.pathname.toLowerCase();
    return url.origin === location.origin
      && !/^\/(offline|api|auth|account|health|sw\.js|uploads)(\/|$)/.test(path)
      // مدیریت سیستم و امنیت (کاربران، نقش‌ها، بکاپ، تنظیمات، نگهداری) فقط آنلاین و هرگز روی دستگاه ذخیره نمی‌شود.
      && !/^\/(users|roles|backups|backuprestore|systemsettings|maintenance|auditlogs|offlinesync)(\/|$)/.test(path)
      && !/(export|download|pdf|excel|print|backup)/.test(path);
  } catch {
    return false;
  }
}

// ---------------------------------------------------------------- session

async function profileForUser() {
  try {
    return (await loadProfiles(CONFIG.userId))[0] || null;
  } catch {
    return null; // حالت خصوصی مرورگر / IndexedDB در دسترس نیست
  }
}

/** نشست بازشدهٔ همین زبانه (بدون پرسیدن رمز)، اگر وجود دارد. */
async function ensureSession() {
  if (state.session) return state.session;
  const restored = await restoreSession(CONFIG.userId);
  if (!restored) return null;
  const profile = (await loadProfiles(CONFIG.userId).catch(() => [])).find((p) => p.id === restored.profileId);
  if (!profile) {
    forgetSession();
    return null;
  }
  await openSession(profile, restored.dataKey);
  state.session.persisted = true;
  return state.session;
}

async function unlockWithPin(pin) {
  const profile = await profileForUser();
  if (!profile || !pin) return { ok: false, message: 'رمز محلی را وارد کنید.' };
  const result = await unlockProfile(profile, pin);
  await (await openRegistry()).put('profiles', result.profile);
  if (!result.ok) {
    return {
      ok: false,
      message: result.reason === 'locked'
        ? `رمز محلی به‌دلیل تلاش‌های ناموفق تا ${h(formatDateTime(result.lockedUntil))} قفل است؛ معلومات روی صفحه باقی است.`
        : 'رمز محلی درست نیست.'
    };
  }
  const persisted = rememberSession(result.profile, result.rawKey);
  await openSession(result.profile, result.dataKey);
  state.session.persisted = persisted;
  return { ok: true };
}

async function openSession(profile, dataKey) {
  closeSession(false);
  closeDrop();
  const store = await IdbStore.open(profileDbName(profile.companyId, profile.userId));
  const sealer = new Sealer(dataKey);
  // صندوق صفحات (یک‌بار): از این پس صفحات بدون رمز محلی هم آماده و فقط با رمز خوانده می‌شوند.
  const keyed = (await ensurePageKeys(profile.id, sealer).catch(() => null)) || profile;
  const pageSealer = (await PageSealer.forProfile(keyed, sealer)) || sealer;
  const repo = new OutboxRepo(store, sealer, profile);
  const deviceAuth = new DeviceAuth({
    store,
    sealer,
    deviceId: profile.deviceId,
    refreshImpl: (token, deviceId) => api.refreshDeviceSession(token, deviceId)
  });
  api.setAuth(deviceAuth);
  const engine = new SyncEngine({
    deviceAuth,
    repo,
    store,
    api,
    monitor,
    profile,
    sealer,
    deviceName: navigator.userAgentData?.platform || navigator.platform || 'مرورگر',
    onEvent: (event) => { if (event.type === 'sync-end') onSyncEnd(event.summary); }
  });
  state.session = { profile, store, repo, engine, vault: new PageVault(store, pageSealer) };
  await engine.recoverInterrupted();
  if (monitor.reachable) engine.run();
}

function closeSession(forget = true) {
  if (forget) forgetSession();
  if (!state.session) return;
  api.setAuth(null);
  state.session.store.close();
  state.session = null;
}

// ---------------------------------------------------------------- page writer (no PIN needed)

let dropPromise = null;

/**
 * نوشتن نسخهٔ صفحات: با نشست باز همان صندوق نشست؛ بدون نشست فقط با کلید عمومی صندوق صفحات (vault.js PageSealer).
 * پس نصب‌شدهٔ PWA که بدون رمز باز شد هم صفحات را برای قطع سرور آماده می‌کند؛ خواندن همیشه پس از رمز محلی است.
 */
async function pageWriter() {
  if (state.session) return state.session.vault;
  if (!dropPromise) {
    dropPromise = (async () => {
      const profile = await profileForUser();
      const sealer = profile ? await PageSealer.forProfile(profile) : null;
      if (!sealer) return null;
      const store = await IdbStore.open(profileDbName(profile.companyId, profile.userId));
      return { profile, store, vault: new PageVault(store, sealer) };
    })().catch(() => null);
  }
  const drop = await dropPromise;
  return state.session ? state.session.vault : drop?.vault || null;
}

function closeDrop() {
  const pending = dropPromise;
  dropPromise = null;
  pending?.then((drop) => drop?.store.close()).catch(() => {});
}

function onSyncEnd(summary) {
  const problems = (summary.conflicts || 0) + (summary.rejected || 0);
  const note = summary.authRequired
    ? ['warning', 'نشست سرور منقضی شده است؛ معلومات در دستگاه محفوظ است. برای ارسال، دوباره وارد مشعل شوید.']
    : summary.userMismatch
      ? ['warning', 'کاربر دیگری در این مرورگر وارد سیستم است؛ عملیات این دستگاه ارسال نشد.']
      : problems > 0
        ? ['danger', `${formatNumber(problems)} عملیات آفلاین ثبت نشد و نیازمند اصلاح است — ${OPS_LINK}.`]
        : summary.applied > 0
          ? ['success', `${formatNumber(summary.applied)} عملیات آفلاین همگام شد و در سیستم ثبت قطعی شد.`]
          : null;
  if (note) {
    const panels = formContexts().filter((ctx) => ctx.panel);
    if (panels.length) panels.forEach((ctx) => state.messages.set(ctx.panel, { tone: note[0], html: note[1] }));
    else state.pageMessage = { tone: note[0], html: note[1] };
  }
  injectPendingOptions();
  refreshAll();
}

// ---------------------------------------------------------------- status

/** شمارش بدون بازکردن رمز (وضعیت و نوع عملیات در ردیف Outbox رمزگذاری نشده‌اند). */
async function countRows(profile) {
  const own = state.session?.profile.id === profile.id ? state.session.store : null;
  const store = own || await IdbStore.open(profileDbName(profile.companyId, profile.userId));
  try {
    const counts = { pending: 0, conflicts: 0, drafts: 0 };
    for (const row of await store.getAll('outbox')) {
      if (row.status === Status.Pending || row.status === Status.Sending) counts.pending += 1;
      if (row.status === Status.Conflict || row.status === Status.Rejected) counts.conflicts += 1;
      if (row.status === Status.Draft) counts.drafts += 1;
    }
    return counts;
  } finally {
    if (!own) store.close();
  }
}

/** همان شمارندهٔ سربرگ (pwa-register.js) با وضعیت واقعیِ دسترسی به سرور. */
function publishStatus() {
  try {
    const counts = state.counts || { pending: 0, conflicts: 0 };
    localStorage.setItem(STATUS_KEY, JSON.stringify({
      pending: counts.pending,
      conflicts: counts.conflicts,
      reachable: monitor.lastCheckAt ? monitor.reachable : null,
      syncing: monitor.syncing,
      at: new Date().toISOString()
    }));
    window.dispatchEvent(new StorageEvent('storage', { key: STATUS_KEY }));
  } catch {
    // حالت خصوصی مرورگر؛ فقط آیکن سربرگ به‌روز نمی‌شود.
  }
}

// ---------------------------------------------------------------- notices (page + form)

let pinSeq = 0;

function pinHtml(profile, buttonLabel) {
  const id = `ptg-offline-pin-${++pinSeq}`;
  return `<div class="ak-field mt-2">
      <label class="ak-label" for="${id}">رمز محلی — ${h(profile.fullName || profile.username)}</label>
      <input class="ak-input" id="${id}" type="password" autocomplete="current-password" data-offline-pin />
    </div>
    <button type="button" class="ak-save mt-2" data-offline-pin-ok>${h(buttonLabel)}</button>`;
}

function paint(host, tone, parts) {
  host.classList.remove(...TONES);
  host.classList.add(`alert-${tone}`);
  host.innerHTML = parts.map((part) => `<div class="mb-1">${part}</div>`).join('');
  host.hidden = parts.length === 0;
}

/** رمزی که کاربر در حال نوشتن است، با بازسازی پیام پاک نشود. */
function isTypingPin(host) {
  const active = document.activeElement;
  return !!active && host.contains(active) && active.matches('[data-offline-pin], [data-offline-new-pin]') && !!active.value;
}

function noticeHost() {
  const main = document.querySelector('main');
  if (!main) return null;
  let host = main.querySelector(':scope > [data-offline-notice]');
  if (!host) {
    host = document.createElement('div');
    host.className = 'alert ak-form-alert';
    host.setAttribute('data-offline-notice', '');
    host.setAttribute('data-offline-pin-host', '');
    host.setAttribute('role', 'status');
    host.setAttribute('aria-live', 'polite');
    host.hidden = true;
    main.prepend(host);
  }
  return host;
}

async function renderNotice(profile) {
  const host = noticeHost();
  if (!host || isTypingPin(host)) return;
  const parts = [];
  let tone = 'info';
  if (state.pageMessage) {
    parts.push(state.pageMessage.html);
    tone = state.pageMessage.tone;
  }
  const savedAt = document.documentElement.getAttribute(ATTR_COPY);
  const formPanels = formContexts().length > 0;
  if (savedAt) {
    parts.push(`حالت آفلاین — نسخهٔ ذخیره‌شده از ${h(formatDateTime(savedAt))} (${h(relativeAge(savedAt))}). `
      + 'ارقام، مانده‌ها و وضعیت‌ها لحظه‌ای و قطعی نیستند.'
      + (monitor.reachable ? ' <button type="button" class="ak-cancel" data-offline-reload>نمایش نسخهٔ تازه</button>' : ''));
    if (!state.pageMessage) tone = 'warning';
  } else if (!monitor.reachable && monitor.lastCheckAt && !formPanels) {
    parts.push(profile
      ? 'سرور در دسترس نیست. صفحات و فورم‌هایی که پیش‌تر باز شده‌اند بدون سرور باز می‌شوند؛ ثبت‌های مجاز در دستگاه می‌ماند و پس از اتصال خودکار فرستاده می‌شود.'
      : 'سرور در دسترس نیست و کار بدون سرور در این دستگاه فعال نشده است (فعال‌سازی: یک‌بار با اینترنت، در داشبورد مشعل).');
    if (!state.pageMessage) tone = 'warning';
  }
  const needsPin = profile && !state.session
    && (state.pendingNav || (state.counts?.pending > 0 && monitor.reachable && !formPanels));
  if (needsPin) {
    parts.push(state.pendingNav
      ? 'برای بازکردن نسخهٔ ذخیره‌شده، رمز محلی را وارد کنید (در این نشست فقط یک‌بار).'
      : `${formatNumber(state.counts.pending)} عملیات آفلاین منتظر ارسال است؛ برای ارسال خودکار، رمز محلی را وارد کنید.`);
    parts.push(pinHtml(profile, state.pendingNav ? 'بازکردن' : 'تأیید رمز'));
  } else if (!state.session && !hasSession() && monitor.reachable && !formPanels && offerSetup()) {
    if (!profile) {
      parts.push('<strong>کار بدون سرور در این دستگاه فعال نیست.</strong> با یک رمز محلی، همین مشعل (داشبورد، منو، صفحات و '
        + 'فورم‌های مجاز شما) هنگام قطع سرور هم باز می‌شود و ثبت‌های مجاز پس از اتصال خودکار فرستاده می‌شوند.');
      parts.push(activationHtml());
    } else if (!profile.pageKeys) {
      parts.push('برای آماده‌کردن صفحات مشعل در این دستگاه برای زمان قطع سرور، یک‌بار رمز محلی را وارد کنید.');
      parts.push(pinHtml(profile, 'تأیید رمز') + laterHtml());
    }
  }
  paint(host, tone, parts);
}

/**
 * پیشنهاد فعال‌سازی/آماده‌سازی در همان مشعل: در برنامهٔ نصب‌شده (PWA) در هر صفحه، در مرورگر فقط در داشبورد؛
 * «بعداً» آن را ۳۰ روز پنهان می‌کند. داخل مودال/iframe هرگز.
 */
function offerSetup() {
  if (window.top !== window || !('indexedDB' in window) || !window.isSecureContext) return false;
  try {
    const snoozed = Number(localStorage.getItem(OFFER_KEY + CONFIG.userId)) || 0;
    if (Date.now() - snoozed < OFFER_SNOOZE_MS) return false;
  } catch {
    // حالت خصوصی؛ پیشنهاد نشان داده می‌شود.
  }
  const standalone = window.matchMedia?.('(display-mode: standalone)').matches || navigator.standalone === true;
  return standalone || location.pathname === '/' || location.pathname.toLowerCase() === '/home';
}

function laterHtml() {
  return ' <button type="button" class="ak-cancel mt-2" data-offline-later>بعداً</button>';
}

function activationHtml() {
  const id = ++pinSeq;
  return `<div class="ak-field mt-2">
      <label class="ak-label" for="ptg-offline-new-${id}">رمز محلی (حداقل ۶ حرف یا رقم؛ فقط روی همین دستگاه)</label>
      <input class="ak-input" id="ptg-offline-new-${id}" type="password" autocomplete="new-password" data-offline-new-pin />
    </div>
    <div class="ak-field mt-2">
      <label class="ak-label" for="ptg-offline-new2-${id}">تکرار رمز محلی</label>
      <input class="ak-input" id="ptg-offline-new2-${id}" type="password" autocomplete="new-password" data-offline-new-pin />
    </div>
    <button type="button" class="ak-save mt-2" data-offline-activate>فعال‌سازی کار بدون سرور</button>${laterHtml()}`;
}

/** فعال‌سازی دستگاه در همان مشعل (همان createProfile و Registry برنامهٔ آفلاین؛ چیزی موازی ساخته نمی‌شود). */
async function onActivate(host) {
  const [pin1, pin2] = host.querySelectorAll('[data-offline-new-pin]');
  const button = host.querySelector('[data-offline-activate]');
  if (!pin1 || !pin2 || !button || !pin1.value) return;
  const fail = (message) => { state.pageMessage = { tone: 'danger', html: h(message) }; };
  button.disabled = true;
  try {
    if (pin1.value !== pin2.value) return fail('دو رمز یکسان نیست.');
    const pinError = validatePin(pin1.value);
    if (pinError) return fail(pinError);
    const session = await api.session();
    if (!session.ok || !session.data || Number(session.data.userId) !== CONFIG.userId) {
      return fail('نشست ورود سرور تأیید نشد؛ صفحه را تازه کنید و دوباره تلاش کنید.');
    }
    const { profile, dataKey, rawKey } = await createProfile({ session: session.data, pin: pin1.value, deviceId: uuid() });
    await (await openRegistry()).put('profiles', profile);
    pin1.value = '';
    pin2.value = '';
    const persisted = rememberSession(profile, rawKey);
    await openSession(profile, dataKey);
    state.session.persisted = persisted;
    navigator.storage?.persist?.().catch(() => {});
    state.pageMessage = {
      tone: 'success',
      html: 'کار بدون سرور در این دستگاه فعال شد. داشبورد، صفحات منو و فورم‌های مجاز شما در پس‌زمینه آماده می‌شوند.'
    };
    afterUnlock();
  } catch (e) {
    fail(isQuotaError(e) ? 'فضای ذخیره‌سازی دستگاه کافی نیست.' : (e?.message || 'فعال‌سازی انجام نشد.'));
  } finally {
    button.disabled = false;
    await refreshAll();
  }
}

function setPageMessage(tone, html) {
  state.pageMessage = html ? { tone, html } : null;
  return refreshAll();
}

// ---------------------------------------------------------------- forms

function formTarget(form) {
  try {
    const url = new URL(form.getAttribute('action') || location.href, location.href);
    return { url, path: url.pathname.toLowerCase().replace(/\/+$/, '') };
  } catch {
    return null;
  }
}

function contextOf(form) {
  if (!(form instanceof HTMLFormElement) || (form.getAttribute('method') || 'get').toLowerCase() !== 'post') return null;
  const target = formTarget(form);
  if (!target || target.url.origin !== location.origin) return null;
  // فورم‌های مودالِ صفحه (iframe با modal=1) و ثبت سریع هم همان فورم اصلی‌اند؛ فقط فورمِ درون‌خطیِ مودال Bootstrap نه.
  const offline = CONFIG.forms.has(target.path) && !form.closest('.modal');
  let panel = null;
  if (offline) {
    panel = form.querySelector(':scope > [data-offline-panel]');
    if (!panel) {
      panel = document.createElement('div');
      panel.className = 'alert ak-form-alert';
      panel.setAttribute('data-offline-panel', '');
      panel.setAttribute('data-offline-pin-host', '');
      panel.setAttribute('role', 'status');
      panel.setAttribute('aria-live', 'polite');
      panel.hidden = true;
      form.prepend(panel);
    }
  }
  return { form, path: target.path, url: target.url, offline, panel, quick: isQuickCreatePage() };
}

function formContexts() {
  return [...document.querySelectorAll('form')].map(contextOf).filter((ctx) => ctx?.offline);
}

function formLabel() {
  const heading = document.querySelector('main h1, main .ak-page-title, main h2');
  return (heading?.textContent || document.title || '').replace(/\s+/g, ' ').trim().slice(0, 120);
}

function formData(form, submitter) {
  try {
    return submitter ? new FormData(form, submitter) : new FormData(form);
  } catch {
    return new FormData(form);
  }
}

/** همان جفت‌های نام/مقدار که مرورگر می‌فرستد (+ querystringِ آدرس فورم). فایل آفلاین پشتیبانی نمی‌شود. */
function capture(ctx, submitter) {
  const fields = [];
  for (const [name, value] of formData(ctx.form, submitter)) {
    if (typeof value !== 'string') {
      if (value && (value.size > 0 || value.name)) {
        return { error: 'ضمیمه/فایل بدون سرور ذخیره نمی‌شود؛ فایل را پس از اتصال اضافه کنید یا فورم را آنلاین ثبت کنید.' };
      }
      continue;
    }
    if (name.startsWith('__RequestVerificationToken') || name === '__FormToken') continue;
    fields.push([name, value]);
  }
  return {
    payload: {
      path: ctx.path,
      label: formLabel(),
      fields,
      // پارامترهای مودال/ثبت سریع کنار گذاشته می‌شوند: سرور همان ثبت عادی را انجام می‌دهد (Redirect، نه JSONِ مودال).
      query: [...ctx.url.searchParams].filter(([name]) => !['modal', 'quickCreate', 'offlineEdit'].includes(name))
    }
  };
}

/**
 * اعتبارسنجی پیش از ذخیرهٔ محلی. آنلاین، سرور (ModelState) اعتبارسنجی می‌کند؛ بدون سرور همان قواعد مدل از
 * ویژگی‌های data-val-* (لازم، حداکثر طول، بازه) و required با همان پیام‌ها در همان جای خطای فیلد نمایش داده می‌شود.
 * سرور هنگام همگام‌سازی دوباره کامل می‌سنجد.
 */
function validateOffline(form) {
  let first = null;
  for (const input of form.querySelectorAll('input[name], select[name], textarea[name]')) {
    if (input.type === 'checkbox' || input.type === 'radio' || input.type === 'hidden' || input.type === 'file'
      || input.disabled || input.closest('[hidden]')) continue;
    const value = String(input.value || '').trim();
    const data = input.dataset;
    const max = Number(data.valMaxlengthMax || data.valLengthMax || 0);
    let message = '';
    if ((data.valRequired !== undefined || input.required) && !value) {
      message = data.valRequired || 'این فیلد لازم است.';
    } else if (max > 0 && value.length > max) {
      message = data.valMaxlength || data.valLength || '';
    } else if (value && data.valRangeMin !== undefined && Number(value) < Number(data.valRangeMin)) {
      message = data.valRange || '';
    } else if (value && data.valRangeMax !== undefined && Number(value) > Number(data.valRangeMax)) {
      message = data.valRange || '';
    }
    showFieldError(form, input, message);
    if (message && !first) first = input;
  }
  first?.focus();
  return !first;
}

function showFieldError(form, input, message) {
  const slot = [...form.querySelectorAll('[data-valmsg-for]')].find((el) => el.getAttribute('data-valmsg-for') === input.name);
  input.classList.toggle('input-validation-error', !!message);
  if (!slot) return;
  slot.textContent = message;
  slot.classList.toggle('field-validation-error', !!message);
  slot.classList.toggle('field-validation-valid', !message);
}

function resetForm(form) {
  form.reset();
  form.querySelectorAll('[name]').forEach((input) => showFieldError(form, input, ''));
  form.querySelector('input:not([type="hidden"]):not([data-offline-pin]), select, textarea')?.focus();
}

function setBusy(form, busy) {
  form.querySelectorAll('button[type="submit"], input[type="submit"]').forEach((button) => { button.disabled = busy; });
}

/** توکن ضد تکرار تازه (هر رشتهٔ یکتا را سرور می‌پذیرد) تا ارسال بعدی با ارسال نامعلوم قبلی یکی گرفته نشود. */
function rotateFormToken(form) {
  form.querySelectorAll('input[name="__FormToken"]').forEach((input) => { input.value = uuid().replace(/-/g, ''); });
}

function setMessage(ctx, tone, html) {
  state.messages.set(ctx.panel, { tone, html });
  return refreshAll();
}

function refreshPanel(ctx, profile) {
  const { panel } = ctx;
  if (!panel?.isConnected || isTypingPin(panel)) return;
  const message = state.messages.get(panel);
  const intent = state.intents.get(ctx.form);
  const parts = [];
  if (message) parts.push(message.html);
  if (!monitor.reachable && monitor.lastCheckAt && !intent) {
    parts.push(profile
      ? 'سرور در دسترس نیست. «ذخیره» همین فورم را در این دستگاه نگه می‌دارد و پس از اتصال، خودکار در سیستم ثبت می‌کند.'
      : 'سرور در دسترس نیست و این دستگاه برای ثبت آفلاین فعال نشده است؛ معلومات فورم فقط تا وقتی این صفحه باز است می‌ماند. '
        + 'فعال‌سازی: یک‌بار با اینترنت، در داشبورد مشعل.');
  }
  const counts = state.counts;
  if (counts?.pending) {
    parts.push(`${formatNumber(counts.pending)} عملیات آفلاین در این دستگاه منتظر همگام‌سازی است`
      + (state.session ? (monitor.reachable ? ' و خودکار فرستاده می‌شود.' : ' و پس از اتصال خودکار فرستاده می‌شود.') : '.'));
  }
  if (counts?.conflicts) parts.push(`${formatNumber(counts.conflicts)} عملیات ثبت نشد و نیازمند اصلاح است — ${OPS_LINK}.`);
  if (counts?.drafts) parts.push(`${formatNumber(counts.drafts)} پیش‌نویس دستگاه (پاسخ نامعلوم سرور)؛ پس از بررسی، از ${OPS_LINK} بفرستید.`);
  const needsPin = profile && !state.session && (intent || (counts?.pending && monitor.reachable));
  if (needsPin) {
    parts.push(`${intent ? 'برای ذخیره در دستگاه' : 'برای ارسال خودکار'}، رمز محلی مشعل آفلاین را وارد کنید (در این نشست فقط یک‌بار).`);
    parts.push(pinHtml(profile, intent ? 'ذخیره در دستگاه' : 'تأیید رمز'));
  }
  const tone = message?.tone || (intent || !monitor.reachable ? 'warning' : counts?.conflicts ? 'danger' : 'info');
  paint(panel, tone, parts);
}

async function refreshAll() {
  const profile = await profileForUser();
  if (profile) state.counts = await countRows(profile).catch(() => state.counts);
  publishStatus();
  for (const ctx of formContexts()) refreshPanel(ctx, profile);
  await renderNotice(profile).catch(() => {});
}

// ---------------------------------------------------------------- local save / edit

/** ذخیرهٔ همان فورم در Outbox (Pending = ارسال خودکار؛ Draft = منتظر بررسی کاربر). */
async function saveLocal(ctx, intent) {
  const { repo, engine } = state.session;
  let record;
  try {
    if (intent.status === Status.Draft) {
      record = await repo.saveDraft(FORM_SUBMIT, intent.payload, todayIso());
    } else {
      record = await repo.submit(FORM_SUBMIT, intent.payload, todayIso());
    }
  } catch (e) {
    state.intents.delete(ctx.form);
    await setMessage(ctx, 'danger', isQuotaError(e)
      ? 'فضای ذخیره‌سازی دستگاه پر است؛ ذخیره نشد. معلومات فورم روی صفحه باقی است.'
      : h(e.message || 'ذخیره در دستگاه انجام نشد.'));
    return;
  }
  state.intents.delete(ctx.form);
  if (ctx.quick && intent.status !== Status.Draft && completeQuickCreate(record)) {
    engine.run();
    return;
  }
  resetForm(ctx.form);
  if (intent.status === Status.Draft) {
    await setMessage(ctx, 'warning', `${h(intent.note)} برای جلوگیری از ثبت تکراری، معلومات به‌صورت پیش‌نویس در دستگاه ذخیره شد؛ `
      + `اگر در فهرست سیستم نیست، از ${OPS_LINK} بفرستید.`);
  } else {
    await setMessage(ctx, 'success', `${intent.note === NOTE.offline ? '' : h(intent.note) + ' '}در دستگاه ذخیره شد؛ پس از اتصال، خودکار در سیستم ثبت می‌شود.`);
    engine.run();
  }
  injectPendingOptions();
}

/** ثبت سریعِ آفلاین: همان سازوکار مودال (PTG.completeQuickCreate) رکورد «در انتظار ثبت» را در فهرست فورم اصلی می‌گذارد. */
function completeQuickCreate(record) {
  const fields = record?.payload?.fields || [];
  const name = NAME_FIELDS.map((n) => fields.find((pair) => pair[0] === n)?.[1]).find((v) => v && v.trim());
  try {
    return !!name && typeof window.parent?.PTG?.completeQuickCreate === 'function'
      && window.parent.PTG.completeQuickCreate({ id: `op:${record.clientOperationId}`, label: `${name} (در انتظار ثبت)` });
  } catch {
    return false;
  }
}

async function keepLocally(ctx, intent, submitter) {
  if (intent.status !== Status.Draft && !validateOffline(ctx.form)) return;
  const captured = capture(ctx, submitter);
  if (captured.error) {
    await setMessage(ctx, 'danger', h(captured.error));
    return;
  }
  intent.payload = intent.formToken ? { ...captured.payload, formToken: intent.formToken } : captured.payload;
  if (!(await profileForUser())) {
    await setMessage(ctx, 'warning', `${h(intent.note)} این دستگاه برای ثبت آفلاین فعال نشده است؛ معلومات فورم روی صفحه باقی است. `
      + 'پس از اتصال دوباره «ذخیره» کنید، یا یک‌بار با اینترنت «مشعل آفلاین» را فعال کنید.');
    return;
  }
  if (!(await ensureSession())) {
    state.intents.set(ctx.form, intent);
    await setMessage(ctx, 'warning', h(intent.note));
    ctx.panel.querySelector('[data-offline-pin]')?.focus();
    return;
  }
  await saveLocal(ctx, intent);
}

/** مقادیر عملیات ذخیره‌شده دوباره در همان فورم (برای اصلاح پس از تعارض یا پیش‌نویس). */
function fillForm(form, pairs) {
  const byName = new Map();
  for (const [name, value] of pairs) {
    if (!byName.has(name)) byName.set(name, []);
    byName.get(name).push(value ?? '');
  }
  // دور اول با رویداد change (منطق خود صفحه: نمایش بخش‌ها، گزینه‌های وابسته)؛ دور دوم بدون رویداد تا مقدار نهایی
  // دقیقاً همان معلوماتِ ذخیره‌شده باشد (منطق صفحه گاهی فیلد مخفیِ وابسته را عوض می‌کند).
  applyValues(form, byName, true);
  return applyValues(form, byName, false);
}

function applyValues(form, byName, notify) {
  let missing = 0;
  for (const [name, values] of byName) {
    const items = [...form.elements].filter((el) => el.name === name);
    if (!items.length) {
      missing += 1;
      continue;
    }
    const hasCheckbox = items.some((el) => el.type === 'checkbox');
    items.forEach((el, index) => {
      if (el.type === 'file' || (el.type === 'hidden' && hasCheckbox)) return;
      if (el.type === 'checkbox' || el.type === 'radio') {
        el.checked = values.includes(el.value || 'on');
      } else if (el.tagName === 'SELECT' && el.multiple) {
        [...el.options].forEach((option) => { option.selected = values.includes(option.value); });
      } else {
        const value = values[Math.min(index, values.length - 1)];
        if (el.tagName === 'SELECT' && value && ![...el.options].some((o) => o.value === value)) {
          el.add(new Option(value.startsWith('op:') ? 'رکورد تازهٔ آفلاین' : value, value));
        }
        el.value = value;
      }
      if (notify) el.dispatchEvent(new Event('change', { bubbles: true }));
    });
  }
  return missing;
}

async function enterEditMode(ctx, id) {
  if (!(await ensureSession())) {
    state.pendingNav = { url: location.href, push: false };
    return;
  }
  const record = await state.session.repo.get(id);
  if (!record || record.operationType !== FORM_SUBMIT || record.payload?.path !== ctx.path
    || !(record.status === Status.Draft || canResubmit(record))) {
    await setMessage(ctx, 'danger', 'این عملیات آفلاین برای این فورم پیدا نشد یا دیگر قابل اصلاح نیست.');
    return;
  }
  const missing = fillForm(ctx.form, record.payload.fields || []);
  state.edit = { id, form: ctx.form };
  await setMessage(ctx, 'info', `اصلاح عملیات آفلاین «${h(typeLabel(record.operationType, record.payload))}».`
    + (record.serverNote ? ` دلیل سرور: ${h(record.serverNote)}.` : '')
    + (missing ? ` ${formatNumber(missing)} فیلد (مثلاً سطرهای اضافه) در فورم پیدا نشد؛ آن‌ها را دوباره وارد کنید.` : '')
    + ' پس از «ذخیره»، همان عملیات (با همان شناسه، بدون ثبت دوباره) فرستاده می‌شود.');
}

async function saveEdit(ctx, submitter) {
  if (!validateOffline(ctx.form)) return;
  const captured = capture(ctx, submitter);
  if (captured.error) {
    await setMessage(ctx, 'danger', h(captured.error));
    return;
  }
  const { repo, engine } = state.session;
  const record = await repo.get(state.edit.id);
  const payload = record?.payload?.formToken ? { ...captured.payload, formToken: record.payload.formToken } : captured.payload;
  try {
    if (record?.status === Status.Draft) {
      await repo.submit(FORM_SUBMIT, payload, record.businessDate, { fromDraftId: record.clientOperationId });
    } else {
      await repo.resubmit(state.edit.id, payload, record?.businessDate);
    }
  } catch (e) {
    await setMessage(ctx, 'danger', h(e.message || 'ذخیره انجام نشد.'));
    return;
  }
  state.edit = null;
  const url = new URL(location.href);
  url.searchParams.delete('offlineEdit');
  history.replaceState(history.state, document.title, url.href);
  resetForm(ctx.form);
  await setMessage(ctx, 'success', 'اصلاح ذخیره شد؛ همان عملیات دوباره و خودکار فرستاده می‌شود.');
  engine.run();
}

// ---------------------------------------------------------------- online submit

/** نسخهٔ ذخیره‌شدهٔ صفحه توکن‌های کهنه دارد؛ توکن تازه از همان فورم گرفته می‌شود. */
async function refreshTokens(ctx) {
  try {
    const response = await fetch(location.pathname + location.search, {
      credentials: 'same-origin', cache: 'no-store', headers: { 'X-PTG-SPA': '1' }
    });
    if (isLoginUrl(response.url) || response.status === 401) return { auth: true };
    if (!response.ok) return { network: true };
    const doc = new DOMParser().parseFromString(await response.text(), 'text/html');
    const token = doc.querySelector('input[name="__RequestVerificationToken"]')?.value;
    if (!token) return { auth: true };
    ctx.form.querySelectorAll('input[name="__RequestVerificationToken"]').forEach((input) => { input.value = token; });
    rotateFormToken(ctx.form);
    return { token };
  } catch {
    return { network: true };
  }
}

/** همان POST فعلی فورم. نتیجه: {done} | {error} | {status, note, formToken?} برای نگهداری محلی. */
async function submitOnline(ctx, submitter) {
  if (isCopy() || ctx.form.dataset.offlineStaleTokens === '1') {
    const fresh = await refreshTokens(ctx);
    if (fresh.network) return { status: Status.Pending, note: NOTE.offline };
    if (fresh.auth) return { status: Status.Pending, note: NOTE.auth };
    delete ctx.form.dataset.offlineStaleTokens;
  }
  const data = formData(ctx.form, submitter);
  const usedToken = data.get('__FormToken');
  const unknown = () => {
    rotateFormToken(ctx.form);
    return usedToken
      ? { status: Status.Pending, note: NOTE.unknownToken, formToken: String(usedToken) }
      : { status: Status.Draft, note: NOTE.unknown };
  };
  let response;
  try {
    response = await fetch(ctx.form.action, {
      method: 'POST', body: data, credentials: 'same-origin', redirect: 'follow', headers: { 'X-PTG-SPA': '1' }
    });
  } catch {
    return unknown();
  }
  // پیش از اجرای اکشن رد شد (ورود/توکن) یا پروکسی سرور را نیافت: هیچ رکوردی ساخته نشده است.
  if (isLoginUrl(response.url) || response.status === 400 || response.status === 401) {
    ctx.form.dataset.offlineStaleTokens = '1';
    return { status: Status.Pending, note: NOTE.auth };
  }
  if (response.status === 502 || response.status === 503) return { status: Status.Pending, note: NOTE.gateway };
  if (response.status === 504) return unknown();
  if (response.status === 403) return { error: 'شما اجازهٔ این ثبت را ندارید.' };
  if (!response.ok) return { error: `خطای سرور (${response.status}). معلومات فورم روی صفحه باقی است؛ دوباره «ذخیره» کنید.` };

  let html;
  try {
    html = await response.text();
  } catch {
    return unknown();
  }
  document.documentElement.removeAttribute(ATTR_COPY);
  state.pageMessage = null;
  // ثبت موفق: صفحهٔ مقصد (مثلاً فهرست با رکورد تازه) همان پاسخ GETِ سرور است و برای خواندن آفلاین تازه می‌شود.
  if (response.redirected) capturePage(response.url, html);
  if (window.PTG && typeof window.PTG.spaApplyHtml === 'function') {
    // موفق: صفحهٔ مقصد (با پیام موفقیت)؛ خطای اعتبارسنجی: همان فورم با پیام سرور، بدون ورودی تاریخچه.
    await window.PTG.spaApplyHtml(response.url, html, response.redirected);
  } else {
    document.open();
    document.write(html);
    document.close();
  }
  return { done: true };
}

function isLogout(form) {
  return /\/auth\/logout/i.test(form.getAttribute('action') || '');
}

async function onSubmit(event) {
  const form = event.target;
  if (!(form instanceof HTMLFormElement)) return;
  if (form.dataset.offlinePass === '1') {
    delete form.dataset.offlinePass;
    return;
  }
  if (isLogout(form)) return;
  const ctx = contextOf(form);
  if (!ctx) return;

  if (!ctx.offline) {
    // فورم فقط-آنلاین: وقتی آخرین بررسی «سرور در دسترس نیست» گفته، پیش از ارسال دوباره سنجیده می‌شود تا معلومات نرود.
    if (monitor.reachable || !monitor.lastCheckAt) return;
    event.preventDefault();
    event.stopImmediatePropagation();
    if (await monitor.check()) {
      form.dataset.offlinePass = '1';
      try {
        form.requestSubmit(event.submitter && event.submitter.form === form ? event.submitter : undefined);
      } catch {
        form.requestSubmit();
      }
      return;
    }
    await setPageMessage('warning', 'سرور در دسترس نیست. این عملیات فقط با اتصال به سرور ثبت می‌شود (وابسته به وضعیت لحظه‌ای سرور یا بدون ضمانت ضد تکرار)؛ '
      + 'معلومات فورم روی صفحه باقی است — پس از اتصال دوباره «ذخیره» کنید.');
    document.querySelector('[data-offline-notice]')?.scrollIntoView({ block: 'nearest' });
    return;
  }

  const pendingRef = hasPendingReference(form);
  if (ctx.quick && !pendingRef && state.edit?.form !== form) {
    // ثبت سریع آنلاین همان جریان core.js (JSON به فورم اصلی) است؛ فقط وقتی سرور در دسترس نیست این‌جا ذخیره می‌شود.
    // نسخهٔ ذخیره‌شدهٔ مودال (بدون سرور باز شده) پیش از نخستین بررسی اتصال هم به‌جای ارسال کور، اول می‌سنجد.
    if (monitor.reachable && (monitor.lastCheckAt || !isCopy())) return;
    event.preventDefault();
    event.stopImmediatePropagation();
    if (await monitor.check()) {
      form.dataset.offlinePass = '1';
      form.requestSubmit();
      return;
    }
    await keepLocally(ctx, { status: Status.Pending, note: NOTE.offline }, event.submitter);
    return;
  }

  // فورم مجاز آفلاین: تصمیم آنلاین/آفلاین با پاسخ واقعی سرور است؛ ارسال عادی (spa-nav) این‌جا گرفته می‌شود.
  event.preventDefault();
  event.stopImmediatePropagation();
  if (form.dataset.offlineBusy === '1') return;
  state.messages.delete(ctx.panel);
  form.dataset.offlineBusy = '1';
  setBusy(form, true);
  try {
    if (state.edit?.form === form && state.session) {
      await saveEdit(ctx, event.submitter);
      return;
    }
    const reachable = await monitor.check();
    const outcome = pendingRef
      ? { status: Status.Pending, note: NOTE.pendingRef }
      : reachable ? await submitOnline(ctx, event.submitter) : { status: Status.Pending, note: NOTE.offline };
    if (outcome.done) return;
    if (outcome.error) {
      await setMessage(ctx, 'danger', h(outcome.error));
      return;
    }
    await keepLocally(ctx, outcome, event.submitter);
  } catch (e) {
    await setMessage(ctx, 'danger', h(e?.message || 'ذخیره انجام نشد؛ معلومات فورم روی صفحه باقی است.'));
  } finally {
    delete form.dataset.offlineBusy;
    setBusy(form, false);
  }
}

// ---------------------------------------------------------------- PIN

async function onPin(host) {
  const input = host.querySelector('[data-offline-pin]');
  const button = host.querySelector('[data-offline-pin-ok]');
  if (!input || !input.value) return;
  button.disabled = true;
  const panelForm = host.matches('[data-offline-panel]') ? host.closest('form') : null;
  const ctx = panelForm ? contextOf(panelForm) : null;
  try {
    const result = await unlockWithPin(input.value);
    input.value = '';
    input.blur();
    if (!result.ok) {
      if (ctx) state.messages.set(ctx.panel, { tone: 'danger', html: result.message });
      else state.pageMessage = { tone: 'danger', html: result.message };
      return;
    }
    if (!ctx) state.pageMessage = null;
    const intent = ctx && state.intents.get(ctx.form);
    if (intent) {
      await saveLocal(ctx, intent);
    } else if (ctx) {
      state.messages.delete(ctx.panel);
    }
    const nav = state.pendingNav;
    state.pendingNav = null;
    if (nav) {
      if (monitor.reachable) location.assign(nav.url);
      else await renderFromVault(nav.url, nav.push);
    }
    afterUnlock();
  } finally {
    button.disabled = false;
    await refreshAll();
    document.querySelector('[data-offline-pin]')?.focus();
  }
}

function afterUnlock() {
  injectPendingOptions();
  editFromUrl();
  if (monitor.reachable) {
    captureCurrent();
    warmUp();
  }
}

// ---------------------------------------------------------------- pages (encrypted copies)

async function capturePage(url, html, full = false) {
  if (!html || !url || !cacheable(url) || !/<main[\s>]/i.test(html)) return;
  const vault = await pageWriter();
  if (!vault) return;
  await vault.put(url, html, { full }).catch(() => {});
  warmAssets(html, url);
}

/** پوستهٔ کامل Layout (منو، سربرگ) + فایل‌های CSS/JSِ آن؛ نشانی CSSها برای صفحهٔ رمزِ بدون سرور. */
async function captureShell(vault, html) {
  await vault.putShell(html);
  warmAssets(html, location.href);
  const styles = [...html.matchAll(/<link\b[^>]*\brel="stylesheet"[^>]*>/gi)]
    .map((m) => /\bhref="([^"]+)"/i.exec(m[0])?.[1]?.replace(/&amp;/g, '&'))
    .filter((href) => href && href.startsWith('/') && !href.startsWith('//'));
  const profile = await profileForUser();
  if (profile && styles.length) await rememberShellStyles(profile.id, styles).catch(() => {});
}

/** CSS/JSِ همان صفحه به Service Worker سپرده می‌شود تا نسخهٔ ذخیره‌شده بدون سرور هم با همان ظاهر و رفتار باز شود. */
function warmAssets(html, base) {
  const worker = navigator.serviceWorker?.controller;
  if (!worker) return;
  const urls = new Set();
  for (const match of html.matchAll(/<(?:link|script)\b[^>]*?\b(?:href|src)="([^"]+)"/gi)) {
    try {
      const url = new URL(match[1].replace(/&amp;/g, '&'), base);
      if (url.origin === location.origin) urls.add(url.href);
    } catch {
      // نادیده
    }
  }
  if (urls.size) worker.postMessage({ type: 'mashal:cache-assets', urls: [...urls] });
}

const isHtml = (response) => response.ok && !response.redirected && (response.headers.get('Content-Type') || '').includes('text/html');

/** صفحهٔ فعلی (و پوستهٔ کامل Layout) اگر نسخهٔ تازه ندارد، یک‌بار در پس‌زمینه گرفته و رمزگذاری می‌شود. */
async function captureCurrent() {
  if (isCopy() || !monitor.reachable || !cacheable(location.href)) return;
  const vault = await pageWriter();
  if (!vault) return;
  try {
    if (isModalUrl(location.href)) {
      // صفحهٔ مودال: سند کامل خودش (بدون پوستهٔ منو) نگه داشته می‌شود.
      const savedAt = await vault.savedAt(location.href);
      if (savedAt && Date.now() - new Date(savedAt).getTime() < PAGE_REFRESH_MS) return;
      const response = await fetch(location.href, { credentials: 'same-origin', cache: 'no-store' });
      if (isHtml(response)) await capturePage(location.href, await response.text(), true);
      return;
    }
    const shell = (await vault.index()).find((e) => e.key === 'shell');
    if (!shell || Date.now() - new Date(shell.savedAt).getTime() > SHELL_REFRESH_MS) {
      const response = await fetch(location.href, { credentials: 'same-origin', cache: 'no-store' });
      if (isHtml(response)) await captureShell(vault, await response.text());
    }
    const savedAt = await vault.savedAt(location.href);
    if (savedAt && Date.now() - new Date(savedAt).getTime() < PAGE_REFRESH_MS) return;
    const response = await fetch(location.href, { credentials: 'same-origin', cache: 'no-store', headers: { 'X-PTG-SPA': '1' } });
    if (isHtml(response)) await capturePage(location.href, await response.text());
  } catch {
    // دفعهٔ بعد.
  }
}

/**
 * صفحاتی که برای کار بدون سرور آماده می‌شوند: داشبورد، همهٔ گزینه‌های منوی کناری همین کاربر (سرور فقط گزینه‌های مجاز نقش
 * او را می‌سازد)، فورم‌های مجاز ثبت آفلاین، فهرست ماژول آن‌ها و فورم‌های ثبت سریع (مودال).
 */
function warmUrls() {
  const urls = new Set(['/']);
  for (const link of document.querySelectorAll('#boltzSidebar .boltz-sidebar-nav a[href]')) {
    try {
      const url = new URL(link.getAttribute('href'), location.href);
      if (cacheable(url.href)) urls.add(url.pathname + url.search);
    } catch {
      // نادیده
    }
  }
  for (const path of CONFIG.forms) {
    urls.add(path);
    urls.add('/' + path.split('/')[1]);
  }
  // ثبت سریعِ طرف‌حساب/وسیله از داخل فورم‌های دیگر (مودال)، همان نشانی که core.js باز می‌کند.
  for (const path of new Set(Object.values(REFERENCE_FORMS))) {
    if (CONFIG.forms.has(path)) urls.add(`${path}?modal=1&quickCreate=1`);
  }
  return [...urls];
}

/**
 * آماده‌سازی کار بدون سرور (آرام، پشت‌سرهم، در پس‌زمینه): هر ۶ ساعت یا وقتی فهرست منو/فورم‌ها عوض شود. صفحه‌ای که
 * کمتر از ۳ ساعت پیش ذخیره شده دوباره گرفته نمی‌شود. بدون رمز محلی هم کار می‌کند (pageWriter).
 */
async function warmUp() {
  if (state.warming || isCopy() || !monitor.reachable || document.visibilityState === 'hidden') return;
  let vault = await pageWriter();
  if (!vault) return;
  const urls = warmUrls();
  const signature = urls.join('|');
  const meta = await vault.store.get('meta', 'pageWarmup').catch(() => null);
  if (meta && meta.signature === signature && Date.now() - new Date(meta.at).getTime() < WARM_EVERY_MS) return;
  state.warming = true;
  try {
    for (const url of urls) {
      if (!monitor.reachable) return;
      vault = await pageWriter();
      if (!vault) return;
      const absolute = new URL(url, location.href).href;
      try {
        const savedAt = await vault.savedAt(absolute);
        if (savedAt && Date.now() - new Date(savedAt).getTime() < WARM_EVERY_MS / 2) continue;
        const modal = isModalUrl(url);
        const response = await fetch(url, { credentials: 'same-origin', cache: 'no-store', headers: modal ? {} : { 'X-PTG-SPA': '1' } });
        if (isHtml(response)) await capturePage(absolute, await response.text(), modal);
      } catch {
        // صفحهٔ بعدی.
      }
      await new Promise((resolve) => setTimeout(resolve, 150));
    }
    await vault.store.put('meta', { key: 'pageWarmup', at: new Date().toISOString(), signature }).catch(() => {});
  } finally {
    state.warming = false;
  }
}

/** بدون سرور: همان صفحه از نسخهٔ رمزگذاری‌شده، با همان ناوبری SPA و همان ظاهر. */
async function renderFromVault(url, push) {
  if (!(await ensureSession())) {
    if (await profileForUser()) {
      state.pendingNav = { url, push };
      await refreshAll();
      document.querySelector('[data-offline-notice] [data-offline-pin]')?.focus();
    } else {
      await setPageMessage('warning', 'سرور در دسترس نیست و این دستگاه برای کار آفلاین فعال نشده است.');
    }
    return;
  }
  const page = await state.session.vault.get(url).catch(() => null);
  if (!page) {
    await setPageMessage('warning', 'سرور در دسترس نیست و این صفحه هنوز برای دسترسی آفلاین ذخیره نشده است. '
      + 'صفحاتی که یک‌بار آنلاین باز شوند، بدون سرور هم باز می‌شوند. ' + OPS_LINK);
    return;
  }
  state.pageMessage = null;
  document.documentElement.setAttribute(ATTR_COPY, page.savedAt);
  await window.PTG.spaApplyHtml(new URL(url, location.href).href, page.html, push !== false);
}

// ---------------------------------------------------------------- lookups (JSON)

/**
 * فهرست‌های وابستهٔ فورم‌ها (fetchِ GETِ هم‌دامنه با پاسخ JSON، مثلاً گزینه‌های طرف قرارداد) آنلاین همان پاسخ سرور را
 * می‌گیرند و رمزگذاری‌شده نگه داشته می‌شوند؛ بدون سرور آخرین پاسخِ همان آدرس برگردانده می‌شود. درخواست‌های دیگر
 * (HTML، POST، API همگام‌سازی) دست نمی‌خورند.
 */
function installLookupCache() {
  const nativeFetch = window.fetch.bind(window);
  const lookupUrl = (input, init) => {
    try {
      const method = String(init?.method || (input instanceof Request ? input.method : 'GET')).toUpperCase();
      const url = new URL(input instanceof Request ? input.url : String(input), location.href);
      return method === 'GET' && url.origin === location.origin && !/^\/(api|offline)\//i.test(url.pathname) ? url : null;
    } catch {
      return null;
    }
  };
  const fromVault = async (url) => {
    const entry = state.session ? await state.session.vault.getData(url.href).catch(() => null) : null;
    return entry ? new Response(entry.body, {
      status: 200,
      headers: { 'Content-Type': entry.contentType || 'application/json', 'X-PTG-Offline-Copy': entry.savedAt }
    }) : null;
  };
  window.fetch = async (input, init) => {
    const url = lookupUrl(input, init);
    if (!url) return nativeFetch(input, init);
    try {
      const response = await nativeFetch(input, init);
      const type = response.headers.get('Content-Type') || '';
      if (response.ok && type.includes('json')) {
        response.clone().text().then(async (body) => (await pageWriter())?.putData(url.href, body, type)).catch(() => {});
      } else if (response.status >= 502 && response.status <= 504) {
        return (await fromVault(url)) || response;
      }
      return response;
    } catch (error) {
      if (error?.name === 'AbortError') throw error;
      const cached = await fromVault(url);
      if (cached) return cached;
      throw error;
    }
  };
}

// ---------------------------------------------------------------- pending references

/** رکوردی که آفلاین ساخته شد (مشتری، موتر، …) در فهرست‌های انتخاب فورم‌های بعدی: پیش از ثبت با op:، پس از ثبت با شناسهٔ واقعی. */
async function injectPendingOptions() {
  if (!state.session) return;
  let records;
  try {
    records = await state.session.repo.list();
  } catch {
    return;
  }
  const items = [];
  for (const record of records) {
    if (record.status === Status.Draft || record.status === Status.Rejected) continue;
    let path = null;
    let name = '';
    if (record.operationType === FORM_SUBMIT) {
      path = record.payload?.path;
      const fields = record.payload?.fields || [];
      name = NAME_FIELDS.map((n) => fields.find((pair) => pair[0] === n)?.[1]).find((v) => v && v.trim()) || '';
    } else if (record.operationType === 'Customer.Draft') {
      path = '/customers/create';
      name = record.payload?.name || '';
    }
    if (!path || !name) continue;
    const realId = record.status === Status.Applied && record.appliedRef ? (record.appliedRef.match(/(\d+)\s*$/) || [])[1] : null;
    items.push({ path, value: realId || `op:${record.clientOperationId}`, label: `${name} (${realId ? 'تازه' : 'در انتظار ثبت'})` });
  }
  for (const select of document.querySelectorAll('form select[name]')) {
    const path = REFERENCE_FORMS[select.name.split('.').pop().toLowerCase()];
    if (!path) continue;
    for (const item of items.filter((i) => i.path === path)) {
      if ([...select.options].some((o) => o.value === item.value)) continue;
      const option = new Option(item.label, item.value);
      option.dataset.offlineRef = '1';
      select.add(option);
    }
  }
}

/**
 * نسخهٔ ذخیره‌شدهٔ فورم، تاریخ پیش‌فرضِ «امروزِ» روز ذخیره را دارد. بدون سرور، تاریخ‌های پیش‌فرضِ دست‌نخورده
 * (برابر روز ذخیره یا data-ptg-today) به امروزِ دستگاه برده می‌شوند تا سند با تاریخ کهنه ثبت نشود.
 */
function refreshStaleDates() {
  const savedAt = document.documentElement.getAttribute(ATTR_COPY);
  if (!savedAt) return;
  const savedDay = todayIso(new Date(savedAt));
  const today = todayIso();
  if (savedDay === today) return;
  for (const ctx of formContexts()) {
    for (const input of ctx.form.querySelectorAll('input[type="date"]')) {
      if (input.dataset.offlineDateRefreshed === '1') continue;
      const pageToday = input.getAttribute('data-ptg-today');
      if (input.value && (input.value === savedDay || input.value === pageToday)) {
        input.value = today;
        input.dispatchEvent(new Event('input', { bubbles: true }));
        input.dispatchEvent(new Event('change', { bubbles: true }));
      }
      if (pageToday) input.setAttribute('data-ptg-today', today);
      input.dataset.offlineDateRefreshed = '1';
    }
  }
}

function editFromUrl() {
  const id = new URL(location.href).searchParams.get('offlineEdit');
  if (!id || state.edit) return;
  const ctx = formContexts()[0];
  if (ctx) enterEditMode(ctx, id).then(refreshAll);
}

// ---------------------------------------------------------------- lifecycle

async function scan() {
  await ensureSession().catch(() => null);
  refreshStaleDates();
  injectPendingOptions();
  editFromUrl();
  await refreshAll();
  if (monitor.reachable) {
    captureCurrent();
    warmUp();
  }
}

function start() {
  installLookupCache();
  window.PTG = window.PTG || {};
  window.PTG.offlineNavigate = (url, push) => {
    monitor.reachable = false;
    monitor.emit();
    renderFromVault(url, push);
    return true;
  };
  window.PTG.offlinePostFailed = () => {
    setPageMessage('danger', 'ارسال به سرور انجام نشد (سرور در دسترس نیست یا پاسخ نداد). معلومات فورم روی صفحه باقی است. '
      + 'اگر مطمئن نیستید ثبت شد یا نه، پس از اتصال ابتدا فهرست را بررسی کنید.');
    monitor.check();
    return true;
  };

  window.addEventListener('submit', onSubmit, true);
  document.addEventListener('click', (event) => {
    const button = event.target.closest?.('[data-offline-pin-ok]');
    const host = button?.closest('[data-offline-pin-host]');
    if (host) {
      event.preventDefault();
      onPin(host);
      return;
    }
    const activateHost = event.target.closest?.('[data-offline-activate]')?.closest('[data-offline-pin-host]');
    if (activateHost) {
      event.preventDefault();
      onActivate(activateHost);
      return;
    }
    if (event.target.closest?.('[data-offline-later]')) {
      event.preventDefault();
      try { localStorage.setItem(OFFER_KEY + CONFIG.userId, String(Date.now())); } catch { /* حالت خصوصی */ }
      refreshAll();
      return;
    }
    if (event.target.closest?.('[data-offline-reload]')) {
      event.preventDefault();
      document.documentElement.removeAttribute(ATTR_COPY);
      location.reload();
    }
  }, true);
  document.addEventListener('keydown', (event) => {
    if (event.key !== 'Enter' || !event.target.matches?.('[data-offline-pin], [data-offline-new-pin]')) return;
    // Enter در فیلد رمز نباید فورم اصلی را بفرستد.
    event.preventDefault();
    const host = event.target.closest('[data-offline-pin-host]');
    if (!host) return;
    if (event.target.matches('[data-offline-new-pin]')) onActivate(host);
    else onPin(host);
  }, true);
  for (const type of ['pointerdown', 'keydown', 'touchstart']) {
    document.addEventListener(type, () => {
      state.lastActivity = Date.now();
      touchSession();
    }, { passive: true });
  }

  window.addEventListener('ptg:page-html', (event) => {
    // صفحهٔ تازه از سرور: نسخهٔ قبلی جایگزین می‌شود و نشانگر «نسخهٔ ذخیره» برداشته می‌شود.
    document.documentElement.removeAttribute(ATTR_COPY);
    state.pageMessage = null;
    capturePage(event.detail?.url, event.detail?.html);
  });
  window.addEventListener('ptg:page-ready', () => { scan(); });

  let wasReachable = monitor.reachable;
  monitor.onChange(() => {
    const cameOnline = monitor.reachable && !wasReachable;
    wasReachable = monitor.reachable;
    if (cameOnline) {
      state.session?.engine.run();
      captureCurrent();
    }
    refreshAll();
  });
  window.addEventListener('online', () => monitor.check());
  window.addEventListener('offline', () => monitor.check());
  document.addEventListener('visibilitychange', () => { if (document.visibilityState === 'visible') monitor.check(); });
  setInterval(async () => {
    if (state.session && (Date.now() - state.lastActivity > SESSION_IDLE_MS || (state.session.persisted && !hasSession()))) {
      // ۱۵ دقیقه بی‌فعالیتی یا خروج: کلید از حافظه برداشته می‌شود؛ معلومات دستگاه می‌ماند.
      closeSession();
      refreshAll();
    }
    if (document.visibilityState === 'hidden') return;
    await monitor.check();
    if (state.session && monitor.reachable) state.session.engine.run();
  }, CHECK_MS);
}

if (CONFIG.userId > 0) {
  start();
  monitor.check().finally(scan);
}
