using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Models.Contracts;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services.Accounting;
using PTGOilSystem.Web.Services.Audit;
using PTGOilSystem.Web.Services.Exceptions;
using PTGOilSystem.Web.Services.Ledger;
using PTGOilSystem.Web.Services.LoadingReceipts;
using PTGOilSystem.Web.Services.Time;

namespace PTGOilSystem.Web.Services;

public sealed record SimplePurchaseWorkflowResult(
    bool Changed,
    int LoadingRegisterId,
    int LoadingReceiptId);

public interface ISimplePurchaseWorkflowService
{
    Task<SimplePurchaseWorkflowResult> ConfirmAsync(int contractId, CancellationToken ct = default);

    Task<SimplePurchaseWorkflowResult> ReverseAsync(
        int contractId,
        string reason,
        int? actorUserId = null,
        CancellationToken ct = default);

    Task<bool> HasPostingAsync(int contractId, CancellationToken ct = default);
}

/// <summary>
/// Profile-specific one-form purchase orchestration. The generated loading and receipt are
/// canonical internal documents, so every existing stock, valuation, payable and reversal
/// adapter continues to receive exactly the same source entities as the full workflow.
/// </summary>
public sealed class SimplePurchaseWorkflowService : ISimplePurchaseWorkflowService
{
    private const string ImportKeyPrefix = "SIMPLE-PURCHASE-CONTRACT:";

    private readonly ApplicationDbContext _db;
    private readonly IInventoryMovementWriter _movements;
    private readonly IPurchaseAccountingAdapter _purchaseAccounting;
    private readonly ILoadingReceiptCancellationService _receiptCancellation;
    private readonly IAuditService _audit;
    private readonly ILedgerPostingService _ledger;

    public SimplePurchaseWorkflowService(
        ApplicationDbContext db,
        IInventoryMovementWriter movements,
        IPurchaseAccountingAdapter purchaseAccounting,
        ILoadingReceiptCancellationService receiptCancellation,
        IAuditService audit,
        ILedgerPostingService ledger)
    {
        _db = db;
        _movements = movements;
        _purchaseAccounting = purchaseAccounting;
        _receiptCancellation = receiptCancellation;
        _audit = audit;
        _ledger = ledger;
    }

    public Task<bool> HasPostingAsync(int contractId, CancellationToken ct = default)
        => _db.LoadingRegisters.AsNoTracking().AnyAsync(
            loading => loading.ContractId == contractId
                && loading.ImportUniqueKey == BuildImportKey(contractId),
            ct);

    public async Task<SimplePurchaseWorkflowResult> ConfirmAsync(
        int contractId,
        CancellationToken ct = default)
    {
        var transaction = await BeginTransactionIfNeededAsync(ct);
        try
        {
            var contract = await _db.Contracts.SingleOrDefaultAsync(x => x.Id == contractId, ct)
                ?? throw Rule("SIMPLE_PURCHASE_CONTRACT_NOT_FOUND", "قرارداد خرید پیدا نشد.");
            var importKey = BuildImportKey(contractId);
            var existing = await _db.LoadingRegisters
                .Include(x => x.Receipts)
                .SingleOrDefaultAsync(x => x.ImportUniqueKey == importKey, ct);

            if (existing is not null)
            {
                var activeReceipt = existing.Receipts.SingleOrDefault(x => !x.IsCancelled);
                if (contract.Status == ContractStatus.Active && activeReceipt is not null)
                {
                    await CommitAsync(transaction, ct);
                    return new SimplePurchaseWorkflowResult(false, existing.Id, activeReceipt.Id);
                }

                throw Rule(
                    "SIMPLE_PURCHASE_ALREADY_POSTED",
                    "برای این قرارداد قبلاً سند خرید مستقیم ساخته شده است؛ ثبت دوباره مجاز نیست.");
            }

            ValidateForConfirmation(contract);
            var tank = await _db.StorageTanks
                .AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == contract.DestinationStorageTankId, ct)
                ?? throw Rule("SIMPLE_PURCHASE_TANK_NOT_FOUND", "مخزن مقصد پیدا نشد.");
            if (!tank.IsActive)
                throw Rule("SIMPLE_PURCHASE_TANK_INACTIVE", "مخزن مقصد غیرفعال است.");
            if (tank.ProductId.HasValue && tank.ProductId != contract.ProductId)
                throw Rule("SIMPLE_PURCHASE_TANK_PRODUCT_MISMATCH", "محصول قرارداد با محصول مخزن مقصد یکسان نیست.");

            var unitPriceUsd = ContractPricingAdapter.GetCanonicalFinalPrice(contract);
            if (unitPriceUsd is not > 0m)
                throw Rule("SIMPLE_PURCHASE_PRICE_PENDING", "قیمت خرید و نرخ تبدیل باید پیش از تأیید کامل باشد.");

            var reference = BuildReference(contract);
            var loading = new LoadingRegister
            {
                ContractId = contract.Id,
                ProductId = contract.ProductId,
                OriginLocationId = contract.PurchaseSourceLocationId,
                TransportType = LoadingTransportType.Unspecified,
                LoadingDate = contract.ContractDate.Date,
                LoadedQuantityMt = contract.QuantityMt,
                BillOfLadingNumber = reference,
                ImportUniqueKey = importKey,
                LoadingPriceUsd = unitPriceUsd,
                SettlementCurrencyCode = SystemCurrency.BaseCurrencyCode,
                DestinationName = tank.DisplayName ?? tank.TankCode,
                Notes = contract.Notes
            };
            _db.LoadingRegisters.Add(loading);
            await _db.SaveChangesAsync(ct);

            await _purchaseAccounting.TryPostPurchaseAsync(loading, ct);
            if (SupplierLoadingLedger.IsPostable(loading, contract))
            {
                var legacyEntry = _ledger.Post(SupplierLoadingLedger.Create(loading, contract));
                await _db.SaveChangesAsync(ct);
                await _audit.LogAsync(
                    nameof(LedgerEntry),
                    legacyEntry.Id,
                    AuditAction.Insert,
                    diff: AuditDiffFormatter.ForCreate(
                        ("SourceType", legacyEntry.SourceType),
                        ("SourceId", legacyEntry.SourceId),
                        ("SupplierId", legacyEntry.SupplierId),
                        ("AmountUsd", legacyEntry.AmountUsd)),
                    ct: ct);
            }

            var receipt = new LoadingReceipt
            {
                LoadingRegisterId = loading.Id,
                ReceiptDestination = LoadingReceiptDestination.ToInventory,
                TerminalId = tank.TerminalId,
                StorageTankId = tank.Id,
                ReceiptDate = contract.ContractDate.Date,
                ReceivedQuantityMt = contract.QuantityMt,
                LossMode = ReceiptLossMode.DeferredTankSettlement,
                ReferenceDocument = reference,
                Notes = contract.Notes
            };
            var movement = new InventoryMovement
            {
                ProductId = contract.ProductId,
                ContractId = contract.Id,
                TerminalId = tank.TerminalId,
                StorageTankId = tank.Id,
                Direction = MovementDirection.In,
                MovementDate = contract.ContractDate.Date,
                QuantityMt = contract.QuantityMt,
                ReferenceDocument = reference,
                Notes = contract.Notes,
                LoadingReceipt = receipt
            };
            var allocation = new LoadingReceiptAllocation
            {
                LoadingReceipt = receipt,
                Destination = LoadingReceiptAllocationDestination.ToInventory,
                Status = LoadingReceiptAllocationStatus.Completed,
                QuantityMt = contract.QuantityMt,
                SourcePurchaseContractId = contract.Id,
                TerminalId = tank.TerminalId,
                StorageTankId = tank.Id,
                InventoryMovement = movement,
                ReferenceDocument = reference,
                Notes = contract.Notes
            };
            _db.LoadingReceipts.Add(receipt);
            _db.LoadingReceiptAllocations.Add(allocation);
            await _movements.PostInboundRangeAsync([movement], ct);
            await _purchaseAccounting.TryPostInventoryReceiptAsync(receipt, ct);

            contract.Status = ContractStatus.Active;
            contract.UpdatedAtUtc = DateTime.UtcNow;
            await _audit.LogAsync(
                nameof(Contract),
                contract.Id,
                AuditAction.Update,
                diff: AuditDiffFormatter.ForUpdate(
                    ("Status", ContractStatus.Draft, ContractStatus.Active),
                    ("SimplePurchaseLoadingRegisterId", null, loading.Id),
                    ("SimplePurchaseLoadingReceiptId", null, receipt.Id),
                    ("DestinationStorageTankId", null, tank.Id)),
                ct: ct);
            await _audit.LogAsync(
                nameof(InventoryMovement),
                movement.Id,
                AuditAction.Insert,
                diff: AuditDiffFormatter.ForCreate(
                    ("ProductId", movement.ProductId),
                    ("ContractId", movement.ContractId),
                    ("StorageTankId", movement.StorageTankId),
                    ("Direction", movement.Direction),
                    ("QuantityMt", movement.QuantityMt),
                    ("LoadingReceiptId", receipt.Id)),
                ct: ct);
            await _db.SaveChangesAsync(ct);
            await CommitAsync(transaction, ct);
            return new SimplePurchaseWorkflowResult(true, loading.Id, receipt.Id);
        }
        catch
        {
            await RollbackAsync(transaction, ct);
            throw;
        }
        finally
        {
            if (transaction is not null) await transaction.DisposeAsync();
        }
    }

    public async Task<SimplePurchaseWorkflowResult> ReverseAsync(
        int contractId,
        string reason,
        int? actorUserId = null,
        CancellationToken ct = default)
    {
        var normalizedReason = (reason ?? string.Empty).Trim();
        if (normalizedReason.Length == 0)
            throw Rule("SIMPLE_PURCHASE_CANCEL_REASON_REQUIRED", "ثبت دلیل لغو الزامی است.");

        var transaction = await BeginTransactionIfNeededAsync(ct);
        try
        {
            var contract = await _db.Contracts.SingleOrDefaultAsync(x => x.Id == contractId, ct)
                ?? throw Rule("SIMPLE_PURCHASE_CONTRACT_NOT_FOUND", "قرارداد خرید پیدا نشد.");
            var loading = await _db.LoadingRegisters
                // Linked custody purchases must reverse both ownership ledgers atomically.
                .Include(x => x.Receipts)
                .SingleOrDefaultAsync(x => x.ImportUniqueKey == BuildImportKey(contractId), ct)
                ?? throw Rule("SIMPLE_PURCHASE_NOT_POSTED", "برای این قرارداد خرید مستقیم تأییدشده‌ای وجود ندارد.");
            var activeReceipt = loading.Receipts.SingleOrDefault(x => !x.IsCancelled);

            if (await _db.CustodyConversions.AnyAsync(x => x.ContractId == contractId && x.Status == CustodyDocumentStatus.Posted, ct))
                throw Rule("CUSTODY_CONVERSION_REVERSAL", "این خرید از تبدیل امانی ساخته شده؛ آن را از سند تبدیل لغو کنید.");

            if (contract.Status == ContractStatus.Cancelled && activeReceipt is null)
            {
                await CommitAsync(transaction, ct);
                return new SimplePurchaseWorkflowResult(false, loading.Id, loading.Receipts.OrderByDescending(x => x.Id).First().Id);
            }
            if (contract.Status != ContractStatus.Active || activeReceipt is null)
                throw Rule("SIMPLE_PURCHASE_INVALID_STATE", "وضعیت خرید مستقیم برای لغو معتبر نیست.");

            var cancellation = await _receiptCancellation.CancelWithinCurrentTransactionAsync(
                [activeReceipt.Id],
                normalizedReason,
                actorUserId,
                ct);
            if (!cancellation.Succeeded)
            {
                var message = string.Join(" ", cancellation.Blockers.Select(x => x.Reason));
                throw Rule("SIMPLE_PURCHASE_REVERSE_BLOCKED", message);
            }

            await _purchaseAccounting.TryPostPurchaseReversalAsync(loading, ct);
            var legacyEntry = await _db.LedgerEntries
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    x => x.SourceType == SupplierLoadingLedger.SourceType && x.SourceId == loading.Id,
                    ct);
            if (legacyEntry is not null)
            {
                await LedgerReversalWriter.ReverseAsync(
                    _db,
                    legacyEntry,
                    AfghanistanBusinessClock.SystemToday,
                    $"لغو خرید مستقیم قرارداد #{contract.Id} | {normalizedReason}",
                    SupplierLoadingLedger.BuildReference(loading),
                    ct);
            }

            contract.Status = ContractStatus.Cancelled;
            contract.UpdatedAtUtc = DateTime.UtcNow;
            await _audit.LogAsync(
                nameof(Contract),
                contract.Id,
                AuditAction.Reverse,
                diff: AuditDiffFormatter.ForUpdate(
                    ("Status", ContractStatus.Active, ContractStatus.Cancelled),
                    ("CancellationReason", null, normalizedReason),
                    ("SimplePurchaseLoadingReceiptId", activeReceipt.Id, null)),
                ct: ct);
            await _db.SaveChangesAsync(ct);
            await CommitAsync(transaction, ct);
            return new SimplePurchaseWorkflowResult(true, loading.Id, activeReceipt.Id);
        }
        catch
        {
            await RollbackAsync(transaction, ct);
            throw;
        }
        finally
        {
            if (transaction is not null) await transaction.DisposeAsync();
        }
    }

    private static void ValidateForConfirmation(Contract contract)
    {
        if (contract.Status != ContractStatus.Draft)
            throw Rule("SIMPLE_PURCHASE_NOT_DRAFT", "فقط خرید پیش‌نویس قابل تأیید است.");
        if (contract.ContractType != ContractType.Purchase || !contract.SupplierId.HasValue)
            throw Rule("SIMPLE_PURCHASE_SUPPLIER_REQUIRED", "تأمین‌کننده برای خرید مستقیم الزامی است.");
        if (contract.QuantityMt <= 0m)
            throw Rule("SIMPLE_PURCHASE_QUANTITY_INVALID", "مقدار خرید باید بزرگ‌تر از صفر باشد.");
        if (!contract.DestinationStorageTankId.HasValue)
            throw Rule("SIMPLE_PURCHASE_TANK_REQUIRED", "انتخاب مخزن مقصد الزامی است.");
    }

    private static string BuildImportKey(int contractId) => $"{ImportKeyPrefix}{contractId}";

    private static string BuildReference(Contract contract)
    {
        var reference = $"SIMPLE-PURCHASE:{contract.ContractNumber}";
        return reference.Length <= 100 ? reference : reference[..100];
    }

    private async Task<IDbContextTransaction?> BeginTransactionIfNeededAsync(CancellationToken ct)
        => _db.Database.IsRelational() && _db.Database.CurrentTransaction is null
            ? await _db.Database.BeginTransactionAsync(ct)
            : null;

    private static Task CommitAsync(IDbContextTransaction? transaction, CancellationToken ct)
        => transaction?.CommitAsync(ct) ?? Task.CompletedTask;

    private static Task RollbackAsync(IDbContextTransaction? transaction, CancellationToken ct)
        => transaction?.RollbackAsync(ct) ?? Task.CompletedTask;

    private static BusinessRuleException Rule(string code, string message) => new(code, message);
}
