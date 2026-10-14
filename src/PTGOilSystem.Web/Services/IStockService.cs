using System;
using System.Threading;
using System.Threading.Tasks;
using PTGOilSystem.Web.Models.Entities;

namespace PTGOilSystem.Web.Services;

/// <summary>
/// Stock control service.
///
/// System rules enforced here:
///   #3 — No Sale beyond free stock
///   #4 — Stock is computed only from <see cref="InventoryMovement"/> rows;
///        no manual on-hand field is ever read or written.
///
/// All quantities are <see cref="decimal"/> (system rule #1).
/// </summary>
public interface IStockService
{
    /// <summary>
    /// Computes free quantity (in MT) available at a terminal for a product
    /// (optionally scoped by contract / batch) as of <paramref name="asOfUtc"/>.
    ///
    /// free = sum(In + Adjustment positive) - sum(Out) for the given filter.
    /// Transfers cancel out across terminals when <paramref name="terminalId"/>
    /// is null; otherwise In/Out at the terminal are counted normally.
    /// </summary>
    Task<decimal> GetFreeQuantityMtAsync(
        int productId,
        int? terminalId = null,
        int? contractId = null,
        int? inventoryBatchId = null,
        int? storageTankId = null,
        DateTime? asOfUtc = null,
        CancellationToken ct = default);

    /// <summary>
    /// Computes total free quantity (in MT) across all products for the given
    /// scope, reusing the same stock rules as <see cref="GetFreeQuantityMtAsync"/>.
    /// </summary>
    Task<decimal> GetTotalFreeQuantityMtAsync(
        int? terminalId = null,
        DateTime? asOfUtc = null,
        CancellationToken ct = default);

    /// <summary>
    /// Returns per-tank free quantity (in MT) for a given product + purchase
    /// contract, using the same signed-sum stock rules as
    /// <see cref="GetFreeQuantityMtAsync"/>. Only tanks with a positive free
    /// balance are returned (movements without a storage tank are excluded).
    /// Read-only; used to offer source tanks for shipment/transport allocation.
    /// </summary>
    Task<IReadOnlyList<TankStockItem>> GetTankAvailabilityAsync(
        int productId,
        int contractId,
        DateTime? asOfUtc = null,
        CancellationToken ct = default);

    /// <summary>
    /// Returns grouped stock balances using the same movement-based stock logic
    /// as <see cref="GetFreeQuantityMtAsync"/>.
    /// </summary>
    Task<IReadOnlyList<StockSummaryItem>> GetStockSummaryAsync(
        int? productId = null,
        int? contractId = null,
        int? terminalId = null,
        DateTime? asOfUtc = null,
        CancellationToken ct = default);

    /// <summary>
    /// Returns stock card rows with running balance per stock scope using the
    /// same movement sign rules enforced by this service.
    /// </summary>
    Task<IReadOnlyList<StockCardItem>> GetStockCardAsync(
        int? productId = null,
        int? contractId = null,
        int? terminalId = null,
        int? storageTankId = null,
        DateTime? fromUtc = null,
        DateTime? toUtc = null,
        CancellationToken ct = default);

    /// <summary>
    /// Server-side movement aggregation for reports. Opening and closing use the
    /// exact stock sign policy; period movement counts exclude opening rows.
    /// </summary>
    Task<IReadOnlyList<StockMovementSummaryItem>> GetMovementSummaryAsync(
        int? productId = null,
        int? contractId = null,
        int? terminalId = null,
        int? storageTankId = null,
        DateTime? fromUtc = null,
        DateTime? toUtc = null,
        CancellationToken ct = default);

    /// <summary>
    /// Throws <see cref="Exceptions.BusinessRuleException"/> if the requested
    /// outgoing quantity exceeds the free stock for the same scope.
    /// Use this before persisting an Out-direction <see cref="InventoryMovement"/>.
    /// </summary>
    Task EnsureSufficientStockForMovementAsync(
        InventoryMovement movement,
        CancellationToken ct = default);

    /// <summary>
    /// Serializes concurrent stock mutations that touch the same scope, closing the
    /// check-then-write race that lets two callers each pass the availability check
    /// and jointly oversell. Takes a row-level <c>FOR UPDATE</c> lock on the storage
    /// tank when the movement is tank-scoped, otherwise a transaction-scoped Postgres
    /// advisory lock keyed by product.
    /// <b>Must be called inside an open transaction</b>, immediately before
    /// <see cref="EnsureSufficientStockForMovementAsync"/>, so the lock spans the
    /// subsequent write. Re-locking a row/key already held by the same transaction
    /// is a harmless no-op, so it is safe on paths that already lock. On non-relational
    /// or non-PostgreSQL providers it is a no-op.
    /// </summary>
    Task AcquireStockMutationLockAsync(
        InventoryMovement movement,
        CancellationToken ct = default);

    /// <summary>
    /// Forward-pass check that prevents a backdated Out/Transfer movement from
    /// causing any <i>subsequent</i> running balance to go negative.
    ///
    /// For new (unpersisted) movements pass <see cref="InventoryMovement.Id"/> = 0;
    /// for updates pass the existing <see cref="InventoryMovement.Id"/> so the
    /// pre-image row is excluded from the simulation.
    /// In/Adjustment movements never reduce stock and are skipped.
    /// </summary>
    Task EnsureMovementDoesNotCauseFutureNegativeStockAsync(
        InventoryMovement movement,
        CancellationToken ct = default);

    Task EnsureMovementTimelineIsNonNegativeAsync(InventoryMovement movement, CancellationToken ct = default)
        => EnsureMovementDoesNotCauseFutureNegativeStockAsync(movement, ct);

    /// <summary>
    /// <b>Do not use.</b>
    /// <see cref="SalesTransaction.ContractId"/> represents the <i>Sales</i> contract,
    /// not the source <i>Purchase</i> contract that backs the stock. Using it as a
    /// stock filter would compare the wrong dimensions and silently mis-validate sales.
    /// Always call <see cref="EnsureSufficientStockForSaleAsync(SalesTransaction, int?, CancellationToken)"/>
    /// with an explicit <c>sourcePurchaseContractId</c>, or call
    /// <see cref="EnsureSufficientStockForMovementAsync"/> with a fully populated
    /// <see cref="InventoryMovement"/> instead.
    /// </summary>
    [Obsolete("Use EnsureSufficientStockForSaleAsync(sale, sourcePurchaseContractId, ct) — sale.ContractId is the Sales contract and is NOT a valid stock filter.", error: true)]
    Task EnsureSufficientStockForSaleAsync(
        SalesTransaction sale,
        CancellationToken ct = default);

    /// <summary>
    /// Throws <see cref="Exceptions.BusinessRuleException"/> if the sale would
    /// drive free stock below zero. Stock is filtered by product and the
    /// caller-provided <paramref name="sourcePurchaseContractId"/> (which must be
    /// the Purchase contract that backs the stock — not the Sales contract).
    /// Pass <c>null</c> to scope by product only.
    /// </summary>
    Task EnsureSufficientStockForSaleAsync(
        SalesTransaction sale,
        int? sourcePurchaseContractId,
        CancellationToken ct = default);
}

public sealed record TankStockItem(
    int StorageTankId,
    string TankCode,
    int TerminalId,
    string TerminalName,
    decimal FreeQuantityMt);

public sealed record StockSummaryItem(
    int ProductId,
    string ProductCode,
    string ProductName,
    int TerminalId,
    string TerminalCode,
    string TerminalName,
    int? ContractId,
    string? ContractNumber,
    decimal FreeQuantityMt,
    DateTime LastMovementDate,
    int MovementCount);

public sealed record StockCardItem(
    int MovementId,
    DateTime MovementDate,
    MovementDirection Direction,
    decimal QuantityMt,
    decimal SignedQuantityMt,
    decimal RunningBalanceMt,
    int ProductId,
    string ProductCode,
    string ProductName,
    int TerminalId,
    string TerminalCode,
    string TerminalName,
    int? ContractId,
    string? ContractNumber,
    int? StorageTankId,
    string? StorageTankCode,
    string? ReferenceDocument,
    string? Notes);

public sealed record StockMovementSummaryItem(
    string ProductName,
    string TerminalName,
    string? StorageTankCode,
    int? ContractId,
    decimal OpeningQuantityMt,
    decimal InQuantityMt,
    decimal OutQuantityMt,
    decimal AdjustmentQuantityMt,
    decimal TransferQuantityMt,
    decimal ClosingQuantityMt,
    int MovementCount,
    DateTime? LastMovementDate);
