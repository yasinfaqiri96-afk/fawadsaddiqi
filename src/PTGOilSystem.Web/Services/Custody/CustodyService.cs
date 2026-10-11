using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Security;
using PTGOilSystem.Web.Services.Exceptions;

namespace PTGOilSystem.Web.Services.Custody;

/// <summary>
/// تنها مسیرِ نوشتن در دفتر امانی. هیچ عملیات اینجا خرید، فروش، Ledger، Journal یا
/// InventoryMovement نمی‌سازد: جنسِ امانی ملکِ شرکت نیست و فقط مقدار و محلش ثبت می‌شود.
/// </summary>
public interface ICustodyService
{
    Task<int> RegisterInTransitAsync(CustodyInTransitInput input, CancellationToken ct = default);
    Task<int> ReceiveAsync(CustodyReceiptInput input, CancellationToken ct = default);
    Task<Guid> ReleaseAsync(CustodyReleaseInput input, CancellationToken ct = default);
    Task<Guid> TransferLocationAsync(CustodyLocationTransferInput input, CancellationToken ct = default);
    Task<int> TransferOwnershipAsync(CustodyOwnershipTransferInput input, CancellationToken ct = default);
    Task<Guid> AdjustAsync(CustodyAdjustmentInput input, CancellationToken ct = default);
    Task<int> PlaceHoldAsync(int lotId, decimal quantityMt, string reason, string? formToken, CancellationToken ct = default);
    Task ReleaseHoldAsync(int holdId, string? note, CancellationToken ct = default);
    Task SetTransitLossResponsibilityAsync(int lotId, CustodyLossResponsibility responsibility, CancellationToken ct = default);
    Task ReverseOperationAsync(Guid operationKey, string reason, CancellationToken ct = default);
    Task<Guid> RecordSaleOutAsync(int lotId, CustodyLocation location, DateTime saleDate, decimal quantityMt, string? documentNumber, CancellationToken ct = default);
    Task ReverseSaleOutAsync(Guid operationKey, string reason, CancellationToken ct = default);
    Task<Guid> RecordConversionOutAsync(int lotId, CustodyLocation location, DateTime date, decimal quantityMt, string reference, CancellationToken ct = default);
    Task ReverseConversionOutAsync(Guid operationKey, DateTime date, string reason, CancellationToken ct = default);
}

public sealed record CustodyOwner(AccountingPartyType PartyType, int PartyId);

public sealed record CustodyLocation(int TerminalId, int? StorageTankId);

public sealed class CustodyInTransitInput
{
    public required CustodyOwner Owner { get; init; }
    public int ProductId { get; init; }
    public DateTime LoadedDate { get; init; }
    public decimal LoadedQuantityMt { get; init; }
    public string? OriginDescription { get; init; }
    public string? TransportReference { get; init; }
    public string? DocumentNumber { get; init; }
    public string? Notes { get; init; }
    public string? FormToken { get; init; }
}

public sealed class CustodyReceiptInput
{
    /// <summary>پر = رسیدِ یک بارِ در راه؛ خالی = ورودِ مستقیمِ بارِ تازه.</summary>
    public int? LotId { get; init; }
    public CustodyOwner? Owner { get; init; }
    public int? ProductId { get; init; }
    public required CustodyLocation Location { get; init; }
    public DateTime ReceiptDate { get; init; }
    public decimal QuantityMt { get; init; }
    public DateTime? LoadedDate { get; init; }
    public decimal? LoadedQuantityMt { get; init; }
    public string? OriginDescription { get; init; }
    public string? TransportReference { get; init; }
    public string? DocumentNumber { get; init; }
    public string? Notes { get; init; }
    public string? FormToken { get; init; }
}

public sealed class CustodyReleaseInput
{
    public int LotId { get; init; }
    public CustodyMovementKind Kind { get; init; }
    public required CustodyLocation Location { get; init; }
    public DateTime MovementDate { get; init; }
    public decimal QuantityMt { get; init; }
    public string? CounterpartyName { get; init; }
    public string? VehicleReference { get; init; }
    public string? DocumentNumber { get; init; }
    public string? Notes { get; init; }
    public string? FormToken { get; init; }
}

public sealed class CustodyLocationTransferInput
{
    public int LotId { get; init; }
    public required CustodyLocation From { get; init; }
    public required CustodyLocation To { get; init; }
    public DateTime MovementDate { get; init; }
    public decimal QuantityMt { get; init; }
    public string? DocumentNumber { get; init; }
    public string? Notes { get; init; }
    public string? FormToken { get; init; }
}

public sealed class CustodyOwnershipTransferInput
{
    public int LotId { get; init; }
    public required CustodyOwner NewOwner { get; init; }
    public required CustodyLocation Location { get; init; }
    public DateTime MovementDate { get; init; }
    public decimal QuantityMt { get; init; }
    public string? DocumentNumber { get; init; }
    public string? Notes { get; init; }
    public string? FormToken { get; init; }
}

public sealed class CustodyAdjustmentInput
{
    public int LotId { get; init; }
    public CustodyMovementKind Kind { get; init; }
    public required CustodyLocation Location { get; init; }
    public DateTime MovementDate { get; init; }
    public decimal QuantityMt { get; init; }
    public CustodyLossResponsibility Responsibility { get; init; } = CustodyLossResponsibility.Undetermined;
    public string? DocumentNumber { get; init; }
    public string? Notes { get; init; }
    public string? FormToken { get; init; }
}

public sealed class CustodyService(
    ApplicationDbContext db,
    IAuditService audit,
    IFormTokenGuard formTokens,
    ICurrentUserContext? currentUser = null) : ICustodyService
{
    /// <summary>طرف‌هایی که می‌توانند مالکِ جنسِ امانی باشند.</summary>
    public static readonly AccountingPartyType[] OwnerPartyTypes =
    [
        AccountingPartyType.Customer,
        AccountingPartyType.Supplier,
        AccountingPartyType.Partner,
        AccountingPartyType.Company
    ];

    private const string LotEntity = "CustodyLot";
    private const string MovementEntity = "CustodyMovement";
    private const string HoldEntity = "CustodyHold";

    public Task<int> RegisterInTransitAsync(CustodyInTransitInput input, CancellationToken ct = default)
        => InTransactionAsync(async () =>
        {
            RequirePositive(input.LoadedQuantityMt, "مقدار بارگیری");
            await EnsureOwnerAsync(input.Owner, ct);
            await EnsureProductAsync(input.ProductId, ct);

            var lot = NewLot(input.Owner, input.ProductId, CustodyLotStatus.InTransit);
            lot.LoadedDate = ToUtc(input.LoadedDate);
            lot.LoadedQuantityMt = input.LoadedQuantityMt;
            lot.OriginDescription = Clean(input.OriginDescription);
            lot.TransportReference = Clean(input.TransportReference);
            lot.DocumentNumber = Clean(input.DocumentNumber);
            lot.Notes = Clean(input.Notes);
            db.CustodyLots.Add(lot);
            formTokens.Stamp(input.FormToken, "Custody.InTransit", LotEntity);
            await db.SaveChangesAsync(ct);
            await AssignLotNumberAsync(lot, ct);
            await audit.LogAsync(LotEntity, lot.Id, AuditAction.Insert, diff: Describe(new
            {
                lot.LotNumber, Status = lot.Status.ToString(), lot.OwnerPartyType, lot.OwnerPartyId,
                lot.ProductId, lot.LoadedQuantityMt
            }), ct: ct);
            await db.SaveChangesAsync(ct);
            return lot.Id;
        }, ct);

    public Task<int> ReceiveAsync(CustodyReceiptInput input, CancellationToken ct = default)
        => InTransactionAsync(async () =>
        {
            RequirePositive(input.QuantityMt, "مقدار رسید");
            CustodyLot lot;
            if (input.LotId.HasValue)
            {
                await LockLotsAsync([input.LotId.Value], ct);
                lot = await LoadLotAsync(input.LotId.Value, ct);
                if (lot.Status == CustodyLotStatus.Cancelled)
                    throw Rule("CUSTODY_LOT_CANCELLED", "این بار لغو شده است.");
                if (lot.Status == CustodyLotStatus.InTransit)
                    lot.Status = CustodyLotStatus.Active;
            }
            else
            {
                if (input.Owner is null || !input.ProductId.HasValue)
                    throw Rule("CUSTODY_OWNER_REQUIRED", "مالک و جنس را انتخاب کنید.");
                if (input.LoadedQuantityMt is <= 0m)
                    throw Rule("CUSTODY_LOADED_QTY", "مقدار بارگیری باید بیشتر از صفر باشد.");
                await EnsureOwnerAsync(input.Owner, ct);
                await EnsureProductAsync(input.ProductId.Value, ct);
                lot = NewLot(input.Owner, input.ProductId.Value, CustodyLotStatus.Active);
                lot.LoadedDate = input.LoadedDate.HasValue ? ToUtc(input.LoadedDate.Value) : null;
                lot.LoadedQuantityMt = input.LoadedQuantityMt;
                lot.OriginDescription = Clean(input.OriginDescription);
                lot.TransportReference = Clean(input.TransportReference);
                lot.DocumentNumber = Clean(input.DocumentNumber);
                lot.Notes = Clean(input.Notes);
                db.CustodyLots.Add(lot);
            }

            await EnsureInboundLocationAsync(input.Location, lot.ProductId, input.QuantityMt, ct);

            var operationKey = Guid.NewGuid();
            var movement = NewMovement(lot, CustodyMovementKind.Receipt, CustodyDirection.In, input.Location,
                input.ReceiptDate, input.QuantityMt, operationKey);
            movement.DocumentNumber = Clean(input.DocumentNumber);
            movement.Notes = Clean(input.Notes);
            formTokens.Stamp(input.FormToken, "Custody.Receipt", MovementEntity);
            await db.SaveChangesAsync(ct);
            if (string.IsNullOrEmpty(lot.LotNumber) || lot.LotNumber.StartsWith("TMP-", StringComparison.Ordinal))
                await AssignLotNumberAsync(lot, ct);
            await LogMovementAsync(movement, lot, ct);
            await db.SaveChangesAsync(ct);
            return lot.Id;
        }, ct);

    public Task<Guid> ReleaseAsync(CustodyReleaseInput input, CancellationToken ct = default)
        => InTransactionAsync(async () =>
        {
            if (input.Kind is not (CustodyMovementKind.ReturnToOwner or CustodyMovementKind.DeliveryToThirdParty))
                throw Rule("CUSTODY_RELEASE_KIND", "نوع خروج معتبر نیست.");
            if (input.Kind == CustodyMovementKind.DeliveryToThirdParty && string.IsNullOrWhiteSpace(input.CounterpartyName))
                throw Rule("CUSTODY_RECIPIENT_REQUIRED", "گیرندهٔ جنس را بنویسید.");
            RequirePositive(input.QuantityMt, "مقدار خروج");

            await LockLotsAsync([input.LotId], ct);
            var lot = await LoadActiveLotAsync(input.LotId, ct);
            var operationKey = Guid.NewGuid();
            var movement = NewMovement(lot, input.Kind, CustodyDirection.Out, input.Location,
                input.MovementDate, input.QuantityMt, operationKey);
            movement.CounterpartyName = Clean(input.CounterpartyName);
            movement.VehicleReference = Clean(input.VehicleReference);
            movement.DocumentNumber = Clean(input.DocumentNumber);
            movement.Notes = Clean(input.Notes);
            await EnsureOutboundAllowedAsync(lot, input.Location, movement.MovementDate, input.QuantityMt, ct);

            formTokens.Stamp(input.FormToken, "Custody.Release", MovementEntity);
            await db.SaveChangesAsync(ct);
            await LogMovementAsync(movement, lot, ct);
            await db.SaveChangesAsync(ct);
            return operationKey;
        }, ct);

    public Task<Guid> TransferLocationAsync(CustodyLocationTransferInput input, CancellationToken ct = default)
        => InTransactionAsync(async () =>
        {
            RequirePositive(input.QuantityMt, "مقدار انتقال");
            if (input.From == input.To)
                throw Rule("CUSTODY_SAME_LOCATION", "مبدأ و مقصد انتقال یکی است.");

            await LockLotsAsync([input.LotId], ct);
            var lot = await LoadActiveLotAsync(input.LotId, ct);
            var date = ToUtc(input.MovementDate);
            foreach (var tankId in new[] { input.From.StorageTankId, input.To.StorageTankId }
                         .Where(id => id.HasValue).Select(id => id!.Value).Distinct().OrderBy(id => id))
                await LockTankAsync(tankId, ct);
            await EnsureOutboundAllowedAsync(lot, input.From, date, input.QuantityMt, ct);
            await EnsureInboundLocationAsync(input.To, lot.ProductId, input.QuantityMt, ct);

            var operationKey = Guid.NewGuid();
            var outRow = NewMovement(lot, CustodyMovementKind.LocationTransfer, CustodyDirection.Out, input.From,
                date, input.QuantityMt, operationKey);
            var inRow = NewMovement(lot, CustodyMovementKind.LocationTransfer, CustodyDirection.In, input.To,
                date, input.QuantityMt, operationKey);
            foreach (var row in new[] { outRow, inRow })
            {
                row.DocumentNumber = Clean(input.DocumentNumber);
                row.Notes = Clean(input.Notes);
            }

            formTokens.Stamp(input.FormToken, "Custody.LocationTransfer", MovementEntity);
            await db.SaveChangesAsync(ct);
            await LogMovementAsync(outRow, lot, ct);
            await LogMovementAsync(inRow, lot, ct);
            await db.SaveChangesAsync(ct);
            return operationKey;
        }, ct);

    public Task<int> TransferOwnershipAsync(CustodyOwnershipTransferInput input, CancellationToken ct = default)
        => InTransactionAsync(async () =>
        {
            RequirePositive(input.QuantityMt, "مقدار انتقال مالکیت");
            await LockLotsAsync([input.LotId], ct);
            var source = await LoadActiveLotAsync(input.LotId, ct);
            if (source.OwnerPartyType == input.NewOwner.PartyType && source.OwnerPartyId == input.NewOwner.PartyId)
                throw Rule("CUSTODY_SAME_OWNER", "مالک جدید همان مالک فعلی است.");
            await EnsureOwnerAsync(input.NewOwner, ct);

            var date = ToUtc(input.MovementDate);
            await EnsureOutboundAllowedAsync(source, input.Location, date, input.QuantityMt, ct);

            var target = NewLot(input.NewOwner, source.ProductId, CustodyLotStatus.Active);
            target.ParentCustodyLot = source;
            target.DocumentNumber = Clean(input.DocumentNumber);
            target.Notes = Clean(input.Notes);
            db.CustodyLots.Add(target);

            var operationKey = Guid.NewGuid();
            var outRow = NewMovement(source, CustodyMovementKind.OwnershipTransfer, CustodyDirection.Out, input.Location,
                date, input.QuantityMt, operationKey);
            var inRow = NewMovement(target, CustodyMovementKind.OwnershipTransfer, CustodyDirection.In, input.Location,
                date, input.QuantityMt, operationKey);
            foreach (var row in new[] { outRow, inRow })
            {
                row.DocumentNumber = Clean(input.DocumentNumber);
                row.Notes = Clean(input.Notes);
            }

            formTokens.Stamp(input.FormToken, "Custody.OwnershipTransfer", MovementEntity);
            await db.SaveChangesAsync(ct);
            await AssignLotNumberAsync(target, ct);
            await LogMovementAsync(outRow, source, ct);
            await LogMovementAsync(inRow, target, ct);
            await db.SaveChangesAsync(ct);
            return target.Id;
        }, ct);

    public Task<Guid> AdjustAsync(CustodyAdjustmentInput input, CancellationToken ct = default)
        => InTransactionAsync(async () =>
        {
            if (input.Kind is not (CustodyMovementKind.Loss or CustodyMovementKind.Surplus))
                throw Rule("CUSTODY_ADJUSTMENT_KIND", "نوع اصلاح معتبر نیست.");
            RequirePositive(input.QuantityMt, "مقدار");

            await LockLotsAsync([input.LotId], ct);
            var lot = await LoadActiveLotAsync(input.LotId, ct);
            var date = ToUtc(input.MovementDate);
            var direction = input.Kind == CustodyMovementKind.Loss ? CustodyDirection.Out : CustodyDirection.In;
            if (direction == CustodyDirection.Out)
                await EnsureOutboundAllowedAsync(lot, input.Location, date, input.QuantityMt, ct, ignoreHolds: true);
            else
                await EnsureInboundLocationAsync(input.Location, lot.ProductId, input.QuantityMt, ct);

            var movement = NewMovement(lot, input.Kind, direction, input.Location, date, input.QuantityMt, Guid.NewGuid());
            movement.LossResponsibility = input.Responsibility;
            movement.DocumentNumber = Clean(input.DocumentNumber);
            movement.Notes = Clean(input.Notes);

            formTokens.Stamp(input.FormToken, "Custody.Adjustment", MovementEntity);
            await db.SaveChangesAsync(ct);
            await LogMovementAsync(movement, lot, ct);
            await db.SaveChangesAsync(ct);
            return movement.OperationKey;
        }, ct);

    public Task<int> PlaceHoldAsync(int lotId, decimal quantityMt, string reason, string? formToken, CancellationToken ct = default)
        => InTransactionAsync(async () =>
        {
            RequirePositive(quantityMt, "مقدار مسدود");
            if (string.IsNullOrWhiteSpace(reason))
                throw Rule("CUSTODY_HOLD_REASON", "دلیل مسدودیت را بنویسید.");

            await LockLotsAsync([lotId], ct);
            var lot = await LoadActiveLotAsync(lotId, ct);
            var balance = await LotBalanceAsync(lot.Id, ct);
            var held = await HeldQuantityAsync(lot.Id, ct);
            if (quantityMt > balance - held)
                throw Rule("CUSTODY_HOLD_EXCEEDS", $"مقدار آزادِ این بار {Format(balance - held)} تُن است.");

            var hold = new CustodyHold
            {
                CustodyLotId = lot.Id,
                QuantityMt = quantityMt,
                Reason = reason.Trim(),
                CreatedByUserId = currentUser?.UserId
            };
            db.CustodyHolds.Add(hold);
            formTokens.Stamp(formToken, "Custody.Hold", HoldEntity);
            await db.SaveChangesAsync(ct);
            await audit.LogAsync(HoldEntity, hold.Id, AuditAction.Insert,
                diff: Describe(new { lot.LotNumber, hold.QuantityMt, hold.Reason }), ct: ct);
            await db.SaveChangesAsync(ct);
            return hold.Id;
        }, ct);

    public Task ReleaseHoldAsync(int holdId, string? note, CancellationToken ct = default)
        => InTransactionAsync(async () =>
        {
            var hold = await db.CustodyHolds.FirstOrDefaultAsync(h => h.Id == holdId, ct)
                ?? throw Rule("CUSTODY_HOLD_NOT_FOUND", "مسدودیت پیدا نشد.");
            await LockLotsAsync([hold.CustodyLotId], ct);
            await db.Entry(hold).ReloadAsync(ct);
            if (hold.ReleasedAtUtc.HasValue)
                throw Rule("CUSTODY_HOLD_RELEASED", "این مسدودیت قبلاً رفع شده است.");
            hold.ReleasedAtUtc = DateTime.UtcNow;
            hold.ReleasedByUserId = currentUser?.UserId;
            hold.ReleaseNote = Clean(note);
            hold.UpdatedAtUtc = DateTime.UtcNow;
            hold.UpdatedByUserId = currentUser?.UserId;
            await audit.LogAsync(HoldEntity, hold.Id, AuditAction.Update,
                diff: Describe(new { Released = true, hold.ReleaseNote }), ct: ct);
            await db.SaveChangesAsync(ct);
            return 0;
        }, ct);

    public Task SetTransitLossResponsibilityAsync(int lotId, CustodyLossResponsibility responsibility, CancellationToken ct = default)
        => InTransactionAsync(async () =>
        {
            var lot = await LoadLotAsync(lotId, ct);
            var previous = lot.TransitLossResponsibility;
            lot.TransitLossResponsibility = responsibility;
            lot.UpdatedAtUtc = DateTime.UtcNow;
            lot.UpdatedByUserId = currentUser?.UserId;
            await audit.LogAsync(LotEntity, lot.Id, AuditAction.Update,
                diff: Describe(new { TransitLossResponsibility = new { From = previous.ToString(), To = responsibility.ToString() } }), ct: ct);
            await db.SaveChangesAsync(ct);
            return 0;
        }, ct);

    public Task ReverseOperationAsync(Guid operationKey, string reason, CancellationToken ct = default)
        => ReverseCoreAsync(operationKey, reason, allowSaleReversal: false, ct);

    /// <summary>
    /// خروجِ فروش امانی. فقط <see cref="CustodySaleService"/> صدا می‌زند، داخلِ همان تراکنشِ سند
    /// فروش؛ همان قواعدِ موجودی، مسدودی، تاریخ و قفلِ خروج‌های دیگر اعمال می‌شود.
    /// </summary>
    public Task<Guid> RecordSaleOutAsync(
        int lotId,
        CustodyLocation location,
        DateTime saleDate,
        decimal quantityMt,
        string? documentNumber,
        CancellationToken ct = default)
        => InTransactionAsync(async () =>
        {
            RequirePositive(quantityMt, "مقدار فروش");
            await LockLotsAsync([lotId], ct);
            var lot = await LoadActiveLotAsync(lotId, ct);
            var operationKey = Guid.NewGuid();
            var movement = NewMovement(lot, CustodyMovementKind.Sale, CustodyDirection.Out, location,
                saleDate, quantityMt, operationKey);
            movement.DocumentNumber = Clean(documentNumber);
            await EnsureOutboundAllowedAsync(lot, location, movement.MovementDate, quantityMt, ct);
            await db.SaveChangesAsync(ct);
            await LogMovementAsync(movement, lot, ct);
            return operationKey;
        }, ct);

    /// <summary>برگشتِ خروجِ فروش؛ فقط از مسیرِ لغوِ سند فروش امانی.</summary>
    public Task ReverseSaleOutAsync(Guid operationKey, string reason, CancellationToken ct = default)
        => ReverseCoreAsync(operationKey, reason, allowSaleReversal: true, ct);

    public Task<Guid> RecordConversionOutAsync(int lotId, CustodyLocation location, DateTime date,
        decimal quantityMt, string reference, CancellationToken ct = default)
        => InTransactionAsync(async () =>
        {
            RequirePositive(quantityMt, "مقدار خرید");
            await LockLotsAsync([lotId], ct);
            if (location.StorageTankId is not { } tankId)
                throw Rule("CUSTODY_CONVERSION_TANK", "تبدیل به خرید نیازمند مخزن مشخص است.");
            await LockTankAsync(tankId, ct);
            var lot = await LoadActiveLotAsync(lotId, ct);
            await EnsureOutboundAllowedAsync(lot, location, ToUtc(date), quantityMt, ct);
            var key = Guid.NewGuid();
            var movement = NewMovement(lot, CustodyMovementKind.ConvertedToCompany, CustodyDirection.Out,
                location, date, quantityMt, key);
            movement.DocumentNumber = Clean(reference);
            await db.SaveChangesAsync(ct);
            await LogMovementAsync(movement, lot, ct);
            return key;
        }, ct);

    public Task ReverseConversionOutAsync(Guid operationKey, DateTime date, string reason, CancellationToken ct = default)
        => ReverseCoreAsync(operationKey, reason, false, ct, true, ToUtc(date));

    private Task ReverseCoreAsync(Guid operationKey, string reason, bool allowSaleReversal, CancellationToken ct, bool allowConversionReversal = false, DateTime? reversalDate = null)
        => InTransactionAsync(async () =>
        {
            if (string.IsNullOrWhiteSpace(reason))
                throw Rule("CUSTODY_REVERSAL_REASON", "دلیل لغو را بنویسید.");

            var lotIds = await db.CustodyMovements.AsNoTracking()
                .Where(m => m.OperationKey == operationKey)
                .Select(m => m.CustodyLotId).Distinct().ToArrayAsync(ct);
            if (lotIds.Length == 0)
                throw Rule("CUSTODY_OPERATION_NOT_FOUND", "عملیات پیدا نشد.");
            await LockLotsAsync(lotIds, ct);

            var originals = await db.CustodyMovements
                .Include(m => m.CustodyLot)
                .Where(m => m.OperationKey == operationKey && m.ReversalOfCustodyMovementId == null)
                .OrderBy(m => m.Id)
                .ToListAsync(ct);
            if (originals.Count == 0 || originals.All(m => m.IsReversed))
                throw Rule("CUSTODY_ALREADY_REVERSED", "این عملیات قبلاً لغو شده است.");
            if (originals.Any(m => m.IsReversed))
                throw Rule("CUSTODY_PARTIALLY_REVERSED", "این عملیات ناقص لغو شده است؛ با مدیر سیستم بررسی کنید.");
            foreach (var tankId in originals.Where(m => m.StorageTankId.HasValue)
                         .Select(m => m.StorageTankId!.Value).Distinct().OrderBy(id => id))
                await LockTankAsync(tankId, ct);
            // خروجِ فروش اثر مالی دارد؛ لغوِ تنهای موجودی، سند فروش و حساب مالک را ناهماهنگ می‌کند.
            if (!allowSaleReversal && originals.Any(m => m.Kind == CustodyMovementKind.Sale))
                throw Rule("CUSTODY_SALE_REVERSAL", "این خروج مربوط به فروش امانی است؛ آن را از صفحهٔ همان فروش لغو کنید.");

            if (!allowConversionReversal && originals.Any(m => m.Kind == CustodyMovementKind.ConvertedToCompany))
                throw Rule("CUSTODY_CONVERSION_REVERSAL", "این خروج مربوط به خرید قطعی است؛ تبدیل را از سند خودش لغو کنید.");

            var originalIds = originals.Select(o => o.Id).ToArray();
            // برگشت خروج، ورود فزیکی است. تنها افزایش خالص را می‌سنجیم تا انتقال مالکیت
            // (دو پای برابر در همان مخزن) ظرفیت را دوبار مصرف نکند.
            foreach (var group in originals.GroupBy(m => new { m.TerminalId, m.StorageTankId, m.CustodyLot!.ProductId })
                         .OrderBy(g => g.Key.StorageTankId))
            {
                var incoming = group.Sum(m => m.Direction == CustodyDirection.Out ? m.QuantityMt : -m.QuantityMt);
                if (incoming > 0m)
                    await EnsureInboundLocationAsync(new CustodyLocation(group.Key.TerminalId, group.Key.StorageTankId),
                        group.Key.ProductId, incoming, ct);
            }
            var reversalKey = Guid.NewGuid();
            var reversals = new List<CustodyMovement>();
            foreach (var original in originals)
            {
                original.IsReversed = true;
                original.UpdatedAtUtc = DateTime.UtcNow;
                original.UpdatedByUserId = currentUser?.UserId;
                var reversal = NewMovement(original.CustodyLot!, original.Kind,
                    original.Direction == CustodyDirection.In ? CustodyDirection.Out : CustodyDirection.In,
                    new CustodyLocation(original.TerminalId, original.StorageTankId),
                    reversalDate ?? original.MovementDate, original.QuantityMt, reversalKey);
                reversal.ReversalOfCustodyMovement = original;
                reversal.LossResponsibility = original.LossResponsibility;
                reversal.DocumentNumber = original.DocumentNumber;
                reversal.Notes = $"لغو: {reason.Trim()}";
                reversals.Add(reversal);
            }

            // لغوِ یک ورود نباید ماندهٔ هیچ تاریخی را منفی کند (مثلاً بعد از خروجِ بخشی از همان ورود).
            foreach (var group in originals.Where(m => m.Direction == CustodyDirection.In)
                         .GroupBy(m => new { m.CustodyLotId, m.TerminalId, m.StorageTankId }))
            {
                await EnsureRunningBalanceAsync(group.Key.CustodyLotId,
                    new CustodyLocation(group.Key.TerminalId, group.Key.StorageTankId),
                    pendingRemovedIds: group.Select(m => m.Id).ToHashSet(), ct);
            }

            foreach (var lot in originals.Select(m => m.CustodyLot!).DistinctBy(l => l.Id))
            {
                var removedIn = originals.Where(m => m.CustodyLotId == lot.Id && m.Direction == CustodyDirection.In).Sum(m => m.QuantityMt);
                var removedOut = originals.Where(m => m.CustodyLotId == lot.Id && m.Direction == CustodyDirection.Out).Sum(m => m.QuantityMt);
                var after = await LotBalanceAsync(lot.Id, ct) - removedIn + removedOut;
                var held = await HeldQuantityAsync(lot.Id, ct);
                if (after < held)
                    throw Rule("CUSTODY_REVERSAL_HELD", $"بعد از لغو، ماندهٔ بار {lot.LotNumber} کمتر از مقدار مسدود می‌شود. اول مسدودیت را رفع کنید.");

                var liveReceipts = await db.CustodyMovements.AsNoTracking()
                    .CountAsync(m => m.CustodyLotId == lot.Id && m.Kind == CustodyMovementKind.Receipt
                        && m.ReversalOfCustodyMovementId == null && !m.IsReversed
                        && !originalIds.Contains(m.Id), ct);
                if (lot.LoadedQuantityMt.HasValue && lot.ParentCustodyLotId == null && liveReceipts == 0
                    && originals.Any(o => o.CustodyLotId == lot.Id && o.Kind == CustodyMovementKind.Receipt))
                {
                    lot.Status = CustodyLotStatus.InTransit;
                }
            }

            await db.SaveChangesAsync(ct);
            foreach (var reversal in reversals)
            {
                await audit.LogAsync(MovementEntity, reversal.Id, AuditAction.Reverse, diff: Describe(new
                {
                    ReversalOf = reversal.ReversalOfCustodyMovementId,
                    Reason = reason.Trim(),
                    Kind = reversal.Kind.ToString(),
                    reversal.QuantityMt
                }), ct: ct);
            }
            await db.SaveChangesAsync(ct);
            return 0;
        }, ct);

    // ---------------------------------------------------------------- rules

    private async Task EnsureOutboundAllowedAsync(
        CustodyLot lot,
        CustodyLocation location,
        DateTime dateUtc,
        decimal quantityMt,
        CancellationToken ct,
        bool ignoreHolds = false)
    {
        var atLocation = await LocationBalanceAsync(lot.Id, location, null, ct);
        if (quantityMt > atLocation)
            throw Rule("CUSTODY_INSUFFICIENT", $"موجودی این بار در این محل {Format(atLocation)} تُن است.");

        if (!ignoreHolds)
        {
            var free = await LotBalanceAsync(lot.Id, ct) - await HeldQuantityAsync(lot.Id, ct);
            if (quantityMt > free)
                throw Rule("CUSTODY_HELD", $"مقدار آزادِ این بار {Format(Math.Max(free, 0m))} تُن است؛ باقی مسدود است.");
        }

        // خروجِ تاریخ‌گذشته نباید ماندهٔ هیچ روزِ بعد را منفی کند.
        var history = await MovementsAtLocationAsync(lot.Id, location, ct);
        var running = 0m;
        var inserted = false;
        foreach (var row in history)
        {
            if (!inserted && row.MovementDate > dateUtc)
            {
                running -= quantityMt;
                inserted = true;
                if (running < 0m) break;
            }
            running += row.Direction == CustodyDirection.In ? row.QuantityMt : -row.QuantityMt;
            if (running < 0m) break;
        }
        if (!inserted) running -= quantityMt;
        if (running < 0m)
            throw Rule("CUSTODY_BACKDATED_NEGATIVE", "این خروج با این تاریخ، ماندهٔ روزهای بعد را منفی می‌کند.");
    }

    private async Task EnsureRunningBalanceAsync(int lotId, CustodyLocation location, HashSet<int> pendingRemovedIds, CancellationToken ct)
    {
        var running = 0m;
        foreach (var row in await MovementsAtLocationAsync(lotId, location, ct))
        {
            if (pendingRemovedIds.Contains(row.Id)) continue;
            running += row.Direction == CustodyDirection.In ? row.QuantityMt : -row.QuantityMt;
            if (running < 0m)
                throw Rule("CUSTODY_REVERSAL_NEGATIVE", "لغوِ این ورود ممکن نیست؛ بخشی از آن قبلاً خارج شده است. اول خروج‌های بعدی را لغو کنید.");
        }
    }

    private async Task EnsureInboundLocationAsync(CustodyLocation location, int productId, decimal quantityMt, CancellationToken ct)
    {
        var terminal = await db.Terminals.AsNoTracking().FirstOrDefaultAsync(t => t.Id == location.TerminalId, ct)
            ?? throw Rule("CUSTODY_TERMINAL", "ترمینال پیدا نشد.");
        if (!terminal.IsActive)
            throw Rule("CUSTODY_TERMINAL_INACTIVE", "این ترمینال غیرفعال است.");
        if (terminal.CustodianPartyType.HasValue)
            throw Rule("CUSTODY_THIRD_PARTY_FACILITY", "تیلِ امانیِ دیگران فقط در مخازنِ خودِ ما ثبت می‌شود؛ این ترمینال متعلق به طرف دیگر است.");

        if (!location.StorageTankId.HasValue)
            return;

        await LockTankAsync(location.StorageTankId.Value, ct);
        var tank = await db.StorageTanks.AsNoTracking().FirstOrDefaultAsync(t => t.Id == location.StorageTankId.Value, ct)
            ?? throw Rule("CUSTODY_TANK", "مخزن پیدا نشد.");
        if (tank.TerminalId != location.TerminalId)
            throw Rule("CUSTODY_TANK_TERMINAL", "این مخزن مربوط به ترمینالِ انتخاب‌شده نیست.");
        if (!tank.IsActive)
            throw Rule("CUSTODY_TANK_INACTIVE", "این مخزن غیرفعال است.");
        if (tank.ProductId.HasValue && tank.ProductId.Value != productId)
            throw Rule("CUSTODY_TANK_PRODUCT", "این مخزن برای جنسِ دیگری تعریف شده است؛ اختلاطِ جنس‌های مختلف مجاز نیست.");

        // جنسِ دیگری (ملکی یا امانی) اکنون در همین مخزن هست؟ اختلاط بدون مجوز ممنوع.
        var companyOther = await db.InventoryMovements.AsNoTracking()
            .Where(m => m.StorageTankId == tank.Id && m.ProductId != productId)
            .GroupBy(m => m.ProductId)
            .Where(g => g.Sum(m =>
                m.Direction == MovementDirection.In || m.Direction == MovementDirection.Adjustment ? m.QuantityMt
                : m.Direction == MovementDirection.Out || m.Direction == MovementDirection.Transfer ? -m.QuantityMt : 0m) > 0m)
            .AnyAsync(ct);
        var custodyOther = await db.CustodyMovements.AsNoTracking()
            .Where(m => m.StorageTankId == tank.Id && m.CustodyLot!.ProductId != productId)
            .GroupBy(m => m.CustodyLot!.ProductId)
            .Where(g => g.Sum(m => m.Direction == CustodyDirection.In ? m.QuantityMt : -m.QuantityMt) > 0m)
            .AnyAsync(ct);
        if (companyOther || custodyOther)
            throw Rule("CUSTODY_TANK_MIXED", "در این مخزن جنسِ دیگری موجود است؛ اختلاطِ جنس‌های مختلف مجاز نیست.");

        if (tank.CapacityMt > 0m)
        {
            var physical = await CompanyTankQuantityAsync(tank.Id, ct) + await CustodyTankQuantityAsync(tank.Id, ct);
            if (physical + quantityMt > tank.CapacityMt)
                throw Rule("CUSTODY_TANK_CAPACITY",
                    $"ظرفیت مخزن {Format(tank.CapacityMt)} تُن است و اکنون {Format(physical)} تُن در آن است.");
        }
    }

    private async Task EnsureOwnerAsync(CustodyOwner owner, CancellationToken ct)
    {
        if (!OwnerPartyTypes.Contains(owner.PartyType) || owner.PartyId <= 0)
            throw Rule("CUSTODY_OWNER_TYPE", "مالک را انتخاب کنید.");

        var exists = owner.PartyType switch
        {
            AccountingPartyType.Customer => await db.Customers.AnyAsync(x => x.Id == owner.PartyId, ct),
            AccountingPartyType.Supplier => await db.Suppliers.AnyAsync(x => x.Id == owner.PartyId, ct),
            AccountingPartyType.Partner => await db.Partners.AnyAsync(x => x.Id == owner.PartyId, ct),
            AccountingPartyType.Company => await db.Companies.AnyAsync(x => x.Id == owner.PartyId, ct),
            _ => false
        };
        if (!exists)
            throw Rule("CUSTODY_OWNER_NOT_FOUND", "مالک پیدا نشد.");

        // تیلِ خودِ شرکت امانی نیست؛ موجودی ملکی است و مسیر خودش را دارد. تبدیلِ امانی به
        // خریدِ شرکت تصمیم حسابداری لازم دارد و در این مرحله پیاده نشده است.
        if (owner.PartyType == AccountingPartyType.Company
            && await db.Companies.AnyAsync(x => x.Id == owner.PartyId && x.IsSystemOwner, ct))
        {
            throw Rule("CUSTODY_OWNER_IS_COMPANY", "شرکتِ خودِ ما نمی‌تواند مالکِ تیلِ امانی باشد.");
        }
    }

    private async Task EnsureProductAsync(int productId, CancellationToken ct)
    {
        if (!await db.Products.AnyAsync(p => p.Id == productId, ct))
            throw Rule("CUSTODY_PRODUCT", "جنس را انتخاب کنید.");
    }

    // ---------------------------------------------------------------- quantities

    private Task<decimal> LotBalanceAsync(int lotId, CancellationToken ct)
        => db.CustodyMovements.AsNoTracking()
            .Where(m => m.CustodyLotId == lotId)
            .SumAsync(m => m.Direction == CustodyDirection.In ? m.QuantityMt : -m.QuantityMt, ct);

    private Task<decimal> HeldQuantityAsync(int lotId, CancellationToken ct)
        => db.CustodyHolds.AsNoTracking()
            .Where(h => h.CustodyLotId == lotId && h.ReleasedAtUtc == null)
            .SumAsync(h => h.QuantityMt, ct);

    private async Task<decimal> LocationBalanceAsync(int lotId, CustodyLocation location, DateTime? asOf, CancellationToken ct)
    {
        var query = db.CustodyMovements.AsNoTracking()
            .Where(m => m.CustodyLotId == lotId && m.TerminalId == location.TerminalId && m.StorageTankId == location.StorageTankId);
        if (asOf.HasValue) query = query.Where(m => m.MovementDate <= asOf.Value);
        return await query.SumAsync(m => m.Direction == CustodyDirection.In ? m.QuantityMt : -m.QuantityMt, ct);
    }

    private Task<List<CustodyMovement>> MovementsAtLocationAsync(int lotId, CustodyLocation location, CancellationToken ct)
        => db.CustodyMovements.AsNoTracking()
            .Where(m => m.CustodyLotId == lotId && m.TerminalId == location.TerminalId && m.StorageTankId == location.StorageTankId)
            .OrderBy(m => m.MovementDate).ThenBy(m => m.Id)
            .ToListAsync(ct);

    private Task<decimal> CustodyTankQuantityAsync(int tankId, CancellationToken ct)
        => db.CustodyMovements.AsNoTracking()
            .Where(m => m.StorageTankId == tankId)
            .SumAsync(m => m.Direction == CustodyDirection.In ? m.QuantityMt : -m.QuantityMt, ct);

    /// <summary>موجودی ملکی در مخزن با همان قاعدهٔ علامتِ StockService.</summary>
    private Task<decimal> CompanyTankQuantityAsync(int tankId, CancellationToken ct)
        => db.InventoryMovements.AsNoTracking()
            .Where(m => m.StorageTankId == tankId)
            .SumAsync(m =>
                m.Direction == MovementDirection.In || m.Direction == MovementDirection.Adjustment ? m.QuantityMt
                : m.Direction == MovementDirection.Out || m.Direction == MovementDirection.Transfer ? -m.QuantityMt : 0m, ct);

    // ---------------------------------------------------------------- plumbing

    private async Task<T> InTransactionAsync<T>(Func<Task<T>> work, CancellationToken ct)
    {
        if (!db.Database.IsRelational() || db.Database.CurrentTransaction is not null)
            return await work();

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var result = await work();
        await transaction.CommitAsync(ct);
        return result;
    }

    private bool IsPostgres
        => db.Database.IsRelational()
            && string.Equals(db.Database.ProviderName, "Npgsql.EntityFrameworkCore.PostgreSQL", StringComparison.Ordinal);

    /// <summary>
    /// قفلِ ردیفیِ بارها به ترتیبِ شناسه — دو خروجِ هم‌زمان از یک بار پشتِ هم اجرا می‌شوند و
    /// هر دو از بررسیِ موجودی عبور نمی‌کنند.
    /// </summary>
    private async Task LockLotsAsync(int[] lotIds, CancellationToken ct)
    {
        if (!IsPostgres || lotIds.Length == 0) return;
        var ids = lotIds.Distinct().OrderBy(x => x).ToArray();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $@"SELECT ""Id"" FROM ""CustodyLots"" WHERE ""Id"" = ANY({ids}) ORDER BY ""Id"" FOR UPDATE", ct);
    }

    /// <summary>همان قفلِ مخزن که StockService برای موجودی ملکی می‌گیرد؛ ظرفیتِ مشترک سریالی می‌شود.</summary>
    private async Task LockTankAsync(int tankId, CancellationToken ct)
    {
        if (!IsPostgres) return;
        await db.Database.ExecuteSqlInterpolatedAsync(
            $@"SELECT ""Id"" FROM ""StorageTanks"" WHERE ""Id"" = {tankId} FOR UPDATE", ct);
    }

    private async Task<CustodyLot> LoadLotAsync(int lotId, CancellationToken ct)
        => await db.CustodyLots.FirstOrDefaultAsync(l => l.Id == lotId, ct)
            ?? throw Rule("CUSTODY_LOT_NOT_FOUND", "بار امانی پیدا نشد.");

    private async Task<CustodyLot> LoadActiveLotAsync(int lotId, CancellationToken ct)
    {
        var lot = await LoadLotAsync(lotId, ct);
        if (lot.Status != CustodyLotStatus.Active)
            throw Rule("CUSTODY_LOT_NOT_ACTIVE", lot.Status == CustodyLotStatus.InTransit
                ? "این بار هنوز در راه است و رسید نشده."
                : "این بار لغو شده است.");
        return lot;
    }

    private CustodyLot NewLot(CustodyOwner owner, int productId, CustodyLotStatus status) => new()
    {
        LotNumber = "TMP-" + Guid.NewGuid().ToString("N"),
        OwnerPartyType = owner.PartyType,
        OwnerPartyId = owner.PartyId,
        ProductId = productId,
        Status = status,
        CreatedByUserId = currentUser?.UserId
    };

    private CustodyMovement NewMovement(
        CustodyLot lot,
        CustodyMovementKind kind,
        CustodyDirection direction,
        CustodyLocation location,
        DateTime date,
        decimal quantityMt,
        Guid operationKey)
    {
        var movement = new CustodyMovement
        {
            CustodyLot = lot,
            Kind = kind,
            Direction = direction,
            TerminalId = location.TerminalId,
            StorageTankId = location.StorageTankId,
            MovementDate = ToUtc(date),
            QuantityMt = quantityMt,
            OperationKey = operationKey,
            CreatedByUserId = currentUser?.UserId
        };
        db.CustodyMovements.Add(movement);
        return movement;
    }

    private async Task AssignLotNumberAsync(CustodyLot lot, CancellationToken ct)
    {
        lot.LotNumber = $"AM-{lot.Id:D5}";
        await db.SaveChangesAsync(ct);
    }

    private Task LogMovementAsync(CustodyMovement movement, CustodyLot lot, CancellationToken ct)
        => audit.LogAsync(MovementEntity, movement.Id, AuditAction.Insert, diff: Describe(new
        {
            lot.LotNumber,
            Kind = movement.Kind.ToString(),
            Direction = movement.Direction.ToString(),
            movement.TerminalId,
            movement.StorageTankId,
            movement.QuantityMt,
            movement.MovementDate,
            movement.OperationKey
        }), ct: ct);

    private static void RequirePositive(decimal value, string label)
    {
        if (value <= 0m)
            throw Rule("CUSTODY_QUANTITY", $"{label} باید بیشتر از صفر باشد.");
        if (decimal.Round(value, 4) != value)
            throw Rule("CUSTODY_PRECISION", $"{label} حداکثر چهار رقم اعشار دارد.");
    }

    private static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Format(decimal value) => value.ToString("#,0.####");

    private static string Describe(object value) => JsonSerializer.Serialize(value);

    private static BusinessRuleException Rule(string code, string message) => new(code, message);
}
