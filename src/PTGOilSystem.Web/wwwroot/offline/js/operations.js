// مشعل آفلاین — صف پایدار عملیات (Outbox) و فرم‌های عملیات مجاز.
//
// هر عملیات یک ClientOperationId یکتا دارد که در همهٔ تلاش‌ها، پس از Restart، پس از بازیابی بکاپ و پس از
// اصلاح ثابت می‌ماند؛ سرور با همین شناسه از ثبت تکراری جلوگیری می‌کند. محتوای عملیاتِ ارسال‌شده بی‌صدا
// عوض نمی‌شود: اصلاحِ عملیاتِ دارای تعارض با Revision تازه و هش تازه فرستاده می‌شود.
// سرور هر عملیات را با همان فرم ثبتِ موجودِ ماژول ثبت قطعی می‌کند؛ «همگام‌شد» یعنی سند واقعی ساخته شد.

import { sha256Hex, todayIso, uuid } from './util.js';

export const Status = Object.freeze({
  Draft: 'Draft',
  Pending: 'Pending',
  Sending: 'Sending',
  Received: 'Received',
  Applied: 'Applied',
  Conflict: 'Conflict',
  Rejected: 'Rejected'
});

export const STATUS_LABELS = Object.freeze({
  Draft: 'پیش‌نویس',
  Pending: 'در دستگاه ذخیره شد؛ در انتظار همگام‌سازی',
  Sending: 'در حال ارسال',
  Received: 'به سرور رسید؛ در حال ثبت قطعی',
  Applied: 'همگام‌شد؛ در سیستم ثبت قطعی شد',
  Conflict: 'ثبت نشد؛ نیازمند اصلاح',
  Rejected: 'رد شد'
});

export const STATUS_SHORT = Object.freeze({
  Draft: 'پیش‌نویس',
  Pending: 'در انتظار ارسال',
  Sending: 'در حال ارسال',
  Received: 'در حال ثبت',
  Applied: 'همگام‌شده',
  Conflict: 'نیازمند اصلاح',
  Rejected: 'ردشده'
});

/** هنوز به سرور نرسیده (یا پاسخ آن معلوم نیست). */
export const UNSYNCED = Object.freeze([Status.Pending, Status.Sending]);
/** سرور دارد ولی هنوز بسته نشده؛ وضعیتش از سرور پرسیده می‌شود. */
export const SERVER_OPEN = Object.freeze([Status.Received, Status.Conflict]);
/** رتبه برای ادغام بکاپ: وضعیت پیشرفته‌تر برنده است. */
export const STATUS_RANK = Object.freeze({ Draft: 0, Pending: 1, Sending: 1, Received: 2, Conflict: 3, Applied: 4, Rejected: 4 });

export const SCHEMA_VERSION = 1;
/** فورم اصلی MVC (همان فیلدهای فورم؛ OfflineFormRegistry سرور). در برنامهٔ آفلاین فرم جدا ندارد. */
export const FORM_SUBMIT = 'Form.Submit';
/** مقدار مرجعی که به رکوردِ ساخته‌شده توسط عملیات پیش‌نیاز اشاره می‌کند (مثلاً مشتری تازهٔ آفلاین). */
export const OPERATION_REFERENCE_PREFIX = 'op:';

const ref = (name, label, source, required = true, extra = {}) => ({ name, label, kind: 'ref', source, required, ...extra });
const num = (name, label, required = true, extra = {}) => ({ name, label, kind: 'number', required, ...extra });
const text = (name, label, required = false) => ({ name, label, kind: 'text', required });
/** بلی/خیر؛ همیشه true/false ذخیره می‌شود (نبودن = پیش‌فرض سرور). */
const flag = (name, label) => ({ name, label, kind: 'flag', required: false });
const currency = (required = true) => ({ name: 'currency', label: 'اسعار', kind: 'currency', source: 'currencies', required });
const fxRate = () => num('fxRate', 'نرخ تبدیل به دالر (برای اسعار غیر دالر)', false);

/**
 * فرم‌ها با OfflineOperationTypes و OfflineOperationApplier سرور هم‌خوان‌اند. فیلدهای لازمِ سرور اینجا هم
 * لازم‌اند؛ بقیهٔ قواعد (موجودی، نرخ، قرارداد، دورهٔ مالی، صلاحیت) را فرم ثبتِ موجود در سرور می‌سنجد.
 */
export const OPERATION_FORMS = Object.freeze({
  'Sale.Request': {
    label: 'فروش از مخزن',
    hint: 'پس از اتصال، با همان فرم فروش مشعل ثبت قطعی می‌شود (کنترل موجودی، فاکتور و حسابداری). نرخ خالی = «قیمت بعداً تعیین می‌شود».',
    fields: [ref('customerId', 'مشتری', 'customers', true, { allowPending: 'Customer.Draft' }), ref('productId', 'جنس', 'products'),
      ref('tankId', 'مخزن', 'tanks'), num('quantity', 'مقدار (تن)'), num('unitPrice', 'نرخ واحد', false),
      currency(), fxRate(), ref('companyId', 'شرکت فروشنده', 'companies', false), text('reference', 'شماره تکت/مرجع'), text('description', 'شرح')]
  },
  'CustomerReceipt.Request': {
    label: 'دریافت از مشتری',
    hint: 'پس از اتصال، در روزنامچه (نقد/بانک) ثبت قطعی می‌شود و مانده مشتری همان‌جا تغییر می‌کند.',
    fields: [ref('customerId', 'مشتری', 'customers', true, { allowPending: 'Customer.Draft' }), ref('cashAccountId', 'حساب دریافت', 'cashAccounts'),
      num('amount', 'مبلغ'), currency(), fxRate(), text('reference', 'شماره رسید'), text('description', 'شرح')]
  },
  'SupplierPayment.Request': {
    label: 'پرداخت به فروشنده',
    hint: 'پس از اتصال، در روزنامچه (نقد/بانک) ثبت قطعی می‌شود.',
    fields: [ref('supplierId', 'فروشنده', 'suppliers'), ref('cashAccountId', 'حساب پرداخت', 'cashAccounts'),
      num('amount', 'مبلغ'), currency(), fxRate(), text('reference', 'شماره مرجع'), text('description', 'شرح')]
  },
  'Expense.Request': {
    label: 'مصرف',
    hint: 'پس از اتصال، با فرم مصارف مشعل ثبت قطعی می‌شود.',
    fields: [ref('expenseTypeId', 'نوع مصرف', 'expenseTypes'), num('amount', 'مبلغ'), currency(), fxRate(),
      ref('cashAccountId', 'حساب پرداخت', 'cashAccounts', false), text('description', 'شرح', true)]
  },
  'Loading.Note': {
    label: 'بارگیری',
    hint: 'پس از اتصال، با فرم بارگیری مشعل (یک موتر) روی قرارداد خرید ثبت قطعی می‌شود.',
    fields: [ref('contractId', 'قرارداد خرید', 'purchaseContracts'), ref('productId', 'جنس', 'products'),
      ref('truckId', 'موتر', 'trucks', false), num('quantity', 'مقدار بارگیری (تن)'), text('reference', 'شماره بارنامه'),
      text('driverName', 'نام راننده'), text('description', 'شرح')]
  },
  'Receipt.Note': {
    label: 'رسید و تخلیه به مخزن',
    hint: 'پس از اتصال، با فرم رسید مشعل ثبت قطعی می‌شود و موجودی مخزن همان‌جا افزایش می‌یابد.',
    fields: [ref('loadingId', 'بار', 'loadings'), ref('tankId', 'مخزن مقصد', 'tanks'), num('quantity', 'مقدار رسید (تن)'),
      text('reference', 'شماره سند'), text('description', 'شرح')]
  },
  'Customer.Draft': {
    label: 'مشتری جدید',
    hint: 'پس از اتصال در فهرست مشتریان ساخته می‌شود؛ فروش و دریافتِ وابسته به آن پس از ساخت ثبت می‌شوند.',
    // همان فیلدهای فورم اصلی Customers/Create (کد، نام، کشور، تلفن، فعال) + یادداشت فرم آفلاین.
    fields: [text('code', 'کد'), text('name', 'نام', true), text('country', 'کشور'), text('phone', 'تلفن'),
      flag('isActive', 'فعال'), text('description', 'یادداشت')]
  },
  'General.Note': {
    label: 'یادداشت عملیاتی',
    hint: 'برای ثبت رویدادی که سند مالی ندارد.',
    fields: [text('text', 'متن', true)]
  },
  'Form.Submit': {
    label: 'فورم اصلی',
    hint: 'در همان صفحهٔ مشعل ثبت شد؛ پس از اتصال با همان فورم سرور ثبت قطعی می‌شود.',
    fields: [],
    hidden: true
  }
});

/** نوع‌های قدیمی که دیگر فرم ندارند (برای نمایش عملیات موجود در دستگاه). */
export const LEGACY_LABELS = Object.freeze({ 'MoneyTransfer.Request': 'انتقال پول (پشتیبانی نمی‌شود)' });

export function typeLabel(type, payload = null) {
  if (type === FORM_SUBMIT && payload?.label) return payload.label;
  return OPERATION_FORMS[type]?.label || LEGACY_LABELS[type] || type;
}

const pairList = (list) => (Array.isArray(list) ? list : [])
  .filter((pair) => Array.isArray(pair) && pair.length === 2 && typeof pair[0] === 'string' && pair[0])
  .map(([name, value]) => [name, value === null || value === undefined ? null : String(value)]);

/** فورم اصلی: مسیر کوچک‌حرف، جفت‌های [نام، مقدار] با همان ترتیب فورم؛ کلیدها ثابت تا هش پایدار باشد. */
function normalizeFormSubmit(payload) {
  const result = {
    path: String(payload.path || '').toLowerCase().split(/[?#]/)[0].replace(/\/+$/, ''),
    label: String(payload.label || '').trim().slice(0, 120),
    fields: pairList(payload.fields)
  };
  const query = pairList(payload.query);
  if (query.length) result.query = query;
  if (payload.formToken) result.formToken = String(payload.formToken);
  return result;
}

export function isOperationReference(value) {
  return typeof value === 'string' && value.startsWith(OPERATION_REFERENCE_PREFIX)
    && /^[0-9a-f-]{36}$/i.test(value.slice(OPERATION_REFERENCE_PREFIX.length));
}

/** بررسی فیلدهای لازم؛ همان قواعد ساختاری سرور. پیام خطا یا null. */
export function validatePayload(type, payload) {
  const form = OPERATION_FORMS[type];
  if (!form) {
    return 'نوع عملیات پشتیبانی نمی‌شود.';
  }
  if (type === FORM_SUBMIT) {
    return payload?.path && Array.isArray(payload.fields) && payload.fields.length ? null : 'فیلدهای فورم خالی است.';
  }
  for (const field of form.fields) {
    const value = payload[field.name];
    const empty = value === undefined || value === null || String(value).trim() === '';
    if (empty) {
      if (field.required) {
        return `«${field.label}» لازم است.`;
      }
      continue;
    }
    if (field.kind === 'number' && !(Number(value) > 0)) {
      return `«${field.label}» باید عدد مثبت باشد.`;
    }
    if (field.kind === 'ref' && !isOperationReference(value) && !(Number.isInteger(Number(value)) && Number(value) > 0)) {
      return `«${field.label}» را انتخاب کنید.`;
    }
  }
  return null;
}

/** فقط فیلدهای فرم، با نوع درست؛ ترتیب کلیدها ثابت است تا متن JSON و هش پایدار باشد. */
export function normalizePayload(type, payload) {
  if (type === FORM_SUBMIT) {
    return normalizeFormSubmit(payload || {});
  }
  const form = OPERATION_FORMS[type];
  const result = {};
  for (const field of form.fields) {
    const value = payload[field.name];
    if (value === undefined || value === null || String(value).trim() === '') {
      continue;
    }
    if (field.kind === 'ref' && isOperationReference(String(value))) {
      result[field.name] = String(value);
    } else if (field.kind === 'flag') {
      result[field.name] = value === true || String(value).trim().toLowerCase() === 'true';
    } else {
      result[field.name] = field.kind === 'number' || field.kind === 'ref' ? Number(value) : String(value).trim();
    }
  }
  return result;
}

/** شناسهٔ عملیات‌هایی که payload به آن‌ها اشاره می‌کند (op:...) — خودکار پیش‌نیاز می‌شوند. */
export function referencedOperations(payload) {
  const values = Array.isArray(payload.fields) ? payload.fields.map((pair) => pair[1]) : Object.values(payload);
  return values
    .filter((value) => isOperationReference(value))
    .map((value) => value.slice(OPERATION_REFERENCE_PREFIX.length));
}

function toRow(record, sealed) {
  return {
    clientOperationId: record.clientOperationId,
    status: record.status,
    operationType: record.operationType,
    createdAt: record.createdAt,
    sealed
  };
}

export class OutboxRepo {
  /**
   * @param {import('./store.js').IdbStore} store
   * @param {import('./vault.js').Sealer} sealer
   * @param {{ userId:number, companyId:number, deviceId:string }} profile
   */
  constructor(store, sealer, profile, clock = () => new Date()) {
    this.store = store;
    this.sealer = sealer;
    this.profile = profile;
    this.clock = clock;
  }

  async rowFor(record) {
    return toRow(record, await this.sealer.seal(record));
  }

  async list() {
    const rows = await this.store.getAll('outbox');
    const records = await Promise.all(rows.map((row) => this.sealer.open(row.sealed)));
    return records.sort((a, b) => (a.createdAt < b.createdAt ? 1 : -1));
  }

  async get(id) {
    const row = await this.store.get('outbox', id);
    return row ? this.sealer.open(row.sealed) : null;
  }

  /** فقط وضعیت‌ها، بدون بازکردن رمز (برای شمارش سریع). */
  async statusCounts() {
    const rows = await this.store.getAll('outbox');
    const counts = Object.fromEntries(Object.values(Status).map((s) => [s, 0]));
    for (const row of rows) {
      counts[row.status] = (counts[row.status] || 0) + 1;
    }
    return counts;
  }

  baseRecord(type, payload, businessDate, now) {
    return {
      clientOperationId: uuid(),
      deviceId: this.profile.deviceId,
      userId: this.profile.userId,
      companyId: this.profile.companyId,
      operationType: type,
      entityId: null,
      baseVersion: null,
      payload,
      payloadJson: null,
      payloadHash: null,
      createdAt: now.toISOString(),
      updatedAt: now.toISOString(),
      businessDate: businessDate || todayIso(now),
      schemaVersion: SCHEMA_VERSION,
      revision: 1,
      dependencies: [],
      status: Status.Draft,
      attempts: 0,
      nextAttemptAt: null,
      lastError: null,
      serverNote: null,
      appliedRef: null,
      history: []
    };
  }

  withHistory(record, status, note = null) {
    const now = this.clock().toISOString();
    return {
      ...record,
      status,
      updatedAt: now,
      history: [...(record.history || []), { at: now, status, note }].slice(-50)
    };
  }

  async saveDraft(type, rawPayload, businessDate, existingId = null) {
    if (!OPERATION_FORMS[type]) {
      throw new Error('نوع عملیات پشتیبانی نمی‌شود.');
    }
    const now = this.clock();
    let record;
    if (existingId) {
      record = await this.get(existingId);
      if (!record || record.status !== Status.Draft) {
        throw new Error('فقط پیش‌نویس قابل ویرایش است.');
      }
      record = { ...record, payload: normalizePayload(type, rawPayload), businessDate: businessDate || record.businessDate, updatedAt: now.toISOString() };
    } else {
      record = this.withHistory(this.baseRecord(type, normalizePayload(type, rawPayload), businessDate, now), Status.Draft);
    }
    await this.store.put('outbox', await this.rowFor(record));
    return record;
  }

  /**
   * ثبت عملیات در دستگاه: معلومات عملیات و دستور ارسال (وضعیت Pending) در یک تراکنش ذخیره می‌شوند.
   * @param {object[]} extraWrites نوشتن‌های دیگری که باید در همان تراکنش باشند (مثلاً meta).
   */
  async submit(type, rawPayload, businessDate, { dependencies = [], fromDraftId = null, extraWrites = [] } = {}) {
    const payload = normalizePayload(type, rawPayload);
    const error = validatePayload(type, payload);
    if (error) {
      throw new Error(error);
    }
    const now = this.clock();
    let record;
    if (fromDraftId) {
      const draft = await this.get(fromDraftId);
      if (!draft || draft.status !== Status.Draft) {
        throw new Error('پیش‌نویس پیدا نشد یا قبلاً ثبت شده است.');
      }
      record = { ...draft, payload, businessDate: businessDate || draft.businessDate };
    } else {
      record = this.baseRecord(type, payload, businessDate, now);
    }
    record.dependencies = [...new Set([...dependencies, ...referencedOperations(payload)])]
      .filter((d) => d && d !== record.clientOperationId);
    record.payloadJson = JSON.stringify(record.payload);
    record.payloadHash = await sha256Hex(record.payloadJson);
    record.nextAttemptAt = now.toISOString();
    record = this.withHistory(record, Status.Pending);
    await this.store.atomic([{ store: 'outbox', type: 'put', value: await this.rowFor(record) }, ...extraWrites]);
    return record;
  }

  /**
   * اصلاح و ارسال دوبارهٔ همان عملیات (همان شناسه). برای «نیازمند اصلاح» (تعارض سرور) نسخه یک واحد بالا می‌رود؛
   * سرور فقط نسخهٔ بزرگ‌تر از نسخهٔ ذخیره‌شده را می‌پذیرد. ردشده‌ای که هرگز در سرور ذخیره نشد (خطای اعتبارسنجی)
   * هم با همان شناسه دوباره فرستاده می‌شود.
   */
  async resubmit(id, rawPayload, businessDate) {
    const record = await this.get(id);
    if (!record || !canResubmit(record)) {
      throw new Error('این عملیات قابل اصلاح نیست.');
    }
    const payload = normalizePayload(record.operationType, rawPayload ?? record.payload);
    const error = validatePayload(record.operationType, payload);
    if (error) {
      throw new Error(error);
    }
    const now = this.clock();
    const payloadJson = JSON.stringify(payload);
    const next = this.withHistory({
      ...record,
      payload,
      payloadJson,
      payloadHash: await sha256Hex(payloadJson),
      businessDate: businessDate || record.businessDate,
      revision: record.status === Status.Conflict ? (record.revision || 1) + 1 : (record.revision || 1),
      dependencies: [...new Set([...(record.dependencies || []), ...referencedOperations(payload)])],
      serverNote: null,
      lastError: null,
      nextAttemptAt: now.toISOString()
    }, Status.Pending, 'اصلاح و ارسال دوباره');
    await this.store.put('outbox', await this.rowFor(next));
    return next;
  }

  /** بازگشت به پیش‌نویس فقط وقتی عملیات هرگز از دستگاه خارج نشده است. */
  async returnToDraft(id) {
    const record = await this.get(id);
    if (!record || record.status !== Status.Pending || record.attempts > 0) {
      throw new Error('این عملیات قبلاً برای ارسال تلاش شده و دیگر قابل ویرایش نیست.');
    }
    const draft = this.withHistory({ ...record, payloadJson: null, payloadHash: null, nextAttemptAt: null }, Status.Draft, 'بازگشت به پیش‌نویس');
    await this.store.put('outbox', await this.rowFor(draft));
    return draft;
  }

  async discardDraft(id) {
    const record = await this.get(id);
    if (!record || record.status !== Status.Draft) {
      throw new Error('فقط پیش‌نویس قابل حذف است.');
    }
    await this.store.delete('outbox', id);
  }

  async putMany(records, extraWrites = []) {
    if (records.length === 0 && extraWrites.length === 0) {
      return;
    }
    const writes = [];
    for (const record of records) {
      writes.push({ store: 'outbox', type: 'put', value: await this.rowFor(record) });
    }
    await this.store.atomic([...writes, ...extraWrites]);
  }
}

/** «نیازمند اصلاح»، یا ردشده‌ای که سرور هرگز ذخیره نکرد (هنوز قابل ارسال با همان شناسه است). */
export function canResubmit(record) {
  return !!OPERATION_FORMS[record.operationType]
    && (record.status === Status.Conflict || (record.status === Status.Rejected && !record.serverAckAt));
}
