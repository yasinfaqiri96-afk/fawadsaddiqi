// مشعل آفلاین — لایهٔ ذخیره‌سازی محلی.
//
// IndexedDB انتخاب شد (نه SQLite WASM/OPFS): در Safari iPhone، Chrome، Edge و Firefox بومی است،
// تراکنشِ چند-store دارد (ذخیرهٔ اتمی معامله + دستور ارسال)، کتابخانه یا WASM اضافی و هدرهای
// COOP/COEP نمی‌خواهد و با بسته‌شدن برنامه/Restart باقی می‌ماند. ارتقای نسخه هرگز store را حذف نمی‌کند.

export const DB_VERSION = 2;

export const STORES = {
  meta: { keyPath: 'key' },
  outbox: { keyPath: 'clientOperationId', indexes: [['status', 'status'], ['createdAt', 'createdAt']] },
  snapshot: { keyPath: 'key' },
  // نسخهٔ رمزگذاری‌شدهٔ صفحات MVC برای خواندن بدون سرور (نسخهٔ ۲؛ ارتقا هیچ store موجودی را دست نمی‌زند).
  pages: { keyPath: 'key' }
};

export const REGISTRY_DB = 'mashal-offline-registry';
export const REGISTRY_STORES = { profiles: { keyPath: 'id' } };

function promisify(request) {
  return new Promise((resolve, reject) => {
    request.onsuccess = () => resolve(request.result);
    request.onerror = () => reject(request.error);
  });
}

function txDone(tx) {
  return new Promise((resolve, reject) => {
    tx.oncomplete = () => resolve();
    tx.onabort = () => reject(tx.error || new DOMException('Transaction aborted', 'AbortError'));
    tx.onerror = () => reject(tx.error);
  });
}

export class IdbStore {
  constructor(db) {
    this.db = db;
    db.onversionchange = () => db.close();
  }

  static open(name, stores = STORES, version = DB_VERSION) {
    return new Promise((resolve, reject) => {
      const request = indexedDB.open(name, version);
      request.onupgradeneeded = () => {
        const db = request.result;
        for (const [storeName, def] of Object.entries(stores)) {
          if (!db.objectStoreNames.contains(storeName)) {
            const os = db.createObjectStore(storeName, { keyPath: def.keyPath });
            for (const [indexName, keyPath] of def.indexes || []) {
              os.createIndex(indexName, keyPath, { unique: false });
            }
          }
        }
      };
      request.onblocked = () => reject(new Error('پایگاه محلی در زبانهٔ دیگری باز است؛ آن را ببندید.'));
      request.onsuccess = () => resolve(new IdbStore(request.result));
      request.onerror = () => reject(request.error);
    });
  }

  async get(storeName, key) {
    const tx = this.db.transaction(storeName, 'readonly');
    return promisify(tx.objectStore(storeName).get(key));
  }

  async getAll(storeName) {
    const tx = this.db.transaction(storeName, 'readonly');
    return promisify(tx.objectStore(storeName).getAll());
  }

  async count(storeName) {
    const tx = this.db.transaction(storeName, 'readonly');
    return promisify(tx.objectStore(storeName).count());
  }

  /** شمارش با index (مثلاً وضعیت Outbox) بدون خواندن ردیف‌ها. */
  async countBy(storeName, indexName, keys) {
    const tx = this.db.transaction(storeName, 'readonly');
    const index = tx.objectStore(storeName).index(indexName);
    const counts = await Promise.all(keys.map((key) => promisify(index.count(key))));
    return counts.reduce((sum, n) => sum + n, 0);
  }

  /** ردیف‌های یک یا چند مقدار index (مثلاً فقط Pending)، بدون خواندن بقیه. */
  async getAllBy(storeName, indexName, keys) {
    const tx = this.db.transaction(storeName, 'readonly');
    const index = tx.objectStore(storeName).index(indexName);
    return (await Promise.all(keys.map((key) => promisify(index.getAll(key))))).flat();
  }

  /** [مقدار index، کلید اصلی] همهٔ ردیف‌ها بدون خواندن بدنهٔ رمزگذاری‌شده. */
  async indexEntries(storeName, indexName) {
    const tx = this.db.transaction(storeName, 'readonly');
    const index = tx.objectStore(storeName).index(indexName);
    return new Promise((resolve, reject) => {
      const result = [];
      const request = index.openKeyCursor();
      request.onsuccess = () => {
        const cursor = request.result;
        if (!cursor) {
          resolve(result);
          return;
        }
        result.push([cursor.key, cursor.primaryKey]);
        cursor.continue();
      };
      request.onerror = () => reject(request.error);
    });
  }

  async put(storeName, value) {
    return this.atomic([{ store: storeName, type: 'put', value }]);
  }

  async delete(storeName, key) {
    return this.atomic([{ store: storeName, type: 'delete', key }]);
  }

  /** همهٔ نوشتن‌ها در یک تراکنش: یا همه ذخیره می‌شوند یا هیچ‌کدام. */
  async atomic(operations) {
    const names = [...new Set(operations.map((o) => o.store))];
    const tx = this.db.transaction(names, 'readwrite', { durability: 'strict' });
    const done = txDone(tx);
    for (const op of operations) {
      const os = tx.objectStore(op.store);
      if (op.type === 'put') {
        os.put(op.value);
      } else if (op.type === 'delete') {
        os.delete(op.key);
      }
    }
    await done;
  }

  close() {
    this.db.close();
  }
}

/** پیاده‌سازی حافظه‌ای با همان رفتار (برای تست‌های واحد در Node). */
export class MemoryStore {
  constructor(stores = STORES) {
    this.data = new Map(Object.keys(stores).map((name) => [name, new Map()]));
    this.keyPaths = Object.fromEntries(Object.entries(stores).map(([name, def]) => [name, def.keyPath]));
    this.failNextAtomic = null;
  }

  static async open(_name, stores = STORES) {
    return new MemoryStore(stores);
  }

  clone(value) {
    return value === undefined ? undefined : structuredClone(value);
  }

  async get(storeName, key) {
    return this.clone(this.data.get(storeName).get(key));
  }

  async getAll(storeName) {
    return [...this.data.get(storeName).values()].map((v) => this.clone(v));
  }

  async count(storeName) {
    return this.data.get(storeName).size;
  }

  async countBy(storeName, indexName, keys) {
    return (await this.getAllBy(storeName, indexName, keys)).length;
  }

  async getAllBy(storeName, indexName, keys) {
    const field = (STORES[storeName]?.indexes || []).find(([name]) => name === indexName)?.[1] || indexName;
    return [...this.data.get(storeName).values()].filter((v) => keys.includes(v[field])).map((v) => this.clone(v));
  }

  async indexEntries(storeName, indexName) {
    const field = (STORES[storeName]?.indexes || []).find(([name]) => name === indexName)?.[1] || indexName;
    return [...this.data.get(storeName).values()].map((v) => [v[field], v[this.keyPaths[storeName]]]);
  }

  async put(storeName, value) {
    return this.atomic([{ store: storeName, type: 'put', value }]);
  }

  async delete(storeName, key) {
    return this.atomic([{ store: storeName, type: 'delete', key }]);
  }

  async atomic(operations) {
    if (this.failNextAtomic) {
      const error = this.failNextAtomic;
      this.failNextAtomic = null;
      throw error;
    }
    for (const op of operations) {
      if (!this.data.has(op.store)) {
        throw new Error(`Unknown store ${op.store}`);
      }
    }
    for (const op of operations) {
      const map = this.data.get(op.store);
      if (op.type === 'put') {
        map.set(op.value[this.keyPaths[op.store]], this.clone(op.value));
      } else if (op.type === 'delete') {
        map.delete(op.key);
      }
    }
  }

  close() {}
}

export function isQuotaError(error) {
  return !!error && (error.name === 'QuotaExceededError'
    || error.code === 22
    || /quota/i.test(String(error.message || '')));
}

/** نام پایگاه هر کاربر/شرکت جدا است؛ معلومات شرکت‌ها و کاربران هرگز در یک پایگاه نیست. */
export function profileDbName(companyId, userId) {
  return `mashal-offline-c${Number(companyId)}-u${Number(userId)}`;
}
