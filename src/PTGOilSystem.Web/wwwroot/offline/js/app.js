// مشعل آفلاین — برنامهٔ اصلی (رابط دری، RTL، بدون فریم‌ورک).

import { IdbStore, REGISTRY_DB, REGISTRY_STORES, isQuotaError, profileDbName } from './store.js';
import { MIN_PIN_LENGTH, Sealer, changePin, createProfile, unlockProfile, validatePin } from './vault.js';
import { ApiClient } from './api.js';
import { ConnState, ConnectivityMonitor } from './connectivity.js';
import { SyncEngine } from './sync-engine.js';
import { DeviceAuth } from './device-auth.js';
import { OPERATION_FORMS, OPERATION_REFERENCE_PREFIX, OutboxRepo, STATUS_LABELS, STATUS_SHORT, Status, baseVersionFrom, canResubmit, isOperationReference, normalizePayload, typeLabel, validatePayload } from './operations.js';
import { MIN_BACKUP_PASSWORD, backupFileName, backupWarning, createBackup, mergeBackup, openBackup } from './backup.js';
import { formatDateTime, formatNumber, relativeAge, todayIso, uuid } from './util.js';
import { ensurePageKeys, forgetSession, rememberSession, restoreSession } from './session.js';
import { FORM_SUBMIT } from './operations.js';

const IDLE_LOCK_MS = 15 * 60_000;
const AUTO_SYNC_MS = 60_000;
const LOGIN_URL = '/Auth/Login?returnUrl=' + encodeURIComponent('/offline/index.html');
const STATUS_KEY = 'mashal.offline.status';
const STORAGE_WARN_RATIO = 0.8;
const STORAGE_MIN_FREE = 50 * 1024 * 1024;

const api = new ApiClient();
const monitor = new ConnectivityMonitor(api);

const app = {
  registry: null,
  profile: null,
  store: null,
  repo: null,
  sealer: null,
  engine: null,
  snapshot: null,
  records: [],
  opsFilter: 'active',
  lastActivity: Date.now(),
  storage: null,
  swVersion: null
};

const main = document.getElementById('ox-main');
const $ = (selector, root = document) => root.querySelector(selector);
const $$ = (selector, root = document) => [...root.querySelectorAll(selector)];

function h(value) {
  return String(value ?? '').replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
}

// ---------------------------------------------------------------- UI helpers

let toastTimer = null;
function toast(message) {
  const el = $('[data-toast]');
  el.textContent = message;
  el.hidden = false;
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => { el.hidden = true; }, 4500);
}

function banner(html, tone = 'warn') {
  const el = $('[data-banner]');
  if (!html) {
    el.hidden = true;
    el.innerHTML = '';
    return;
  }
  el.dataset.tone = tone;
  el.innerHTML = html;
  el.hidden = false;
}

const CONN_TEXT = {
  [ConnState.Online]: 'آنلاین',
  [ConnState.Offline]: 'آفلاین',
  [ConnState.Syncing]: 'در حال همگام‌سازی'
};

function updateConnUi() {
  const el = $('[data-conn]');
  el.dataset.conn = monitor.state;
  $('[data-conn-text]').textContent = CONN_TEXT[monitor.state];
  const button = $('[data-sync-now]');
  button.hidden = !app.profile;
  button.disabled = monitor.state === ConnState.Syncing;
}

function setActiveTab(name) {
  for (const a of $$('[data-tab]')) {
    if (a.dataset.tab === name) a.setAttribute('aria-current', 'page');
    else a.removeAttribute('aria-current');
  }
}

function counts() {
  const c = Object.fromEntries(Object.values(Status).map((s) => [s, 0]));
  for (const r of app.records) c[r.status] = (c[r.status] || 0) + 1;
  return c;
}

function publishStatus() {
  const c = counts();
  const pending = c.Pending + c.Sending;
  const badge = $('[data-pending-badge]');
  badge.hidden = pending === 0;
  badge.textContent = formatNumber(pending);
  try {
    localStorage.setItem(STATUS_KEY, JSON.stringify({
      pending, conflicts: c.Conflict + c.Rejected, at: new Date().toISOString()
    }));
  } catch {
    // حالت خصوصی مرورگر؛ فقط شمارندهٔ سربرگ وب به‌روز نمی‌شود.
  }
}

function pendingRecordName(value) {
  const record = app.records.find((r) => OPERATION_REFERENCE_PREFIX + r.clientOperationId === value);
  return record ? `${record.payload?.name || typeLabel(record.operationType, record.payload)} (تازه)` : 'رکورد تازه';
}

function refName(source, id) {
  if (isOperationReference(String(id))) return pendingRecordName(String(id));
  const list = app.snapshot?.reference?.[source] || [];
  const item = list.find((x) => String(x.id) === String(id));
  return item ? item.name : `#${id}`;
}

function fieldDisplay(field, value) {
  if (value === undefined || value === null || value === '') return '—';
  if (field.kind === 'ref') return refName(field.source, value);
  if (field.kind === 'number') return formatNumber(value);
  if (field.kind === 'flag') return value === true || value === 'true' ? 'بلی' : 'خیر';
  return String(value);
}

/** عملیاتِ ثبت‌شده در فورم اصلی مشعل: اصلاح در همان فورم (همان شناسه). */
function mainFormUrl(record) {
  const p = record.payload || {};
  // عملیات روی رکورد (لغو از فهرست، …): صفحه‌ای که فورم در آن بود؛ ثبت تازه: خود آدرس فورم.
  const url = new URL(p.page || p.path || '/', location.origin);
  if (!p.page) for (const [name, value] of p.query || []) url.searchParams.append(name, value ?? '');
  url.searchParams.set('offlineEdit', record.clientOperationId);
  return url.pathname + url.search;
}

function formSubmitDetails(p) {
  const shown = (p.fields || []).filter(([name, value]) => value && !/^(__|.*\.Index$)/.test(name)).slice(0, 40);
  return `<dt>فورم</dt><dd><code>${h(p.path || '')}</code></dd>
    ${shown.map(([name, value]) => `<dt>${h(name)}</dt><dd>${h(String(value).slice(0, 200))}</dd>`).join('')}
    ${(p.fields || []).length > shown.length ? `<dt>فیلدهای دیگر</dt><dd>${formatNumber((p.fields || []).length - shown.length)}</dd>` : ''}`;
}

function summarize(record) {
  if (record.operationType === FORM_SUBMIT) return record.payload?.path || '';
  const form = OPERATION_FORMS[record.operationType];
  if (!form) return '';
  const p = record.payload || {};
  const parts = [];
  for (const field of form.fields) {
    if (p[field.name] === undefined) continue;
    if (field.kind === 'ref' && field.required) parts.push(fieldDisplay(field, p[field.name]));
    if (field.kind === 'number' && field.required) parts.push(formatNumber(p[field.name]) + (p.currency && field.name === 'amount' ? ' ' + p.currency : ''));
    if (field.kind === 'text' && field.required) parts.push(String(p[field.name]).slice(0, 40));
  }
  return parts.join(' · ');
}

async function readMeta(key) {
  return app.store ? (await app.store.get('meta', key)) || null : null;
}

// ---------------------------------------------------------------- storage health

async function refreshStorage() {
  const info = { supported: !!navigator.storage?.estimate, persisted: null, usage: 0, quota: 0, level: 'ok' };
  try {
    if (navigator.storage?.persisted) info.persisted = await navigator.storage.persisted();
    if (info.supported) {
      const estimate = await navigator.storage.estimate();
      info.usage = estimate.usage || 0;
      info.quota = estimate.quota || 0;
      const free = info.quota - info.usage;
      if (info.quota && (info.usage / info.quota > 0.95 || free < STORAGE_MIN_FREE / 2)) info.level = 'danger';
      else if (info.quota && (info.usage / info.quota > STORAGE_WARN_RATIO || free < STORAGE_MIN_FREE)) info.level = 'warn';
    }
  } catch {
    info.supported = false;
  }
  app.storage = info;
  return info;
}

async function requestPersist() {
  try {
    if (navigator.storage?.persist && !(await navigator.storage.persisted())) {
      await navigator.storage.persist();
    }
  } catch {
    // مرورگر اجازه نداد؛ در تنظیمات نشان داده می‌شود.
  }
}

function mb(bytes) {
  return formatNumber(Math.round((bytes / (1024 * 1024)) * 10) / 10) + ' MB';
}

function storageHtml(info) {
  if (!info || !info.supported) {
    return '<p class="ox-muted">مرورگر اطلاعات فضای ذخیره‌سازی را نمی‌دهد.</p>';
  }
  const ratio = info.quota ? Math.min(100, Math.round((info.usage / info.quota) * 100)) : 0;
  const persisted = info.persisted === true
    ? 'ذخیره‌سازی ماندگار: فعال (مرورگر معلومات را خودکار پاک نمی‌کند).'
    : 'ذخیره‌سازی ماندگار: فعال نیست؛ مرورگر ممکن است در کمبود فضا معلومات را پاک کند. بکاپ منظم بگیرید.';
  return `
    <progress class="ox-meter" data-level="${info.level}" max="100" value="${ratio}" aria-label="فضای استفاده‌شده">${ratio}%</progress>
    <p class="ox-muted">استفاده: ${mb(info.usage)} از ${mb(info.quota)}</p>
    <p class="ox-muted">${persisted}</p>
    ${info.level !== 'ok' ? '<p class="ox-error">فضای ذخیره‌سازی دستگاه کم است. بکاپ بگیرید و فضای دستگاه را آزاد کنید.</p>' : ''}`;
}

// ---------------------------------------------------------------- lock / activation

async function loadProfiles() {
  return (await app.registry.getAll('profiles')).sort((a, b) => (a.lastUnlockAt < b.lastUnlockAt ? 1 : -1));
}

let lockRenderSeq = 0;

async function renderLock(message = '') {
  const seq = ++lockRenderSeq;
  setActiveTab(null);
  $('[data-tabs]').hidden = true;
  $('[data-profile-name]').textContent = 'کار آنلاین و آفلاین';
  updateConnUi();
  const profiles = await loadProfiles();

  let activation = '';
  if (monitor.reachable && monitor.authenticated) {
    const session = await api.session();
    if (session.ok && session.data) {
      const s = session.data;
      const exists = profiles.some((p) => p.userId === s.userId && p.companyId === s.companyId);
      activation = exists
        ? `<p class="ox-muted">این دستگاه برای «${h(s.fullName || s.username)}» قبلاً فعال شده است؛ با رمز محلی وارد شوید.</p>`
        : `
        <form class="ox-form" data-activate>
          <p>فعال‌سازی کار آفلاین برای <strong>${h(s.fullName || s.username)}</strong> — ${h(s.companyName)}</p>
          <p class="ox-hint">رمز محلی فقط روی همین دستگاه است و برای باز کردن سیستم صدیقی بدون اینترنت لازم می‌شود. حداقل ${MIN_PIN_LENGTH} حرف یا رقم؛ رمز طولانی‌تر امن‌تر است.</p>
          <div class="ox-form-grid">
            <div class="ox-field"><label for="pin1">رمز محلی</label><input class="ox-input" id="pin1" type="password" autocomplete="new-password" required minlength="${MIN_PIN_LENGTH}" /></div>
            <div class="ox-field"><label for="pin2">تکرار رمز</label><input class="ox-input" id="pin2" type="password" autocomplete="new-password" required minlength="${MIN_PIN_LENGTH}" /></div>
          </div>
          <p class="ox-error" data-error hidden></p>
          <div class="ox-actions"><button class="ox-btn" type="submit">فعال‌سازی این دستگاه</button></div>
        </form>`;
      app.pendingSession = s;
    }
  } else if (monitor.reachable) {
    activation = `<p>برای فعال‌سازی کار آفلاین، یک‌بار با اینترنت وارد سیستم صدیقی شوید.</p>
      <div class="ox-actions"><a class="ox-btn" href="${LOGIN_URL}">ورود به سیستم صدیقی</a></div>`;
  } else {
    activation = '<p class="ox-muted">فعال‌سازی دستگاه تازه فقط با اینترنت و ورود موفق ممکن است.</p>';
  }

  // رندرهای هم‌زمان (شروع برنامه + تغییر اتصال): فقط آخرین؛ و رمزی که کاربر همین حالا تایپ کرده پاک نمی‌شود.
  if (seq !== lockRenderSeq) return;
  const active = document.activeElement;
  if (!message && active && main.contains(active) && active.tagName === 'INPUT' && active.value) return;

  main.innerHTML = `
    ${message ? `<section class="ox-card"><p class="ox-error">${h(message)}</p></section>` : ''}
    ${profiles.length ? `
    <section class="ox-card">
      <h2>ورود آفلاین</h2>
      <form class="ox-form" data-unlock>
        <div class="ox-field">
          <label for="profile">کاربر</label>
          <select class="ox-input" id="profile">
            ${profiles.map((p) => `<option value="${h(p.id)}">${h(p.fullName)} — ${h(p.companyName)}</option>`).join('')}
          </select>
        </div>
        <div class="ox-field"><label for="pin">رمز محلی</label><input class="ox-input" id="pin" type="password" autocomplete="current-password" required /></div>
        <p class="ox-error" data-error hidden></p>
        <div class="ox-actions"><button class="ox-btn" type="submit">ورود</button></div>
      </form>
    </section>` : ''}
    <section class="ox-card">
      <h2>فعال‌سازی دستگاه</h2>
      ${activation}
    </section>
    <section class="ox-card">
      <p class="ox-muted">سیستم کامل صدیقی (آنلاین): <a href="/">بازکردن سیستم صدیقی</a></p>
    </section>`;

  const unlockForm = $('[data-unlock]');
  if (unlockForm) {
    unlockForm.addEventListener('submit', async (event) => {
      event.preventDefault();
      const error = $('[data-error]', unlockForm);
      const button = $('button', unlockForm);
      button.disabled = true;
      error.hidden = true;
      try {
        const profile = profiles.find((p) => p.id === $('#profile').value);
        const result = await unlockProfile(profile, $('#pin').value);
        await app.registry.put('profiles', result.profile);
        if (!result.ok) {
          error.textContent = result.reason === 'locked'
            ? `به‌دلیل تلاش‌های ناموفق، ورود تا ${formatDateTime(result.lockedUntil)} قفل است.`
            : result.lockedUntil
              ? `رمز نادرست است. ورود تا ${formatDateTime(result.lockedUntil)} قفل شد.`
              : `رمز نادرست است. (${formatNumber(result.profile.failedAttempts)} تلاش ناموفق)`;
          error.hidden = false;
          return;
        }
        // همان نشست برای صفحات اصلی مشعل: رمز محلی در این نشست دوباره پرسیده نمی‌شود.
        rememberSession(result.profile, result.rawKey);
        await openProfile(result.profile, result.dataKey);
      } catch (e) {
        error.textContent = e.message || 'ورود انجام نشد.';
        error.hidden = false;
      } finally {
        button.disabled = false;
      }
    });
  }

  const activateForm = $('[data-activate]');
  if (activateForm) {
    activateForm.addEventListener('submit', async (event) => {
      event.preventDefault();
      const error = $('[data-error]', activateForm);
      const button = $('button', activateForm);
      error.hidden = true;
      const pin = $('#pin1').value;
      if (pin !== $('#pin2').value) {
        error.textContent = 'دو رمز یکسان نیست.';
        error.hidden = false;
        return;
      }
      const pinError = validatePin(pin);
      if (pinError) {
        error.textContent = pinError;
        error.hidden = false;
        return;
      }
      button.disabled = true;
      try {
        const { profile, dataKey, rawKey } = await createProfile({ session: app.pendingSession, pin, deviceId: uuid() });
        await app.registry.put('profiles', profile);
        rememberSession(profile, rawKey);
        await openProfile(profile, dataKey, true);
        toast('دستگاه فعال شد. معلومات مرجع دریافت می‌شود…');
      } catch (e) {
        error.textContent = isQuotaError(e) ? 'فضای ذخیره‌سازی دستگاه کافی نیست.' : (e.message || 'فعال‌سازی انجام نشد.');
        error.hidden = false;
      } finally {
        button.disabled = false;
      }
    });
  }
}

async function openProfile(profile, dataKey, firstTime = false) {
  const store = await IdbStore.open(profileDbName(profile.companyId, profile.userId));
  app.profile = profile;
  app.store = store;
  app.sealer = new Sealer(dataKey);
  // صندوق صفحات (session.js): صفحات مشعل از این پس بدون رمز محلی هم آماده می‌شوند.
  await ensurePageKeys(profile.id, app.sealer).catch(() => null);
  app.repo = new OutboxRepo(store, app.sealer, profile);
  app.deviceAuth = new DeviceAuth({
    store,
    sealer: app.sealer,
    deviceId: profile.deviceId,
    refreshImpl: (token, deviceId) => api.refreshDeviceSession(token, deviceId)
  });
  api.setAuth(app.deviceAuth);
  app.engine = new SyncEngine({
    deviceAuth: app.deviceAuth,
    repo: app.repo,
    store,
    api,
    monitor,
    profile,
    sealer: app.sealer,
    deviceName: navigator.userAgentData?.platform || navigator.platform || 'مرورگر',
    onEvent: (event) => { if (event.type === 'sync-end') onSyncEnd(event.summary); }
  });
  await app.engine.recoverInterrupted();
  await loadSnapshot();
  await requestPersist();
  await reloadRecords();
  app.lastActivity = Date.now();
  $('[data-tabs]').hidden = false;
  $('[data-profile-name]').textContent = `${profile.fullName} — ${profile.companyName}`;
  updateConnUi();
  if (!location.hash || location.hash === '#' || location.hash === '#/lock') location.hash = '#/ops';
  else route();
  app.engine.run({ refreshSnapshot: firstTime });
}

function lock(message = '') {
  forgetSession();
  api.setAuth(null);
  if (app.store) app.store.close();
  Object.assign(app, { profile: null, store: null, repo: null, sealer: null, engine: null, snapshot: null, records: [] });
  banner('');
  renderLock(message);
}

async function loadSnapshot() {
  const row = await app.store.get('snapshot', 'current');
  app.snapshot = row ? await app.sealer.open(row.sealed) : null;
}

async function reloadRecords() {
  if (!app.repo) return;
  app.records = await app.repo.list();
  publishStatus();
}

// ---------------------------------------------------------------- sync

async function onSyncEnd(summary) {
  if (!app.profile) return;
  await loadSnapshot();
  await reloadRecords();
  if (summary.authRequired) {
    banner(`نشست سرور منقضی شده است. معلومات محلی حفظ است؛ برای ارسال، <a href="${LOGIN_URL}">دوباره وارد شوید</a>.`);
  } else if (summary.userMismatch) {
    banner('کاربر دیگری در این مرورگر وارد سیستم آنلاین است؛ عملیات این پروفایل ارسال نشد. با همین کاربر وارد شوید.');
  } else if (summary.error) {
    banner(h(summary.error));
  } else {
    await renderBackupBanner();
  }
  const problems = (summary.rejected || 0) + (summary.conflicts || 0);
  if (problems > 0) toast(`${formatNumber(problems)} عملیات ثبت نشد و نیازمند اصلاح است؛ جزئیات در «عملیات».`);
  else if (summary.applied > 0) toast(`${formatNumber(summary.applied)} عملیات همگام شد و در سیستم ثبت قطعی شد.`);
  const name = currentRoute().name;
  if (name === 'home' || name === 'ops' || name === 'op') route();
}

async function renderBackupBanner() {
  const meta = await readMeta('backup');
  const warning = backupWarning(app.records, meta?.lastBackupAt || null);
  if (warning.shouldWarn) {
    banner(`${formatNumber(warning.notBackedUp)} عملیات ارسال‌نشده بکاپ ندارد. <a href="#/backup">گرفتن بکاپ</a>`, 'info');
  } else {
    banner('');
  }
}

function syncNow() {
  if (!app.engine) return;
  monitor.check().then((reachable) => {
    if (!reachable) {
      toast('سرور در دسترس نیست؛ عملیات در دستگاه محفوظ است.');
      return;
    }
    app.engine.run({ refreshSnapshot: true });
  });
}

function autoSync() {
  if (!app.engine || document.visibilityState === 'hidden') return;
  app.engine.run();
}

// ---------------------------------------------------------------- routing

function currentRoute() {
  // پیش‌فرض «عملیات»: این صفحه فقط مدیریت Pending/خطای همگام‌سازی/بکاپ است؛ کار اصلی در خود مشعل.
  const parts = (location.hash || '#/ops').replace(/^#\/?/, '').split('/');
  return { name: parts[0] || 'ops', args: parts.slice(1).map(decodeURIComponent) };
}

async function route() {
  if (!app.profile) {
    renderLock();
    return;
  }
  const { name, args } = currentRoute();
  const renderers = { home: renderHome, new: renderNew, edit: renderEdit, ops: renderOps, op: renderOp, backup: renderBackup, settings: renderSettings };
  const render = renderers[name] || renderOps;
  setActiveTab(name === 'op' || name === 'edit' || name === 'home' || name === 'new' ? 'ops' : (renderers[name] ? name : 'ops'));
  try {
    await render(...args);
  } catch (e) {
    main.innerHTML = `<section class="ox-card"><p class="ox-error">${h(e.message || 'خطا در نمایش صفحه.')}</p></section>`;
  }
}

function allowedTypes() {
  const auth = app.authMeta;
  const keys = auth?.operationTypes?.length ? auth.operationTypes.map((t) => t.key) : Object.keys(OPERATION_FORMS);
  return keys.filter((k) => OPERATION_FORMS[k] && !OPERATION_FORMS[k].hidden);
}

// ---------------------------------------------------------------- screens

function kpi(label, value, unit = '') {
  return `<div class="ox-kpi"><span>${h(label)}</span><strong>${value}</strong>${unit ? `<span>${h(unit)}</span>` : ''}</div>`;
}

async function renderHome() {
  app.authMeta = await readMeta('auth');
  const c = counts();
  const sync = await readMeta('sync');
  const backup = await readMeta('backup');
  const snapMeta = await readMeta('snapshot');
  const storage = await refreshStorage();
  const d = app.snapshot?.dashboard;

  const dashboard = d ? `
    <section class="ox-card">
      <h2>داشبورد</h2>
      <p><span class="ox-stale">آخرین بروزرسانی: ${h(formatDateTime(snapMeta?.asOf))} (${h(relativeAge(snapMeta?.fetchedAt))})</span></p>
      <p class="ox-hint">این اعداد آخرین نسخهٔ دریافت‌شده از سرور است و عملیات آفلاینِ تأییدنشده را شامل نمی‌شود.</p>
      <div class="ox-grid">
        ${d.inventory ? kpi('موجودی مخازن', formatNumber(d.inventory.totalMt), 'تن') : ''}
        ${d.goodsInTransit ? kpi('بارهای در راه', formatNumber(d.goodsInTransit.totalMt), `${formatNumber(d.goodsInTransit.loadCount)} بار`) : ''}
        ${d.todaySales ? kpi('فروش امروز', formatNumber(d.todaySales.amountUsd), 'USD') : ''}
        ${d.receivables ? kpi('طلب از مشتریان', formatNumber(d.receivables.totalUsd), 'USD') : ''}
        ${d.cashPosition ? kpi('موجودی نقدی', formatNumber(d.cashPosition.totalUsd), 'USD') : ''}
      </div>
      ${(d.alerts || []).length ? `<ul class="ox-list">${d.alerts.slice(0, 5).map((a) => `<li class="ox-item"><div><strong>${h(a.title)}</strong><span class="ox-muted">${h(a.message)}</span></div></li>`).join('')}</ul>` : ''}
    </section>` : `
    <section class="ox-card">
      <h2>داشبورد</h2>
      <p class="ox-muted">هنوز معلوماتی از سرور دریافت نشده است. با اولین اتصال، معلومات مرجع و داشبورد ذخیره می‌شود.</p>
    </section>`;

  main.innerHTML = `
    <section class="ox-card">
      <h2>وضعیت همگام‌سازی</h2>
      <div class="ox-grid">
        ${kpi('ارسال‌نشده', formatNumber(c.Pending + c.Sending))}
        ${kpi('در حال ثبت در سرور', formatNumber(c.Received))}
        ${kpi('همگام‌شده', formatNumber(c.Applied))}
        ${kpi('نیازمند اصلاح', formatNumber(c.Conflict + c.Rejected))}
      </div>
      <p class="ox-muted">آخرین همگام‌سازی موفق: ${h(formatDateTime(sync?.lastSyncAt))} — آخرین بکاپ: ${h(formatDateTime(backup?.lastBackupAt))}</p>
      <div class="ox-actions">
        <button class="ox-btn" type="button" data-home-sync>همگام‌سازی اکنون</button>
        <a class="ox-btn ox-btn--ghost" href="#/ops">جزئیات عملیات</a>
        <a class="ox-btn ox-btn--ghost" href="/">سیستم کامل (آنلاین)</a>
      </div>
    </section>
    <section class="ox-card">
      <h2>ثبت جدید</h2>
      <div class="ox-actions">
        ${allowedTypes().map((k) => `<a class="ox-btn ox-btn--ghost" href="#/new/${encodeURIComponent(k)}">${h(OPERATION_FORMS[k].label)}</a>`).join('')}
      </div>
      ${allowedTypes().includes('Customer.Draft') ? `<p class="ox-muted">فورم اصلی سیستم صدیقی که بدون سرور هم باز می‌شود (پس از یک‌بار بازکردن آنلاین):
        <a href="/Customers/Create">ثبت مشتری</a></p>` : ''}
    </section>
    ${dashboard}
    <section class="ox-card">
      <h2>فضای ذخیره‌سازی</h2>
      ${storageHtml(storage)}
    </section>`;
  $('[data-home-sync]').addEventListener('click', syncNow);
}

async function renderEdit(id) {
  const record = await app.repo.get(id);
  if (!record || !canResubmit(record)) {
    main.innerHTML = '<section class="ox-card"><p class="ox-error">این عملیات قابل اصلاح نیست.</p></section>';
    return;
  }
  if (record.operationType === FORM_SUBMIT) {
    location.assign(mainFormUrl(record));
    return;
  }
  await renderNew(record.operationType, null, record);
}

async function renderNew(type, draftId, editRecord = null) {
  app.authMeta = await readMeta('auth');
  if (!type) {
    // ثبت تازه در همان فورم‌های اصلی مشعل (با و بدون سرور)؛ این صفحه فقط پیش‌نویس‌ها و اصلاح عملیات موجود را باز می‌کند.
    main.innerHTML = `
      <section class="ox-card">
        <h2>ثبت جدید</h2>
        <p>ثبت جدید در همان فورم‌های اصلی سیستم صدیقی انجام می‌شود؛ بدون سرور هم همان فورم در دستگاه ذخیره و پس از اتصال خودکار فرستاده می‌شود.</p>
        <div class="ox-actions"><a class="ox-btn" href="/">بازکردن سیستم صدیقی</a><a class="ox-btn ox-btn--ghost" href="#/ops">عملیات آفلاین</a></div>
      </section>`;
    return;
  }

  const form = OPERATION_FORMS[type];
  if (!form || (!editRecord && !allowedTypes().includes(type))) {
    main.innerHTML = '<section class="ox-card"><p class="ox-error">این نوع عملیات برای شما فعال نیست.</p></section>';
    return;
  }
  const draft = draftId ? await app.repo.get(draftId) : null;
  if (draftId && (!draft || draft.status !== Status.Draft)) {
    main.innerHTML = '<section class="ox-card"><p class="ox-error">پیش‌نویس پیدا نشد یا قبلاً ثبت شده است.</p></section>';
    return;
  }
  const values = editRecord?.payload || draft?.payload || {};
  const reference = app.snapshot?.reference || {};
  const missingRefs = form.fields.filter((f) => (f.kind === 'ref' || f.kind === 'currency') && f.required && !(reference[f.source] || []).length);

  const fieldHtml = (field) => {
    const id = `f-${field.name}`;
    const label = `<label for="${id}">${h(field.label)}${field.required ? ' <span class="ox-req">*</span>' : ''}</label>`;
    const value = values[field.name];
    if (field.kind === 'ref' || field.kind === 'currency') {
      const items = [...(reference[field.source] || []), ...pendingOptions(field, reference[field.source] || [])];
      const optionValue = (item) => (field.kind === 'currency' ? item.code : item.id);
      return `<div class="ox-field">${label}<select class="ox-input" id="${id}" name="${field.name}" ${field.required ? 'required' : ''}>
        <option value="">— انتخاب —</option>
        ${items.map((item) => `<option value="${h(optionValue(item))}" ${String(optionValue(item)) === String(value ?? '') ? 'selected' : ''}>${h(item.name)}${item.code && field.kind !== 'currency' ? ` (${h(item.code)})` : ''}${item.extra && field.source === 'cashAccounts' ? ` — ${h(item.extra)}` : ''}</option>`).join('')}
      </select></div>`;
    }
    if (field.kind === 'number') {
      return `<div class="ox-field">${label}<input class="ox-input" id="${id}" name="${field.name}" type="number" inputmode="decimal" step="any" min="0" value="${h(value ?? '')}" ${field.required ? 'required' : ''} /></div>`;
    }
    if (field.kind === 'flag') {
      const no = value === false || value === 'false';
      return `<div class="ox-field">${label}<select class="ox-input" id="${id}" name="${field.name}">
        <option value="true" ${no ? '' : 'selected'}>بلی</option><option value="false" ${no ? 'selected' : ''}>خیر</option></select></div>`;
    }
    return `<div class="ox-field">${label}<input class="ox-input" id="${id}" name="${field.name}" maxlength="500" value="${h(value ?? '')}" ${field.required ? 'required' : ''} /></div>`;
  };

  function pendingOptions(field, existing) {
    // رکوردی که آفلاین ساخته شده (مثلاً مشتری تازه): پیش از ساخت با op:، پس از ساخت با شناسهٔ واقعی.
    if (!field.allowPending) return [];
    return app.records
      .filter((r) => r.operationType === field.allowPending && r.status !== Status.Rejected && r.status !== Status.Draft)
      .map((r) => {
        const realId = r.status === Status.Applied && r.appliedRef ? (r.appliedRef.match(/(\d+)\s*$/) || [])[1] : null;
        return realId
          ? { id: Number(realId), name: `${r.payload?.name || ''} (تازه)` }
          : { id: OPERATION_REFERENCE_PREFIX + r.clientOperationId, name: `${r.payload?.name || ''} (در انتظار ساخت)` };
      })
      .filter((o) => !existing.some((e) => String(e.id) === String(o.id)));
  }

  const dependencyOptions = app.records
    .filter((r) => [Status.Pending, Status.Sending, Status.Received, Status.Applied, Status.Conflict].includes(r.status))
    .slice(0, 50);

  main.innerHTML = `
    <section class="ox-card">
      <h2>${h(form.label)}${draft ? ' — پیش‌نویس' : ''}${editRecord ? ' — اصلاح و ارسال دوباره' : ''}</h2>
      ${editRecord?.serverNote ? `<p class="ox-error">${h(editRecord.serverNote)}</p>` : ''}
      <p class="ox-hint">${h(form.hint)}</p>
      ${missingRefs.length ? `<p class="ox-error">اطلاعات مرجع (${missingRefs.map((f) => h(f.label)).join('، ')}) هنوز روی این دستگاه نیست. یک‌بار با اینترنت همگام‌سازی کنید.</p>` : ''}
      <form class="ox-form" data-op-form novalidate>
        <div class="ox-form-grid">
          <div class="ox-field"><label for="f-businessDate">تاریخ کاری <span class="ox-req">*</span></label>
            <input class="ox-input" id="f-businessDate" name="businessDate" type="date" required value="${h(editRecord?.businessDate || draft?.businessDate || todayIso())}" /></div>
          ${form.fields.map(fieldHtml).join('')}
          ${dependencyOptions.length && !editRecord ? `<div class="ox-field"><label for="f-dependency">پس از این عملیات ارسال شود (اختیاری)</label>
            <select class="ox-input" id="f-dependency" name="dependency"><option value="">— ندارد —</option>
            ${dependencyOptions.map((r) => `<option value="${h(r.clientOperationId)}">${h(typeLabel(r.operationType, r.payload))} · ${h(summarize(r))} · ${h(formatDateTime(r.createdAt))}</option>`).join('')}
            </select></div>` : ''}
        </div>
        <p class="ox-error" data-error hidden></p>
        <div class="ox-actions">
          <button class="ox-btn" type="submit" ${missingRefs.length ? 'disabled' : ''}>${editRecord ? 'ذخیره و ارسال دوباره' : 'ثبت در دستگاه'}</button>
          ${editRecord ? '' : '<button class="ox-btn ox-btn--ghost" type="button" data-save-draft>ذخیرهٔ پیش‌نویس</button>'}
          <a class="ox-btn ox-btn--ghost" href="#/ops">انصراف</a>
        </div>
      </form>
    </section>`;

  const formEl = $('[data-op-form]');
  const read = () => {
    const data = Object.fromEntries(new FormData(formEl).entries());
    const businessDate = data.businessDate;
    const dependency = data.dependency;
    delete data.businessDate;
    delete data.dependency;
    return { payload: data, businessDate, dependencies: dependency ? [dependency] : [] };
  };
  const showError = (message) => {
    const error = $('[data-error]', formEl);
    error.textContent = message;
    error.hidden = false;
  };

  formEl.addEventListener('submit', async (event) => {
    event.preventDefault();
    const { payload, businessDate, dependencies } = read();
    if (!/^\d{4}-\d{2}-\d{2}$/.test(businessDate || '')) {
      showError('تاریخ کاری را وارد کنید.');
      return;
    }
    const error = validatePayload(type, normalizePayload(type, payload));
    if (error) {
      showError(error);
      return;
    }
    const button = $('button[type="submit"]', formEl);
    button.disabled = true;
    try {
      const record = editRecord
        ? await app.repo.resubmit(editRecord.clientOperationId, payload, businessDate)
        : await app.repo.submit(type, payload, businessDate, { dependencies, fromDraftId: draft?.clientOperationId || null });
      await reloadRecords();
      toast('در دستگاه ذخیره شد؛ در انتظار همگام‌سازی');
      location.hash = `#/op/${encodeURIComponent(record.clientOperationId)}`;
      app.engine.run();
    } catch (e) {
      showError(isQuotaError(e) ? 'فضای ذخیره‌سازی دستگاه پر است؛ عملیات ذخیره نشد. بکاپ بگیرید و فضا آزاد کنید.' : (e.message || 'ذخیره انجام نشد.'));
    } finally {
      button.disabled = false;
    }
  });

  $('[data-save-draft]')?.addEventListener('click', async () => {
    const { payload, businessDate } = read();
    try {
      const record = await app.repo.saveDraft(type, payload, businessDate, draft?.clientOperationId || null);
      await reloadRecords();
      toast('پیش‌نویس در دستگاه ذخیره شد.');
      location.hash = `#/op/${encodeURIComponent(record.clientOperationId)}`;
    } catch (e) {
      showError(isQuotaError(e) ? 'فضای ذخیره‌سازی دستگاه پر است.' : (e.message || 'ذخیره انجام نشد.'));
    }
  });
}

const OPS_FILTERS = [
  ['active', 'نیازمند پیگیری', (r) => [Status.Draft, Status.Pending, Status.Sending, Status.Received, Status.Conflict, Status.Rejected].includes(r.status)],
  ['unsynced', 'ارسال‌نشده', (r) => r.status === Status.Pending || r.status === Status.Sending],
  ['problem', 'نیازمند اصلاح', (r) => r.status === Status.Conflict || r.status === Status.Rejected],
  ['applied', 'همگام‌شده', (r) => r.status === Status.Applied],
  ['draft', 'پیش‌نویس', (r) => r.status === Status.Draft],
  ['all', 'همه', () => true]
];

async function renderOps() {
  await reloadRecords();
  const [, , predicate] = OPS_FILTERS.find(([key]) => key === app.opsFilter) || OPS_FILTERS[0];
  const items = app.records.filter(predicate);
  main.innerHTML = `
    <section class="ox-card">
      <h2>عملیات این دستگاه</h2>
      <div class="ox-chips" role="group" aria-label="فیلتر وضعیت">
        ${OPS_FILTERS.map(([key, label]) => `<button type="button" class="ox-chip" data-filter="${key}" aria-pressed="${key === app.opsFilter}">${h(label)}</button>`).join('')}
      </div>
    </section>
    <ul class="ox-list">
      ${items.map((r) => `
        <li><a class="ox-item" href="#/op/${encodeURIComponent(r.clientOperationId)}">
          <div><strong>${h(typeLabel(r.operationType, r.payload))}</strong>
            <span class="ox-muted">${h(summarize(r))} · ${h(formatDateTime(r.createdAt))}</span></div>
          <span class="ox-status" data-status="${r.status}">${h(STATUS_SHORT[r.status] || r.status)}</span>
        </a></li>`).join('') || '<li class="ox-card ox-muted">موردی نیست.</li>'}
    </ul>`;
  for (const chip of $$('[data-filter]')) {
    chip.addEventListener('click', () => {
      app.opsFilter = chip.dataset.filter;
      renderOps();
    });
  }
}

async function renderOp(id) {
  const record = await app.repo.get(id);
  if (!record) {
    main.innerHTML = '<section class="ox-card"><p class="ox-error">عملیات پیدا نشد.</p></section>';
    return;
  }
  const form = OPERATION_FORMS[record.operationType];
  const p = record.payload || {};
  const canReturn = record.status === Status.Pending && !record.attempts;
  const isDraft = record.status === Status.Draft;
  const canRetryNow = record.status === Status.Pending;
  const canFix = canResubmit(record);
  const isMainForm = record.operationType === FORM_SUBMIT;
  const canCopy = !isMainForm && !canFix && (record.status === Status.Rejected || record.status === Status.Conflict) && !!OPERATION_FORMS[record.operationType];

  main.innerHTML = `
    <section class="ox-card">
      <h2>${h(typeLabel(record.operationType, record.payload))}</h2>
      <p><span class="ox-status" data-status="${record.status}">${h(STATUS_LABELS[record.status] || record.status)}</span></p>
      ${record.serverNote ? `<p>${h(record.serverNote)}</p>` : ''}
      ${record.status !== Status.Applied ? '<p class="ox-hint">این عملیات هنوز ثبت قطعی نشده و در مانده‌ها و موجودی رسمی حساب نیست. پس از اتصال، سرور خودکار با فرم موجود ثبتش می‌کند.</p>' : ''}
      <dl class="ox-dl">
        <dt>تاریخ کاری</dt><dd>${h(record.businessDate)}</dd>
        ${isMainForm ? formSubmitDetails(p) : (form?.fields || []).map((f) => `<dt>${h(f.label)}</dt><dd>${h(fieldDisplay(f, p[f.name]))}</dd>`).join('')}
        <dt>ثبت در دستگاه</dt><dd>${h(formatDateTime(record.createdAt))}</dd>
        <dt>تلاش ارسال</dt><dd>${formatNumber(record.attempts || 0)}${record.nextAttemptAt && record.status === Status.Pending ? ` — تلاش بعدی: ${h(formatDateTime(record.nextAttemptAt))}` : ''}</dd>
        ${record.lastError && record.status === Status.Pending ? `<dt>آخرین خطا</dt><dd>${h(record.lastError)}</dd>` : ''}
        ${record.appliedRef ? `<dt>سند ثبت‌شده در سیستم</dt><dd>${h(record.appliedRef)}</dd>` : ''}
        ${record.revision > 1 ? `<dt>نسخهٔ اصلاح</dt><dd>${formatNumber(record.revision)}</dd>` : ''}
        ${(record.dependencies || []).length ? `<dt>پیش‌نیاز</dt><dd>${record.dependencies.map((d) => `<a href="#/op/${encodeURIComponent(d)}">${h(d.slice(0, 8))}</a>`).join('، ')}</dd>` : ''}
        <dt>شناسهٔ عملیات</dt><dd><code>${h(record.clientOperationId)}</code></dd>
      </dl>
      <div class="ox-actions">
        ${isDraft ? `<a class="ox-btn" href="${isMainForm ? h(mainFormUrl(record)) : `#/new/${encodeURIComponent(record.operationType)}/${encodeURIComponent(record.clientOperationId)}`}">${isMainForm ? 'بررسی و ارسال در فورم اصلی' : 'ادامهٔ پیش‌نویس'}</a>
          <button class="ox-btn ox-btn--danger" type="button" data-discard>حذف پیش‌نویس</button>` : ''}
        ${canRetryNow ? '<button class="ox-btn" type="button" data-retry>ارسال اکنون</button>' : ''}
        ${canReturn ? '<button class="ox-btn ox-btn--ghost" type="button" data-return>بازگشت به پیش‌نویس</button>' : ''}
        ${canFix ? `<a class="ox-btn" href="${isMainForm ? h(mainFormUrl(record)) : `#/edit/${encodeURIComponent(record.clientOperationId)}`}">${isMainForm ? 'اصلاح در فورم اصلی و ارسال دوباره' : 'اصلاح و ارسال دوباره'}</a>` : ''}
        ${canFix && record.status === Status.Conflict ? `<button class="ox-btn ox-btn--ghost" type="button" data-resend>${record.baseVersion ? 'وضعیت فعلی سند را دیدم؛ ارسال دوباره' : 'ارسال دوباره بدون تغییر'}</button>` : ''}
        ${canCopy ? '<button class="ox-btn ox-btn--ghost" type="button" data-copy>ثبت تازه از روی همین</button>' : ''}
        <a class="ox-btn ox-btn--ghost" href="#/ops">بازگشت</a>
      </div>
    </section>
    <section class="ox-card">
      <h3>تاریخچه</h3>
      <ol class="ox-timeline">
        ${(record.history || []).slice().reverse().map((e) => `<li><strong>${h(STATUS_SHORT[e.status] || e.status)}</strong> — ${h(formatDateTime(e.at))}${e.note ? `<br><span class="ox-muted">${h(e.note)}</span>` : ''}</li>`).join('')}
      </ol>
    </section>`;

  $('[data-discard]')?.addEventListener('click', async () => {
    if (!confirm('این پیش‌نویس حذف شود؟')) return;
    await app.repo.discardDraft(record.clientOperationId);
    await reloadRecords();
    location.hash = '#/ops';
  });
  $('[data-retry]')?.addEventListener('click', async () => {
    await app.repo.putMany([{ ...record, nextAttemptAt: new Date().toISOString() }]);
    syncNow();
  });
  $('[data-return]')?.addEventListener('click', async () => {
    try {
      const draft = await app.repo.returnToDraft(record.clientOperationId);
      await reloadRecords();
      location.hash = `#/new/${encodeURIComponent(draft.operationType)}/${encodeURIComponent(draft.clientOperationId)}`;
    } catch (e) {
      toast(e.message);
    }
  });
  $('[data-resend]')?.addEventListener('click', async () => {
    // مثلاً پس از رسیدن موجودی یا وقتی «احتمال تکرار» بررسی شد: همان عملیات، نسخهٔ تازه.
    try {
      // عملیات روی رکورد: کاربر وضعیت فعلی سند را دید و آگاهانه دوباره می‌فرستد؛ نسخهٔ پایه = همین حالا.
      await app.repo.resubmit(record.clientOperationId, record.payload, record.businessDate,
        { baseVersion: record.baseVersion ? baseVersionFrom(new Date().toISOString()) : null });
      await reloadRecords();
      toast('دوباره برای ثبت فرستاده می‌شود.');
      syncNow();
      route();
    } catch (e) {
      toast(e.message);
    }
  });
  $('[data-copy]')?.addEventListener('click', async () => {
    // ثبت تازه با شناسهٔ تازه؛ عملیات ردشده/تعارض دست نمی‌خورد و در تاریخچه می‌ماند.
    const draft = await app.repo.saveDraft(record.operationType, record.payload, record.businessDate);
    await reloadRecords();
    location.hash = `#/new/${encodeURIComponent(draft.operationType)}/${encodeURIComponent(draft.clientOperationId)}`;
  });
}

async function renderBackup() {
  const meta = await readMeta('backup');
  const warning = backupWarning(app.records, meta?.lastBackupAt || null);
  main.innerHTML = `
    <section class="ox-card">
      <h2>بکاپ رمزگذاری‌شده</h2>
      <p class="ox-hint">حافظهٔ مرورگر به‌تنهایی برای نگهداری طولانی مطمئن نیست. فایل بکاپ را بیرون از مرورگر (فلش، کمپیوتر دیگر، Drive) نگه دارید.</p>
      <p class="ox-muted">آخرین بکاپ: ${h(formatDateTime(meta?.lastBackupAt))} — ارسال‌نشده: ${formatNumber(warning.unsynced)} — بدون بکاپ: ${formatNumber(warning.notBackedUp)}</p>
      <form class="ox-form" data-export>
        <div class="ox-form-grid">
          <div class="ox-field"><label for="bp1">رمز بکاپ (حداقل ${MIN_BACKUP_PASSWORD} حرف)</label><input class="ox-input" id="bp1" type="password" autocomplete="new-password" required /></div>
          <div class="ox-field"><label for="bp2">تکرار رمز بکاپ</label><input class="ox-input" id="bp2" type="password" autocomplete="new-password" required /></div>
        </div>
        <p class="ox-hint">بدون این رمز، بکاپ قابل بازیابی نیست. آن را جدا از فایل نگه دارید.</p>
        <p class="ox-error" data-error hidden></p>
        <div class="ox-actions"><button class="ox-btn" type="submit">ساخت و ذخیرهٔ فایل بکاپ</button></div>
      </form>
    </section>
    <section class="ox-card">
      <h2>بازیابی از بکاپ</h2>
      <p class="ox-hint">عملیات با همان شناسهٔ قبلی برمی‌گردند؛ ارسال دوباره در سرور تکراری ثبت نمی‌شود. بکاپ فقط در پروفایل همان کاربر و شرکت بازیابی می‌شود.</p>
      <form class="ox-form" data-import>
        <div class="ox-form-grid">
          <div class="ox-field"><label for="bf">فایل بکاپ</label><input class="ox-input" id="bf" type="file" accept=".mashalbak,application/json" required /></div>
          <div class="ox-field"><label for="bpw">رمز بکاپ</label><input class="ox-input" id="bpw" type="password" autocomplete="off" required /></div>
        </div>
        <p class="ox-error" data-error hidden></p>
        <div class="ox-actions"><button class="ox-btn" type="submit">بررسی و بازیابی</button></div>
      </form>
    </section>`;

  const exportForm = $('[data-export]');
  exportForm.addEventListener('submit', async (event) => {
    event.preventDefault();
    const error = $('[data-error]', exportForm);
    error.hidden = true;
    const password = $('#bp1').value;
    if (password !== $('#bp2').value) {
      error.textContent = 'دو رمز یکسان نیست.';
      error.hidden = false;
      return;
    }
    const button = $('button', exportForm);
    button.disabled = true;
    try {
      const records = await app.repo.list();
      const now = new Date();
      const envelope = await createBackup({ profile: app.profile, records, password, now,
        meta: { sync: await readMeta('sync'), backupOf: app.profile.deviceId } });
      const fileName = backupFileName(app.profile, now);
      const blob = new Blob([JSON.stringify(envelope)], { type: 'application/json' });
      const file = typeof File === 'function' ? new File([blob], fileName, { type: 'application/json' }) : null;
      let shared = false;
      if (file && navigator.canShare?.({ files: [file] }) && /Android|iPhone|iPad/i.test(navigator.userAgent)) {
        try {
          await navigator.share({ files: [file], title: 'بکاپ سیستم صدیقی' });
          shared = true;
        } catch {
          shared = false;
        }
      }
      if (!shared) {
        const url = URL.createObjectURL(blob);
        const a = document.createElement('a');
        a.href = url;
        a.download = fileName;
        document.body.appendChild(a);
        a.click();
        a.remove();
        setTimeout(() => URL.revokeObjectURL(url), 10_000);
      }
      await app.store.put('meta', { key: 'backup', lastBackupAt: now.toISOString(), count: records.length });
      toast('بکاپ ساخته شد. فایل را در محل امن دیگری نگه دارید.');
      banner('');
      renderBackup();
    } catch (e) {
      error.textContent = e.message || 'ساخت بکاپ انجام نشد.';
      error.hidden = false;
    } finally {
      button.disabled = false;
    }
  });

  const importForm = $('[data-import]');
  importForm.addEventListener('submit', async (event) => {
    event.preventDefault();
    const error = $('[data-error]', importForm);
    error.hidden = true;
    const file = $('#bf').files[0];
    if (!file) return;
    const button = $('button', importForm);
    button.disabled = true;
    try {
      const content = await openBackup(await file.text(), $('#bpw').value);
      const merge = mergeBackup(await app.repo.list(), content, app.profile);
      await app.repo.putMany(merge.toWrite);
      await reloadRecords();
      toast(`بازیابی شد: ${formatNumber(merge.added)} تازه، ${formatNumber(merge.updated)} به‌روز، ${formatNumber(merge.skipped)} بدون تغییر.`);
      app.engine.run();
      location.hash = '#/ops';
    } catch (e) {
      error.textContent = isQuotaError(e) ? 'فضای ذخیره‌سازی دستگاه کافی نیست.' : (e.message || 'بازیابی انجام نشد.');
      error.hidden = false;
    } finally {
      button.disabled = false;
    }
  });
}

async function renderSettings() {
  const storage = await refreshStorage();
  const sync = await readMeta('sync');
  const auth = await readMeta('auth');
  const p = app.profile;
  const c = counts();
  const unsynced = c.Draft + c.Pending + c.Sending;
  main.innerHTML = `
    <section class="ox-card">
      <h2>این دستگاه</h2>
      <dl class="ox-dl">
        <dt>کاربر</dt><dd>${h(p.fullName)} (${h(p.username)})</dd>
        <dt>شرکت</dt><dd>${h(p.companyName)}</dd>
        <dt>شناسهٔ دستگاه</dt><dd><code>${h(p.deviceId)}</code></dd>
        <dt>فعال‌سازی</dt><dd>${h(formatDateTime(p.createdAt))}</dd>
        <dt>آخرین تأیید سرور</dt><dd>${h(formatDateTime(auth?.lastServerAuthAt))}</dd>
        <dt>آخرین تلاش همگام‌سازی</dt><dd>${h(formatDateTime(sync?.lastAttemptAt))}</dd>
        <dt>نسخهٔ برنامه</dt><dd>${h(app.swVersion || '—')}</dd>
      </dl>
      <div class="ox-actions">
        <button class="ox-btn" type="button" data-lock>قفل کردن</button>
        <button class="ox-btn ox-btn--ghost" type="button" data-switch>تغییر کاربر</button>
        <a class="ox-btn ox-btn--ghost" href="/">سیستم کامل (آنلاین)</a>
      </div>
    </section>
    <section class="ox-card">
      <h2>تغییر رمز محلی</h2>
      <form class="ox-form" data-pin>
        <div class="ox-form-grid">
          <div class="ox-field"><label for="op">رمز فعلی</label><input class="ox-input" id="op" type="password" autocomplete="current-password" required /></div>
          <div class="ox-field"><label for="np1">رمز تازه</label><input class="ox-input" id="np1" type="password" autocomplete="new-password" required /></div>
          <div class="ox-field"><label for="np2">تکرار رمز تازه</label><input class="ox-input" id="np2" type="password" autocomplete="new-password" required /></div>
        </div>
        <p class="ox-error" data-error hidden></p>
        <div class="ox-actions"><button class="ox-btn" type="submit">تغییر رمز</button></div>
      </form>
    </section>
    <section class="ox-card">
      <h2>فضای ذخیره‌سازی</h2>
      ${storageHtml(storage)}
    </section>
    <section class="ox-card">
      <h2>حذف پروفایل از این دستگاه</h2>
      ${unsynced > 0
        ? `<p class="ox-error">${formatNumber(unsynced)} عملیات ارسال‌نشده یا پیش‌نویس روی این دستگاه است. پیش از حذف، همگام‌سازی کنید یا بکاپ بگیرید.</p>`
        : '<p class="ox-hint">همهٔ عملیات به سرور رسیده‌اند. حذف پروفایل، معلومات محلی این کاربر را از همین دستگاه پاک می‌کند.</p>'}
      <div class="ox-actions"><button class="ox-btn ox-btn--danger" type="button" data-remove ${unsynced > 0 ? 'disabled' : ''}>حذف پروفایل</button></div>
    </section>`;

  $('[data-lock]').addEventListener('click', () => lock());
  $('[data-switch]').addEventListener('click', () => {
    if (unsynced > 0) toast(`${formatNumber(unsynced)} عملیات ارسال‌نشده در همین پروفایل محفوظ می‌ماند.`);
    lock();
  });
  const pinForm = $('[data-pin]');
  pinForm.addEventListener('submit', async (event) => {
    event.preventDefault();
    const error = $('[data-error]', pinForm);
    error.hidden = true;
    if ($('#np1').value !== $('#np2').value) {
      error.textContent = 'دو رمز تازه یکسان نیست.';
      error.hidden = false;
      return;
    }
    try {
      const updated = await changePin(app.profile, $('#op').value, $('#np1').value);
      await app.registry.put('profiles', updated);
      app.profile = updated;
      toast('رمز محلی تغییر کرد.');
      pinForm.reset();
    } catch (e) {
      error.textContent = e.name === 'OperationError' ? 'رمز فعلی نادرست است.' : (e.message || 'تغییر رمز انجام نشد.');
      error.hidden = false;
    }
  });
  $('[data-remove]').addEventListener('click', async () => {
    const answer = prompt('برای حذف معلومات محلی این کاربر، کلمهٔ «حذف» را بنویسید.');
    if (answer !== 'حذف') return;
    const fresh = await app.repo.list();
    if (fresh.some((r) => [Status.Draft, Status.Pending, Status.Sending].includes(r.status))) {
      toast('عملیات ارسال‌نشده پیدا شد؛ حذف انجام نشد.');
      return;
    }
    const profile = app.profile;
    // نشست دستگاه در سرور هم باطل شود (اگر آنلاین باشیم).
    const tokens = await app.deviceAuth?.load().catch(() => null);
    if (tokens?.refreshToken) {
      await api.request('POST', '/auth/logout', { refreshToken: tokens.refreshToken }, 10000, { base: '/api/mobile/v1', useAuth: false }).catch(() => {});
    }
    lock();
    await app.registry.delete('profiles', profile.id);
    await new Promise((resolve) => {
      const request = indexedDB.deleteDatabase(profileDbName(profile.companyId, profile.userId));
      request.onsuccess = request.onerror = request.onblocked = () => resolve();
    });
    renderLock('پروفایل از این دستگاه حذف شد.');
  });
}

// ---------------------------------------------------------------- boot

async function registerServiceWorker() {
  if (!('serviceWorker' in navigator)) return;
  try {
    const registration = await navigator.serviceWorker.register('/sw.js', { scope: '/', updateViaCache: 'none' });
    navigator.serviceWorker.addEventListener('message', (event) => {
      if (event.data?.type === 'mashal:version') app.swVersion = event.data.version;
    });
    const worker = registration.active || registration.waiting || registration.installing;
    worker?.postMessage({ type: 'mashal:version' });
  } catch {
    // بدون SW برنامه آنلاین کار می‌کند؛ بازشدن آفلاین ممکن نیست.
  }
}

function trackIdle() {
  const touch = () => { app.lastActivity = Date.now(); };
  for (const type of ['pointerdown', 'keydown', 'touchstart']) {
    document.addEventListener(type, touch, { passive: true });
  }
  setInterval(() => {
    if (app.profile && Date.now() - app.lastActivity > IDLE_LOCK_MS) {
      lock('به‌دلیل بی‌فعالیتی قفل شد.');
    }
  }, 30_000);
}

async function boot() {
  registerServiceWorker();
  try {
    app.registry = await IdbStore.open(REGISTRY_DB, REGISTRY_STORES);
  } catch (e) {
    main.innerHTML = `<section class="ox-card"><p class="ox-error">ذخیره‌سازی محلی در این مرورگر در دسترس نیست (${h(e.message || '')}). حالت خصوصی (Private) مرورگر را خاموش کنید.</p></section>`;
    return;
  }
  let wasReachable = monitor.reachable;
  monitor.onChange(() => {
    updateConnUi();
    const cameOnline = monitor.reachable && !wasReachable;
    const changed = monitor.reachable !== wasReachable;
    wasReachable = monitor.reachable;
    // فقط گذار آفلاین → آنلاین همگام‌سازی را شروع می‌کند (تغییر SYNCING خودش رویداد است).
    if (cameOnline && app.engine) app.engine.run();
    const typing = document.activeElement && ['INPUT', 'SELECT'].includes(document.activeElement.tagName);
    if (changed && !app.profile && !typing) renderLock();
  });
  $('[data-sync-now]').addEventListener('click', syncNow);
  window.addEventListener('hashchange', route);
  document.addEventListener('visibilitychange', () => {
    if (document.visibilityState === 'visible') {
      monitor.check();
      autoSync();
    }
  });
  window.addEventListener('pageshow', () => monitor.check());
  setInterval(autoSync, AUTO_SYNC_MS);
  trackIdle();
  await monitor.start();
  updateConnUi();
  // نشستی که در صفحات اصلی مشعل با رمز محلی باز شد، همین‌جا هم باز است (رمز در هر نشست یک‌بار).
  const restored = await restoreSession();
  const profile = restored && (await loadProfiles()).find((p) => p.id === restored.profileId);
  if (profile) {
    await openProfile(profile, restored.dataKey);
    return;
  }
  await renderLock();
}

boot();
