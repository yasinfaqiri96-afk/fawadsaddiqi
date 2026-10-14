// مشعل آفلاین — نسخهٔ رمزگذاری‌شدهٔ صفحات مشعل برای خواندن و کار بدون سرور.
//
// - هر صفحه‌ای که کاربر آنلاین باز می‌کند یا آماده‌سازی خودکار (prepare.js) می‌گیرد، با کلید پروفایل همین کاربر
//   رمزگذاری و در IndexedDBِ همان پروفایل نگه داشته می‌شود؛ کلید ردیف SHA-256ِ آدرس است (آدرس، عنوان و محتوا
//   رمزگذاری‌شده‌اند). در فهرست فقط هَش مسیر/آدرس، اندازه، زمان، اولویت و ETagِ سرور آشکار است.
// - «پوسته» (Layout کامل با منو و سربرگ همین کاربر) جدا نگه داشته می‌شود تا صفحه بدون سرور با همان ظاهر ساخته شود.
// - صفحه‌ای که با همین آدرس دقیق ذخیره نشده، از نسخهٔ هم‌مسیر ساخته می‌شود (resolve): فهرست با جستجو/صفحه‌بندی
//   محلی (page-resolve.js)، فورم از الگوی همان فورم، و پارامترهای فقط-نمایشی (returnUrl، tab، …) نادیده.
// - نسخه‌ها با قطع طولانی منقضی یا پاک نمی‌شوند؛ وقتی سقف حجم/تعداد پر شود، اول کم‌اولویت‌ترین و قدیمی‌ترین صفحه
//   جای خود را می‌دهد (فورم‌ها و داشبورد آخر از همه). Outbox در store جدا است و هرگز این‌جا حذف نمی‌شود.
// - این‌ها نسخهٔ لحظهٔ ذخیره‌اند، نه ارقام لحظه‌ای؛ زمان ذخیره همیشه همراه صفحه نمایش داده می‌شود.

import { sha256Hex } from './util.js';
import { isQuotaError } from './store.js';

export const SHELL_KEY = 'shell';
const INDEX_KEY = 'pagesIndex';
export const MAX_PAGES = 700;
export const MAX_DATA = 400;
export const MAX_BYTES = 150 * 1024 * 1024;

/** اولویت نگهداری: کمتر = مهم‌تر (دیرتر حذف می‌شود). */
export const Prio = Object.freeze({ Core: 0, Form: 1, List: 2, Page: 3 });

/** پارامترهایی که محتوای صفحه را عوض نمی‌کنند (بازگشت، تب سمت کاربر، منبع ورود). */
export const UI_PARAMS = ['returnurl', 'tab', 'detailtab', 'guide', 'source', 'offlineedit', '_'];
/** پارامترهای فهرست که بدون سرور روی سطرهای ذخیره‌شده اعمال می‌شوند. */
export const LIST_PARAMS = ['page', 'pagesize', 'q', 'search', 'term', 'searchterm'];

/** آدرس پایدار صفحه: مسیر کوچک‌حرف + querystring (بدون پارامترهای موقت و #). */
export function normalizePageUrl(raw, base = globalThis.location?.href || 'http://localhost/') {
  const url = new URL(raw, base);
  for (const name of ['offlineEdit', '_', 'source']) url.searchParams.delete(name);
  url.searchParams.sort();
  const path = url.pathname.toLowerCase().replace(/\/+$/, '') || '/';
  const query = url.searchParams.toString();
  return query ? `${path}?${query}` : path;
}

export function pagePath(raw, base = globalThis.location?.href || 'http://localhost/') {
  return new URL(raw, base).pathname.toLowerCase().replace(/\/+$/, '') || '/';
}

/** آدرس بدون پارامترهای فقط-نمایشی (نام پارامترها کوچک‌حرف؛ ASP.NET به حروف حساس نیست). */
export function loosePageUrl(raw, base = globalThis.location?.href || 'http://localhost/') {
  const url = new URL(raw, base);
  const pairs = [...url.searchParams]
    .map(([k, v]) => [k.toLowerCase(), v])
    .filter(([k, v]) => !UI_PARAMS.includes(k) && v !== '' && !(k === 'page' && v === '1'))
    .sort((a, b) => (a[0] === b[0] ? (a[1] < b[1] ? -1 : 1) : a[0] < b[0] ? -1 : 1));
  const path = pagePath(raw, base);
  return pairs.length ? `${path}?${new URLSearchParams(pairs)}` : path;
}


export function pageTitle(html) {
  const match = /<title[^>]*>([\s\S]*?)<\/title>/i.exec(html || '');
  return match ? match[1].replace(/\s+/g, ' ').trim().slice(0, 200) : '';
}

/**
 * فهرست قابل صفحه‌بندی محلی: جدول + نوار صفحه‌بندی سرور. complete=همهٔ سطرها در همین نسخه است (یک صفحه).
 * @returns {{ rows: number, complete: boolean } | null}
 */
export function listInfo(html) {
  if (!html || !/data-server-pagination="true"/.test(html)) return null;
  const main = html.slice(Math.max(0, html.search(/<main[\s>]/i)));
  const body = /<tbody[^>]*>([\s\S]*?)<\/tbody>/i.exec(main);
  if (!body) return null;
  const rows = (body[1].match(/<tr[\s>]/gi) || []).length;
  // نوار صفحه‌بندی (ul.pagination) فقط وقتی سرور بیش از یک صفحه دارد ساخته می‌شود.
  const complete = !/class="pagination[\s"]/.test(main);
  return { rows, complete };
}

// نوشتن فهرست صفحات (read-modify-write) در هر پایگاه پشت‌سرهم، حتی از دو نمونهٔ PageVault (آماده‌سازی هم‌زمان).
const writeLocks = new Map();
function serialized(store, task) {
  const previous = writeLocks.get(store) || Promise.resolve();
  const next = previous.catch(() => {}).then(task);
  writeLocks.set(store, next.catch(() => {}));
  return next;
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

  /**
   * ذخیرهٔ نسخهٔ تازهٔ یک صفحه (و حذف کم‌اولویت‌ترین/قدیمی‌ترین‌ها اگر سقف پر شود)، در یک تراکنش.
   * full: سند کامل خودش است (صفحهٔ مودال/ثبت سریع)، نه محتوایی که در پوستهٔ منو جای‌گذاری شود.
   * listOf: این نسخه سطرهای فهرستِ آدرس دیگری است (مثلاً ?pageSize=200 برای همان مسیر).
   */
  async put(url, html, { shell = false, full = false, prio = null, etag = null } = {}) {
    if (!html) return;
    if (shell) {
      await this.write(SHELL_KEY, 'shell', { url: null, title: pageTitle(html), html, full }, { prio: Prio.Core, etag });
      return;
    }
    const key = await PageVault.keyFor(url);
    const list = listInfo(html);
    // فقط نسخهٔ بدون فیلتر/جستجو منبع سطرهای فهرست است (صفحهٔ ۱، هر اندازهٔ صفحه).
    const query = new URL(url, globalThis.location?.href || 'http://localhost/').searchParams;
    const unfiltered = [...query.keys()].every((k) => ['pagesize', ...UI_PARAMS].includes(k.toLowerCase()))
      || (query.get('page') === '1' && [...query.keys()].every((k) => ['page', 'pagesize', ...UI_PARAMS].includes(k.toLowerCase())));
    await this.write(key, 'page', { url: normalizePageUrl(url), title: pageTitle(html), html, full }, {
      prio: prio ?? (full ? Prio.Form : Prio.Page),
      etag,
      pk: await sha256Hex(pagePath(url)),
      lk: await sha256Hex(loosePageUrl(url)),
      list: list && unfiltered ? { rows: list.rows, complete: list.complete } : null
    });
  }

  /**
   * پاسخ JSONِ فهرست‌های وابستهٔ فورم (مثلاً گزینه‌های طرف قرارداد) برای همان آدرس؛ بدون سرور همان پاسخِ
   * آخرین بار برگردانده می‌شود. سقف جدا دارد تا صفحات را بیرون نکند.
   */
  async putData(url, body, contentType) {
    if (typeof body !== 'string') return;
    const key = 'data:' + await PageVault.keyFor(url);
    await this.write(key, 'data', { url: normalizePageUrl(url), body, contentType }, { prio: Prio.Page });
  }

  async getData(url) {
    return this.read('data:' + await PageVault.keyFor(url));
  }

  async write(key, kind, value, extra = {}) {
    const savedAt = this.clock().toISOString();
    const sealed = await this.sealer.seal({ ...value, savedAt });
    return serialized(this.store, async () => {
      const size = sealed.ct.length;
      let entries = (await this.index()).filter((e) => e.key !== key);
      const entry = { key, kind, savedAt, size, prio: extra.prio ?? Prio.Page };
      for (const name of ['etag', 'pk', 'lk', 'list']) if (extra[name]) entry[name] = extra[name];
      if (value.full) entry.full = true;
      entries.push(entry);
      const writes = [{ store: 'pages', type: 'put', value: { key, sealed } }];
      // مهم‌ترین و تازه‌ترین اول نگه داشته می‌شوند.
      entries.sort((a, b) => ((a.prio ?? Prio.Page) - (b.prio ?? Prio.Page)) || (a.savedAt < b.savedAt ? 1 : -1));
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
      try {
        await this.store.atomic(writes);
      } catch (error) {
        if (!isQuotaError(error)) throw error;
        // فضای دستگاه پر است: کم‌اولویت‌ترین صفحات جا باز می‌کنند (هرگز پوسته، هرگز Outbox) و یک‌بار دوباره.
        const freed = await this.evictLocked(Math.max(size * 2, 0.3 * total), key);
        if (!freed) throw error;
        const fresh = (await this.index()).filter((e) => e.key !== key);
        fresh.push(entry);
        await this.store.atomic([writes[0], { store: 'meta', type: 'put', value: { key: INDEX_KEY, entries: fresh } }]);
      }
    });
  }

  /** آزادکردن جا (پیش از ذخیرهٔ عملیات Outbox یا وقتی دستگاه پر است): کم‌اولویت‌ترین و قدیمی‌ترین اول. */
  async shrink(bytes) {
    return serialized(this.store, () => this.evictLocked(bytes, null));
  }

  async evictLocked(bytes, keepKey) {
    const entries = await this.index();
    const victims = entries
      .filter((e) => e.key !== SHELL_KEY && e.key !== keepKey)
      .sort((a, b) => ((b.prio ?? Prio.Page) - (a.prio ?? Prio.Page)) || (a.savedAt < b.savedAt ? -1 : 1));
    const drop = new Set();
    let freed = 0;
    for (const e of victims) {
      if (freed >= bytes) break;
      drop.add(e.key);
      freed += e.size || 0;
    }
    if (!drop.size) return 0;
    await this.store.atomic([
      ...[...drop].map((key) => ({ store: 'pages', type: 'delete', key })),
      { store: 'meta', type: 'put', value: { key: INDEX_KEY, entries: entries.filter((e) => !drop.has(e.key)) } }
    ]);
    return freed;
  }

  /** سرور گفت تغییری نکرده (304): فقط زمان بررسی تازه می‌شود؛ محتوا دوباره رمزگذاری و نوشته نمی‌شود. */
  async touch(url, { shell = false } = {}) {
    const key = shell ? SHELL_KEY : await PageVault.keyFor(url);
    return serialized(this.store, async () => {
      const entries = await this.index();
      const entry = entries.find((e) => e.key === key);
      if (!entry) return false;
      entry.checkedAt = this.clock().toISOString();
      await this.store.put('meta', { key: INDEX_KEY, entries });
      return true;
    });
  }

  async putShell(html, etag = null) {
    return this.put(null, html, { shell: true, etag });
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

  async entry(url, { shell = false } = {}) {
    const key = shell ? SHELL_KEY : await PageVault.keyFor(url);
    return (await this.index()).find((e) => e.key === key) || null;
  }

  async savedAt(url) {
    return (await this.entry(url))?.savedAt || null;
  }

  /**
   * نسخهٔ مناسب برای نمایش بدون سرور:
   *  exact    همان آدرس؛
   *  loose    همان آدرس بدون پارامترهای فقط-نمایشی (returnUrl، tab، …)؛
   *  list     فهرست همان مسیر (کامل‌ترین نسخه) تا جستجو/صفحه‌بندی روی سطرهای ذخیره‌شده اعمال شود؛
   *  template الگوی همان فورم (فقط وقتی isForm(path) — فورم ثبت تازه به رکورد خاصی وابسته نیست).
   * صفحهٔ جزئیات/گزارش با پارامتر دیگر هرگز از نسخهٔ رکورد/بازهٔ دیگر ساخته نمی‌شود.
   * @returns {Promise<{ page: object, match: 'exact'|'loose'|'list'|'template', entry: object } | null>}
   */
  async resolve(url, { isForm = () => false } = {}) {
    const entries = await this.index();
    const exactKey = await PageVault.keyFor(url);
    const exact = entries.find((e) => e.key === exactKey);
    if (exact) {
      const page = await this.read(exact.key).catch(() => null);
      if (page) return { page, match: 'exact', entry: exact };
    }
    const path = pagePath(url);
    const pk = await sha256Hex(path);
    // سند کامل مودال فقط جای مودال، و صفحهٔ داخل پوسته فقط جای صفحه.
    const modal = new URL(url, globalThis.location?.href || 'http://localhost/').searchParams.get('modal') === '1';
    const sameKind = entries.filter((e) => e.pk === pk && e.kind !== 'data' && e.key !== SHELL_KEY && !!e.full === modal);
    if (!sameKind.length) return null;
    const lk = await sha256Hex(loosePageUrl(url));
    const tryRead = async (candidates, match) => {
      for (const entry of candidates) {
        const page = await this.read(entry.key).catch(() => null);
        if (page) return { page, match, entry };
      }
      return null;
    };
    const newest = (a, b) => (a.savedAt < b.savedAt ? 1 : -1);
    const loose = await tryRead(sameKind.filter((e) => e.lk === lk).sort(newest), 'loose');
    if (loose) return loose;
    // فهرست: جستجو، صفحه‌بندی و فیلترهای قابل‌تطبیق روی سطرهای ذخیره‌شده (page-resolve.js)؛ فیلتر ناممکن صریح اعلام می‌شود.
    const lists = sameKind.filter((e) => e.list)
      .sort((a, b) => (Number(b.list.complete) - Number(a.list.complete)) || (b.list.rows - a.list.rows) || newest(a, b));
    const list = await tryRead(lists, 'list');
    if (list) return list;
    if (isForm(path)) {
      const plainKey = await sha256Hex(path);
      const templates = sameKind.filter((e) => !e.list)
        .sort((a, b) => (Number(b.lk === plainKey) - Number(a.lk === plainKey)) || newest(a, b));
      return tryRead(templates, 'template');
    }
    return null;
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

  /** حجم و تعداد (بدون بازکردن رمز). */
  async usage() {
    const entries = await this.index();
    return {
      pages: entries.filter((e) => e.kind !== 'data' && e.key !== SHELL_KEY).length,
      data: entries.filter((e) => e.kind === 'data').length,
      bytes: entries.reduce((sum, e) => sum + (e.size || 0), 0)
    };
  }
}
