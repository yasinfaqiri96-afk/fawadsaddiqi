// مشعل آفلاین — ابزارهای عمومی (بدون وابستگی به DOM تا در Node هم تست شوند).

const cryptoApi = globalThis.crypto;

export function uuid() {
  if (typeof cryptoApi.randomUUID === 'function') {
    return cryptoApi.randomUUID();
  }
  const bytes = cryptoApi.getRandomValues(new Uint8Array(16));
  bytes[6] = (bytes[6] & 0x0f) | 0x40;
  bytes[8] = (bytes[8] & 0x3f) | 0x80;
  const hex = Array.from(bytes, (b) => b.toString(16).padStart(2, '0')).join('');
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
}

export function utf8(text) {
  return new TextEncoder().encode(text);
}

export function fromUtf8(bytes) {
  return new TextDecoder().decode(bytes);
}

export function toHex(buffer) {
  return Array.from(new Uint8Array(buffer), (b) => b.toString(16).padStart(2, '0')).join('');
}

/** SHA-256 هگزِ کوچک — همان الگوریتم سرور (OfflineSyncService.ComputeHash). */
export async function sha256Hex(text) {
  return toHex(await cryptoApi.subtle.digest('SHA-256', utf8(text)));
}

export function toBase64(buffer) {
  const bytes = buffer instanceof Uint8Array ? buffer : new Uint8Array(buffer);
  let binary = '';
  for (let i = 0; i < bytes.length; i += 0x8000) {
    binary += String.fromCharCode.apply(null, bytes.subarray(i, i + 0x8000));
  }
  return btoa(binary);
}

export function fromBase64(text) {
  const binary = atob(text);
  const bytes = new Uint8Array(binary.length);
  for (let i = 0; i < binary.length; i += 1) {
    bytes[i] = binary.charCodeAt(i);
  }
  return bytes;
}

/** تاریخ محلیِ دستگاه به شکل yyyy-MM-dd (تاریخ کاری). */
export function todayIso(now = new Date()) {
  const y = now.getFullYear();
  const m = String(now.getMonth() + 1).padStart(2, '0');
  const d = String(now.getDate()).padStart(2, '0');
  return `${y}-${m}-${d}`;
}

const faDigits = new Intl.NumberFormat('fa-AF', { maximumFractionDigits: 3 });

export function formatNumber(value) {
  const n = Number(value);
  return Number.isFinite(n) ? faDigits.format(n) : '—';
}

export function formatDateTime(iso) {
  if (!iso) {
    return '—';
  }
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) {
    return '—';
  }
  try {
    return new Intl.DateTimeFormat('fa-AF', { dateStyle: 'medium', timeStyle: 'short' }).format(date);
  } catch {
    return date.toLocaleString();
  }
}

export function relativeAge(iso, now = Date.now()) {
  if (!iso) {
    return 'هرگز';
  }
  const minutes = Math.round((now - new Date(iso).getTime()) / 60000);
  if (minutes < 1) return 'همین حالا';
  if (minutes < 60) return `${formatNumber(minutes)} دقیقه پیش`;
  const hours = Math.round(minutes / 60);
  if (hours < 48) return `${formatNumber(hours)} ساعت پیش`;
  return `${formatNumber(Math.round(hours / 24))} روز پیش`;
}

export function sleep(ms) {
  return new Promise((resolve) => setTimeout(resolve, ms));
}
