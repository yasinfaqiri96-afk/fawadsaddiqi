// مشعل آفلاین — گاوصندوق محلی: فعال‌سازی دستگاه، ورود آفلاین با رمز محلی و رمزگذاری معلومات.
//
// - برای هر کاربر/شرکت یک کلید دادهٔ تصادفی AES-GCM-256 ساخته می‌شود.
// - این کلید با کلیدی که از رمز محلی (PBKDF2-SHA256) مشتق شده رمزگذاری می‌شود؛ رمز محلی هرگز ذخیره نمی‌شود.
// - کلید داده فقط در حافظهٔ برنامه و فقط پس از ورود است (non-extractable).
// - تلاش ناموفق: پس از ۵ تلاش، قفل موقت با دو برابر شدن تا حداکثر یک ساعت. هیچ معلوماتی پاک نمی‌شود.
//
// محدودیت صریح: رمزگذاری سمت مرورگر در برابر دسترسی گذرا محافظت می‌کند؛ کسی که فایل‌های مرورگر را
// کپی کند می‌تواند رمز کوتاه را آفلاین حدس بزند. رمز طولانی‌تر و قفل/رمزگذاری خود دستگاه لازم است.

import { fromBase64, fromUtf8, toBase64, utf8 } from './util.js';

const subtle = globalThis.crypto.subtle;

export const KDF_ITERATIONS = 310000;
export const MIN_PIN_LENGTH = 6;
export const FREE_ATTEMPTS = 5;
export const BASE_LOCK_MS = 30_000;
export const MAX_LOCK_MS = 60 * 60_000;

export function profileId(companyId, userId) {
  return `c${Number(companyId)}-u${Number(userId)}`;
}

export function validatePin(pin) {
  if (typeof pin !== 'string' || pin.length < MIN_PIN_LENGTH) {
    return `رمز محلی باید حداقل ${MIN_PIN_LENGTH} حرف یا رقم باشد.`;
  }
  if (/^(\d)\1+$/.test(pin) || pin === '123456' || pin === '654321') {
    return 'این رمز بسیار ساده است؛ رمز دیگری انتخاب کنید.';
  }
  return null;
}

export async function deriveKey(secret, saltBytes, iterations) {
  const base = await subtle.importKey('raw', utf8(secret), 'PBKDF2', false, ['deriveKey']);
  return subtle.deriveKey(
    { name: 'PBKDF2', hash: 'SHA-256', salt: saltBytes, iterations },
    base,
    { name: 'AES-GCM', length: 256 },
    false,
    ['encrypt', 'decrypt']);
}

export async function encryptBytes(key, bytes) {
  const iv = globalThis.crypto.getRandomValues(new Uint8Array(12));
  const ct = await subtle.encrypt({ name: 'AES-GCM', iv }, key, bytes);
  return { iv: toBase64(iv), ct: toBase64(ct) };
}

export async function decryptBytes(key, sealed) {
  return new Uint8Array(await subtle.decrypt({ name: 'AES-GCM', iv: fromBase64(sealed.iv) }, key, fromBase64(sealed.ct)));
}

/** کلید داده از نسخهٔ base64 نشست جاری (session.js). */
export async function importRawKey(rawKey) {
  return importDataKey(fromBase64(rawKey));
}

async function importDataKey(raw) {
  return subtle.importKey('raw', raw, { name: 'AES-GCM' }, false, ['encrypt', 'decrypt']);
}

/** پروفایل تازه برای کاربرِ واردشده (فقط وقتی سرور در دسترس و نشست معتبر است). */
export async function createProfile({ session, pin, deviceId, now = new Date() }) {
  const pinError = validatePin(pin);
  if (pinError) {
    throw new Error(pinError);
  }
  const raw = globalThis.crypto.getRandomValues(new Uint8Array(32));
  const salt = globalThis.crypto.getRandomValues(new Uint8Array(16));
  const pinKey = await deriveKey(pin, salt, KDF_ITERATIONS);
  const wrapped = await encryptBytes(pinKey, raw);
  const dataKey = await importDataKey(raw);
  const rawKey = toBase64(raw);
  raw.fill(0);

  const profile = {
    id: profileId(session.companyId, session.userId),
    userId: session.userId,
    username: session.username,
    fullName: session.fullName || session.username,
    role: session.role || null,
    companyId: session.companyId,
    companyName: session.companyName,
    deviceId,
    kdf: { alg: 'PBKDF2-SHA256', salt: toBase64(salt), iterations: KDF_ITERATIONS },
    wrapped,
    failedAttempts: 0,
    lockedUntil: null,
    createdAt: now.toISOString(),
    lastUnlockAt: now.toISOString(),
    lastServerAuthAt: now.toISOString()
  };
  return { profile, dataKey, rawKey };
}

export function lockDurationMs(failedAttempts) {
  if (failedAttempts < FREE_ATTEMPTS) {
    return 0;
  }
  return Math.min(MAX_LOCK_MS, BASE_LOCK_MS * 2 ** (failedAttempts - FREE_ATTEMPTS));
}

/**
 * ورود آفلاین. پروفایل به‌روزشده (شمارندهٔ تلاش) همیشه برگردانده می‌شود تا فراخواننده ذخیره کند.
 * @returns {{ ok: boolean, profile: object, dataKey?: CryptoKey, reason?: string, lockedUntil?: string }}
 */
export async function unlockProfile(profile, pin, now = new Date()) {
  const updated = { ...profile };
  if (updated.lockedUntil && new Date(updated.lockedUntil) > now) {
    return { ok: false, profile: updated, reason: 'locked', lockedUntil: updated.lockedUntil };
  }

  try {
    const pinKey = await deriveKey(pin, fromBase64(profile.kdf.salt), profile.kdf.iterations);
    const raw = await decryptBytes(pinKey, profile.wrapped);
    const dataKey = await importDataKey(raw);
    const rawKey = toBase64(raw);
    raw.fill(0);
    updated.failedAttempts = 0;
    updated.lockedUntil = null;
    updated.lastUnlockAt = now.toISOString();
    // rawKey فقط برای نگهداری در sessionStorage همین نشست (session.js)؛ رمز محلی در هر نشست یک‌بار.
    return { ok: true, profile: updated, dataKey, rawKey };
  } catch {
    updated.failedAttempts = (updated.failedAttempts || 0) + 1;
    const lockMs = lockDurationMs(updated.failedAttempts);
    updated.lockedUntil = lockMs > 0 ? new Date(now.getTime() + lockMs).toISOString() : null;
    return { ok: false, profile: updated, reason: 'wrong', lockedUntil: updated.lockedUntil };
  }
}

export async function changePin(profile, oldPin, newPin) {
  const pinError = validatePin(newPin);
  if (pinError) {
    throw new Error(pinError);
  }
  const pinKey = await deriveKey(oldPin, fromBase64(profile.kdf.salt), profile.kdf.iterations);
  const raw = await decryptBytes(pinKey, profile.wrapped);
  const salt = globalThis.crypto.getRandomValues(new Uint8Array(16));
  const newKey = await deriveKey(newPin, salt, KDF_ITERATIONS);
  const wrapped = await encryptBytes(newKey, raw);
  raw.fill(0);
  return { ...profile, kdf: { alg: 'PBKDF2-SHA256', salt: toBase64(salt), iterations: KDF_ITERATIONS }, wrapped };
}

/** رمزگذاری/بازکردن رکورد JSON با کلید داده. */
export class Sealer {
  constructor(dataKey) {
    this.key = dataKey;
  }

  async seal(value) {
    return encryptBytes(this.key, utf8(JSON.stringify(value)));
  }

  async open(sealed) {
    return JSON.parse(fromUtf8(await decryptBytes(this.key, sealed)));
  }
}

// ---------------------------------------------------------------- صندوق صفحات (بدون رمز محلی قابل نوشتن)
//
// صفحات مشعل باید وقتی کاربر آنلاین است (حتی اگر در این نشست رمز محلی را وارد نکرده باشد) برای کار بدون سرور
// آماده شوند. برای این کار هر پروفایل یک جفت کلید RSA-OAEP دارد: کلید عمومی آشکار در پروفایل (فقط رمزگذاری) و کلید
// خصوصی رمزگذاری‌شده با کلید دادهٔ همان پروفایل. هر صفحه با یک کلید AES تصادفی رمزگذاری و آن کلید با کلید عمومی
// پیچیده می‌شود؛ پس نوشتن بدون رمز ممکن است ولی خواندن فقط پس از رمز محلی. Outbox و معلومات دیگر دست نمی‌خورند.

const PAGE_RSA = { name: 'RSA-OAEP', hash: 'SHA-256' };

export async function createPageKeys(sealer) {
  const pair = await subtle.generateKey({ ...PAGE_RSA, modulusLength: 2048, publicExponent: new Uint8Array([1, 0, 1]) },
    true, ['encrypt', 'decrypt']);
  return {
    alg: 'RSA-OAEP-256',
    publicJwk: await subtle.exportKey('jwk', pair.publicKey),
    privateSealed: await sealer.seal(await subtle.exportKey('jwk', pair.privateKey))
  };
}

/**
 * رمزگذاری صفحات: با کلید داده (نشست باز) یا فقط با کلید عمومی (نشست بسته)؛ بازکردن هر دو قالب پس از رمز محلی.
 * همان رابط Sealer (seal/open) برای PageVault.
 */
export class PageSealer {
  constructor({ sealer = null, publicKey = null, privateKey = null } = {}) {
    this.sealer = sealer;
    this.publicKey = publicKey;
    this.privateKey = privateKey;
  }

  /** @returns {Promise<PageSealer | null>} null یعنی این پروفایل هنوز صندوق صفحات ندارد و نشست هم باز نیست. */
  static async forProfile(profile, sealer = null) {
    const keys = profile?.pageKeys;
    const publicKey = keys?.publicJwk
      ? await subtle.importKey('jwk', keys.publicJwk, PAGE_RSA, false, ['encrypt']).catch(() => null)
      : null;
    let privateKey = null;
    if (sealer && keys?.privateSealed) {
      privateKey = await sealer.open(keys.privateSealed)
        .then((jwk) => subtle.importKey('jwk', jwk, PAGE_RSA, false, ['decrypt']))
        .catch(() => null);
    }
    return sealer || publicKey ? new PageSealer({ sealer, publicKey, privateKey }) : null;
  }

  get canOpen() {
    return !!this.sealer;
  }

  async seal(value) {
    if (this.sealer) return this.sealer.seal(value);
    const key = await subtle.generateKey({ name: 'AES-GCM', length: 256 }, true, ['encrypt', 'decrypt']);
    const raw = new Uint8Array(await subtle.exportKey('raw', key));
    const wk = toBase64(await subtle.encrypt(PAGE_RSA, this.publicKey, raw));
    raw.fill(0);
    return { ...(await encryptBytes(key, utf8(JSON.stringify(value)))), wk };
  }

  async open(sealed) {
    if (!sealed?.wk) {
      if (!this.sealer) throw new Error('locked');
      return this.sealer.open(sealed);
    }
    if (!this.privateKey) throw new Error('locked');
    const raw = new Uint8Array(await subtle.decrypt(PAGE_RSA, this.privateKey, fromBase64(sealed.wk)));
    const key = await subtle.importKey('raw', raw, { name: 'AES-GCM' }, false, ['decrypt']);
    raw.fill(0);
    return JSON.parse(fromUtf8(await decryptBytes(key, sealed)));
  }
}
