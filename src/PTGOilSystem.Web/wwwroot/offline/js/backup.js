// مشعل آفلاین — بکاپ رمزگذاری‌شده و بازیابی.
//
// فایل بکاپ بیرون از حافظهٔ مرورگر نگهداری می‌شود (دانلود، یا اشتراک به Files/Drive/فلش).
// محتوا با رمز جداگانهٔ بکاپ (PBKDF2-SHA256 + AES-GCM-256) رمزگذاری و با SHA-256 بررسی صحت می‌شود.
// بازیابی شناسهٔ عملیات‌ها را حفظ می‌کند؛ پس ارسال دوباره پس از Restore در سرور تکراری ثبت نمی‌شود.

import { decryptBytes, deriveKey, encryptBytes } from './vault.js';
import { STATUS_RANK, Status } from './operations.js';
import { fromBase64, fromUtf8, sha256Hex, toBase64, utf8 } from './util.js';

export const BACKUP_FORMAT = 'mashal-offline-backup';
export const BACKUP_VERSION = 1;
export const BACKUP_KDF_ITERATIONS = 310000;
export const MIN_BACKUP_PASSWORD = 8;

export async function createBackup({ profile, records, meta = {}, password, now = new Date() }) {
  if (typeof password !== 'string' || password.length < MIN_BACKUP_PASSWORD) {
    throw new Error(`رمز بکاپ باید حداقل ${MIN_BACKUP_PASSWORD} حرف باشد.`);
  }
  const content = {
    format: BACKUP_FORMAT,
    version: BACKUP_VERSION,
    createdAt: now.toISOString(),
    profile: {
      userId: profile.userId,
      username: profile.username,
      companyId: profile.companyId,
      companyName: profile.companyName,
      deviceId: profile.deviceId
    },
    meta,
    records
  };
  const plaintext = JSON.stringify(content);
  const sha256 = await sha256Hex(plaintext);
  const salt = globalThis.crypto.getRandomValues(new Uint8Array(16));
  const key = await deriveKey(password, salt, BACKUP_KDF_ITERATIONS);
  const sealed = await encryptBytes(key, utf8(plaintext));
  return {
    format: BACKUP_FORMAT,
    version: BACKUP_VERSION,
    createdAt: content.createdAt,
    userId: profile.userId,
    companyId: profile.companyId,
    count: records.length,
    kdf: { alg: 'PBKDF2-SHA256', salt: toBase64(salt), iterations: BACKUP_KDF_ITERATIONS },
    cipher: 'AES-GCM-256',
    iv: sealed.iv,
    ciphertext: sealed.ct,
    sha256
  };
}

export function backupFileName(profile, now = new Date()) {
  const pad = (n) => String(n).padStart(2, '0');
  const stamp = `${now.getFullYear()}${pad(now.getMonth() + 1)}${pad(now.getDate())}-${pad(now.getHours())}${pad(now.getMinutes())}`;
  return `mashal-backup-c${profile.companyId}-u${profile.userId}-${stamp}.mashalbak`;
}

/** بازکردن و بررسی صحت. خطا با پیام فارسی. */
export async function openBackup(fileText, password) {
  let envelope;
  try {
    envelope = JSON.parse(fileText);
  } catch {
    throw new Error('فایل بکاپ معتبر نیست.');
  }
  if (!envelope || envelope.format !== BACKUP_FORMAT || typeof envelope.ciphertext !== 'string') {
    throw new Error('این فایل بکاپ سیستم صدیقی نیست.');
  }
  if (envelope.version > BACKUP_VERSION) {
    throw new Error('این بکاپ با نسخهٔ جدیدتر سیستم صدیقی ساخته شده است؛ ابتدا برنامه را به‌روز کنید.');
  }
  let plaintext;
  try {
    const key = await deriveKey(password, fromBase64(envelope.kdf.salt), envelope.kdf.iterations);
    plaintext = fromUtf8(await decryptBytes(key, { iv: envelope.iv, ct: envelope.ciphertext }));
  } catch {
    throw new Error('رمز بکاپ نادرست است یا فایل خراب شده است.');
  }
  if ((await sha256Hex(plaintext)) !== envelope.sha256) {
    throw new Error('بررسی صحت بکاپ ناموفق بود؛ فایل تغییر کرده یا خراب است.');
  }
  const content = JSON.parse(plaintext);
  if (!Array.isArray(content.records) || !content.profile) {
    throw new Error('ساختار بکاپ ناقص است.');
  }
  for (const record of content.records) {
    if (!record || typeof record.clientOperationId !== 'string' || !record.operationType || !record.status) {
      throw new Error('یکی از عملیات داخل بکاپ ناقص است.');
    }
    if (record.payloadJson && record.payloadHash && (await sha256Hex(record.payloadJson)) !== record.payloadHash) {
      throw new Error('هش یکی از عملیات داخل بکاپ درست نیست.');
    }
  }
  return content;
}

/**
 * ادغام بکاپ با صف فعلی. هر شناسه فقط یک‌بار؛ وضعیت پیشرفته‌تر برنده است؛ «در حال ارسال» به
 * «در انتظار» برمی‌گردد تا با همان شناسه دوباره بررسی شود.
 * @returns {{ toWrite: object[], added: number, updated: number, skipped: number }}
 */
export function mergeBackup(currentRecords, backupContent, profile, now = new Date()) {
  if (backupContent.profile.userId !== profile.userId || backupContent.profile.companyId !== profile.companyId) {
    throw new Error('این بکاپ به کاربر یا شرکت دیگری تعلق دارد و در این پروفایل بازیابی نمی‌شود.');
  }
  const current = new Map(currentRecords.map((r) => [r.clientOperationId, r]));
  const toWrite = [];
  let added = 0;
  let updated = 0;
  let skipped = 0;
  for (const source of backupContent.records) {
    if (source.userId !== profile.userId || source.companyId !== profile.companyId) {
      skipped += 1;
      continue;
    }
    let incoming = { ...source };
    if (incoming.status === Status.Sending) {
      incoming = {
        ...incoming,
        status: Status.Pending,
        nextAttemptAt: now.toISOString(),
        history: [...(incoming.history || []), { at: now.toISOString(), status: Status.Pending, note: 'بازیابی از بکاپ' }]
      };
    }
    const existing = current.get(incoming.clientOperationId);
    if (!existing) {
      toWrite.push(incoming);
      added += 1;
    } else if ((STATUS_RANK[incoming.status] ?? 0) > (STATUS_RANK[existing.status] ?? 0)) {
      toWrite.push({ ...incoming, payload: existing.payload, payloadJson: existing.payloadJson || incoming.payloadJson,
        payloadHash: existing.payloadHash || incoming.payloadHash });
      updated += 1;
    } else {
      skipped += 1;
    }
  }
  return { toWrite, added, updated, skipped };
}

/** هشدار بکاپ: عملیات ارسال‌نشده‌ای که پس از آخرین بکاپ ساخته شده است. */
export function backupWarning(records, lastBackupAt, now = new Date(), maxAgeMs = 24 * 60 * 60_000) {
  const unsynced = records.filter((r) => r.status === Status.Draft || r.status === Status.Pending || r.status === Status.Sending);
  const notBackedUp = lastBackupAt ? unsynced.filter((r) => r.updatedAt > lastBackupAt) : unsynced;
  const stale = !lastBackupAt || now.getTime() - new Date(lastBackupAt).getTime() > maxAgeMs;
  return { unsynced: unsynced.length, notBackedUp: notBackedUp.length, shouldWarn: notBackedUp.length > 0 && stale };
}
