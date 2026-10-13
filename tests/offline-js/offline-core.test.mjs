// مشعل آفلاین — تست‌های واحد منطق کلاینت (Node 22، بدون وابستگی).
// اجرا: node --test tests/offline-js/
// این تست‌ها منطق واقعی فایل‌های wwwroot/offline/js را با MemoryStore و سرور جعلی اجرا می‌کنند؛
// تست دستگاه واقعی نیستند (برای مرورگر واقعی: tests/offline-js/offline-e2e.cjs).

import test from 'node:test';
import assert from 'node:assert/strict';

import { MemoryStore, isQuotaError, profileDbName } from '../../src/PTGOilSystem.Web/wwwroot/offline/js/store.js';
import { Sealer, createProfile, unlockProfile, changePin, lockDurationMs, validatePin } from '../../src/PTGOilSystem.Web/wwwroot/offline/js/vault.js';
import { OutboxRepo, Status, canResubmit, normalizePayload, validatePayload } from '../../src/PTGOilSystem.Web/wwwroot/offline/js/operations.js';
import { SyncEngine, retryDelayMs, MAX_BATCHES_PER_RUN } from '../../src/PTGOilSystem.Web/wwwroot/offline/js/sync-engine.js';
import { createBackup, openBackup, mergeBackup, backupWarning } from '../../src/PTGOilSystem.Web/wwwroot/offline/js/backup.js';
import { sha256Hex, uuid } from '../../src/PTGOilSystem.Web/wwwroot/offline/js/util.js';

const SESSION = { userId: 5, username: 'op5', fullName: 'کاربر پنج', companyId: 1, companyName: 'مشعل' };
const SALE = { customerId: 7, productId: 3, tankId: 4, quantity: 12.5, currency: 'USD' };
const PIN = 'kabul-2026';

class FakeServer {
  constructor() {
    this.ops = new Map();
    this.devices = new Set();
    this.sessionUser = { ...SESSION };
    this.failPushWith = null; // 'network' | 401 | 500
    this.loseNextResponse = false; // ذخیره می‌کند ولی پاسخ گم می‌شود (قطع اینترنت وسط Sync)
    this.rejectTypes = new Set();
    this.conflictTypes = new Set(); // خطای تجاری فرم موجود (مثلاً موجودی ناکافی)
    this.applyMode = 'applied'; // 'applied' | 'received' (ثبت هنوز در جریان)
    this.pushCalls = 0;
    this.documents = 0;
  }

  async session() {
    if (this.sessionStatus === 401) return { ok: false, status: 401, data: null, networkError: false };
    return { ok: true, status: 200, data: { ...this.sessionUser, operationTypes: [] }, networkError: false };
  }

  async registerDevice(id) {
    this.devices.add(id);
    return { ok: true, status: 204, data: null, networkError: false };
  }

  async push(deviceId, operations) {
    this.pushCalls += 1;
    if (this.failPushWith === 'network') return { ok: false, status: 0, data: null, networkError: true };
    if (typeof this.failPushWith === 'number') return { ok: false, status: this.failPushWith, data: null, networkError: false };
    const results = [];
    for (const op of operations) {
      const hash = await sha256Hex(op.payloadJson);
      assert.equal(hash, op.payloadHash, 'client hash must match server algorithm');
      const existing = this.ops.get(op.clientOperationId);
      if (existing && existing.status === 'Conflict' && op.revision > existing.revision) {
        Object.assign(existing, op);
        existing.status = this.applyStatus(op);
        results.push({ clientOperationId: op.clientOperationId, outcome: 'stored', status: existing.status, revision: op.revision });
        continue;
      }
      if (existing) {
        results.push({ clientOperationId: op.clientOperationId, outcome: 'duplicate', status: existing.status });
        continue;
      }
      if (this.rejectTypes.has(op.operationType)) {
        results.push({ clientOperationId: op.clientOperationId, outcome: 'rejected', code: 'payload_invalid', message: 'رد آزمایشی' });
        continue;
      }
      const status = this.applyStatus(op);
      this.ops.set(op.clientOperationId, { ...op, status, deviceId, note: status === 'Conflict' ? 'موجودی مخزن کافی نیست.' : null });
      results.push({ clientOperationId: op.clientOperationId, outcome: 'stored', status,
        appliedReferenceType: status === 'Applied' ? 'Doc' : null, appliedReferenceId: status === 'Applied' ? String(this.documents) : null,
        message: status === 'Conflict' ? 'موجودی مخزن کافی نیست.' : null });
    }
    if (this.loseNextResponse) {
      this.loseNextResponse = false;
      return { ok: false, status: 0, data: null, networkError: true };
    }
    return { ok: true, status: 200, data: { results }, networkError: false };
  }

  applyStatus(op) {
    if (this.applyMode === 'received') return 'Received';
    if (this.conflictTypes.has(op.operationType) && !(op.revision > 1)) return 'Conflict';
    this.documents += 1;
    return 'Applied';
  }

  async statuses(ids) {
    return {
      ok: true, status: 200, networkError: false,
      data: { operations: ids.map((id) => ({ clientOperationId: id, status: this.ops.get(id)?.status || 'Unknown', note: this.ops.get(id)?.note || null })) }
    };
  }

  async snapshot() {
    return { ok: true, status: 200, networkError: false, data: { asOfUtc: new Date().toISOString(), reference: { customers: [{ id: 7, name: 'مشتری الف' }] } } };
  }
}

class FakeMonitor {
  constructor(reachable = true) { this.reachable = reachable; }
  async check() { return this.reachable; }
  setSyncing() {}
}

async function setup({ reachable = true, server = new FakeServer(), store = new MemoryStore() } = {}) {
  const { profile, dataKey } = await createProfile({ session: SESSION, pin: PIN, deviceId: uuid() });
  const sealer = new Sealer(dataKey);
  const repo = new OutboxRepo(store, sealer, profile);
  const monitor = new FakeMonitor(reachable);
  const engine = new SyncEngine({ repo, store, api: server, monitor, profile, sealer, random: () => 0 });
  return { profile, dataKey, sealer, repo, store, server, monitor, engine };
}

// ---------------------------------------------------------------- storage / outbox

test('TEST 05: submit stores operation and send order atomically, encrypted at rest', async () => {
  const { repo, store } = await setup();
  const record = await repo.submit('Sale.Request', SALE, '2026-10-01');

  assert.equal(record.status, Status.Pending);
  assert.equal(record.payloadHash, await sha256Hex(record.payloadJson));
  const row = await store.get('outbox', record.clientOperationId);
  assert.equal(row.status, Status.Pending);
  assert.ok(!JSON.stringify(row).includes('12.5'), 'payload must not be stored in clear text');
  assert.deepEqual((await repo.get(record.clientOperationId)).payload, SALE);
});

test('TEST 05/18: failed transaction (e.g. quota) stores nothing', async () => {
  const { repo, store } = await setup();
  const quota = new DOMException('quota', 'QuotaExceededError');
  store.failNextAtomic = quota;
  await assert.rejects(() => repo.submit('Sale.Request', SALE, '2026-10-01'), (e) => isQuotaError(e));
  assert.equal(await store.count('outbox'), 0);
});

test('validation mirrors the server contract', () => {
  assert.equal(validatePayload('Sale.Request', SALE), null);
  assert.match(validatePayload('Sale.Request', { ...SALE, quantity: 0 }), /مثبت/);
  assert.match(validatePayload('Sale.Request', { customerId: 7, tankId: 4, currency: 'USD', quantity: 1 }), /جنس/);
  assert.match(validatePayload('Sale.Request', { customerId: 7, productId: 3, currency: 'USD', quantity: 1 }), /مخزن/);
  assert.equal(validatePayload('Sale.Request', { ...SALE, customerId: 'op:0b8f4b9e-7a43-4b6f-9a61-1f1f9c8c1a10' }), null);
  assert.match(validatePayload('Expense.Request', { expenseTypeId: 2, amount: 10, currency: 'USD' }), /شرح/);
  assert.match(validatePayload('MoneyTransfer.Request', { amount: 5 }), /پشتیبانی/);
  assert.match(validatePayload('Ledger.Post', {}), /پشتیبانی/);
});

test('main customer form fields (Customers/Create) are kept with stable types; isActive is always a boolean', () => {
  const payload = normalizePayload('Customer.Draft',
    { code: ' c-01 ', name: ' مشتری ', country: 'AF', phone: '0700', isActive: false, Unrelated: 'x' });
  assert.deepEqual(payload, { code: 'c-01', name: 'مشتری', country: 'AF', phone: '0700', isActive: false });
  assert.equal(normalizePayload('Customer.Draft', { name: 'a', isActive: 'true' }).isActive, true);
  assert.equal(normalizePayload('Customer.Draft', { name: 'a', isActive: 'false' }).isActive, false);
  assert.equal('isActive' in normalizePayload('Customer.Draft', { name: 'a' }), false);
  assert.equal(validatePayload('Customer.Draft', payload), null);
  assert.match(validatePayload('Customer.Draft', { code: 'c-01', isActive: true }), /نام/);
});

test('TEST 06/07/08 (simulated): data survives app restart and re-unlock with the PIN', async () => {
  const store = new MemoryStore();
  const first = await setup({ store });
  for (let i = 0; i < 20; i += 1) {
    await first.repo.submit('Sale.Request', { ...SALE, quantity: i + 1 }, '2026-10-01');
  }
  // «بستن برنامه»: کلید از حافظه می‌رود؛ فقط پروفایل رمزشده و store می‌ماند.
  const unlocked = await unlockProfile(first.profile, PIN);
  assert.equal(unlocked.ok, true);
  const repo = new OutboxRepo(store, new Sealer(unlocked.dataKey), unlocked.profile);
  const records = await repo.list();
  assert.equal(records.length, 20);
  assert.ok(records.every((r) => r.status === Status.Pending));
});

test('drafts are editable; sent operations are immutable', async () => {
  const { repo } = await setup();
  const draft = await repo.saveDraft('Expense.Request', { expenseTypeId: 2, amount: 10, currency: 'USD', description: 'x' }, '2026-10-02');
  const edited = await repo.saveDraft('Expense.Request', { expenseTypeId: 2, amount: 15, currency: 'USD', description: 'x' }, '2026-10-02', draft.clientOperationId);
  assert.equal(edited.payload.amount, 15);
  const submitted = await repo.submit('Expense.Request', edited.payload, '2026-10-02', { fromDraftId: draft.clientOperationId });
  assert.equal(submitted.clientOperationId, draft.clientOperationId);
  const back = await repo.returnToDraft(submitted.clientOperationId);
  assert.equal(back.status, Status.Draft);
  const again = await repo.submit('Expense.Request', back.payload, '2026-10-02', { fromDraftId: back.clientOperationId });
  await repo.putMany([{ ...again, attempts: 1 }]);
  await assert.rejects(() => repo.returnToDraft(again.clientOperationId), /قابل ویرایش نیست/);
  await assert.rejects(() => repo.discardDraft(again.clientOperationId), /پیش‌نویس/);
});

// ---------------------------------------------------------------- vault

test('TEST 15/§11: offline PIN unlock, failed-attempt lockout, no data wipe', async () => {
  const { profile, repo } = await setup();
  await repo.submit('Sale.Request', SALE, '2026-10-01');
  let current = profile;
  for (let i = 1; i <= 5; i += 1) {
    const result = await unlockProfile(current, 'wrong-pin', new Date('2026-10-13T08:00:00Z'));
    assert.equal(result.ok, false);
    current = result.profile;
  }
  assert.equal(current.failedAttempts, 5);
  assert.ok(current.lockedUntil, 'locked after 5 failures');
  const whileLocked = await unlockProfile(current, PIN, new Date('2026-10-13T08:00:10Z'));
  assert.equal(whileLocked.reason, 'locked');
  const afterLock = await unlockProfile(whileLocked.profile, PIN, new Date('2026-10-13T08:05:00Z'));
  assert.equal(afterLock.ok, true);
  assert.equal(afterLock.profile.failedAttempts, 0);
  assert.equal((await repo.list()).length, 1, 'pending data is never deleted by lockout');
  assert.equal(lockDurationMs(4), 0);
  assert.equal(lockDurationMs(40), 60 * 60_000);
});

test('PIN policy and PIN change keep the same data key', async () => {
  assert.ok(validatePin('12345'));
  assert.ok(validatePin('111111'));
  assert.equal(validatePin(PIN), null);
  const { profile, repo } = await setup();
  const record = await repo.submit('Sale.Request', SALE, '2026-10-01');
  const changed = await changePin(profile, PIN, 'herat-9988');
  const unlocked = await unlockProfile(changed, 'herat-9988');
  const repo2 = new OutboxRepo(repo.store, new Sealer(unlocked.dataKey), changed);
  assert.equal((await repo2.get(record.clientOperationId)).payload.quantity, 12.5);
  assert.equal((await unlockProfile(changed, PIN)).ok, false);
});

test('TEST 19: each user/company has a separate database name', () => {
  assert.notEqual(profileDbName(1, 5), profileDbName(1, 6));
  assert.notEqual(profileDbName(1, 5), profileDbName(2, 5));
});

// ---------------------------------------------------------------- sync engine

test('offline: nothing is sent and nothing changes', async () => {
  const { repo, engine, server } = await setup({ reachable: false });
  await repo.submit('Sale.Request', SALE, '2026-10-01');
  const summary = await engine.run();
  assert.equal(summary.offline, true);
  assert.equal(server.pushCalls, 0);
  assert.equal((await repo.list())[0].status, Status.Pending);
});

test('TEST 10/21: reconnect sends once and the server posts it automatically (Applied + document ref)', async () => {
  const { repo, engine, server } = await setup();
  const record = await repo.submit('Sale.Request', SALE, '2026-10-01');
  const summary = await engine.run();
  assert.equal(summary.stored, 1);
  assert.equal(summary.applied, 1);
  const synced = await repo.get(record.clientOperationId);
  assert.equal(synced.status, Status.Applied);
  assert.equal(synced.appliedRef, 'Doc 1');
  assert.deepEqual(JSON.parse(server.ops.get(record.clientOperationId).payloadJson), SALE);
});

test('in-progress server posting (Received) is polled until Applied', async () => {
  const { repo, engine, server } = await setup();
  server.applyMode = 'received';
  const record = await repo.submit('Sale.Request', SALE, '2026-10-01');
  await engine.run();
  assert.equal((await repo.get(record.clientOperationId)).status, Status.Received);
  server.ops.get(record.clientOperationId).status = 'Applied';
  await engine.run();
  assert.equal((await repo.get(record.clientOperationId)).status, Status.Applied);
});

test('§6: business conflict is kept with the reason; the same operation is corrected and resent (revision 2)', async () => {
  const { repo, engine, server } = await setup();
  server.conflictTypes.add('Sale.Request');
  const record = await repo.submit('Sale.Request', SALE, '2026-10-01');
  const first = await engine.run();
  assert.equal(first.conflicts, 1);
  const conflict = await repo.get(record.clientOperationId);
  assert.equal(conflict.status, Status.Conflict);
  assert.equal(conflict.serverNote, 'موجودی مخزن کافی نیست.');
  assert.equal(canResubmit(conflict), true);

  const fixed = await repo.resubmit(record.clientOperationId, { ...SALE, quantity: 5 }, '2026-10-01');
  assert.equal(fixed.status, Status.Pending);
  assert.equal(fixed.revision, 2);
  assert.equal(fixed.clientOperationId, record.clientOperationId);
  await engine.run();
  const done = await repo.get(record.clientOperationId);
  assert.equal(done.status, Status.Applied);
  assert.equal(server.ops.size, 1, 'same id, no second operation');
  assert.equal(JSON.parse(server.ops.get(record.clientOperationId).payloadJson).quantity, 5);
});

test('reference to an offline-created customer becomes a dependency automatically', async () => {
  const { repo, engine, server } = await setup();
  const customer = await repo.submit('Customer.Draft', { name: 'مشتری تازه' }, '2026-10-01');
  const sale = await repo.submit('Sale.Request', { ...SALE, customerId: `op:${customer.clientOperationId}` }, '2026-10-01');
  assert.deepEqual(sale.dependencies, [customer.clientOperationId]);
  await engine.run();
  const order = [...server.ops.keys()];
  assert.ok(order.indexOf(customer.clientOperationId) < order.indexOf(sale.clientOperationId));
  assert.equal((await repo.get(sale.clientOperationId)).status, Status.Applied);
});

test('TEST 11/12: connection lost mid-sync → same id resent, server stores once', async () => {
  const { repo, engine, server } = await setup();
  const record = await repo.submit('Sale.Request', SALE, '2026-10-01');
  server.loseNextResponse = true;
  const first = await engine.run();
  assert.equal(first.offline, true);
  const afterLoss = await repo.get(record.clientOperationId);
  assert.equal(afterLoss.status, Status.Pending);
  assert.equal(afterLoss.attempts, 1);

  await repo.putMany([{ ...afterLoss, nextAttemptAt: new Date().toISOString() }]);
  const second = await engine.run();
  assert.equal(second.duplicate, 1);
  assert.equal(server.ops.size, 1);
  assert.equal(server.documents, 1, 'exactly one document');
  assert.equal((await repo.get(record.clientOperationId)).status, Status.Applied);
});

test('TEST 11: app closed while Sending → recovered to Pending and resent with the same id', async () => {
  const { repo, engine, server } = await setup();
  const record = await repo.submit('Sale.Request', SALE, '2026-10-01');
  await repo.putMany([repo.withHistory({ ...record, attempts: 1 }, Status.Sending)]);
  assert.equal(await engine.recoverInterrupted(), 1);
  await engine.run();
  assert.equal(server.ops.size, 1);
  assert.ok(server.ops.has(record.clientOperationId));
});

test('TEST 15: expired server session keeps operations pending', async () => {
  const { repo, engine, server } = await setup();
  await repo.submit('Sale.Request', SALE, '2026-10-01');
  server.sessionStatus = 401;
  const summary = await engine.run();
  assert.equal(summary.authRequired, true);
  assert.equal((await repo.list())[0].status, Status.Pending);
  server.sessionStatus = 200;
  server.failPushWith = 401;
  const again = await engine.run();
  assert.equal(again.authRequired, true);
  const record = (await repo.list())[0];
  assert.equal(record.status, Status.Pending);
  assert.ok(record.nextAttemptAt <= new Date().toISOString(), '401 does not add backoff');
});

test('TEST 19: another user signed in → this profile does not send', async () => {
  const { repo, engine, server } = await setup();
  await repo.submit('Sale.Request', SALE, '2026-10-01');
  server.sessionUser = { ...SESSION, userId: 99 };
  const summary = await engine.run();
  assert.equal(summary.userMismatch, true);
  assert.equal(server.pushCalls, 0);
});

test('TEST 22: rejected operation is kept with the reason and can be corrected as a new one', async () => {
  const { repo, engine, server } = await setup();
  server.rejectTypes.add('Sale.Request');
  const record = await repo.submit('Sale.Request', SALE, '2026-10-01');
  const summary = await engine.run();
  assert.equal(summary.rejected, 1);
  const rejected = await repo.get(record.clientOperationId);
  assert.equal(rejected.status, Status.Rejected);
  assert.equal(rejected.serverNote, 'رد آزمایشی');
  const copy = await repo.saveDraft(rejected.operationType, rejected.payload, rejected.businessDate);
  assert.notEqual(copy.clientOperationId, rejected.clientOperationId);
  assert.equal((await repo.list()).length, 2);
});

test('server error → retry with growing backoff, never deleted', async () => {
  const { repo, engine, server } = await setup();
  const record = await repo.submit('Sale.Request', SALE, '2026-10-01');
  server.failPushWith = 500;
  await engine.run();
  const after = await repo.get(record.clientOperationId);
  assert.equal(after.status, Status.Pending);
  assert.ok(after.nextAttemptAt > new Date().toISOString());
  assert.equal(retryDelayMs(1, () => 0), 15_000);
  assert.equal(retryDelayMs(30, () => 0), 60 * 60_000);
  const second = await engine.run();
  assert.equal(server.pushCalls, 1, 'not resent before nextAttemptAt');
  assert.equal(second.stored, 0);
});

test('dependency ordering: child waits for parent; rejected parent → child Conflict', async () => {
  const { repo, engine, server } = await setup();
  const parent = await repo.submit('Customer.Draft', { name: 'مشتری تازه' }, '2026-10-01');
  const child = await repo.submit('General.Note', { text: 'بعد از مشتری' }, '2026-10-01', { dependencies: [parent.clientOperationId] });
  await engine.run();
  assert.equal((await repo.get(child.clientOperationId)).status, Status.Applied);
  const order = [...server.ops.keys()];
  assert.ok(order.indexOf(parent.clientOperationId) < order.indexOf(child.clientOperationId));

  const s2 = await setup();
  s2.server.rejectTypes.add('Customer.Draft');
  const p2 = await s2.repo.submit('Customer.Draft', { name: 'x' }, '2026-10-01');
  await s2.engine.run();
  const c2 = await s2.repo.submit('General.Note', { text: 'y' }, '2026-10-01', { dependencies: [p2.clientOperationId] });
  await s2.engine.run();
  assert.equal((await s2.repo.get(c2.clientOperationId)).status, Status.Conflict);
  assert.equal(s2.server.ops.has(c2.clientOperationId), false);
});

test('TEST 09: long outage with 1200 queued operations syncs in batches without loss or duplicates', async () => {
  const { repo, engine, server } = await setup();
  for (let i = 0; i < 1200; i += 1) {
    await repo.submit('General.Note', { text: `رویداد ${i}` }, '2026-10-01');
  }
  const first = await engine.run();
  assert.equal(first.stored, MAX_BATCHES_PER_RUN * 10, 'one run sends a bounded number of batches');
  for (let i = 0; i < 10 && server.ops.size < 1200; i += 1) {
    await engine.run();
  }
  assert.equal(server.ops.size, 1200);
  const records = await repo.list();
  assert.ok(records.every((r) => r.status === Status.Applied));
  assert.equal(server.documents, 1200);
});

test('server lost an operation (Unknown) → it is resent with the same id', async () => {
  const { repo, engine, server } = await setup();
  server.applyMode = 'received';
  const record = await repo.submit('Sale.Request', SALE, '2026-10-01');
  await engine.run();
  server.ops.clear(); // مثلاً بازیابی دیتابیس سرور
  await engine.run(); // poll → Unknown → Pending
  assert.equal((await repo.get(record.clientOperationId)).status, Status.Pending);
  await engine.run();
  assert.ok(server.ops.has(record.clientOperationId));
});

test('snapshot is stored encrypted with its timestamp', async () => {
  const { engine, store, sealer } = await setup();
  await engine.run({ refreshSnapshot: true });
  const row = await store.get('snapshot', 'current');
  assert.ok(!JSON.stringify(row).includes('مشتری الف'));
  assert.equal((await sealer.open(row.sealed)).reference.customers[0].id, 7);
  assert.ok((await store.get('meta', 'snapshot')).asOf);
});

// ---------------------------------------------------------------- backup

test('TEST 16: encrypted backup → restore on a fresh store → pending re-sync without duplicates', async () => {
  const a = await setup();
  const sent = await a.repo.submit('Sale.Request', SALE, '2026-10-01');
  await a.engine.run();
  const pending = await a.repo.submit('Expense.Request', { expenseTypeId: 2, amount: 40, currency: 'USD', description: 'y' }, '2026-10-02');
  const records = await a.repo.list();
  const envelope = await createBackup({ profile: a.profile, records, password: 'backup-pass-1' });
  const fileText = JSON.stringify(envelope);
  assert.ok(!fileText.includes('expenseTypeId'), 'backup content is encrypted');

  await assert.rejects(() => openBackup(fileText, 'wrong-password'), /نادرست/);
  const tampered = JSON.stringify({ ...envelope, sha256: '0'.repeat(64) });
  await assert.rejects(() => openBackup(tampered, 'backup-pass-1'), /صحت/);

  const content = await openBackup(fileText, 'backup-pass-1');
  // دستگاه تازه با همان کاربر: پایگاه خالی، همان سرور.
  const fresh = new MemoryStore();
  const repo = new OutboxRepo(fresh, a.sealer, a.profile);
  const merge = mergeBackup([], content, a.profile);
  assert.equal(merge.added, 2);
  await repo.putMany(merge.toWrite);
  const engine = new SyncEngine({ repo, store: fresh, api: a.server, monitor: new FakeMonitor(), profile: a.profile, sealer: a.sealer, random: () => 0 });
  await engine.run();
  assert.equal(a.server.ops.size, 2, 'no duplicate for the already-sent operation');
  assert.equal((await repo.get(pending.clientOperationId)).status, Status.Applied);
  assert.equal((await repo.get(sent.clientOperationId)).status, Status.Applied);
  assert.equal(a.server.documents, 2, 'restore does not post the already-synced operation again');

  const again = mergeBackup(await repo.list(), content, a.profile);
  assert.equal(again.added, 0);
});

test('backup of another user/company is refused; Sending is restored as Pending', async () => {
  const a = await setup();
  const record = await a.repo.submit('Sale.Request', SALE, '2026-10-01');
  const sending = a.repo.withHistory({ ...record, attempts: 1 }, Status.Sending);
  const content = await openBackup(JSON.stringify(await createBackup({ profile: a.profile, records: [sending], password: 'backup-pass-1' })), 'backup-pass-1');
  assert.throws(() => mergeBackup([], content, { ...a.profile, companyId: 2 }), /شرکت دیگری/);
  const merge = mergeBackup([], content, a.profile);
  assert.equal(merge.toWrite[0].status, Status.Pending);
});

test('backup reminder warns about unsynced work without a recent backup', async () => {
  const { repo } = await setup();
  await repo.submit('Sale.Request', SALE, '2026-10-01');
  const records = await repo.list();
  assert.equal(backupWarning(records, null).shouldWarn, true);
  assert.equal(backupWarning(records, new Date().toISOString()).notBackedUp, 0);
  assert.equal(backupWarning([], null).shouldWarn, false);
});

// ---------------------------------------------------------------- trusted device auth

import { DeviceAuth } from '../../src/PTGOilSystem.Web/wwwroot/offline/js/device-auth.js';

test('device auth: tokens are stored encrypted, refreshed once for concurrent callers, cleared when revoked', async () => {
  const { store, sealer, profile } = await setup();
  let calls = 0;
  let mode = 'ok';
  const auth = new DeviceAuth({
    store, sealer, deviceId: profile.deviceId,
    refreshImpl: async () => {
      calls += 1;
      await new Promise((r) => setTimeout(r, 10));
      if (mode === 'revoked') return { ok: false, status: 401, networkError: false };
      return { ok: true, data: { accessToken: `A${calls}`, accessTokenExpiresAtUtc: new Date(Date.now() + 900_000).toISOString(),
        refreshToken: `R${calls}`, refreshTokenExpiresAtUtc: new Date(Date.now() + 86_400_000).toISOString() } };
    }
  });
  await auth.save({ accessToken: 'A0', accessTokenExpiresAtUtc: new Date(Date.now() - 1000).toISOString(),
    refreshToken: 'R0', refreshTokenExpiresAtUtc: new Date(Date.now() + 86_400_000).toISOString() });
  assert.ok(!JSON.stringify(await store.get('meta', 'deviceAuth')).includes('R0'), 'refresh token encrypted at rest');

  const headers = await Promise.all([auth.header(), auth.header(), auth.header()]);
  assert.equal(calls, 1, 'rotation-safe single refresh');
  assert.deepEqual(new Set(headers), new Set(['Bearer A1']));

  mode = 'revoked';
  assert.equal(await auth.refresh(), null);
  assert.equal(await auth.has(), false, 'revoked/disabled user → back to normal login');
});

// ---------------------------------------------------------------- فورم‌های اصلی MVC، صفحات ذخیره‌شده، نشست

import { FORM_SUBMIT, referencedOperations } from '../../src/PTGOilSystem.Web/wwwroot/offline/js/operations.js';
import { PageVault, normalizePageUrl, MAX_PAGES } from '../../src/PTGOilSystem.Web/wwwroot/offline/js/page-vault.js';

test('main form (Form.Submit): exact field pairs kept in order, path normalised, op: references become dependencies', async () => {
  const opId = '0b8f4b9e-7a43-4b6f-9a61-1f1f9c8c1a10';
  const payload = normalizePayload(FORM_SUBMIT, {
    path: '/Payments/Create/?x=1', label: ' دریافت ', fields: [['CustomerId', `op:${opId}`], ['IsActive', 'true'], ['IsActive', 'false'], ['', 'x'], ['Amount', 5]],
    query: [['returnUrl', '/Payments']]
  });
  assert.deepEqual(payload, {
    path: '/payments/create', label: 'دریافت',
    fields: [['CustomerId', `op:${opId}`], ['IsActive', 'true'], ['IsActive', 'false'], ['Amount', '5']],
    query: [['returnUrl', '/Payments']]
  });
  assert.equal(validatePayload(FORM_SUBMIT, payload), null);
  assert.match(validatePayload(FORM_SUBMIT, { path: '/x', fields: [] }), /خالی/);
  assert.deepEqual(referencedOperations(payload), [opId]);

  const store = await MemoryStore.open('t');
  const { dataKey } = await createProfile({ session: SESSION, pin: PIN, deviceId: 'dev-1' });
  const repo = new OutboxRepo(store, new Sealer(dataKey), { ...SESSION, deviceId: 'dev-1' });
  const record = await repo.submit(FORM_SUBMIT, payload, '2026-10-13');
  assert.deepEqual(record.dependencies, [opId]);
  assert.equal(record.payloadHash, await sha256Hex(JSON.stringify(record.payload)));
});

test('page vault: pages are encrypted at rest, found by normalised URL, oldest evicted, shell kept', async () => {
  const store = await MemoryStore.open('pages');
  const { dataKey } = await createProfile({ session: SESSION, pin: PIN, deviceId: 'dev-1' });
  let t = Date.parse('2026-10-13T08:00:00Z');
  const vault = new PageVault(store, new Sealer(dataKey), { clock: () => new Date(t += 1000), maxPages: 2 });
  const html = (n) => `<html><head><title>صفحه ${n}</title></head><body><main>مانده ${n}</main></body></html>`;

  await vault.putShell('<html><body><nav>منو</nav><main>x</main></body></html>');
  await vault.put('http://h/Customers?page=2&q=a', html(1));
  await vault.put('http://h/Sales', html(2));
  assert.equal((await vault.get('http://h/customers/?q=a&page=2&source=pwa')).title, 'صفحه 1');
  assert.ok(!JSON.stringify(await store.getAll('pages')).includes('مانده'), 'stored page must be encrypted');

  await vault.put('http://h/Payments', html(3)); // سقف ۲ صفحه: قدیمی‌ترین می‌رود
  assert.equal(await vault.get('http://h/Customers?page=2&q=a'), null);
  assert.ok(await vault.get('http://h/Sales'));
  assert.ok((await vault.getShell()).html.includes('منو'));
  assert.deepEqual((await vault.list()).map((p) => p.url).sort(), ['/payments', '/sales']);
  assert.equal(normalizePageUrl('/?source=pwa', 'http://h/'), '/');

  // پاسخ JSONِ فهرست وابسته: رمزگذاری‌شده، با سقف جدا (صفحات را بیرون نمی‌کند)، و در فهرست صفحات نمی‌آید.
  await vault.putData('http://h/Payments/ContractPartnerOptions?contractId=5', '[{"id":7,"name":"شریک"}]', 'application/json');
  assert.equal((await vault.getData('http://h/payments/contractpartneroptions?contractId=5')).body, '[{"id":7,"name":"شریک"}]');
  assert.ok(await vault.get('http://h/Sales'), 'data entries do not evict pages');
  assert.ok(!(await vault.list()).some((p) => p.url.includes('contractpartneroptions')));
  assert.ok(MAX_PAGES >= 100);
});

test('page vault without PIN: pages written with the public page key, readable only after unlock, old format still opens', async () => {
  const vaultModule = await import('../../src/PTGOilSystem.Web/wwwroot/offline/js/vault.js');
  const store = await MemoryStore.open('pages');
  const { profile, dataKey } = await createProfile({ session: SESSION, pin: PIN, deviceId: 'dev-1' });
  const sealer = new Sealer(dataKey);
  const keyed = { ...profile, pageKeys: await vaultModule.createPageKeys(sealer) };
  assert.ok(!JSON.stringify(keyed.pageKeys).includes('"d"'), 'private key never stored in clear');

  // نشست بسته (برنامهٔ نصب‌شده بدون رمز): فقط نوشتن.
  const locked = await vaultModule.PageSealer.forProfile(keyed);
  assert.equal(locked.canOpen, false);
  const writer = new PageVault(store, locked);
  await writer.put('http://h/Reports/Stock', '<html><head><title>راپور</title></head><body><main>موجودی ۵۰۰</main></body></html>');
  await writer.putShell('<html><body><nav>منوی کامل</nav><main></main></body></html>');
  assert.ok(!JSON.stringify(await store.getAll('pages')).includes('موجودی'), 'locked write is encrypted');
  await assert.rejects(() => writer.get('http://h/Reports/Stock'), /locked/);

  // پس از رمز محلی: هر دو قالب (کلید عمومی و کلید داده) باز می‌شوند.
  const unlocked = await unlockProfile(keyed, PIN);
  const reader = new PageVault(store, await vaultModule.PageSealer.forProfile(unlocked.profile, new Sealer(unlocked.dataKey)));
  assert.equal((await reader.get('http://h/reports/stock')).title, 'راپور');
  assert.ok((await reader.getShell()).html.includes('منوی کامل'));
  await reader.put('http://h/Sales', '<html><head><title>فروش</title></head><body><main>x</main></body></html>');
  assert.equal((await new PageVault(store, sealer).get('http://h/Sales')).title, 'فروش', 'unlocked writes keep the data-key format');

  // کلید عمومی کاربر دیگر نمی‌تواند صفحات این کاربر را باز کند.
  const other = await createProfile({ session: { ...SESSION, userId: SESSION.userId + 1 }, pin: PIN, deviceId: 'dev-2' });
  const otherKeys = { ...other.profile, pageKeys: await vaultModule.createPageKeys(new Sealer(other.dataKey)) };
  const otherReader = new PageVault(store, await vaultModule.PageSealer.forProfile(otherKeys, new Sealer(other.dataKey)));
  await assert.rejects(() => otherReader.get('http://h/Reports/Stock'));
  assert.equal(await vaultModule.PageSealer.forProfile(profile), null, 'no page keys and no session: nothing is written');
});

test('session: local PIN once per session; expires after idle, never crosses users, forget on logout', async () => {
  const bag = new Map();
  globalThis.sessionStorage = {
    getItem: (k) => (bag.has(k) ? bag.get(k) : null), setItem: (k, v) => bag.set(k, String(v)), removeItem: (k) => bag.delete(k)
  };
  const session = await import('../../src/PTGOilSystem.Web/wwwroot/offline/js/session.js');
  const { profile, rawKey } = await createProfile({ session: SESSION, pin: PIN, deviceId: 'dev-1' });
  const now = Date.now();
  assert.equal(session.rememberSession(profile, rawKey, now), true);

  const restored = await session.restoreSession(SESSION.userId, now + 60_000);
  const sealed = await new Sealer(restored.dataKey).seal({ ok: 1 });
  assert.deepEqual(await new Sealer((await unlockProfile(profile, PIN)).dataKey).open(sealed), { ok: 1 }, 'same data key as the PIN unlock');
  assert.equal(await session.restoreSession(999, now), null, 'another user never gets this key');
  assert.equal(await session.restoreSession(SESSION.userId, now + session.SESSION_IDLE_MS + 1), null, 'idle session expires');
  assert.equal(session.hasSession(), false);

  session.rememberSession(profile, rawKey, now);
  session.forgetSession();
  assert.equal(await session.restoreSession(SESSION.userId, now), null);
  delete globalThis.sessionStorage;
});
