// مشعل آفلاین — کلاینت /api/sync/v1 (هم‌دامنه، کوکی وب؛ بدون ذخیره در Cache مرورگر).

export const API_BASE = '/api/sync/v1';

export class ApiClient {
  constructor({ base = API_BASE, fetchImpl = globalThis.fetch.bind(globalThis), timeoutMs = 20000 } = {}) {
    this.base = base;
    this.fetch = fetchImpl;
    this.timeoutMs = timeoutMs;
    this.auth = null;
  }

  /** احراز هویت دستگاه (Bearer) پس از ورود با رمز محلی؛ null یعنی فقط کوکی وب. */
  setAuth(auth) {
    this.auth = auth;
  }

  async request(method, path, body, timeoutMs = this.timeoutMs, { base = this.base, useAuth = true } = {}) {
    const authHeader = useAuth && this.auth ? await this.auth.header().catch(() => null) : null;
    let result = await this.send(method, base + path, body, timeoutMs, authHeader);
    if (result.status === 401 && authHeader && this.auth) {
      // توکن دسترسی رد شد: یک‌بار تازه‌سازی، وگرنه کوکی وب.
      const refreshed = await this.auth.refresh().catch(() => null);
      result = await this.send(method, base + path, body, timeoutMs, refreshed ? `Bearer ${refreshed.accessToken}` : null);
    }
    return result;
  }

  /**
   * @returns {Promise<{ ok: boolean, status: number, data: any, networkError: boolean }>}
   * networkError=true یعنی پاسخ معلوم نیست (قطع شبکه/timeout)؛ عملیات باید با همان شناسه دوباره بررسی شود.
   */
  async send(method, url, body, timeoutMs, authHeader) {
    const controller = typeof AbortController === 'function' ? new AbortController() : null;
    const timer = controller ? setTimeout(() => controller.abort(), timeoutMs) : null;
    try {
      const response = await this.fetch(url, {
        method,
        credentials: 'same-origin',
        cache: 'no-store',
        headers: {
          Accept: 'application/json',
          'X-PTG-Offline': '1',
          ...(authHeader ? { Authorization: authHeader } : {}),
          ...(body === undefined ? {} : { 'Content-Type': 'application/json' })
        },
        body: body === undefined ? undefined : JSON.stringify(body),
        signal: controller ? controller.signal : undefined
      });
      let data = null;
      const type = response.headers.get('Content-Type') || '';
      if (type.includes('json')) {
        try {
          data = await response.json();
        } catch {
          data = null;
        }
      }
      return { ok: response.ok, status: response.status, data, networkError: false };
    } catch {
      return { ok: false, status: 0, data: null, networkError: true };
    } finally {
      if (timer) clearTimeout(timer);
    }
  }

  ping() {
    return this.request('GET', '/ping', undefined, 6000);
  }

  session() {
    return this.request('GET', '/session');
  }

  snapshot() {
    return this.request('GET', '/snapshot', undefined, 60000);
  }

  registerDevice(deviceId, deviceName) {
    return this.request('POST', '/devices', { deviceId, deviceName });
  }

  push(deviceId, operations) {
    return this.request('POST', '/operations', { deviceId, operations }, 120000);
  }

  /** فقط با کوکی ورود عادی: نشست تازه‌سازیِ مخصوص این دستگاه. */
  deviceSession(deviceId, deviceName) {
    return this.request('POST', '/device-session', { deviceId, deviceName }, this.timeoutMs, { useAuth: false });
  }

  refreshDeviceSession(refreshToken, deviceId) {
    return this.request('POST', '/auth/refresh', { refreshToken, deviceId }, this.timeoutMs,
      { base: '/api/mobile/v1', useAuth: false });
  }

  statuses(ids) {
    return this.request('POST', '/operations/status', { ids });
  }
}
