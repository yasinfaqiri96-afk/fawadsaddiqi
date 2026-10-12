using System.ComponentModel.DataAnnotations;

namespace PTGOilSystem.Web.Models.Entities;

/// <summary>Explicit, evidenced link between existing role records of the same person; never creates a person.</summary>
public class CustodySupplierIdentityLink : BaseEntity
{
    public int? CustomerId { get; set; }
    public int? PartnerId { get; set; }
    /// <summary>شرکتِ دیگر (غیرمالکِ سیستم) که همان شخصِ حقوقیِ نقش فروشنده است.</summary>
    public int? CompanyId { get; set; }
    public int SupplierId { get; set; }
    [Required, MaxLength(500)] public string Evidence { get; set; } = "";
}

/// <summary>Ownership change linked to the canonical purchase, not a second financial ledger.</summary>
public class CustodyConversion : BaseEntity
{
    public int CustodyLotId { get; set; }
    public CustodyLot? CustodyLot { get; set; }
    public int ContractId { get; set; }
    public Contract? Contract { get; set; }
    public Guid StockOperationKey { get; set; }
    public DateTime ConversionDate { get; set; }
    public decimal QuantityMt { get; set; }
    [Required, MaxLength(500)] public string AgreementReference { get; set; } = "";
    public CustodyDocumentStatus Status { get; set; } = CustodyDocumentStatus.Posted;
    public DateTime? CancelledAtUtc { get; set; }
    [MaxLength(500)] public string? CancellationReason { get; set; }
}
