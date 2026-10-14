// مشعل آفلاین — آماده‌سازی خودکار کار بدون سرور در پس‌زمینه (بدون نیاز به بازکردن دستی صفحات).
//
// - صف اولویت‌دار: پوستهٔ Layout و داشبورد → فورم‌های مجاز ثبت آفلاین (و ثبت سریع) → صفحات منو و فهرست‌ها →
//   صفحات زیرمنو (پیوندهای داخل صفحات هاب، فقط مسیرهای بدون شناسهٔ رکورد).
// - هم‌زمانی محدود (پیش‌فرض ۲) با اولویت پایین شبکه؛ هنگام ناوبری/ثبت کاربر مکث می‌کند تا رابط کاربری کند نشود.
// - هر صفحه با ETag سرور (OfflinePrepareMiddleware) بررسی می‌شود: بدون تغییر = 304 بدون بدنه؛ دوباره رمزگذاری و
//   نوشته نمی‌شود. صفحهٔ تازه‌تر از maxAge اصلاً درخواست نمی‌شود.
// - فهرست‌ها با ?pageSize=200 گرفته می‌شوند: ۲۰۰ سطر اخیر با همان ترتیب سرور، تا جستجو و صفحه‌بندی بدون سرور
//   روی همان سطرها انجام شود (page-resolve.js). تاریخچهٔ کامل اسناد دانلود نمی‌شود.
// - هیچ صفحه‌ای بدون نشست ورود معتبر گرفته نمی‌شود (Redirect به ورود = توقف).

import { listInfo, Prio } from './page-vault.js';
import { isQuotaError } from './store.js';

export const LIST_ROWS = 200;
export const DEFAULT_CONCURRENCY = 2;
export const MAX_CRAWL = 250;

/** پیوندهایی که هرگز پیمایش نمی‌شوند: عملیات، فایل، جزئیات رکورد، ویرایش و مدیریت سیستم. */
const DENY = /(^|\/)(delete|cancel|reverse|close|export|download|pdf|excel|print|backup|backups|backuprestore|logout|login|edit|details|import|toggle|archive|restore|approve|post|apply|settle|confirm|lock|unlock|reopen|void|remove|reset|run|recalculate|sync|users|roles|systemsettings|maintenance|auditlogs|offlinesync|api|offline|uploads|auth|account)(\/|$)/i;

export function withParam(url, name, value) {
  const [path, query = ''] = url.split('?');
  const params = new URLSearchParams(query);
  params.set(name, String(value));
  return `${path}?${params}`;
}

/** پیوندهای داخل <main> یک صفحهٔ هاب که ارزش آماده‌سازی دارند (بدون querystring و شناسهٔ رکورد). */
export function hubLinks(html, allowed = () => true) {
  const start = html.search(/<main[\s>]/i);
  if (start < 0) return [];
  const end = html.indexOf('</main>', start);
  const main = html.slice(start, end > 0 ? end : undefined);
  const links = new Set();
  for (const match of main.matchAll(/<a\b[^>]*?\bhref="([^"#]+)"[^>]*>/gi)) {
    const tag = match[0];
    if (/\b(download|data-no-spa|data-export-link|data-bs-toggle|target=)/i.test(tag)) continue;
    const href = match[1].replace(/&amp;/g, '&');
    if (!href.startsWith('/') || href.startsWith('//') || href.includes('?')) continue;
    const path = href.replace(/\/+$/, '') || '/';
    const segments = path.split('/').filter(Boolean);
    if (!segments.length || segments.length > 2 || segments.some((s) => /^\d+$/.test(s) || /^[0-9a-f-]{20,}$/i.test(s))) continue;
    if (DENY.test(path) || !allowed(path)) continue;
    links.add(path);
  }
  return [...links];
}

/** چند رکورد اخیرِ هر فهرست منو که صفحهٔ جزئیات و ویرایششان هم آماده می‌شود (ویرایش/لغو بدون بازدید قبلی). */
export const RECENT_RECORDS = 3;
export const MAX_RECORD_PAGES = 120;
/** اولویت پایین‌تر از همهٔ صفحات: آخر از همه گرفته و اول از همه (در کمبود جا) کنار گذاشته می‌شود. */
export const RECORD_PRIO = Prio.Page + 1;

/**
 * صفحات جزئیات/ویرایشِ نخستین رکوردهای یک فهرست (همان ترتیب سرور = اخیرترین): «/sales/details/12»، «/sales/edit/12».
 * فقط رکوردهای همان ماژول؛ هیچ پیوند عملیاتی (لغو/حذف/…) پیمایش نمی‌شود.
 */
export function recordLinks(html, listPath, limit = RECENT_RECORDS) {
  const module = String(listPath || '').split(/[/?#]/).filter(Boolean)[0]?.toLowerCase();
  const start = html.search(/<main[\s>]/i);
  if (!module || start < 0) return [];
  const end = html.indexOf('</main>', start);
  const main = html.slice(start, end > 0 ? end : undefined);
  const ids = [];
  const links = [];
  for (const match of main.matchAll(/<a\b[^>]*?\bhref="\/([A-Za-z]+)\/(details|edit)\/(\d+)\/?"[^>]*>/gi)) {
    if (match[1].toLowerCase() !== module) continue;
    const id = match[3];
    if (!ids.includes(id)) {
      if (ids.length >= limit) break;
      ids.push(id);
    }
    for (const kind of ['details', 'edit']) {
      const link = `/${module}/${kind}/${id}`;
      if (!links.includes(link)) links.push(link);
    }
  }
  // پیوند «ویرایش» در فهرست ممکن است نباشد؛ اگر صفحهٔ ویرایش وجود نداشته باشد پاسخ 404 فقط شمرده می‌شود.
  return links.filter((link) => ids.includes(link.split('/').pop()));
}

export class Preparer {
  /**
   * @param {{
   *   vault: () => Promise<import('./page-vault.js').PageVault | null>,
   *   fetchImpl?: typeof fetch,
   *   isReachable?: () => boolean,
   *   isBusy?: () => boolean,
   *   allowed?: (path: string) => boolean,
   *   onPage?: (url: string, html: string) => void,
   *   concurrency?: number,
   *   now?: () => number,
   *   sleep?: (ms: number) => Promise<void>,
   *   maxCrawl?: number
   * }} options
   */
  constructor({ vault, fetchImpl = (...args) => globalThis.fetch(...args), isReachable = () => true, isBusy = () => false,
    allowed = () => true, onPage = () => {}, concurrency = DEFAULT_CONCURRENCY, now = () => Date.now(),
    sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms)), maxCrawl = MAX_CRAWL }) {
    Object.assign(this, { vault, fetchImpl, isReachable, isBusy, allowed, onPage, concurrency, now, sleep, maxCrawl });
    this.stopped = false;
  }

  stop() {
    this.stopped = true;
  }

  /**
   * @param {{ url: string, prio: number, mode?: 'page'|'shell'|'modal', maxAgeMs: number, crawl?: boolean, list?: boolean }[]} tasks
   */
  async run(tasks) {
    const summary = { fetched: 0, notModified: 0, skipped: 0, failed: 0, bytes: 0, lists: 0, crawled: 0, records: 0, aborted: null, crawlLinks: [] };
    const queued = new Set();
    const queue = [];
    let seq = 0;
    const add = (task) => {
      const id = `${task.mode || 'page'}|${task.url.toLowerCase()}`;
      if (queued.has(id)) return;
      queued.add(id);
      queue.push({ ...task, seq: seq += 1 });
      queue.sort((a, b) => (a.prio - b.prio) || (a.seq - b.seq));
    };
    tasks.forEach(add);

    const worker = async () => {
      while (queue.length && !this.stopped && !summary.aborted) {
        while (this.isBusy() && !this.stopped) await this.sleep(300);
        if (!this.isReachable()) {
          summary.aborted = 'offline';
          return;
        }
        const task = queue.shift();
        try {
          const outcome = await this.one(task, summary);
          if (outcome?.html && task.records) {
            for (const link of recordLinks(outcome.html, task.url, task.records)) {
              if (summary.records >= MAX_RECORD_PAGES) break;
              if (!this.allowed(link)) continue;
              const before = queued.size;
              add({ url: link, prio: RECORD_PRIO, maxAgeMs: task.crawlMaxAgeMs || task.maxAgeMs * 2 });
              if (queued.size > before) summary.records += 1;
            }
          }
          if (outcome?.html && task.crawl && summary.crawled < this.maxCrawl) {
            for (const link of hubLinks(outcome.html, this.allowed)) {
              if (summary.crawled >= this.maxCrawl) break;
              const before = queued.size;
              add({ url: link, prio: Prio.Page, maxAgeMs: task.crawlMaxAgeMs || task.maxAgeMs * 2, list: true });
              if (queued.size > before) {
                summary.crawled += 1;
                summary.crawlLinks.push(link);
              }
            }
          }
        } catch (error) {
          summary.failed += 1;
          if (error?.auth) summary.aborted = 'auth';
          // دستگاه حتی پس از آزادکردن صفحات کم‌اولویت جا ندارد: آماده‌سازی متوقف؛ Outbox دست نمی‌خورد.
          else if (isQuotaError(error)) summary.aborted = 'quota';
        }
      }
    };
    await Promise.all(Array.from({ length: Math.max(1, this.concurrency) }, worker));
    return summary;
  }

  /** یک صفحه: تازه = رد؛ ETag برابر = 304؛ وگرنه ذخیرهٔ رمزگذاری‌شده. */
  async one(task, summary) {
    const vault = await this.vault();
    if (!vault) throw Object.assign(new Error('no vault'), { auth: false });
    const shell = task.mode === 'shell';
    // فهرست: همان مسیر با ۲۰۰ سطر؛ صفحهٔ غیر فهرست همان پاسخ را می‌دهد و با آدرس خودش ذخیره می‌شود.
    const variant = task.list ? withParam(task.url, 'pageSize', LIST_ROWS) : task.url;
    let known = null;
    let storedUrl = task.url;
    if (shell) {
      known = await vault.entry(task.url, { shell: true });
    } else if (task.list) {
      known = await vault.entry(variant);
      if (known) storedUrl = variant;
      else known = await vault.entry(task.url);
    } else {
      known = await vault.entry(task.url);
    }
    const checked = known ? Math.max(Date.parse(known.savedAt) || 0, Date.parse(known.checkedAt || '') || 0) : 0;
    if (known && this.now() - checked < task.maxAgeMs) {
      summary.skipped += 1;
      return null;
    }
    const headers = { 'X-PTG-Prepare': '1' };
    if (task.mode === 'page' || !task.mode) headers['X-PTG-SPA'] = '1';
    if (known?.etag) headers['If-None-Match'] = known.etag;
    const response = await this.fetchImpl(variant, { credentials: 'same-origin', cache: 'no-store', headers, priority: 'low' });
    if (response.redirected || response.status === 401 || response.status === 403) {
      // ورود منقضی/بدون صلاحیت: چیزی ذخیره نمی‌شود.
      if (response.redirected && /\/auth\/login/i.test(response.url || '')) throw Object.assign(new Error('auth'), { auth: true });
      summary.failed += 1;
      return null;
    }
    if (response.status === 304 && known) {
      await vault.touch(storedUrl, { shell });
      summary.notModified += 1;
      // صفحهٔ هاب بدون تغییر: پیوندهایش از دور قبلی (form-bridge آن‌ها را نگه می‌دارد) — HTML لازم نیست.
      return null;
    }
    if (!response.ok || !(response.headers.get('Content-Type') || '').includes('text/html')) {
      summary.failed += 1;
      return null;
    }
    const html = await response.text();
    if (!/<main[\s>]/i.test(html)) {
      summary.failed += 1;
      return null;
    }
    const etag = response.headers.get('ETag');
    if (shell) {
      await vault.putShell(html, etag);
    } else if (task.list && listInfo(html)) {
      await vault.put(variant, html, { prio: task.prio === Prio.Page ? Prio.Page : Prio.List, etag });
      summary.lists += 1;
    } else {
      await vault.put(task.url, html, { full: task.mode === 'modal', prio: task.prio, etag });
    }
    summary.fetched += 1;
    summary.bytes += html.length;
    this.onPage(task.url, html, task);
    return { html };
  }
}
