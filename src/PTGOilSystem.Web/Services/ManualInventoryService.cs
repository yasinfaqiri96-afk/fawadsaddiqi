using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PTGOilSystem.Web.Configuration;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Inventory;
using PTGOilSystem.Web.Services.Accounting;
using PTGOilSystem.Web.Services.Audit;
using PTGOilSystem.Web.Services.Exceptions;

namespace PTGOilSystem.Web.Services;

/// <summary>
/// Manual documents use the same movement writer, valuation and journal posting as
/// operational documents. Transfer halves and the consumed form token commit together.
/// Existing Transfer/Adjustment rows retain their historical signed meaning.
/// </summary>
public sealed class ManualInventoryService(
    ApplicationDbContext db,
    IStockService stock,
    IInventoryMovementWriter movements,
    IAuditService audit,
    IFormTokenGuard tokens,
    IOptions<AccountingOptions>? options = null,
    IInventoryValuationService? valuation = null,
    IAccountingPostingService? posting = null,
    ISystemCompanyProvider? systemCompany = null)
{
    public async Task PostAsync(InventoryMovementCreateViewModel input, string? formToken,
        CancellationToken ct = default)
    {
        if (!Enum.IsDefined(input.Direction) || input.QuantityMt <= 0m)
            throw Rule("MANUAL_INVENTORY_QUANTITY", "نوع ثبت و مقدار موجودی را درست وارد کنید.");
        if (input.Direction is MovementDirection.Adjustment or MovementDirection.Transfer
            && string.IsNullOrWhiteSpace(input.Reason))
            throw Rule("MANUAL_INVENTORY_REASON", "دلیل اصلاح یا انتقال موجودی را بنویسید.");
        if (string.IsNullOrWhiteSpace(formToken))
            throw Rule("MANUAL_INVENTORY_TOKEN", "فورم را دوباره باز کنید و سند را ثبت کنید.");

        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(ct) : null;
        if (await db.ProcessedFormTokens.AsNoTracking().AnyAsync(t => t.Token == formToken.Trim(), ct))
            return;

        var transfer = input.Direction == MovementDirection.Transfer;
        if (transfer && (!input.DestinationTerminalId.HasValue
            || (input.TerminalId == input.DestinationTerminalId && input.StorageTankId == input.DestinationStorageTankId)))
            throw Rule("MANUAL_INVENTORY_DESTINATION", "مقصد انتقال را انتخاب کنید؛ مبدأ و مقصد باید فرق داشته باشند.");
        await ValidateLocationAsync(input.ProductId, input.TerminalId, input.StorageTankId, ct);
        if (transfer)
            await ValidateLocationAsync(input.ProductId, input.DestinationTerminalId!.Value, input.DestinationStorageTankId, ct);
        if (input.ContractId.HasValue && !await db.Contracts.AnyAsync(c => c.Id == input.ContractId
            && c.ContractType == ContractType.Purchase && c.ProductId == input.ProductId, ct))
            throw Rule("MANUAL_INVENTORY_CONTRACT", "قرارداد منبع باید قرارداد خرید همین جنس باشد.");

        // Stable lock order for simultaneous opposite transfers. Full guards below
        // check dated availability and every later point in the source timeline.
        if (transfer)
        {
            foreach (var location in new[] { (input.TerminalId, input.StorageTankId),
                (input.DestinationTerminalId!.Value, input.DestinationStorageTankId) }
                .OrderBy(x => x.Item1).ThenBy(x => x.Item2))
                await stock.AcquireStockMutationLockAsync(new InventoryMovement
                { ProductId = input.ProductId, TerminalId = location.Item1, StorageTankId = location.Item2 }, ct);
        }

        var inbound = input.Direction == MovementDirection.In
            || (input.Direction == MovementDirection.Adjustment && !input.NegativeAdjustment);
        var reference = string.IsNullOrWhiteSpace(input.ReferenceDocument)
            ? $"MANUAL-INVENTORY:{formToken.Trim()}" : input.ReferenceDocument.Trim();
        var kind = transfer ? "انتقال موجودی" : input.Direction == MovementDirection.Adjustment
            ? input.NegativeAdjustment ? "اصلاح موجودی: کم‌شدن" : "اصلاح موجودی: اضافه‌شدن"
            : inbound ? "ورود موجودی" : "خروج موجودی";
        var notes = string.Join(" | ", new[] { kind, input.Reason?.Trim(), input.Notes?.Trim() }
            .Where(x => !string.IsNullOrWhiteSpace(x)));
        if (notes.Length > 1000)
            throw Rule("MANUAL_INVENTORY_NOTES", "مجموع دلیل و یادداشت نباید بیشتر از ۱۰۰۰ حرف باشد.");
        var date = DateTime.SpecifyKind(input.MovementDate.Date, DateTimeKind.Utc);
        var request = new InventoryMovementRequest
        {
            ProductId = input.ProductId, ContractId = input.ContractId,
            TerminalId = input.TerminalId, StorageTankId = input.StorageTankId,
            MovementDate = date, QuantityMt = input.QuantityMt,
            ReferenceDocument = reference, Notes = notes
        };
        // Token uniqueness is per submission, not per human document number:
        // multi-line documents and valid repeated reference numbers remain valid.
        tokens.Stamp(formToken, "Inventory.Create", nameof(InventoryMovement));
        var first = inbound
            ? input.Direction == MovementDirection.Adjustment
                ? await movements.PostAdjustmentAsync(request, ct)
                : await movements.PostInboundAsync(request, ct)
            : await movements.PostOutboundAsync(request, StockGuard.Full | StockGuard.StrictTimeline, ct);
        InventoryMovement? destination = null;
        if (transfer)
            destination = await movements.PostInboundAsync(request with
            {
                TerminalId = input.DestinationTerminalId!.Value,
                StorageTankId = input.DestinationStorageTankId
            }, ct);

        if (options?.Value.Enabled == true)
            await PostAccountingAsync(input, first, destination, inbound, ct);
        foreach (var row in new[] { first, destination }.OfType<InventoryMovement>())
            await audit.LogAndSaveAsync(nameof(InventoryMovement), row.Id, AuditAction.Insert,
                diff: AuditDiffFormatter.ForCreate(("ProductId", row.ProductId), ("ContractId", row.ContractId),
                    ("TerminalId", row.TerminalId), ("StorageTankId", row.StorageTankId),
                    ("Direction", row.Direction), ("QuantityMt", row.QuantityMt),
                    ("MovementDate", row.MovementDate), ("ReferenceDocument", row.ReferenceDocument),
                    ("Notes", row.Notes), ("PairedMovementId", destination is null ? (int?)null : row == first ? destination.Id : first.Id)));
        if (transaction is not null) await transaction.CommitAsync(ct);
    }

    private async Task ValidateLocationAsync(int productId, int terminalId, int? tankId, CancellationToken ct)
    {
        if (!await db.Products.AnyAsync(p => p.Id == productId && p.IsActive, ct)
            || !await db.Terminals.AnyAsync(t => t.Id == terminalId && t.IsActive, ct))
            throw Rule("MANUAL_INVENTORY_LOCATION", "جنس یا ترمینال فعال پیدا نشد.");
        if (tankId.HasValue && !await db.StorageTanks.AnyAsync(t => t.Id == tankId
            && t.IsActive && t.TerminalId == terminalId && (!t.ProductId.HasValue || t.ProductId == productId), ct))
            throw Rule("MANUAL_INVENTORY_TANK", "مخزن باید فعال و مربوط به همین ترمینال و جنس باشد.");
    }

    private async Task PostAccountingAsync(InventoryMovementCreateViewModel input, InventoryMovement source,
        InventoryMovement? destination, bool inbound, CancellationToken ct)
    {
        if (valuation is null || posting is null || systemCompany is null)
            throw Rule("MANUAL_INVENTORY_ACCOUNTING", "خدمات حسابداری موجودی آماده نیست؛ سند ثبت نشد.");
        var companyId = input.ContractId.HasValue
            ? await db.Contracts.Where(c => c.Id == input.ContractId).Select(c => c.CompanyId).SingleAsync(ct)
            : await systemCompany.GetOwnerCompanyIdAsync(ct);
        var settings = await db.AccountingSettings.AsNoTracking().SingleOrDefaultAsync(s => s.CompanyId == companyId, ct)
            ?? throw Rule("MANUAL_INVENTORY_ACCOUNTING", "تنظیمات حسابداری شرکت تکمیل نشده است.");
        if (settings.FunctionalCurrencyCode != "USD")
            throw Rule("MANUAL_INVENTORY_CURRENCY", "ارزش موجودی باید از حسابداری دالری خوانده شود.");
        if (destination is null && (!input.OffsetAccountId.HasValue || input.OffsetAccountId == settings.InventoryAccountId
            || !await db.Accounts.AnyAsync(a => a.Id == input.OffsetAccountId && a.CompanyId == companyId && a.IsActive
                && (a.AccountType == AccountType.Revenue || a.AccountType == AccountType.Expense || a.AccountType == AccountType.Equity), ct)))
            throw Rule("MANUAL_INVENTORY_OFFSET", "حساب مقابل موجودی را از حساب‌های فعال همین شرکت انتخاب کنید.");
        decimal value;
        if (inbound)
        {
            value = input.ValueUsd is > 0m ? decimal.Round(input.ValueUsd.Value, 4)
                : throw Rule("MANUAL_INVENTORY_VALUE", "ارزش مجموع ورودی را به دالر با سند معتبر وارد کنید.");
            await valuation.ApplyReceiptAsync(companyId, source.ProductId, source.TerminalId, source.QuantityMt, value, ct);
        }
        else
        {
            var consumption = await valuation.TryConsumeAsync(companyId, source.ProductId, source.TerminalId, source.QuantityMt, ct);
            if (!consumption.Succeeded || consumption.CostUsd <= 0m)
                throw Rule("MANUAL_INVENTORY_VALUE", "ارزش ثبت‌شدهٔ موجودی کافی نیست؛ سند ثبت نشد.");
            value = consumption.CostUsd;
            if (destination is not null)
                await valuation.ApplyReceiptAsync(companyId, source.ProductId, destination.TerminalId, source.QuantityMt, value, ct);
        }
        var debitAccount = inbound || destination is not null ? settings.InventoryAccountId : input.OffsetAccountId!.Value;
        var creditAccount = inbound ? input.OffsetAccountId!.Value : settings.InventoryAccountId;
        await posting.PostAsync(new AccountingPostRequest(companyId, $"INV-{companyId:D6}-{source.Id:D9}",
            source.MovementDate, source.MovementDate, source.MovementDate, "ManualInventory",
            [new AccountingPostLine(debitAccount, value, 0m, "USD", value, 1m,
                ContractId: source.ContractId, TankId: destination?.StorageTankId ?? (inbound ? source.StorageTankId : null),
                ProductId: source.ProductId, Description: source.Notes),
             new AccountingPostLine(creditAccount, 0m, value, "USD", value, 1m,
                ContractId: source.ContractId, TankId: inbound ? null : source.StorageTankId,
                ProductId: source.ProductId, Description: source.Notes)],
            SourceEventId: $"ManualInventory:{source.Id}:Created", SourceEntityType: nameof(InventoryMovement),
            SourceEntityId: source.Id, Description: source.Notes,
            IsAdjustment: input.Direction == MovementDirection.Adjustment), ct);
    }

    private static BusinessRuleException Rule(string code, string message) => new(code, message);
}
