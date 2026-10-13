// مشعل آفلاین — نشست بازشدهٔ دستگاه: رمز محلی در هر نشست برنامه فقط یک‌بار.
//
// پس از ورود با رمز محلی، کلید دادهٔ پروفایل برای همین پنجره/زبانه در sessionStorage نگه داشته می‌شود تا صفحات
// مشعل (ناوبری کامل، Refresh، بازشدن نسخهٔ آفلاین) بدون پرسیدن دوبارهٔ رمز همان Outbox و نسخه‌های ذخیره را باز کنند.
// - با بستن پنجرهٔ برنامه، خروج (Logout)، قفل دستی یا ۱۵ دقیقه بی‌فعالیتی پاک می‌شود؛ معلومات دستگاه پاک نمی‌شود.
// - محدودیت صریح: هر اسکریپتی که در همین دامنه اجرا شود، در طول نشست به کلید دسترسی دارد (همان سطحی که به
//   معلومات بازشده دارد). رمز محلی و کلیدِ رمزگذاری‌شدهٔ پروفایل هرگز این‌جا نیستند.

import { IdbStore, REGISTRY_DB, REGISTRY_STORES } from './store.js';
import { importRawKey } from './vault.js';

export const SESSION_KEY = 'mashal.offline.session';
export const SESSION_IDLE_MS = 15 * 60_000;
const TOUCH_EVERY_MS = 30_000;

function read() {
  try {
    return JSON.parse(sessionStorage.getItem(SESSION_KEY) || 'null');
  } catch {
    return null;
  }
}

/** @returns {boolean} true اگر کلید برای همین نشست نگه داشته شد. */
export function rememberSession(profile, rawKey, now = Date.now()) {
  if (!profile || !rawKey) return false;
  try {
    sessionStorage.setItem(SESSION_KEY, JSON.stringify({
      profileId: profile.id, userId: profile.userId, companyId: profile.companyId, key: rawKey, at: now
    }));
    return true;
  } catch {
    // حالت خصوصی مرورگر: رمز در هر بارگذاری کامل دوباره پرسیده می‌شود.
    return false;
  }
}

/** نشست هنوز در sessionStorage هست (خروج یا قفل در زبانهٔ برنامهٔ آفلاین آن را برمی‌دارد). */
export function hasSession() {
  return !!read()?.key;
}

export function forgetSession() {
  try {
    sessionStorage.removeItem(SESSION_KEY);
  } catch {
    // ignore
  }
}

/** فعالیت کاربر: مهلت بی‌فعالیتی تمدید می‌شود (حداکثر هر ۳۰ ثانیه یک نوشتن). */
export function touchSession(now = Date.now()) {
  const session = read();
  if (session && now - (session.at || 0) > TOUCH_EVERY_MS) {
    session.at = now;
    try {
      sessionStorage.setItem(SESSION_KEY, JSON.stringify(session));
    } catch {
      // ignore
    }
  }
}

/**
 * کلید نشست جاری، اگر هنوز معتبر است و (در صورت داده‌شدن) به همین کاربر تعلق دارد.
 * @returns {Promise<{ profileId: string, userId: number, companyId: number, dataKey: CryptoKey, rawKey: string } | null>}
 */
export async function restoreSession(userId = null, now = Date.now()) {
  const session = read();
  if (!session || !session.key) return null;
  if (now - (session.at || 0) > SESSION_IDLE_MS) {
    forgetSession();
    return null;
  }
  if (userId !== null && Number(session.userId) !== Number(userId)) return null;
  try {
    return { ...session, rawKey: session.key, dataKey: await importRawKey(session.key) };
  } catch {
    forgetSession();
    return null;
  }
}

let registryPromise = null;

export function openRegistry() {
  if (!registryPromise) {
    registryPromise = IdbStore.open(REGISTRY_DB, REGISTRY_STORES).catch((error) => {
      registryPromise = null;
      throw error;
    });
  }
  return registryPromise;
}

/** پروفایل‌های فعال‌شدهٔ این دستگاه (جدیدترین ورود اول)؛ با userId فقط پروفایل همان کاربر. */
export async function loadProfiles(userId = null) {
  const registry = await openRegistry();
  return (await registry.getAll('profiles'))
    .filter((p) => userId === null || Number(p.userId) === Number(userId))
    .sort((a, b) => (a.lastUnlockAt < b.lastUnlockAt ? 1 : -1));
}
