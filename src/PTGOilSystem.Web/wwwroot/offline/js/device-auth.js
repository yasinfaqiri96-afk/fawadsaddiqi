// مشعل آفلاین — احراز هویت دستگاه مورداعتماد.
//
// کوکی ورود وب با بستن کامل برنامه از بین می‌رود. پس از فعال‌سازی، سرور یک نشست تازه‌سازیِ مخصوص همین دستگاه
// (همان زیرساخت توکن موبایل: چرخش، تشخیص استفادهٔ دوباره، ابطال، بررسی فعال‌بودن کاربر) صادر می‌کند. توکن‌ها فقط
// رمزگذاری‌شده با کلید دادهٔ همین پروفایل ذخیره می‌شوند و فقط پس از ورود با رمز محلی در دسترس‌اند.
// اگر تازه‌سازی رد شود (کاربر غیرفعال، نشست باطل، ۳۰ روز بی‌استفاده)، توکن پاک و به ورود عادی برگردانده می‌شود.

const META_KEY = 'deviceAuth';
const EARLY_REFRESH_MS = 60_000;

export class DeviceAuth {
  constructor({ store, sealer, deviceId, refreshImpl, clock = () => Date.now() }) {
    this.store = store;
    this.sealer = sealer;
    this.deviceId = deviceId;
    this.refreshImpl = refreshImpl;
    this.clock = clock;
    this.cache = undefined;
    this.refreshing = null;
  }

  async load() {
    if (this.cache !== undefined) return this.cache;
    const row = await this.store.get('meta', META_KEY);
    this.cache = row?.sealed ? await this.sealer.open(row.sealed) : null;
    return this.cache;
  }

  async has() {
    const tokens = await this.load();
    return !!tokens && new Date(tokens.refreshTokenExpiresAtUtc).getTime() > this.clock();
  }

  async save(pair) {
    const tokens = {
      accessToken: pair.accessToken,
      accessTokenExpiresAtUtc: pair.accessTokenExpiresAtUtc,
      refreshToken: pair.refreshToken,
      refreshTokenExpiresAtUtc: pair.refreshTokenExpiresAtUtc
    };
    await this.store.put('meta', { key: META_KEY, sealed: await this.sealer.seal(tokens) });
    this.cache = tokens;
  }

  async clear() {
    await this.store.delete('meta', META_KEY);
    this.cache = null;
  }

  /** هدر Authorization معتبر، یا null (یعنی کوکی وب استفاده شود). */
  async header() {
    let tokens = await this.load();
    if (!tokens) return null;
    if (new Date(tokens.accessTokenExpiresAtUtc).getTime() - EARLY_REFRESH_MS <= this.clock()) {
      tokens = await this.refresh();
    }
    return tokens ? `Bearer ${tokens.accessToken}` : null;
  }

  /** تازه‌سازی تک‌پرواز: چند درخواست هم‌زمان یک توکن را دوبار مصرف نمی‌کنند (چرخش امن). */
  refresh() {
    if (!this.refreshing) {
      this.refreshing = (async () => {
        const tokens = await this.load();
        if (!tokens) return null;
        const result = await this.refreshImpl(tokens.refreshToken, this.deviceId);
        if (result.ok && result.data?.accessToken) {
          await this.save(result.data);
          return this.cache;
        }
        if (result.networkError) {
          return null; // پاسخ نامعلوم؛ توکن حفظ می‌شود و دفعهٔ بعد دوباره تلاش می‌شود.
        }
        await this.clear(); // باطل/منقضی/کاربر غیرفعال: ورود عادی لازم است.
        return null;
      })().finally(() => {
        this.refreshing = null;
      });
    }
    return this.refreshing;
  }
}
