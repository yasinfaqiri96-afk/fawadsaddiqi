// مشعل آفلاین — تشخیص واقعی اتصال. navigator.onLine فقط «حتماً آفلاین» را می‌گوید؛
// «آنلاین» یعنی پاسخ واقعیِ /api/sync/v1/ping از سرور.

export const ConnState = Object.freeze({
  Online: 'ONLINE',
  Offline: 'OFFLINE',
  Syncing: 'SYNCING'
});

export class ConnectivityMonitor {
  constructor(api, { intervalMs = 30000, target = globalThis } = {}) {
    this.api = api;
    this.intervalMs = intervalMs;
    this.target = target;
    this.reachable = false;
    this.authenticated = false;
    this.syncing = false;
    this.lastCheckAt = null;
    this.listeners = new Set();
    this.timer = null;
    this.checking = null;
  }

  get state() {
    if (!this.reachable) return ConnState.Offline;
    return this.syncing ? ConnState.Syncing : ConnState.Online;
  }

  onChange(listener) {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  }

  emit() {
    for (const listener of this.listeners) {
      try {
        listener(this.state, this);
      } catch {
        // شنونده نباید پایش را متوقف کند.
      }
    }
  }

  setSyncing(value) {
    this.syncing = value;
    this.emit();
  }

  async check() {
    if (this.checking) {
      return this.checking;
    }
    this.checking = (async () => {
      const before = `${this.reachable}|${this.authenticated}`;
      if (this.target.navigator && this.target.navigator.onLine === false) {
        this.reachable = false;
      } else {
        const result = await this.api.ping();
        this.reachable = result.ok && !!result.data && result.data.ok === true;
        this.authenticated = this.reachable && result.data.authenticated === true;
      }
      this.lastCheckAt = new Date().toISOString();
      if (before !== `${this.reachable}|${this.authenticated}`) {
        this.emit();
      }
      return this.reachable;
    })();
    try {
      return await this.checking;
    } finally {
      this.checking = null;
    }
  }

  start() {
    const recheck = () => { this.check(); };
    if (this.target.addEventListener) {
      this.target.addEventListener('online', recheck);
      this.target.addEventListener('offline', () => {
        this.reachable = false;
        this.emit();
      });
    }
    this.timer = setInterval(() => {
      const hidden = this.target.document && this.target.document.visibilityState === 'hidden';
      if (!hidden) {
        this.check();
      }
    }, this.intervalMs);
    return this.check();
  }

  stop() {
    if (this.timer) clearInterval(this.timer);
    this.timer = null;
  }
}
