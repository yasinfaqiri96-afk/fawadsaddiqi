// مشعل آفلاین — ساختن همان صفحهٔ مشعل برای آدرسی که دقیقاً ذخیره نشده، از نسخهٔ هم‌مسیرِ ذخیره‌شده (بدون سرور).
//
// - فهرست: سطرهای ذخیره‌شدهٔ همان فهرست (آماده‌سازی تا ۲۰۰ سطر اخیر با همان ترتیب سرور) با همان قواعد سرور
//   صفحه‌بندی می‌شوند (page، pageSize)؛ جستجوی متنی (q) روی ستون‌های نمایش‌داده‌شده و فیلترهای انتخابی فقط وقتی
//   ستون هم‌نام در جدول هست، با برابری دقیق متن. هر فیلتری که بدون سرور قابل اجرا نیست صریح اعلام می‌شود.
// - فورم: همان فورم ثبت از الگوی ذخیره‌شده؛ پارامترهای آدرس (مثلاً contractId) در فیلد هم‌نام گذاشته می‌شوند.
// هیچ رقم یا سطری ساخته نمی‌شود؛ فقط سطرهای واقعیِ ذخیره‌شده نشان داده یا پنهان می‌شوند.

import { LIST_PARAMS, UI_PARAMS } from './page-vault.js';
import { formatDateTime, formatNumber } from './util.js';

const DEFAULT_PAGE_SIZE = 10; // Helpers/ListPageSize.Default
const MAX_PAGE_SIZE = 500;
const SEARCH_PARAMS = ['q', 'search', 'term', 'searchterm'];

const h = (value) => String(value ?? '').replace(/[&<>"']/g,
  (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));

/** متن قابل مقایسه: حروف عربی/فارسی یکسان، ارقام لاتین، بدون نیم‌فاصله و اعراب. */
export function normalizeText(value) {
  return String(value ?? '')
    .replace(/[ي]/g, 'ی').replace(/[ك]/g, 'ک').replace(/[ۀة]/g, 'ه').replace(/[أإآ]/g, 'ا')
    .replace(/[۰-۹]/g, (d) => String(d.charCodeAt(0) - 0x06F0))
    .replace(/[٠-٩]/g, (d) => String(d.charCodeAt(0) - 0x0660))
    .replace(/[ً-ٰٟ‌‍‎‏]/g, '')
    .replace(/\s+/g, ' ')
    .trim()
    .toLowerCase();
}

function notice(doc, tone, lines) {
  const box = doc.createElement('div');
  box.className = `alert ak-form-alert alert-${tone}`;
  box.setAttribute('role', 'status');
  box.setAttribute('data-offline-derived', '');
  box.innerHTML = lines.map((line) => `<div class="mb-1">${line}</div>`).join('');
  return box;
}

function placeNotice(doc, box) {
  const main = doc.querySelector('main');
  if (!main) return;
  const anchor = main.querySelector('.ak-filter-host, .ak-table-wrap, table');
  if (anchor?.parentNode) anchor.parentNode.insertBefore(box, anchor);
  else main.prepend(box);
}

function serialize(doc) {
  return '<!DOCTYPE html>\n' + doc.documentElement.outerHTML;
}

function dataRows(tbody) {
  return [...tbody.rows].filter((row) => !(row.cells.length === 1 && row.cells[0].colSpan > 1));
}

function columnIndex(table, label) {
  const want = normalizeText(label);
  if (!want) return -1;
  const head = table.tHead?.rows[table.tHead.rows.length - 1];
  if (!head) return -1;
  return [...head.cells].findIndex((cell) => normalizeText(cell.textContent) === want);
}

/** فیلترهای انتخابیِ همان نوار جستجو (data-ak-filter-config). */
function filterConfig(main) {
  try {
    return JSON.parse(main.querySelector('[data-ak-filter-config]')?.textContent || '[]') || [];
  } catch {
    return [];
  }
}

function pagerHtml(url, page, pageCount, pageSize) {
  const base = new URL(url);
  const keep = [...base.searchParams].filter(([k]) => !['page', 'pagesize'].includes(k.toLowerCase()));
  const link = (p, size = pageSize) => '?' + new URLSearchParams([...keep, ['page', String(p)], ['pageSize', String(size)]]).toString();
  const sizes = [...new Set([10, 20, 50, 100, 200, 500, DEFAULT_PAGE_SIZE, pageSize])].filter((v) => v > 0 && v <= MAX_PAGE_SIZE).sort((a, b) => a - b);
  let html = '<div class="ak-pager-bar" data-server-pagination="true" data-offline-pager="true"><div class="ak-page-size">'
    + '<span class="ak-page-size-label">تعداد سطر:</span>'
    + '<select class="form-select form-select-sm ak-page-size-select" aria-label="تعداد سطر در هر صفحه" onchange="if(this.value) window.location.href = this.value;">'
    + sizes.map((s) => `<option value="${h(link(1, s))}"${s === pageSize ? ' selected' : ''}>${s}</option>`).join('')
    + '</select></div>';
  if (pageCount > 1) {
    let start = Math.max(1, page - 2);
    const end = Math.min(pageCount, start + 4);
    if (end - start < 4) start = Math.max(1, end - 4);
    const item = (p, label, cls = '') => `<li class="page-item ${cls}"><a class="page-link" href="${h(link(p))}">${label}</a></li>`;
    const dots = '<li class="page-item disabled"><span class="page-link">…</span></li>';
    html += '<nav aria-label="صفحه‌بندی" class="ak-pager"><ul class="pagination pagination-sm mb-0">'
      + item(Math.max(1, page - 1), '«', page === 1 ? 'disabled' : '')
      + (start > 1 ? item(1, '۱') + (start > 2 ? dots : '') : '');
    for (let i = start; i <= end; i += 1) html += item(i, String(i), i === page ? 'active' : '');
    html += (end < pageCount ? (end < pageCount - 1 ? dots : '') + item(pageCount, String(pageCount)) : '')
      + item(Math.min(pageCount, page + 1), '»', page === pageCount ? 'disabled' : '')
      + '</ul></nav>';
  }
  return html + '</div>';
}

/**
 * فهرست از سطرهای ذخیره‌شده با پارامترهای آدرس.
 * @param {string} html نسخهٔ ذخیره‌شدهٔ فهرست (بدون فیلتر، صفحهٔ ۱)
 * @param {string} rawUrl آدرس درخواستی
 * @param {{ complete: boolean, savedAt: string }} info
 * @returns {{ html: string, shown: number, matched: number, unapplied: string[] } | null}
 */
export function deriveListPage(html, rawUrl, info) {
  const doc = new DOMParser().parseFromString(html, 'text/html');
  const main = doc.querySelector('main');
  const table = main && ([...main.querySelectorAll('table.ak-table, table')].find((t) => t.tBodies[0]));
  if (!table) return null;
  const tbody = table.tBodies[0];
  const url = new URL(rawUrl, globalThis.location?.href || 'http://localhost/');
  const params = [...url.searchParams].map(([k, v]) => [k, k.toLowerCase(), v]);
  const all = dataRows(tbody);
  const text = new Map(all.map((row) => [row, normalizeText(row.textContent)]));
  let rows = all;
  const applied = [];
  const unapplied = [];

  const query = params.find(([, k, v]) => SEARCH_PARAMS.includes(k) && v.trim());
  if (query) {
    const tokens = normalizeText(query[2]).split(' ').filter(Boolean);
    rows = rows.filter((row) => tokens.every((t) => text.get(row).includes(t)));
    applied.push(`جستجوی «${h(query[2])}»`);
    main.querySelectorAll(`input[name="${query[0]}" i], [data-ak-filter-input]`).forEach((input) => { input.value = query[2]; });
  }

  const config = filterConfig(main);
  const grouped = new Map();
  for (const [name, lower, value] of params) {
    if (LIST_PARAMS.includes(lower) || UI_PARAMS.includes(lower) || value === '') continue;
    if (!grouped.has(name)) grouped.set(name, []);
    grouped.get(name).push(value);
  }
  for (const [name, values] of grouped) {
    const field = config.find((f) => String(f.key || '').toLowerCase() === name.toLowerCase());
    const column = field && field.type !== 'date' && field.type !== 'daterange' ? columnIndex(table, field.label) : -1;
    const labels = field ? values.map((v) => (field.options || []).find((o) => String(o.value) === v)?.label).filter(Boolean) : [];
    if (column < 0 || labels.length !== values.length) {
      unapplied.push(field?.label || name);
      continue;
    }
    const wanted = new Set(labels.map(normalizeText));
    rows = rows.filter((row) => wanted.has(normalizeText(row.cells[column]?.textContent)));
    applied.push(`${h(field.label)}: ${labels.map(h).join('، ')}`);
  }

  const sizeParam = Number(params.find(([, k]) => k === 'pagesize')?.[2]);
  const pageSize = Number.isFinite(sizeParam) && sizeParam > 0 ? Math.min(Math.floor(sizeParam), MAX_PAGE_SIZE) : DEFAULT_PAGE_SIZE;
  const pageCount = Math.max(1, Math.ceil(rows.length / pageSize));
  const requested = Math.max(1, Math.floor(Number(params.find(([, k]) => k === 'page')?.[2]) || 1));
  const page = Math.min(requested, pageCount);
  const visible = new Set(rows.slice((page - 1) * pageSize, page * pageSize));
  for (const row of [...tbody.rows]) if (!visible.has(row)) row.remove();
  if (visible.size === 0) {
    const cols = Math.max(1, table.tHead?.rows[0]?.cells.length || all[0]?.cells.length || 1);
    tbody.insertAdjacentHTML('beforeend', `<tr><td colspan="${cols}" class="text-center text-muted">در سطرهای ذخیره‌شدهٔ این دستگاه موردی پیدا نشد.</td></tr>`);
  }

  const count = main.querySelector('.ak-list-result-count');
  if (count) count.textContent = `نمایش ${formatNumber(visible.size)} نتیجه`;
  const pager = main.querySelector('.ak-pager-bar[data-server-pagination]');
  const fresh = doc.createElement('div');
  fresh.innerHTML = pagerHtml(url.href, page, pageCount, pageSize);
  if (pager) pager.replaceWith(fresh.firstElementChild);
  else table.closest('.ak-table-wrap, table')?.after(fresh.firstElementChild);

  const lines = [`فهرست از <strong>${formatNumber(all.length)} سطر</strong> ذخیره‌شده در این دستگاه (${h(formatDateTime(info.savedAt))})`
    + (info.complete ? ' — همهٔ سطرهای این فهرست.' : ' — سطرهای قدیمی‌تر هنوز روی دستگاه نیست؛ فهرست کامل پس از اتصال.')];
  if (applied.length) lines.push(`اعمال‌شده روی همین سطرها: ${applied.join(' · ')} — ${formatNumber(rows.length)} مورد.`);
  if (query) lines.push('جستجو فقط در ستون‌های نمایش‌داده‌شده انجام شد؛ جستجوی کامل سرور پس از اتصال.');
  if (unapplied.length) {
    lines.push(`<strong>فیلتر ${unapplied.map((n) => `«${h(n)}»`).join('، ')} بدون سرور قابل اجرا نیست؛ سطرهای زیر بر اساس آن فیلتر نشده‌اند.</strong>`);
  }
  if (requested > page) lines.push(`صفحهٔ ${formatNumber(requested)} در سطرهای ذخیره‌شده نیست؛ صفحهٔ ${formatNumber(page)} نمایش داده شد.`);
  placeNotice(doc, notice(doc, unapplied.length || !info.complete ? 'warning' : 'info', lines));
  return { html: serialize(doc), shown: visible.size, matched: rows.length, unapplied };
}

/**
 * فورم ثبت از الگوی ذخیره‌شدهٔ همان فورم؛ پارامترهای آدرس در فیلدهای هم‌نام (بی‌توجه به حروف بزرگ/کوچک).
 * @returns {{ html: string, applied: string[], missing: string[] }}
 */
export function deriveFormPage(html, rawUrl, info) {
  const doc = new DOMParser().parseFromString(html, 'text/html');
  const url = new URL(rawUrl, globalThis.location?.href || 'http://localhost/');
  const forms = [...doc.querySelectorAll('main form[method="post" i]')];
  const applied = [];
  const missing = [];
  for (const [name, value] of url.searchParams) {
    const lower = name.toLowerCase();
    if (['modal', 'quickcreate', 'offlineedit', '_', 'source'].includes(lower)) continue;
    const fields = forms.flatMap((form) => [...form.elements])
      .filter((el) => el.name && (el.name.toLowerCase() === lower || el.name.toLowerCase().endsWith('.' + lower)));
    if (!fields.length) continue; // پارامتر نمایشی (مثلاً tab)
    let ok = false;
    for (const field of fields) {
      if (field.tagName === 'SELECT') {
        const option = [...field.options].find((o) => o.value === value);
        if (option) {
          [...field.options].forEach((o) => { o.selected = o === option; if (o === option) o.setAttribute('selected', ''); else o.removeAttribute('selected'); });
          ok = true;
        }
      } else if (field.type === 'checkbox' || field.type === 'radio') {
        if (field.value === value || (value === 'true' && field.type === 'checkbox')) {
          field.checked = true;
          field.setAttribute('checked', '');
          ok = true;
        }
      } else if (field.type !== 'file') {
        field.value = value;
        field.setAttribute('value', value);
        ok = true;
      }
    }
    (ok ? applied : missing).push(name);
  }
  const lines = [`فورم از الگوی ذخیره‌شدهٔ همین فورم در این دستگاه باز شد (${h(formatDateTime(info.savedAt))}).`];
  if (missing.length) {
    lines.push(`<strong>مقدار ${missing.map((n) => `«${h(n)}»`).join('، ')} در فهرست‌های ذخیره‌شدهٔ این فورم نیست؛ آن را خودتان انتخاب کنید.</strong>`);
  }
  const box = notice(doc, missing.length ? 'warning' : 'info', lines);
  const firstForm = forms[0];
  if (firstForm) firstForm.parentNode.insertBefore(box, firstForm);
  else placeNotice(doc, box);
  return { html: serialize(doc), applied, missing };
}

/** پیام «جزئیات/صفحه هنوز روی دستگاه دریافت نشده» داخل همان پوسته (بدون ساختن اطلاعات). */
export function missingPageHtml(rawUrl, pages = []) {
  const path = new URL(rawUrl, globalThis.location?.href || 'http://localhost/').pathname;
  const isRecord = /\/\d+(\/|$)/.test(path) || /\/(details|edit|view|print)(\/|$)/i.test(path);
  const items = pages.slice(0, 150).map((p) => `<li><a href="${h(p.url)}">${h(p.title || p.url)}</a>
    <small class="text-muted">— ${h(formatDateTime(p.savedAt))}</small></li>`).join('');
  return `<div class="alert ak-form-alert alert-warning" role="status" data-offline-missing>
      <div class="mb-1"><strong>${isRecord ? 'معلومات این رکورد هنوز روی این دستگاه دریافت نشده است.' : 'این صفحه هنوز روی این دستگاه آماده نشده است.'}</strong></div>
      <div class="mb-1">سرور در دسترس نیست. داشبورد، منو، فهرست‌ها و فورم‌های مجاز شما بدون سرور کار می‌کنند؛ ${isRecord
        ? 'جزئیات این رکورد پس از اتصال نمایش داده می‌شود (هیچ عددی حدس زده نمی‌شود).'
        : 'این صفحه هنگام اتصال آماده می‌شود.'}</div>
      <div class="mb-1"><a href="/">داشبورد</a> · <a href="/offline/index.html#/ops">عملیات آفلاین</a> · <a href="${h(rawUrl)}">تلاش دوباره با سرور</a></div>
    </div>
    ${items ? `<div class="alert ak-form-alert alert-info"><div class="mb-1">صفحات آماده برای کار بدون سرور:</div><ul class="mb-0">${items}</ul></div>` : ''}`;
}

/**
 * نسخهٔ قابل نمایش برای آدرس: همان نسخه، یا فهرست/فورم ساخته‌شده از نسخهٔ هم‌مسیر.
 * @param {import('./page-vault.js').PageVault} vault
 * @returns {Promise<{ html: string, savedAt: string, full: boolean, match: string } | null>}
 */
export async function resolvePage(vault, rawUrl, isForm) {
  const found = await vault.resolve(rawUrl, { isForm });
  if (!found) return null;
  const { page, match, entry } = found;
  if (match === 'list') {
    const derived = deriveListPage(page.html, rawUrl, { complete: !!entry.list?.complete, savedAt: page.savedAt });
    if (derived) return { html: derived.html, savedAt: page.savedAt, full: !!page.full, match };
    return null;
  }
  if (match === 'template') {
    return { html: deriveFormPage(page.html, rawUrl, { savedAt: page.savedAt }).html, savedAt: page.savedAt, full: !!page.full, match };
  }
  return { html: page.html, savedAt: page.savedAt, full: !!page.full, match };
}
