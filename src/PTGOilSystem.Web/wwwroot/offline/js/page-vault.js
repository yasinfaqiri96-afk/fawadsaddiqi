// مشعل آفلاین — نسخهٔ رمزگذاری‌شدهٔ صفحات مشعل برای خواندن و کار بدون سرور.
//
// - هر صفحه‌ای که کاربر آنلاین باز می‌کند (همان پاسخ HTMLِ ناوبری SPA) با کلید پروفایل همین کاربر رمزگذاری و در
//   IndexedDBِ همان پروفایل نگه داشته می‌شود؛ کلید ردیف SHA-256ِ آدرس است (آدرس و عنوان صفحه هم رمزگذاری‌شده‌اند).
// - «پوسته» (Layout کامل با منو و سربرگ همین کاربر) جدا نگه داشته می‌شود تا صفحه بدون سرور با همان ظاهر ساخته شود.
// - نسخه‌ها با قطع طولانی منقضی یا پاک نمی‌شوند؛ فقط وقتی سقف حجم/تعداد پر شود، قدیمی‌ترین صفحه جای خود را می‌دهد.
// - این‌ها نسخهٔ لحظهٔ ذخیره‌اند، نه ارقام لحظه‌ای؛ زمان ذخیره همیشه همراه صفحه نمایش داده می‌شود.

import { sha256Hex } from './util.js';

export const SHELL_KEY = 'shell';
const INDEX_KEY = 'pagesIndex';
export const MAX_PAGES = 300;
export const MAX_DATA = 400;
export const MAX_BYTES = 80 * 1024 * 1024;

/** آدرس پایدار صفحه: مسیر کوچک‌حرف + querystring (بدون پارامترهای موقت و #). */
export function normalizePageUrl(raw, base = globalThis.location?.href || 'http://localhost/') {
  const url = new URL(raw, base);
  for (const name of ['offlineEdit', '_', 'source']) url.searchParams.delete(name);
  url.searchParams.sort();
  const path = url.pathname.toLowerCase().replace(/\/+$/, '') || '/';
  const query = url.searchParams.toString();
  return query ? `${path}?${query}` : path;
}

export function pageTitle(html) {
  const match = /<title[^>]*>([\s\S]*?)<\/title>/i.exec(html || '');
  return match ? match[1].replace(/\s+/g, ' ').trim().slice(0, 200) : '';
}

export class PageVault {
  /**
   * @param {import('./store.js').IdbStore} store
   * @param {import('./vault.js').Sealer} sealer
   */
  constructor(store, sealer, { clock = () => new Date(), maxPages = MAX_PAGES, maxData = MAX_DATA, maxBytes = MAX_BYTES } = {}) {
    this.store = store;
    this.sealer = sealer;
    this.clock = clock;
    this.maxPages = maxPages;
    this.maxData = maxData;
    this.maxBytes = maxBytes;
  }

  static async keyFor(url) {
    return sha256Hex(normalizePageUrl(url));
  }

  async index() {
    return (await this.store.get('meta', INDEX_KEY))?.entries || [];
  }

  /** ذخیرهٔ نسخهٔ تازهٔ یک صفحه (و حذف قدیمی‌ترین‌ها اگر سقف پر شود)، در یک تراکنش. */
  /** full: سند کامل خودش است (صفحهٔ مودال/ثبت سریع)، نه محتوایی که در پوستهٔ منو جای‌گذاری شود. */
  async put(url, html, { shell = false, full = false } = {}) {
    if (!html) return;
    const key = shell ? SHELL_KEY : await PageVault.keyFor(url);
    await this.write(key, shell ? 'shell' : 'page', { url: shell ? null : normalizePageUrl(url), title: pageTitle(html), html, full });
  }

  /**
   * پاسخ JSONِ فهرست‌های وابستهٔ فورم (مثلاً گزینه‌های طرف قرارداد) برای همان آدرس؛ بدون سرور همان پاسخِ
   * آخرین بار برگردانده می‌شود. سقف جدا دارد تا صفحات را بیرون نکند.
   */
  async putData(url, body, contentType) {
    if (typeof body !== 'string') return;
    const key = 'data:' + await PageVault.keyFor(url);
    await this.write(key, 'data', { url: normalizePageUrl(url), body, contentType });
  }

  async getData(url) {
    return this.read('data:' + await PageVault.keyFor(url));
  }

  async write(key, kind, value) {
    const savedAt = this.clock().toISOString();
    const sealed = await this.sealer.seal({ ...value, savedAt });
    const size = sealed.ct.length;
    let entries = (await this.index()).filter((e) => e.key !== key);
    entries.push({ key, kind, savedAt, size });
    const writes = [{ store: 'pages', type: 'put', value: { key, sealed } }];
    entries.sort((a, b) => (a.savedAt < b.savedAt ? 1 : -1));
    let total = 0;
    const seen = { page: 0, data: 0 };
    entries = entries.filter((e) => {
      const entryKind = e.kind || (e.key === SHELL_KEY ? 'shell' : 'page');
      total += e.size;
      if (entryKind !== 'shell') seen[entryKind] = (seen[entryKind] || 0) + 1;
      const limit = entryKind === 'data' ? this.maxData : this.maxPages;
      const keep = entryKind === 'shell' || e.key === key || (seen[entryKind] <= limit && total <= this.maxBytes);
      if (!keep) writes.push({ store: 'pages', type: 'delete', key: e.key });
      return keep;
    });
    writes.push({ store: 'meta', type: 'put', value: { key: INDEX_KEY, entries } });
    await this.store.atomic(writes);
  }

  async putShell(html) {
    return this.put(null, html, { shell: true });
  }

  async read(key) {
    const row = await this.store.get('pages', key);
    return row ? this.sealer.open(row.sealed) : null;
  }

  /** @returns {Promise<{ url: string, title: string, html: string, savedAt: string } | null>} */
  async get(url) {
    return this.read(await PageVault.keyFor(url));
  }

  async getShell() {
    return this.read(SHELL_KEY);
  }

  async savedAt(url) {
    const key = await PageVault.keyFor(url);
    return (await this.index()).find((e) => e.key === key)?.savedAt || null;
  }

  /** فهرست صفحات ذخیره‌شده (عنوان و آدرس پس از بازکردن رمز). */
  async list() {
    const result = [];
    for (const entry of await this.index()) {
      if (entry.key === SHELL_KEY || entry.kind === 'data') continue;
      const page = await this.read(entry.key).catch(() => null);
      if (page) result.push({ url: page.url, title: page.title, savedAt: page.savedAt });
    }
    return result;
  }
}
