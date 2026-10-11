using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PTGOilSystem.Web.Configuration;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Security;
using PTGOilSystem.Web.Services.Exceptions;
using PTGOilSystem.Web.Services.Time;

namespace PTGOilSystem.Web.Services.Custody;

public sealed record CustodyConversionInput(int LotId, int StorageTankId, DateTime Date,
    decimal QuantityMt, decimal UnitPriceUsd, string AgreementReference, string? FormToken);

/// <summary>Uses the existing purchase workflow for inventory, cost, payable and GL; no automatic offset.</summary>
public sealed class CustodyConversionService(ApplicationDbContext db, ICustodyService custody,
    ISimplePurchaseWorkflowService purchase, IFormTokenGuard tokens, IAuditService audit,
    IOptions<AccountingOptions> accounting, IStockService stock, ICurrentUserContext? user = null)
{
    public async Task LinkSupplierIdentityAsync(CustodyOwner owner, int supplierId, string evidence,
        bool confirmed, CancellationToken ct = default)
    {
        if (!confirmed || string.IsNullOrWhiteSpace(evidence) || evidence.Trim().Length > 500)
            throw Rule("CUSTODY_IDENTITY_EVIDENCE", "تأیید صریح هویت یکسان و مرجع معتبر آن الزامی است.");
        if (owner.PartyType is not (AccountingPartyType.Customer or AccountingPartyType.Partner))
            throw Rule("CUSTODY_IDENTITY_ROLE", "این پیوند فقط برای مشتری یا شریک موجود است.");
        await using var tx = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct) : null;
        // Serialize confirmation on the existing identity, including before the first link exists.
        if (owner.PartyType == AccountingPartyType.Customer)
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"Customers\" WHERE \"Id\" = {owner.PartyId} FOR UPDATE", ct);
        else
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"Partners\" WHERE \"Id\" = {owner.PartyId} FOR UPDATE", ct);
        // The supplier side is serialized too: two different customer (or partner) records must
        // never both claim the same supplier person.
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"Suppliers\" WHERE \"Id\" = {supplierId} FOR UPDATE", ct);
        var exists = owner.PartyType == AccountingPartyType.Customer
            ? await db.Customers.AnyAsync(x => x.Id == owner.PartyId && x.IsActive, ct)
            : await db.Partners.AnyAsync(x => x.Id == owner.PartyId && x.IsActive, ct);
        if (!exists || !await db.Suppliers.AnyAsync(x => x.Id == supplierId && x.IsActive, ct))
            throw Rule("CUSTODY_IDENTITY_NOT_FOUND", "مالک و نقش فروشنده باید موجود و فعال باشند؛ شخص تازه ساخته نمی‌شود.");
        var existing = await db.CustodySupplierIdentityLinks.SingleOrDefaultAsync(x =>
            owner.PartyType == AccountingPartyType.Customer ? x.CustomerId == owner.PartyId : x.PartnerId == owner.PartyId, ct);
        if (existing is not null)
        {
            if (existing.SupplierId != supplierId)
                throw Rule("CUSTODY_IDENTITY_CONFLICT", "این مالک قبلاً با فروشنده دیگری پیوند معتبر دارد؛ تغییر هویت خودکار مجاز نیست.");
            if (tx is not null) await tx.CommitAsync(ct);
            return;
        }
        if (await db.CustodySupplierIdentityLinks.AnyAsync(x => x.SupplierId == supplierId
                && (owner.PartyType == AccountingPartyType.Customer ? x.CustomerId != null : x.PartnerId != null), ct))
            throw Rule("CUSTODY_IDENTITY_CONFLICT", "این فروشنده قبلاً با شخص دیگری در همین نقش پیوند خورده است؛ دو شخص را یکی نکنید.");
        var link = new CustodySupplierIdentityLink
        {
            CustomerId = owner.PartyType == AccountingPartyType.Customer ? owner.PartyId : null,
            PartnerId = owner.PartyType == AccountingPartyType.Partner ? owner.PartyId : null,
            SupplierId = supplierId, Evidence = evidence.Trim(), CreatedByUserId = user?.UserId
        };
        db.CustodySupplierIdentityLinks.Add(link);
        await db.SaveChangesAsync(ct);
        await audit.LogAsync(nameof(CustodySupplierIdentityLink), link.Id, AuditAction.Insert,
            diff: System.Text.Json.JsonSerializer.Serialize(new { owner, supplierId, link.Evidence }), ct: ct);
        await db.SaveChangesAsync(ct);
        if (tx is not null) await tx.CommitAsync(ct);
    }

    public const string EditBlockedMessage = "این خرید از تبدیل امانی ساخته شده؛ مقدار و قیمت آن فقط با لغو و ثبت دوبارهٔ سند تبدیل اصلاح می‌شود.";

    /// <summary>Contract created by a posted custody conversion; quantity/price edits must go through the conversion.</summary>
    public static Task<bool> IsActiveConversionContractAsync(ApplicationDbContext db, int? contractId, CancellationToken ct = default)
        => contractId is null
            ? Task.FromResult(false)
            : db.CustodyConversions.AnyAsync(x => x.ContractId == contractId && x.Status == CustodyDocumentStatus.Posted, ct);

    public async Task<int?> ResolveSupplierAsync(CustodyOwner owner, CancellationToken ct = default)
        => owner.PartyType switch
        {
            AccountingPartyType.Supplier => owner.PartyId,
            AccountingPartyType.Customer => await db.CustodySupplierIdentityLinks.Where(x => x.CustomerId == owner.PartyId)
                .Select(x => (int?)x.SupplierId).SingleOrDefaultAsync(ct),
            AccountingPartyType.Partner => await db.CustodySupplierIdentityLinks.Where(x => x.PartnerId == owner.PartyId)
                .Select(x => (int?)x.SupplierId).SingleOrDefaultAsync(ct),
            _ => null
        };

    public async Task<int> ConvertAsync(CustodyConversionInput input, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockLotAsync(input.LotId, ct);
        var lot = await db.CustodyLots.AsNoTracking().SingleOrDefaultAsync(x => x.Id == input.LotId, ct)
            ?? throw Rule("CUSTODY_LOT_NOT_FOUND", "بار امانی پیدا نشد.");
        if (input.UnitPriceUsd <= 0 || decimal.Round(input.UnitPriceUsd, 4) != input.UnitPriceUsd)
            throw Rule("CUSTODY_CONVERSION_PRICE", "قیمت توافقی خرید به دالر باید مثبت و حداکثر چهار رقم اعشار باشد.");
        if (string.IsNullOrWhiteSpace(input.AgreementReference) || input.AgreementReference.Trim().Length > 500)
            throw Rule("CUSTODY_CONVERSION_AGREEMENT", "مرجع توافق معتبر خرید الزامی است.");
        var supplierId = await ResolveSupplierAsync(new(lot.OwnerPartyType, lot.OwnerPartyId), ct)
            ?? throw Rule("CUSTODY_SUPPLIER_IDENTITY_REQUIRED", "نقش فروشندهٔ همین مالک با سند هویت یکسان تأیید نشده است؛ شخص تکراری نسازید.");
        if (!await db.Suppliers.AnyAsync(x => x.Id == supplierId && x.IsActive, ct))
            throw Rule("CUSTODY_SUPPLIER_INACTIVE", "فروشندهٔ مالک غیرفعال است.");
        var companyId = await db.Companies.Where(x => x.IsSystemOwner && x.IsActive).Select(x => (int?)x.Id).SingleOrDefaultAsync(ct)
            ?? throw Rule("CUSTODY_COMPANY_REQUIRED", "شرکت مالک سیستم باید مشخص و فعال باشد.");
        var tank = await db.StorageTanks.AsNoTracking().SingleOrDefaultAsync(x => x.Id == input.StorageTankId, ct)
            ?? throw Rule("CUSTODY_TANK", "مخزن پیدا نشد.");
        if (!tank.IsActive || await db.Terminals.AnyAsync(x => x.Id == tank.TerminalId && (!x.IsActive || x.CustodianPartyType != null), ct))
            throw Rule("CUSTODY_CONVERSION_LOCATION", "تبدیل فقط در مخزن فعال تأسیسات خود شرکت مجاز است.");
        if (accounting.Value.Enabled && (!accounting.Value.Pilots.Purchase || !accounting.Value.Pilots.InventoryReceipt))
            throw Rule("CUSTODY_PURCHASE_GL_NOT_READY", "ثبت خرید و رسید در دفتر کل فعال نیست؛ تنظیمات حسابداری را تکمیل کنید.");

        var key = await custody.RecordConversionOutAsync(lot.Id, new(tank.TerminalId, tank.Id),
            input.Date, input.QuantityMt, lot.LotNumber, ct);
        var contract = new Contract
        {
            ContractNumber = "CP-" + key.ToString("N"), ContractName = "خرید قطعی از بار " + lot.LotNumber,
            CompanyId = companyId, ProductId = lot.ProductId, SupplierId = supplierId,
            ContractType = ContractType.Purchase, Status = ContractStatus.Draft, ContractDate = Utc(input.Date.Date),
            QuantityMt = input.QuantityMt, PricingMethod = PricingMethod.Fixed, Currency = "USD",
            UnitPriceInCurrency = input.UnitPriceUsd, UnitPriceUsd = input.UnitPriceUsd, AppliedFxRateToUsd = 1m,
            DestinationStorageTankId = tank.Id, Notes = input.AgreementReference.Trim()
        };
        db.Contracts.Add(contract);
        tokens.Stamp(input.FormToken, "Custody.Convert", nameof(CustodyConversion));
        await db.SaveChangesAsync(ct);
        await purchase.ConfirmAsync(contract.Id, ct);
        // Purchase adapters can deliberately skip unsupported mappings. Never accept a silent GL gap.
        if (accounting.Value.Enabled)
        {
            var loading = await db.LoadingRegisters.SingleAsync(x => x.ContractId == contract.Id, ct);
            var receipt = await db.LoadingReceipts.SingleAsync(x => x.LoadingRegisterId == loading.Id, ct);
            if (!await db.JournalEntries.AnyAsync(x => x.SourceModule == "Purchase" && x.SourceEntityType == nameof(LoadingRegister) && x.SourceEntityId == loading.Id && x.Status == JournalEntryStatus.Posted, ct)
                || !await db.JournalEntries.AnyAsync(x => x.SourceModule == "Purchase" && x.SourceEntityType == nameof(LoadingReceipt) && x.SourceEntityId == receipt.Id && x.Status == JournalEntryStatus.Posted, ct))
                throw Rule("CUSTODY_PURCHASE_GL_NOT_READY", "سند دفتر کل خرید یا رسید کامل ساخته نشد؛ هیچ تبدیل ثبت نگردید.");
        }
        var conversion = new CustodyConversion
        {
            CustodyLotId = lot.Id, ContractId = contract.Id, StockOperationKey = key,
            ConversionDate = Utc(input.Date.Date), QuantityMt = input.QuantityMt,
            AgreementReference = input.AgreementReference.Trim(), CreatedByUserId = user?.UserId
        };
        db.CustodyConversions.Add(conversion);
        await db.SaveChangesAsync(ct);
        await audit.LogAsync(nameof(CustodyConversion), conversion.Id, AuditAction.Insert,
            diff: System.Text.Json.JsonSerializer.Serialize(new { lot.LotNumber, conversion.ContractId,
                conversion.QuantityMt, input.UnitPriceUsd, conversion.AgreementReference, key }), ct: ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return conversion.Id;
    }

    public async Task CancelAsync(int id, string reason, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length > 500)
            throw Rule("CUSTODY_CONVERSION_CANCEL_REASON", "دلیل لغو الزامی و حداکثر ۵۰۰ حرف است.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var lotId = await db.CustodyConversions.Where(x => x.Id == id).Select(x => (int?)x.CustodyLotId).SingleOrDefaultAsync(ct)
            ?? throw Rule("CUSTODY_CONVERSION_NOT_FOUND", "تبدیل پیدا نشد.");
        await LockLotAsync(lotId, ct);
        var conversion = await db.CustodyConversions.SingleAsync(x => x.Id == id, ct);
        await db.Entry(conversion).ReloadAsync(ct);
        if (conversion.Status != CustodyDocumentStatus.Posted)
            throw Rule("CUSTODY_CONVERSION_ALREADY_CANCELLED", "این تبدیل قبلاً لغو شده است.");
        var hasJournal = await db.JournalEntries.AnyAsync(x => x.SourceModule == "Purchase"
            && x.SourceEntityType == nameof(LoadingRegister)
            && db.LoadingRegisters.Any(l => l.Id == x.SourceEntityId && l.ContractId == conversion.ContractId), ct);
        if (hasJournal && (!accounting.Value.Enabled || !accounting.Value.Pilots.Purchase || !accounting.Value.Pilots.InventoryReceipt))
            throw Rule("CUSTODY_PURCHASE_GL_NOT_READY", "این خرید سند دفتر کل دارد؛ لغو بدون برگشت همان اسناد مجاز نیست.");
        if (!hasJournal && accounting.Value.Enabled)
            throw Rule("CUSTODY_PURCHASE_RECONCILIATION_REQUIRED", "خرید تاریخی سند دفتر کل ندارد؛ نخست از مسیر مصوب خرید تطبیق شود.");
        if (await db.SupplierPaymentAllocations.AnyAsync(x => x.ContractId == conversion.ContractId && x.Status == SupplierPaymentAllocationStatus.Active, ct)
            || await db.PaymentTransactions.AnyAsync(x => x.ContractId == conversion.ContractId, ct)
            || await db.ContractBalanceTransfers.AnyAsync(x => !x.IsCancelled
                && (x.FromContractId == conversion.ContractId || x.ToContractId == conversion.ContractId), ct)
            || await db.SupplierBalanceTransfers.AnyAsync(x => x.Status == SupplierBalanceTransferStatus.Active
                && (x.ContractId == conversion.ContractId || x.Sources.Any(s => db.LedgerEntries.Any(l =>
                    l.Id == s.LedgerEntryId && l.ContractId == conversion.ContractId))), ct))
            throw Rule("CUSTODY_CONVERSION_PAYMENT_DEPENDENCY", "این خرید پرداخت وابسته دارد؛ نخست پرداخت و تخصیص آن را بررسی و برگشت کنید.");
        var inbound = await db.InventoryMovements.AsNoTracking().SingleAsync(x =>
            x.ContractId == conversion.ContractId && x.Direction == MovementDirection.In && x.ReversalOfInventoryMovementId == null, ct);
        await stock.AcquireStockMutationLockAsync(inbound, ct);
        // Contract-scoped stock alone excludes ordinary withdrawals with no purchase contract.
        // Check the complete physical company stock timeline as well, under the same tank lock.
        var probe = new InventoryMovement
        {
            ProductId = inbound.ProductId, TerminalId = inbound.TerminalId, StorageTankId = inbound.StorageTankId,
            Direction = MovementDirection.Out, QuantityMt = conversion.QuantityMt,
            MovementDate = AfghanistanBusinessClock.SystemToday
        };
        await stock.EnsureSufficientStockForMovementAsync(probe, ct);
        await stock.EnsureMovementDoesNotCauseFutureNegativeStockAsync(probe, ct);
        conversion.Status = CustodyDocumentStatus.Cancelled;
        conversion.CancelledAtUtc = DateTime.UtcNow;
        conversion.CancellationReason = reason.Trim();
        conversion.UpdatedByUserId = user?.UserId;
        // Allows the canonical receipt cancellation only within this transaction; failure rolls back both sides.
        await db.SaveChangesAsync(ct);
        await purchase.ReverseAsync(conversion.ContractId, reason, user?.UserId, ct);
        await custody.ReverseConversionOutAsync(conversion.StockOperationKey, AfghanistanBusinessClock.SystemToday, reason, ct);
        await audit.LogAsync(nameof(CustodyConversion), id, AuditAction.Reverse, diff: reason.Trim(), ct: ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    private async Task LockLotAsync(int id, CancellationToken ct)
    {
        if (db.Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL")
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"CustodyLots\" WHERE \"Id\" = {id} FOR UPDATE", ct);
    }
    private static DateTime Utc(DateTime date) => DateTime.SpecifyKind(date, DateTimeKind.Utc);
    private static BusinessRuleException Rule(string code, string message) => new(code, message);
}
