/*
 * مشعل — Service Worker
 *
 * قواعد امنیتی و داده:
 *  - «پوستهٔ آفلاین» (HTML/CSS/JS/فونت/آیکن عمومی) cache می‌شود.
 *  - هیچ پاسخ /api و هیچ HTMLِ صفحات MVC در Cache مرورگر ذخیره نمی‌شود. صفحات مشعل فقط رمزگذاری‌شده با کلید
 *    پروفایل همان کاربر در IndexedDB نگه داشته می‌شوند (offline/js/page-vault.js). وقتی سرور جواب نمی‌دهد، این‌جا فقط
 *    «بازکنندهٔ» /offline/page.html داده می‌شود که همان آدرس را از نسخهٔ رمزگذاری‌شده می‌سازد.
 *  - فایل‌های ثابت CSS/JS/فونت/تصویرِ صفحات (نه /uploads): نسخه‌دار (?v=، asp-append-version) تغییرناپذیرند و
 *    مستقیم از نسخهٔ ذخیره داده می‌شوند (بدون شبکه و بدون بازنویسی در هر بارگذاری)؛ بی‌نسخه اول از شبکه. از هر فایل
 *    فقط آخرین نسخه نگه داشته می‌شود.
 *  - به‌روزرسانی SW هرگز IndexedDB را پاک نمی‌کند؛ پس عملیات Pending و صفحات ذخیره‌شده با تغییر نسخه حفظ می‌شوند.
 */
'use strict';

const VERSION = 'mashal-offline-v9';
const SHELL_CACHE = VERSION + '-shell';
const OFFLINE_URL = '/offline/index.html';
const PAGE_LOADER_URL = '/offline/page.html';
const NAVIGATION_TIMEOUT_MS = 9000;
const SHELL_TIMEOUT_MS = 5000;

// نام بدون نسخه تا با به‌روزرسانی SW فایل‌های صفحات از دست نروند.
const PAGE_ASSETS_CACHE = 'mashal-page-assets';
const LEGACY_CACHES = ['mashal-pages'];
const ASSET_PATTERN = /\.(css|js|mjs|woff2?|ttf|otf|eot|svg|png|jpe?g|gif|webp|ico)$/i;
const GATEWAY_DOWN = [502, 503, 504];

const SHELL_ASSETS = [
  OFFLINE_URL,
  PAGE_LOADER_URL,
  '/offline/offline.css',
  '/offline/js/app.js',
  '/offline/js/store.js',
  '/offline/js/vault.js',
  '/offline/js/connectivity.js',
  '/offline/js/api.js',
  '/offline/js/device-auth.js',
  '/offline/js/sync-engine.js',
  '/offline/js/backup.js',
  '/offline/js/operations.js',
  '/offline/js/util.js',
  '/offline/js/session.js',
  '/offline/js/page-vault.js',
  '/offline/js/page-resolve.js',
  '/offline/js/prepare.js',
  '/offline/js/page-loader.js',
  '/offline/js/page-apply.js',
  '/offline/js/form-bridge.js',
  '/manifest.webmanifest',
  '/mashal-logo-192.png',
  '/mashal-logo-512.png',
  '/mashal-logo-180.png',
  '/mashal-logo.ico',
  '/vendor/fonts/vazirmatn/files/vazirmatn-400.woff2',
  '/vendor/fonts/vazirmatn/files/vazirmatn-700.woff2'
];

self.addEventListener('install', (event) => {
  event.waitUntil((async () => {
    const cache = await caches.open(SHELL_CACHE);
    // cache: 'reload' تا نسخهٔ HTTP-cacheِ قدیمی وارد پوستهٔ تازه نشود.
    await cache.addAll(SHELL_ASSETS.map((url) => new Request(url, { cache: 'reload' })));
    await self.skipWaiting();
  })());
});

self.addEventListener('activate', (event) => {
  event.waitUntil((async () => {
    const keys = await caches.keys();
    await Promise.all(keys
      .filter((key) => (key.startsWith('mashal-offline-') && !key.startsWith(VERSION)) || LEGACY_CACHES.includes(key))
      .map((key) => caches.delete(key)));
    // ناوبری هم‌زمان با بیدارشدن SW از شبکه شروع می‌شود (تأخیر راه‌اندازی SW روی موبایل حذف).
    if (self.registration.navigationPreload) {
      await self.registration.navigationPreload.enable().catch(() => {});
    }
    await self.clients.claim();
  })());
});

self.addEventListener('message', (event) => {
  const data = event.data || {};
  if (data.type === 'mashal:version') {
    event.source && event.source.postMessage({ type: 'mashal:version', version: VERSION });
  } else if (data.type === 'mashal:cache-assets' && Array.isArray(data.urls)) {
    event.waitUntil(cacheAssets(data.urls.slice(0, 200)).catch(() => {}));
  }
});

/**
 * فایل‌های ثابتِ صفحه‌ای که برای کار آفلاین آماده شد ولی هنوز در مرورگر باز نشده (آماده‌سازی پس‌زمینه):
 * بدون آن‌ها، همان صفحه بدون سرور بدون CSS/JSِ خودش باز می‌شد.
 */
async function cacheAssets(urls) {
  const store = await caches.open(PAGE_ASSETS_CACHE);
  for (const raw of urls) {
    if (!isPageAsset(raw)) continue;
    const url = new URL(raw, self.location.origin).href;
    if (await store.match(url, { ignoreVary: true })) continue;
    try {
      const response = await fetch(url, { credentials: 'same-origin' });
      if (response.ok && response.type === 'basic') {
        await rememberAsset(new Request(url), response);
      }
    } catch {
      // دفعهٔ بعد.
    }
  }
}

function isPageAsset(raw) {
  try {
    const url = new URL(raw, self.location.origin);
    const path = url.pathname.toLowerCase();
    return url.origin === self.location.origin && !path.startsWith('/api/') && !path.startsWith('/offline/')
      && !path.startsWith('/uploads/') && path !== '/sw.js' && ASSET_PATTERN.test(path);
  } catch {
    return false;
  }
}

/** آخرین نسخهٔ فایل نگه داشته و نسخه‌های قبلیِ همان مسیر (asp-append-version) پاک می‌شوند. */
async function rememberAsset(request, response) {
  const store = await caches.open(PAGE_ASSETS_CACHE);
  const url = new URL(request.url);
  // فقط نسخه‌های دیگرِ همین مسیر (نه پیمایش کل Cache).
  for (const old of await store.keys(url.origin + url.pathname, { ignoreSearch: true })) {
    if (new URL(old.url).search !== url.search) await store.delete(old);
  }
  await store.put(request.url, response);
}

/** نسخه‌دار (?v=hash): محتوای این آدرس هرگز عوض نمی‌شود. */
const isVersioned = (url) => /[?&]v=[^&]+/.test(url);

/**
 * فایل صفحه: نسخه‌دار = مستقیم از نسخهٔ ذخیره (بی‌شبکه)؛ وگرنه اول شبکه (با HTTP cache عادی مرورگر) و نسخهٔ ذخیره
 * فقط وقتی سرور در دسترس نیست. فایلی که همین آدرس را در Cache دارد دوباره نوشته نمی‌شود.
 */
async function pageAsset(event) {
  const request = event.request;
  const store = await caches.open(PAGE_ASSETS_CACHE);
  const exact = await store.match(request.url, { ignoreVary: true });
  if (exact && (isVersioned(request.url) || serverDown())) return exact;
  const fromCache = async () => exact || store.match(request.url, { ignoreSearch: true, ignoreVary: true });
  if (serverDown()) {
    const cached = await fromCache();
    if (cached) return cached;
  }
  try {
    const response = await fetch(request);
    if (GATEWAY_DOWN.includes(response.status)) {
      markDown();
      return (await fromCache()) || response;
    }
    if (response.ok && response.type === 'basic' && (!exact || changed(exact, response))) {
      event.waitUntil(rememberAsset(request, response.clone()).catch(() => {}));
    }
    return response;
  } catch (error) {
    markDown();
    const cached = await fromCache();
    if (cached) {
      return cached;
    }
    throw error;
  }
}

/** فایل بی‌نسخه تغییر کرده؟ (ETag/Last-Modified/اندازه؛ بدون این‌ها = تغییر فرض می‌شود). */
function changed(cached, response) {
  const tag = (r) => r.headers.get('ETag') || r.headers.get('Last-Modified') || '';
  const a = tag(cached);
  const b = tag(response);
  if (a && b) return a !== b;
  return true;
}

function isShellAsset(url) {
  return SHELL_ASSETS.includes(url.pathname) || url.pathname.startsWith('/offline/');
}

/**
 * پوستهٔ آفلاین (/offline/*، manifest، آیکن): اول شبکه با اعتبارسنجی (ETag) تا پس از هر نشر همان نسخهٔ سرور اجرا شود؛
 * نسخهٔ ذخیره فقط وقتی سرور در دسترس نیست یا دیر جواب می‌دهد. پیش‌تر نسخهٔ ذخیره اول داده می‌شد و ماژول‌های
 * بدون ?v= (store.js، vault.js، …) تا یک بارگذاری بعد قدیمی می‌ماندند؛ مخلوط نسخهٔ قدیم/جدید در موبایل.
 */
async function shellAsset(event) {
  const request = event.request;
  const cache = await caches.open(SHELL_CACHE);
  const fromCache = () => cache.match(request, { ignoreSearch: true });
  if (serverDown()) {
    // سرور همین حالا جواب نداد: ماژول‌های پوسته بی‌درنگ از نسخهٔ ذخیره (هر ماژول منتظر شکست شبکه نماند).
    const cached = await fromCache();
    if (cached) return cached;
  }
  try {
    // ناوبری مستقیم به /offline/* هم از navigation preload استفاده می‌کند (بدون درخواست دوم).
    const response = await withTimeout(Promise.resolve(event.preloadResponse).catch(() => null)
      .then((preloaded) => preloaded || fetch(request, { cache: 'no-cache' })), SHELL_TIMEOUT_MS);
    if (GATEWAY_DOWN.includes(response.status)) {
      markDown();
      return (await fromCache()) || response;
    }
    markUp();
    if (response.ok && response.type === 'basic') {
      const url = new URL(request.url);
      event.waitUntil(cache.put(url.origin + url.pathname, response.clone()).catch(() => {}));
    }
    return response;
  } catch {
    markDown();
    return (await fromCache()) || Response.error();
  }
}

// آخرین شکست سرور (ناوبری یا فایل پوسته): تا ۱۵ ثانیه فایل‌های پوسته و صفحه اول از نسخهٔ ذخیره داده می‌شوند.
const DOWN_WINDOW_MS = 15000;
let downUntil = 0;
const serverDown = () => Date.now() < downUntil;
const markDown = () => { downUntil = Date.now() + DOWN_WINDOW_MS; };
const markUp = () => { downUntil = 0; };

function withTimeout(promise, ms) {
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error('timeout')), ms);
    promise.then((value) => { clearTimeout(timer); resolve(value); },
      (error) => { clearTimeout(timer); reject(error); });
  });
}

/** همان آدرس از نسخهٔ رمزگذاری‌شدهٔ کاربر (page-loader.js) با همان ظاهر و منو. */
async function offlineLoader() {
  const cache = await caches.open(SHELL_CACHE);
  const loader = (await cache.match(PAGE_LOADER_URL)) || (await cache.match(OFFLINE_URL));
  return loader || new Response(
    '<!doctype html><meta charset="utf-8"><title>مشعل</title><p dir="rtl">سرور در دسترس نیست.</p>',
    { status: 503, headers: { 'Content-Type': 'text/html; charset=utf-8' } });
}

async function navigate(event) {
  const request = event.request;
  // دستگاه اصلاً شبکه ندارد: بی‌انتظار، نسخهٔ ذخیره.
  if (self.navigator && self.navigator.onLine === false) {
    markDown();
    return offlineLoader();
  }
  try {
    // صفحهٔ MVC همیشه از شبکه؛ هرگز cache نمی‌شود. پروکسیِ بدون سرور (502/503/504) هم یعنی آفلاین.
    // navigation preload: درخواست هم‌زمان با بیدارشدن SW فرستاده شده است (همان یک درخواست، نه دو).
    const pending = Promise.resolve(event.preloadResponse).catch(() => null).then((preloaded) => preloaded || fetch(request));
    // سرور چند لحظه پیش جواب نداد: انتظار کوتاه‌تر پیش از نسخهٔ ذخیره.
    const response = await withTimeout(pending, serverDown() ? 4000 : NAVIGATION_TIMEOUT_MS);
    if (!GATEWAY_DOWN.includes(response.status)) {
      markUp();
      return response;
    }
    throw new Error('gateway');
  } catch {
    markDown();
    return offlineLoader();
  }
}

self.addEventListener('fetch', (event) => {
  const request = event.request;
  if (request.method !== 'GET') {
    return;
  }

  const url = new URL(request.url);
  if (url.origin !== self.location.origin) {
    return;
  }

  // API: فقط شبکه، بدون cache (معلومات حساس و همیشه تازه).
  if (url.pathname.startsWith('/api/')) {
    return;
  }

  if (isShellAsset(url)) {
    event.respondWith(shellAsset(event));
    return;
  }

  // ناوبری SPAی MVC (X-PTG-SPA) fetch عادی است، نه navigate؛ قطعِ آن را spa-nav + form-bridge مدیریت می‌کنند.
  if (request.mode === 'navigate') {
    event.respondWith(navigate(event));
    return;
  }

  if (['style', 'script', 'font', 'image'].includes(request.destination) && isPageAsset(request.url)) {
    event.respondWith(pageAsset(event));
  }
});
