using System.Linq.Expressions;
using PTGOilSystem.Web.Models.Entities;

namespace PTGOilSystem.Web.Services;

/// <summary>Shared read projection for owned stock. Custody never enters this query.</summary>
public static class InventoryStockQuery
{
    public static readonly Expression<Func<InventoryMovement, InventoryStockQuantity>> Projection = m => new InventoryStockQuantity
    {
        ProductId = m.ProductId,
        ProductName = m.Product != null ? m.Product.Name : "",
        TerminalId = m.TerminalId,
        StorageTankId = m.StorageTankId,
        ContractId = m.ContractId ?? (m.LoadingReceipt != null && m.LoadingReceipt.LoadingRegister != null
            ? (int?)m.LoadingReceipt.LoadingRegister.ContractId : null),
        CompanyId = m.Contract != null ? (int?)m.Contract.CompanyId
            : m.LoadingReceipt != null && m.LoadingReceipt.LoadingRegister != null
                && m.LoadingReceipt.LoadingRegister.Contract != null
                ? (int?)m.LoadingReceipt.LoadingRegister.Contract.CompanyId : null,
        MovementDate = m.MovementDate,
        SignedQuantityMt = m.Direction == MovementDirection.In || m.Direction == MovementDirection.Adjustment
            ? m.QuantityMt : m.Direction == MovementDirection.Out || m.Direction == MovementDirection.Transfer
                ? -m.QuantityMt : 0m
    };

    public static IQueryable<InventoryStockQuantity> Quantities(this IQueryable<InventoryMovement> query)
        => query.Select(Projection);
}

public sealed class InventoryStockQuantity
{
    public int ProductId { get; init; }
    public string ProductName { get; init; } = "";
    public int TerminalId { get; init; }
    public int? StorageTankId { get; init; }
    public int? ContractId { get; init; }
    public int? CompanyId { get; init; }
    public DateTime MovementDate { get; init; }
    public decimal SignedQuantityMt { get; init; }
}
