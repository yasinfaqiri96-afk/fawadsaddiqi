/*
 * مشعل — Service Worker
 *
 * قواعد امنیتی و داده:
 *  - فقط «پوستهٔ آفلاین» (HTML/CSS/JS/فونت/آیکن عمومی) cache می‌شود.
 *  - هیچ پاسخ /api و هیچ صفحهٔ HTMLِ واردشدهٔ MVC در Cache مرورگر ذخیره نمی‌شود.
 *  - معلومات کاربر فقط در IndexedDBِ رمزگذاری‌شدهٔ برنامهٔ آفلاین است، نه این‌جا.
 *  - به‌روزرسانی SW هرگز IndexedDB را پاک نمی‌کند؛ پس عملیات Pending با تغییر نسخه حفظ می‌شود.
 */
'use strict';

const VERSION = 'mashal-offline-v2';
const SHELL_CACHE = VERSION + '-shell';
const OFFLINE_URL = '/offline/index.html';
const NAVIGATION_TIMEOUT_MS = 9000;

const SHELL_ASSETS = [
  OFFLINE_URL,
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
  '/manifest.webmanifest',
  '/android-chrome-v3-192x192.png',
  '/android-chrome-v3-512x512.png',
  '/apple-touch-icon-v3.png',
  '/favicon-v3.ico',
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
      .filter((key) => key.startsWith('mashal-offline-') && !key.startsWith(VERSION))
      .map((key) => caches.delete(key)));
    await self.clients.claim();
  })());
});

self.addEventListener('message', (event) => {
  if (event.data && event.data.type === 'mashal:version') {
    event.source && event.source.postMessage({ type: 'mashal:version', version: VERSION });
  }
});

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
    if (![502, 503, 504].includes(response.status)) {
      return response;
    }
    throw new Error('gateway');
  } catch {
    const cache = await caches.open(SHELL_CACHE);
    const shell = await cache.match(OFFLINE_URL);
    return shell || new Response(
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

  // ناوبری SPAی MVC (X-PTG-SPA) fetch عادی است، نه navigate؛ دست نمی‌خورد.
  if (request.mode === 'navigate') {
    event.respondWith(navigate(request));
  }
});
