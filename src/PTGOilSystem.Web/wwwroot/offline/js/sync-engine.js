// مشعل آفلاین — موتور همگام‌سازی.
//
// تضمین‌ها:
//  - عملیات فقط پس از پاسخ قطعی سرور از Pending خارج می‌شود؛ قطع شبکه وسط ارسال = Pending با همان شناسه.
//  - هیچ عملیاتی حذف نمی‌شود؛ حتی «اعمال‌شده» و «ردشده» برای تاریخچه می‌مانند.
//  - ترتیب پیش‌نیاز رعایت می‌شود؛ پیش‌نیازِ ردشده → «تعارض»، نه ارسال کور.
//  - تکرار با تأخیر افزایشی (حداکثر ۶۰ دقیقه)، بدون محدودیت زمانی برای حذف.
//  - کاربر دیگری که در مرورگر وارد شده باشد، عملیات این پروفایل را نمی‌فرستد.

import { SERVER_OPEN, Status } from './operations.js';

export const BASE_RETRY_MS = 15_000;
export const MAX_RETRY_MS = 60 * 60_000;
export const SNAPSHOT_MAX_AGE_MS = 15 * 60_000;
export const MAX_BATCHES_PER_RUN = 20;

export function retryDelayMs(attempts, random = Math.random) {
  const base = Math.min(MAX_RETRY_MS, BASE_RETRY_MS * 2 ** Math.max(0, attempts - 1));
  return Math.round(base * (1 + 0.2 * random()));
}

export function toDto(record) {
  return {
    clientOperationId: record.clientOperationId,
    userId: record.userId,
    companyId: record.companyId,
    operationType: record.operationType,
    entityId: record.entityId,
    baseVersion: record.baseVersion,
    payloadJson: record.payloadJson,
    payloadHash: record.payloadHash,
    createdAtUtc: record.createdAt,
    businessDate: record.businessDate,
    schemaVersion: record.schemaVersion,
    revision: record.revision || 1,
    dependencies: record.dependencies || []
  };
}

const SERVER_STATUSES = new Set([Status.Received, Status.Applied, Status.Conflict, Status.Rejected]);

/** شمارهٔ سند واقعی که سرور ساخت (مثلاً «SalesTransaction 125»). */
function appliedRefOf(result) {
  return result && (result.appliedReferenceType || result.appliedReferenceId)
    ? `${result.appliedReferenceType || ''} ${result.appliedReferenceId || ''}`.trim()
    : null;
}
const ACKNOWLEDGED = new Set([Status.Received, Status.Applied, Status.Conflict]);

export class SyncEngine {
  constructor({ repo, store, api, monitor, profile, sealer, deviceAuth = null, clock = () => new Date(), random = Math.random,
    batchSize = 10, deviceName = 'مرورگر', onEvent = () => {} }) {
    this.deviceAuth = deviceAuth;
    this.repo = repo;
    this.store = store;
    this.api = api;
    this.monitor = monitor;
    this.profile = profile;
    this.sealer = sealer;
    this.clock = clock;
    this.random = random;
    this.batchSize = batchSize;
    this.deviceName = deviceName;
    this.onEvent = onEvent;
    this.running = null;
  }

  now() {
    return this.clock();
  }

  /** پس از Restart: عملیاتی که وسط ارسال ماند، با همان شناسه دوباره فرستاده می‌شود. */
  async recoverInterrupted() {
    const records = await this.repo.list();
    const stuck = records.filter((r) => r.status === Status.Sending)
      .map((r) => this.repo.withHistory({ ...r, nextAttemptAt: this.now().toISOString() }, Status.Pending,
        'پاسخ سرور نامعلوم ماند؛ با همان شناسه دوباره بررسی می‌شود.'));
    await this.repo.putMany(stuck);
    return stuck.length;
  }

  run(options = {}) {
    if (!this.running) {
      this.running = this.runInternal(options).finally(() => {
        this.running = null;
      });
    }
    return this.running;
  }

  async runInternal({ refreshSnapshot = false } = {}) {
    const summary = { offline: false, authRequired: false, userMismatch: false, stored: 0, duplicate: 0, rejected: 0, applied: 0,
      retry: 0, conflicts: 0, statusUpdates: 0, snapshot: false, error: null };

    const reachable = this.monitor ? await this.monitor.check() : true;
    if (!reachable) {
      summary.offline = true;
      return summary;
    }

    this.monitor && this.monitor.setSyncing(true);
    this.onEvent({ type: 'sync-start' });
    try {
      const session = await this.api.session();
      if (session.status === 401) {
        summary.authRequired = true;
        return await this.finish(summary);
      }
      if (session.networkError) {
        summary.offline = true;
        return await this.finish(summary);
      }
      if (!session.ok || !session.data) {
        summary.error = 'سرور اطلاعات نشست را نداد.';
        return await this.finish(summary);
      }
      if (session.data.userId !== this.profile.userId || session.data.companyId !== this.profile.companyId) {
        // کاربر دیگری در مرورگر وارد است؛ عملیات این پروفایل نباید با نشست او ارسال شود.
        summary.userMismatch = true;
        return await this.finish(summary);
      }
      await this.putMeta('auth', { lastServerAuthAt: this.now().toISOString(), operationTypes: session.data.operationTypes || [] });

      if (!(await this.ensureDevice(summary))) {
        return await this.finish(summary);
      }
      await this.ensureDeviceAuth();

      await this.pushPending(summary);
      if (!summary.authRequired && !summary.offline) {
        await this.pollStatuses(summary);
        await this.refreshSnapshot(summary, refreshSnapshot);
      }
      return await this.finish(summary);
    } catch (error) {
      summary.error = error && error.message ? error.message : String(error);
      return await this.finish(summary);
    } finally {
      this.monitor && this.monitor.setSyncing(false);
    }
  }

  async finish(summary) {
    const ok = !summary.offline && !summary.authRequired && !summary.userMismatch && !summary.error;
    const previous = (await this.store.get('meta', 'sync')) || { key: 'sync' };
    await this.putMeta('sync', {
      ...previous,
      lastAttemptAt: this.now().toISOString(),
      lastSyncAt: ok ? this.now().toISOString() : previous.lastSyncAt || null,
      lastResult: summary
    });
    this.onEvent({ type: 'sync-end', summary });
    return summary;
  }

  async putMeta(key, value) {
    await this.store.put('meta', { ...value, key });
  }

  /** دستگاه مورداعتماد: اگر توکن دستگاه نداریم و با کوکی وارد هستیم، یک نشست دستگاه گرفته می‌شود. */
  async ensureDeviceAuth() {
    if (!this.deviceAuth || !this.api.deviceSession || (await this.deviceAuth.has())) return;
    const result = await this.api.deviceSession(this.profile.deviceId, this.deviceName);
    if (result.ok && result.data?.refreshToken) {
      await this.deviceAuth.save(result.data);
    }
  }

  async ensureDevice(summary) {
    const device = await this.store.get('meta', 'device');
    if (device && device.registeredAt && device.deviceId === this.profile.deviceId) {
      return true;
    }
    const result = await this.api.registerDevice(this.profile.deviceId, this.deviceName);
    if (result.ok) {
      await this.putMeta('device', { deviceId: this.profile.deviceId, registeredAt: this.now().toISOString() });
      return true;
    }
    if (result.status === 401) summary.authRequired = true;
    else if (result.networkError) summary.offline = true;
    else summary.error = 'ثبت دستگاه انجام نشد.';
    return false;
  }

  /** عملیات قابل ارسال به ترتیب ایجاد، با رعایت پیش‌نیاز. */
  selectBatch(records) {
    const nowIso = this.now().toISOString();
    const byId = new Map(records.map((r) => [r.clientOperationId, r]));
    const conflicts = [];
    const batch = [];
    const inBatch = new Set();
    const candidates = records
      .filter((r) => r.status === Status.Pending && (!r.nextAttemptAt || r.nextAttemptAt <= nowIso))
      .sort((a, b) => (a.createdAt < b.createdAt ? -1 : a.createdAt > b.createdAt ? 1 : 0));

    for (const record of candidates) {
      if (batch.length >= this.batchSize) break;
      let ready = true;
      for (const depId of record.dependencies || []) {
        const dep = byId.get(depId);
        if (!dep) continue; // روی این دستگاه نیست؛ سرور تصمیم می‌گیرد.
        if (dep.status === Status.Rejected) {
          conflicts.push(this.repo.withHistory({ ...record, serverNote: 'عملیات پیش‌نیاز رد شده است.' }, Status.Conflict,
            'عملیات پیش‌نیاز رد شده است.'));
          ready = false;
          break;
        }
        if (!ACKNOWLEDGED.has(dep.status) && !inBatch.has(depId)) {
          ready = false;
          break;
        }
      }
      if (ready) {
        batch.push(record);
        inBatch.add(record.clientOperationId);
      }
    }
    return { batch, conflicts };
  }

  async pushPending(summary) {
    for (let round = 0; round < MAX_BATCHES_PER_RUN; round += 1) {
      const records = await this.repo.list();
      const { batch, conflicts } = this.selectBatch(records);
      if (conflicts.length) {
        summary.conflicts += conflicts.length;
        await this.repo.putMany(conflicts);
      }
      if (batch.length === 0) return;

      // ابتدا «در حال ارسال» ذخیره می‌شود؛ اگر برنامه وسط کار بسته شود، recoverInterrupted آن را برمی‌گرداند.
      const sending = batch.map((r) => this.repo.withHistory({ ...r, attempts: (r.attempts || 0) + 1 }, Status.Sending));
      await this.repo.putMany(sending);

      const response = await this.api.push(this.profile.deviceId, sending.map(toDto));
      if (!response.ok || !response.data || !Array.isArray(response.data.results)) {
        const reason = response.networkError ? 'ارتباط با سرور قطع شد؛ با همان شناسه دوباره فرستاده می‌شود.'
          : response.status === 401 ? 'نشست سرور منقضی شده است؛ دوباره وارد شوید.'
            : response.status === 403 ? 'سرور اجازهٔ این ارسال را نداد.'
              : `خطای سرور (${response.status}).`;
        const delay = response.status === 401 ? 0 : null;
        await this.repo.putMany(sending.map((r) => this.backToPending(r, reason, delay)));
        if (response.status === 401) summary.authRequired = true;
        else if (response.networkError) summary.offline = true;
        else summary.error = reason;
        summary.retry += sending.length;
        return;
      }

      const results = new Map(response.data.results.map((r) => [r.clientOperationId, r]));
      const updated = [];
      for (const record of sending) {
        const result = results.get(record.clientOperationId);
        if (!result) {
          updated.push(this.backToPending(record, 'سرور برای این عملیات پاسخی نداد.'));
          summary.retry += 1;
          continue;
        }
        if (result.outcome === 'stored' || result.outcome === 'duplicate') {
          // سرور همان لحظه با فرم موجود ثبت قطعی می‌کند: Applied (همگام‌شده)، Conflict (با دلیل) یا هنوز در جریان.
          const status = SERVER_STATUSES.has(result.status) ? result.status : Status.Received;
          summary[result.outcome === 'stored' ? 'stored' : 'duplicate'] += 1;
          if (status === Status.Conflict) summary.conflicts += 1;
          if (status === Status.Applied) summary.applied += 1;
          updated.push(this.repo.withHistory({ ...record, serverNote: result.message || null, lastError: null, nextAttemptAt: null,
            serverAckAt: record.serverAckAt || this.now().toISOString(),
            appliedRef: appliedRefOf(result) || record.appliedRef || null }, status, result.message || null));
        } else if (result.outcome === 'rejected') {
          summary.rejected += 1;
          updated.push(this.repo.withHistory({ ...record, serverNote: result.message || 'سرور عملیات را رد کرد.',
            lastError: result.code || null, nextAttemptAt: null }, Status.Rejected, result.message || null));
        } else {
          summary.retry += 1;
          updated.push(this.backToPending(record, result.message || 'بعداً دوباره فرستاده می‌شود.'));
        }
      }
      await this.repo.putMany(updated);
    }
  }

  backToPending(record, reason, delayMs = null) {
    const delay = delayMs ?? retryDelayMs(record.attempts || 1, this.random);
    return this.repo.withHistory({
      ...record,
      lastError: reason,
      nextAttemptAt: new Date(this.now().getTime() + delay).toISOString()
    }, Status.Pending, reason);
  }

  async pollStatuses(summary) {
    const records = await this.repo.list();
    // فقط عملیاتی که سرور دریافتش را تأیید کرده؛ تعارضِ محلی (پیش‌نیاز ردشده) هرگز به سرور نرفته است.
    const open = records.filter((r) => SERVER_OPEN.includes(r.status) && r.serverAckAt);
    for (let i = 0; i < open.length; i += 200) {
      const chunk = open.slice(i, i + 200);
      const response = await this.api.statuses(chunk.map((r) => r.clientOperationId));
      if (!response.ok || !response.data) {
        if (response.status === 401) summary.authRequired = true;
        return;
      }
      const byId = new Map(response.data.operations.map((o) => [o.clientOperationId, o]));
      const updated = [];
      for (const record of chunk) {
        const server = byId.get(record.clientOperationId);
        if (!server) continue;
        if (server.status === 'Unknown') {
          // سرور این عملیات را ندارد (مثلاً بازیابی دیتابیس سرور): با همان شناسه دوباره فرستاده می‌شود.
          updated.push(this.repo.withHistory({ ...record, nextAttemptAt: this.now().toISOString() }, Status.Pending,
            'سرور این عملیات را پیدا نکرد؛ دوباره فرستاده می‌شود.'));
          continue;
        }
        const appliedRef = appliedRefOf(server) || record.appliedRef || null;
        const serverStatus = SERVER_STATUSES.has(server.status) ? server.status : Status.Received;
        if (serverStatus === Status.Applied && record.status !== Status.Applied) summary.applied += 1;
        if (serverStatus !== record.status || (server.note || null) !== (record.serverNote || null) || appliedRef !== record.appliedRef) {
          updated.push(this.repo.withHistory({ ...record, serverNote: server.note || null, appliedRef }, serverStatus, server.note || null));
        }
      }
      summary.statusUpdates += updated.length;
      await this.repo.putMany(updated);
    }
  }

  async refreshSnapshot(summary, force) {
    const meta = await this.store.get('meta', 'snapshot');
    const age = meta && meta.asOf ? this.now().getTime() - new Date(meta.fetchedAt || meta.asOf).getTime() : Infinity;
    if (!force && age < SNAPSHOT_MAX_AGE_MS) return;
    const response = await this.api.snapshot();
    if (!response.ok || !response.data) return;
    const sealed = await this.sealer.seal(response.data);
    await this.store.atomic([
      { store: 'snapshot', type: 'put', value: { key: 'current', sealed } },
      { store: 'meta', type: 'put', value: { key: 'snapshot', asOf: response.data.asOfUtc, fetchedAt: this.now().toISOString() } }
    ]);
    summary.snapshot = true;
  }
}
