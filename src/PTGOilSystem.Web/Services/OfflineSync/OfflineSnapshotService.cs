using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.OfflineSync;
using PTGOilSystem.Web.Security;
using PTGOilSystem.Web.Services.Mobile;

namespace PTGOilSystem.Web.Services.OfflineSync;

public interface IOfflineSnapshotService
{
    Task<OfflineSnapshotResponse> BuildAsync(ClaimsPrincipal user, CancellationToken ct = default);
}

/// <summary>
/// اطلاعات مرجعِ لازم برای فرم‌های آفلاین (فقط نام/کد رکوردهای فعال) و همان داشبورد موبایل.
/// هر فهرست فقط وقتی فرستاده می‌شود که کاربر بخش مربوط را در وب ببیند. هیچ مانده یا موجودی
/// جدیدی اینجا محاسبه نمی‌شود؛ اعداد داشبورد از <see cref="IMobileDashboardService"/> می‌آیند.
/// </summary>
public sealed class OfflineSnapshotService(
    ApplicationDbContext db,
    IMobileDashboardService dashboard,
    TimeProvider clock,
    ILogger<OfflineSnapshotService> logger,
    ClientModuleProfile? clientModules = null) : IOfflineSnapshotService
{
    private const int Max = OfflineSyncLimits.MaxReferenceRows;

    public async Task<OfflineSnapshotResponse> BuildAsync(ClaimsPrincipal user, CancellationToken ct = default)
    {
        bool Can(params string[] keys) => keys.Any(k => RoleAccessRules.CanAccessNavigation(user, k, clientModules));

        var response = new OfflineSnapshotResponse { AsOfUtc = clock.GetUtcNow().UtcDateTime };
        var reference = response.Reference;

        reference["products"] = await db.Products.AsNoTracking().Where(p => p.IsActive).OrderBy(p => p.Name).Take(Max)
            .Select(p => new OfflineReferenceItem { Id = p.Id, Name = p.NamePersian ?? p.Name, Code = p.Code, Extra = p.UnitOfMeasure })
            .ToListAsync(ct);
        reference["companies"] = await db.Companies.AsNoTracking().Where(c => c.IsActive).OrderByDescending(c => c.IsSystemOwner).ThenBy(c => c.Name).Take(Max)
            .Select(c => new OfflineReferenceItem { Id = c.Id, Name = c.NamePersian ?? c.Name, Extra = c.IsSystemOwner ? "owner" : null })
            .ToListAsync(ct);
        reference["currencies"] = await db.Currencies.AsNoTracking().Where(c => c.IsActive).OrderBy(c => c.Code).Take(Max)
            .Select(c => new OfflineReferenceItem { Id = c.Id, Name = c.NamePersian ?? c.Name, Code = c.Code })
            .ToListAsync(ct);

        if (Can(RoleNavigationKeys.Sales, RoleNavigationKeys.Payments, RoleNavigationKeys.Partners))
        {
            reference["customers"] = await db.Customers.AsNoTracking().Where(c => c.IsActive).OrderBy(c => c.Name).Take(Max)
                .Select(c => new OfflineReferenceItem { Id = c.Id, Name = c.NamePersian ?? c.Name, Code = c.Code })
                .ToListAsync(ct);
        }

        if (Can(RoleNavigationKeys.Payments, RoleNavigationKeys.Partners, RoleNavigationKeys.Contracts))
        {
            reference["suppliers"] = await db.Suppliers.AsNoTracking().Where(s => s.IsActive).OrderBy(s => s.Name).Take(Max)
                .Select(s => new OfflineReferenceItem { Id = s.Id, Name = s.NamePersian ?? s.Name, Code = s.Code })
                .ToListAsync(ct);
        }

        if (Can(RoleNavigationKeys.CashAccounts, RoleNavigationKeys.Payments, RoleNavigationKeys.Operations))
        {
            reference["cashAccounts"] = await db.CashAccounts.AsNoTracking().Where(a => a.IsActive).OrderBy(a => a.Name).Take(Max)
                .Select(a => new OfflineReferenceItem { Id = a.Id, Name = a.Name, Code = a.Code, Extra = a.Currency })
                .ToListAsync(ct);
        }

        if (Can(RoleNavigationKeys.Operations, RoleNavigationKeys.CashAccounts))
        {
            reference["expenseTypes"] = await db.ExpenseTypes.AsNoTracking().Where(e => e.IsActive).OrderBy(e => e.Name).Take(Max)
                .Select(e => new OfflineReferenceItem { Id = e.Id, Name = e.NamePersian ?? e.Name, Code = e.Code })
                .ToListAsync(ct);
        }

        if (Can(RoleNavigationKeys.Inventory, RoleNavigationKeys.Operations, RoleNavigationKeys.Sales))
        {
            reference["tanks"] = await db.StorageTanks.AsNoTracking().Where(t => t.IsActive).OrderBy(t => t.TankCode).Take(Max)
                .Select(t => new OfflineReferenceItem
                {
                    Id = t.Id,
                    Name = (t.DisplayName ?? t.TankCode) + (t.Terminal != null ? " — " + t.Terminal.Name : ""),
                    Code = t.TankCode,
                    Extra = t.ProductId.HasValue ? t.ProductId.Value.ToString() : null
                })
                .ToListAsync(ct);
        }

        if (Can(RoleNavigationKeys.Operations))
        {
            reference["trucks"] = await db.Trucks.AsNoTracking().Where(t => t.IsActive).OrderBy(t => t.PlateNumber).Take(Max)
                .Select(t => new OfflineReferenceItem { Id = t.Id, Name = t.PlateNumber })
                .ToListAsync(ct);
            // فقط فهرست انتخاب؛ اعتبار قرارداد/بار را اکشن ثبتِ موجود در لحظهٔ همگام‌سازی می‌سنجد.
            reference["purchaseContracts"] = await db.Contracts.AsNoTracking()
                .Where(c => c.ContractType == ContractType.Purchase && !c.IsArchived
                    && (c.Status == ContractStatus.Active || c.Status == ContractStatus.Draft))
                .OrderByDescending(c => c.Id).Take(Max)
                .Select(c => new OfflineReferenceItem { Id = c.Id, Name = c.ContractName, Code = c.ContractNumber, Extra = c.ProductId.ToString() })
                .ToListAsync(ct);
            reference["loadings"] = await db.LoadingRegisters.AsNoTracking()
                .Where(l => !l.IsCancelled && !l.IsArchived)
                .OrderByDescending(l => l.LoadingDate).ThenByDescending(l => l.Id).Take(500)
                .Select(l => new OfflineReferenceItem
                {
                    Id = l.Id,
                    Name = (l.Truck != null ? l.Truck.PlateNumber : (l.BillOfLadingNumber ?? ("#" + l.Id))) + " — " + l.LoadedQuantityMt.ToString() + " MT",
                    Code = l.BillOfLadingNumber,
                    Extra = l.ProductId.ToString()
                })
                .ToListAsync(ct);
            reference["drivers"] = await db.Drivers.AsNoTracking().Where(d => d.IsActive).OrderBy(d => d.FullName).Take(Max)
                .Select(d => new OfflineReferenceItem { Id = d.Id, Name = d.FullName })
                .ToListAsync(ct);
        }

        if (Can(RoleNavigationKeys.Dashboard))
        {
            try
            {
                response.Dashboard = await dashboard.BuildAsync(user, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // داشبورد اختیاری است؛ نبودنش نباید دریافت اطلاعات مرجع را متوقف کند.
                logger.LogWarning(ex, "Offline snapshot: dashboard could not be built.");
            }
        }

        return response;
    }
}
