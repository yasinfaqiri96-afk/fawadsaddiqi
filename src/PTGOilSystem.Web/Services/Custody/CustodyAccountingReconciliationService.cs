using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services.Accounting;
using PTGOilSystem.Web.Services.Exceptions;

namespace PTGOilSystem.Web.Services.Custody;

public sealed record CustodyReconciliationRow(string EventId, string Reference, DateTime Date,
    decimal AmountUsd, decimal CommissionUsd, bool AlreadyPosted, string? Blocker);

public sealed record CustodyReconciliationPreview(CustodyOwner Owner, string Fingerprint,
    IReadOnlyList<CustodyReconciliationRow> Rows, string? Blocker)
{
    public int MissingCount => Rows.Count(r => !r.AlreadyPosted);
    public bool CanApply => Blocker is null && Rows.All(r => r.Blocker is null) && MissingCount > 0;
}

/// <summary>
/// تطبیق فقط اسناد واقعیِ فعال، با تاریخ و مبلغ خودشان؛ بدون Ledger/Payment/Stock تازه.
/// قفلِ حساب مالک با مسیرهای عملیاتی مشترک است. تغییر بعد از Preview نیازمند Preview تازه است.
/// </summary>
public sealed class CustodyAccountingReconciliationService(ApplicationDbContext db,
    ICustodyAccountingAdapter accounting, ISystemCompanyProvider company, IPeriodGuard periods, IAuditService audit)
{
    public static async Task<bool> HasMissingJournalsAsync(ApplicationDbContext db, CustodyOwner owner, CancellationToken ct)
    {
        var saleIds = await db.CustodySales.AsNoTracking()
            .Where(s => s.OwnerPartyType == owner.PartyType && s.OwnerPartyId == owner.PartyId && s.Status == CustodyDocumentStatus.Posted)
            .Select(s => s.Id).ToListAsync(ct);
        var financeIds = await db.CustodyFinanceEntries.AsNoTracking()
            .Where(s => s.OwnerPartyType == owner.PartyType && s.OwnerPartyId == owner.PartyId && s.Status == CustodyDocumentStatus.Posted)
            .Select(s => s.Id).ToListAsync(ct);
        var returnIds = await db.CustodySaleReturns.AsNoTracking()
            .Where(s => s.OwnerPartyType == owner.PartyType && s.OwnerPartyId == owner.PartyId && s.Status == CustodyDocumentStatus.Posted)
            .Select(s => s.Id).ToListAsync(ct);
        var events = saleIds.Select(CustodyAccountingAdapter.SaleEventId)
            .Concat(financeIds.Select(CustodyAccountingAdapter.FinanceEventId))
            .Concat(returnIds.Select(CustodyAccountingAdapter.ReturnEventId)).ToArray();
        if (events.Length == 0) return false;
        var posted = await db.JournalEntries.AsNoTracking().CountAsync(j => j.SourceModule == CustodyAccountingAdapter.SourceModule
            && events.Contains(j.SourceEventId!) && j.Status == JournalEntryStatus.Posted, ct);
        return posted != events.Length;
    }

    public async Task<CustodyReconciliationPreview> PreviewAsync(CustodyOwner owner, CancellationToken ct = default)
    {
        var companyId = await company.GetOwnerCompanyIdAsync(ct);
        var sales = await db.CustodySales.AsNoTracking()
            .Where(s => s.OwnerPartyType == owner.PartyType && s.OwnerPartyId == owner.PartyId && s.Status == CustodyDocumentStatus.Posted)
            .OrderBy(s => s.Id).ToListAsync(ct);
        var finance = await db.CustodyFinanceEntries.AsNoTracking()
            .Where(e => e.OwnerPartyType == owner.PartyType && e.OwnerPartyId == owner.PartyId && e.Status == CustodyDocumentStatus.Posted)
            .OrderBy(e => e.Id).ToListAsync(ct);
        var returns = await db.CustodySaleReturns.AsNoTracking()
            .Where(r => r.OwnerPartyType == owner.PartyType && r.OwnerPartyId == owner.PartyId && r.Status == CustodyDocumentStatus.Posted)
            .OrderBy(r => r.Id).ToListAsync(ct);
        var events = sales.Select(s => CustodyAccountingAdapter.SaleEventId(s.Id))
            .Concat(finance.Select(e => CustodyAccountingAdapter.FinanceEventId(e.Id)))
            .Concat(returns.Select(r => CustodyAccountingAdapter.ReturnEventId(r.Id))).ToArray();
        var journals = await db.JournalEntries.AsNoTracking()
            .Where(j => j.CompanyId == companyId && j.SourceModule == CustodyAccountingAdapter.SourceModule && events.Contains(j.SourceEventId!))
            .OrderBy(j => j.Id).Select(j => new { j.Id, j.SourceEventId, j.Status }).ToListAsync(ct);
        var settings = await db.AccountingSettings.AsNoTracking().SingleOrDefaultAsync(s => s.CompanyId == companyId, ct);
        string? blocker = !accounting.IsEnabled ? "دفتر کل خاموش است." :
            !accounting.IsPilotEnabled ? "ثبت دفتر کل امانی فعال نیست." :
            settings is null || settings.CustodyFundsPayableAccountId is null || settings.CustodyPendingCollectionAccountId is null
                || settings.CustodyCommissionRevenueAccountId is null || settings.CustodyServiceRevenueAccountId is null
                ? "نقشه حساب‌های امانی تکمیل نشده است." :
            settings.FunctionalCurrencyCode != "USD" ? "ارز پایه دفتر کل برای امانی پشتیبانی نمی‌شود." :
            !CustodySaleService.FinancialOwnerTypes.Contains(owner.PartyType) ? "نوع مالک برای حساب مالی پشتیبانی نمی‌شود." : null;
        var rows = new List<CustodyReconciliationRow>();
        async Task Add(string eventId, string reference, DateTime date, decimal amount, decimal commissionAmount)
        {
            var matches = journals.Where(j => j.SourceEventId == eventId).ToList();
            var posted = matches.Count == 1 && matches[0].Status == JournalEntryStatus.Posted;
            string? rowBlocker = matches.Count > 0 && !posted ? "ژورنال موجود نیازمند بررسی است؛ ثبت تازه مجاز نیست." : null;
            if (!posted && rowBlocker is null)
            {
                try { await periods.EnsurePostingAllowedAsync(companyId, date, ct); }
                catch (AccountingValidationException) { rowBlocker = "دوره مالی این سند برای ثبت باز نیست."; }
            }
            rows.Add(new(eventId, reference, date, amount, commissionAmount, posted, rowBlocker));
        }
        foreach (var sale in sales)
            await Add(CustodyAccountingAdapter.SaleEventId(sale.Id), sale.SaleNumber, sale.SaleDate, sale.AmountUsd, sale.CommissionRecognizedAtSaleUsd);
        foreach (var entry in finance)
        {
            await Add(CustodyAccountingAdapter.FinanceEventId(entry.Id), $"AF-{entry.Id:D5} · {Models.Custody.CustodyFinanceForm.Title(entry.Kind)}", entry.EntryDate, entry.AmountUsd, entry.CommissionUsd);
            if (entry.CustodySaleId.HasValue && sales.All(s => s.Id != entry.CustodySaleId.Value))
                blocker = "سند مالی به فروش فعالِ همین مالک وصل نیست؛ بررسی لازم است.";
            if (entry.Kind is CustodyFinanceKind.ProviderCharge or CustodyFinanceKind.ProviderPayment)
            {
                var chargeTypeId = entry.Kind == CustodyFinanceKind.ProviderCharge ? entry.ExpenseTypeId
                    : finance.SingleOrDefault(c => c.Id == entry.SettlesFinanceEntryId)?.ExpenseTypeId;
                var payableKind = chargeTypeId is null ? null : await db.ExpenseTypes.AsNoTracking()
                    .Where(t => t.Id == chargeTypeId).Select(t => t.PayableAccountKind).SingleOrDefaultAsync(ct);
                if (payableKind is null)
                    blocker = "برای نوعِ مصرفِ نسیهٔ خدمات‌دهنده «حساب پرداختنی» تعیین نشده است؛ پیش از تطبیق آن را در انواع مصرف تعیین کنید.";
            }
        }
        foreach (var saleReturn in returns)
        {
            await Add(CustodyAccountingAdapter.ReturnEventId(saleReturn.Id), $"{saleReturn.ReturnNumber} · برگشت فروش", saleReturn.ReturnDate,
                saleReturn.AmountUsd, saleReturn.RecognizedCommissionReversedUsd);
            if (sales.All(s => s.Id != saleReturn.CustodySaleId))
                blocker = "برگشت فروش به فروش فعالِ همین مالک وصل نیست؛ بررسی لازم است.";
        }
        // مالی، منبع، نقشه حساب‌ها و وضعیت دوره/ژورنال در Preview مقید می‌شوند.
        var snapshot = JsonSerializer.Serialize(new { owner, companyId, sales, finance, returns, journals, settings, rows, blocker });
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(snapshot)));
        return new(owner, fingerprint, rows, blocker);
    }

    public async Task<int> ApplyAsync(CustodyOwner owner, string fingerprint, bool confirmed, string reason, CancellationToken ct = default)
    {
        if (!confirmed || string.IsNullOrWhiteSpace(reason))
            throw Rule("CUSTODY_RECONCILIATION_CONFIRMATION", "تأیید صریح پیش‌نمایش و دلیل تطبیق الزامی است.");
        await using var tx = db.Database.IsRelational() && db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(ct) : null;
        if (db.Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL")
        {
            var key = 9275 * 10 + (int)owner.PartyType;
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({key}, {owner.PartyId})", ct);
        }
        var preview = await PreviewAsync(owner, ct);
        if (!string.Equals(preview.Fingerprint, fingerprint, StringComparison.Ordinal))
            throw Rule("CUSTODY_RECONCILIATION_STALE", "اسناد یا تنظیمات پس از پیش‌نمایش تغییر کرده‌اند؛ پیش‌نمایش تازه را بررسی کنید.");
        if (!preview.CanApply)
            throw Rule("CUSTODY_RECONCILIATION_BLOCKED", preview.Blocker ?? "ثبت تطبیق مجاز نیست؛ وضعیت اسناد و دوره‌ها را بررسی کنید.");
        var saleIds = preview.Rows.Where(r => !r.AlreadyPosted && r.EventId.Contains(":Sale:")).Select(r => r.EventId).ToHashSet();
        var sales = await db.CustodySales.AsNoTracking()
            .Where(s => s.OwnerPartyType == owner.PartyType && s.OwnerPartyId == owner.PartyId && s.Status == CustodyDocumentStatus.Posted)
            .OrderBy(s => s.Id).ToListAsync(ct);
        var count = 0;
        foreach (var sale in sales.Where(s => saleIds.Contains(CustodyAccountingAdapter.SaleEventId(s.Id))))
        {
            RequirePosted(await accounting.TryPostSaleAsync(sale, ct));
            count++;
        }
        var financeIds = preview.Rows.Where(r => !r.AlreadyPosted && r.EventId.Contains(":Finance:")).Select(r => r.EventId).ToHashSet();
        var finance = await db.CustodyFinanceEntries.AsNoTracking()
            .Where(e => e.OwnerPartyType == owner.PartyType && e.OwnerPartyId == owner.PartyId && e.Status == CustodyDocumentStatus.Posted)
            .OrderBy(e => e.EntryDate).ThenBy(e => e.Id).ToListAsync(ct);
        foreach (var entry in finance.Where(e => financeIds.Contains(CustodyAccountingAdapter.FinanceEventId(e.Id))))
        {
            RequirePosted(await accounting.TryPostFinanceAsync(entry, sales.SingleOrDefault(s => s.Id == entry.CustodySaleId), ct));
            count++;
        }
        var returnEvents = preview.Rows.Where(r => !r.AlreadyPosted && r.EventId.Contains(":Return:")).Select(r => r.EventId).ToHashSet();
        var returns = await db.CustodySaleReturns.AsNoTracking()
            .Where(r => r.OwnerPartyType == owner.PartyType && r.OwnerPartyId == owner.PartyId && r.Status == CustodyDocumentStatus.Posted)
            .OrderBy(r => r.Id).ToListAsync(ct);
        foreach (var saleReturn in returns.Where(r => returnEvents.Contains(CustodyAccountingAdapter.ReturnEventId(r.Id))))
        {
            RequirePosted(await accounting.TryPostReturnAsync(saleReturn, sales.Single(s => s.Id == saleReturn.CustodySaleId), ct));
            count++;
        }
        await audit.LogAsync("CustodyReconciliation", owner.PartyId, AuditAction.Insert,
            diff: JsonSerializer.Serialize(new { owner, fingerprint, Reason = reason.Trim(), JournalCount = count }), ct: ct);
        await db.SaveChangesAsync(ct);
        if (tx is not null) await tx.CommitAsync(ct);
        return count;
    }

    private static void RequirePosted(CustodyAccountingResult result)
    {
        if (result.Status != CustodyPostingStatus.Posted)
            throw Rule("CUSTODY_RECONCILIATION_NOT_POSTED", "ژورنال تطبیق ثبت نشد؛ حساب‌های امانی و فعال‌بودن دفتر کل را بررسی کنید.");
    }
    private static BusinessRuleException Rule(string code, string message) => new(code, message);
}
