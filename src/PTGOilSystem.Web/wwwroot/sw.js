/*
 * مشعل — Service Worker
 *
 * قواعد امنیتی و داده:
 *  - «پوستهٔ آفلاین» (HTML/CSS/JS/فونت/آیکن عمومی) cache می‌شود.
 *  - هیچ پاسخ /api و هیچ HTMLِ صفحات MVC در Cache مرورگر ذخیره نمی‌شود. صفحات مشعل فقط رمزگذاری‌شده با کلید
 *    پروفایل همان کاربر در IndexedDB نگه داشته می‌شوند (offline/js/page-vault.js). وقتی سرور جواب نمی‌دهد، این‌جا فقط
 *    «بازکنندهٔ» /offline/page.html داده می‌شود که همان آدرس را از نسخهٔ رمزگذاری‌شده می‌سازد.
 *  - فایل‌های ثابت CSS/JS/فونت/تصویرِ صفحات (نه /uploads) همیشه اول از شبکه گرفته می‌شوند و نسخهٔ آخرشان برای
 *    زمان قطع سرور نگه داشته می‌شود (از هر فایل فقط آخرین نسخه).
 *  - به‌روزرسانی SW هرگز IndexedDB را پاک نمی‌کند؛ پس عملیات Pending و صفحات ذخیره‌شده با تغییر نسخه حفظ می‌شوند.
 */
'use strict';

const VERSION = 'mashal-offline-v6';
const SHELL_CACHE = VERSION + '-shell';
const OFFLINE_URL = '/offline/index.html';
const PAGE_LOADER_URL = '/offline/page.html';
const NAVIGATION_TIMEOUT_MS = 9000;

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
  '/offline/js/page-loader.js',
  '/offline/js/page-apply.js',
  '/offline/js/form-bridge.js',
  '/manifest.webmanifest',
  '/mashal-icon-192.png',
  '/mashal-icon-512.png',
  '/mashal-apple-touch-icon-180.png',
  '/MASHAL_White.ico',
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
  for (const old of await store.keys()) {
    const oldUrl = new URL(old.url);
    if (oldUrl.pathname === url.pathname && oldUrl.search !== url.search) {
      await store.delete(old);
    }
  }
  await store.put(request.url, response);
}

/** فایل صفحه: همیشه اول شبکه (با HTTP cache عادی مرورگر)؛ نسخهٔ ذخیره فقط وقتی سرور در دسترس نیست. */
async function pageAsset(event) {
  const request = event.request;
  const fromCache = async () => {
    const store = await caches.open(PAGE_ASSETS_CACHE);
    return (await store.match(request.url, { ignoreVary: true }))
      || store.match(request.url, { ignoreSearch: true, ignoreVary: true });
  };
  try {
    const response = await fetch(request);
    if (GATEWAY_DOWN.includes(response.status)) {
      return (await fromCache()) || response;
    }
    if (response.ok && response.type === 'basic') {
      event.waitUntil(rememberAsset(request, response.clone()).catch(() => {}));
    }
    return response;
  } catch (error) {
    const cached = await fromCache();
    if (cached) {
      return cached;
    }
    throw error;
  }
}

function isShellAsset(url) {
  return SHELL_ASSETS.includes(url.pathname) || url.pathname.startsWith('/offline/');
}

async function cacheFirst(request) {
  const cache = await caches.open(SHELL_CACHE);
  const cached = await cache.match(request, { ignoreSearch: true });
  const network = fetch(request).then((response) => {
    if (response && response.ok && response.type === 'basic') {
      cache.put(request, response.clone()).catch(() => {});
    }
    return response;
  }).catch(() => null);
  if (cached) {
    return cached;
  }
  const response = await network;
  return response || Response.error();
}

function withTimeout(promise, ms) {
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error('timeout')), ms);
    promise.then((value) => { clearTimeout(timer); resolve(value); },
      (error) => { clearTimeout(timer); reject(error); });
  });
}

async function navigate(request) {
  try {
    // صفحهٔ MVC همیشه از شبکه؛ هرگز cache نمی‌شود. پروکسیِ بدون سرور (502/503/504) هم یعنی آفلاین.
    const response = await withTimeout(fetch(request), NAVIGATION_TIMEOUT_MS);
    if (!GATEWAY_DOWN.includes(response.status)) {
      return response;
    }
    throw new Error('gateway');
  } catch {
    // همان آدرس از نسخهٔ رمزگذاری‌شدهٔ کاربر (page-loader.js) با همان ظاهر و منو.
    const cache = await caches.open(SHELL_CACHE);
    const loader = (await cache.match(PAGE_LOADER_URL)) || (await cache.match(OFFLINE_URL));
    return loader || new Response(
      '<!doctype html><meta charset="utf-8"><title>مشعل</title><p dir="rtl">سرور در دسترس نیست.</p>',
      { status: 503, headers: { 'Content-Type': 'text/html; charset=utf-8' } });
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
    event.respondWith(cacheFirst(request));
    return;
  }

  // ناوبری SPAی MVC (X-PTG-SPA) fetch عادی است، نه navigate؛ قطعِ آن را spa-nav + form-bridge مدیریت می‌کنند.
  if (request.mode === 'navigate') {
    event.respondWith(navigate(request));
    return;
  }

  if (['style', 'script', 'font', 'image'].includes(request.destination) && isPageAsset(request.url)) {
    event.respondWith(pageAsset(event));
  }
});
