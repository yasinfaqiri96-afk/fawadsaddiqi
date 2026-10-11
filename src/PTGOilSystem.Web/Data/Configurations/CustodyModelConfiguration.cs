using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Models.Entities;

namespace PTGOilSystem.Web.Data.Configurations;

public static class CustodyModelConfiguration
{
    public static void ConfigureCustody(this ModelBuilder modelBuilder)
    {
        var identity = modelBuilder.Entity<CustodySupplierIdentityLink>();
        identity.ToTable("CustodySupplierIdentityLinks", t => t.HasCheckConstraint("CK_CustodyIdentity_OneRole",
            "(\"CustomerId\" IS NOT NULL AND \"PartnerId\" IS NULL) OR (\"PartnerId\" IS NOT NULL AND \"CustomerId\" IS NULL)"));
        identity.HasIndex(x => x.CustomerId).IsUnique();
        identity.HasIndex(x => x.PartnerId).IsUnique();
        identity.HasOne<Customer>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
        identity.HasOne<Partner>().WithMany().HasForeignKey(x => x.PartnerId).OnDelete(DeleteBehavior.Restrict);
        identity.HasOne<Supplier>().WithMany().HasForeignKey(x => x.SupplierId).OnDelete(DeleteBehavior.Restrict);

        var conversion = modelBuilder.Entity<CustodyConversion>();
        conversion.ToTable("CustodyConversions", t => t.HasCheckConstraint("CK_CustodyConversion_Quantity", "\"QuantityMt\" > 0"));
        conversion.Property(x => x.QuantityMt).HasColumnType("numeric(18,4)");
        conversion.HasIndex(x => x.ContractId).IsUnique();
        conversion.HasIndex(x => x.StockOperationKey).IsUnique();
        conversion.HasOne(x => x.CustodyLot).WithMany().HasForeignKey(x => x.CustodyLotId).OnDelete(DeleteBehavior.Restrict);
        conversion.HasOne(x => x.Contract).WithMany().HasForeignKey(x => x.ContractId).OnDelete(DeleteBehavior.Restrict);

        var lot = modelBuilder.Entity<CustodyLot>();
        lot.ToTable("CustodyLots", table =>
        {
            table.HasCheckConstraint("CK_CustodyLots_LoadedQuantity", "\"LoadedQuantityMt\" IS NULL OR \"LoadedQuantityMt\" > 0");
        });
        lot.Property(x => x.LoadedQuantityMt).HasColumnType("numeric(18,4)");
        lot.HasIndex(x => x.LotNumber).IsUnique();
        lot.HasIndex(x => new { x.OwnerPartyType, x.OwnerPartyId });
        lot.HasIndex(x => x.ProductId);
        lot.HasIndex(x => x.ParentCustodyLotId);
        lot.HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId)
            .OnDelete(DeleteBehavior.Restrict);
        lot.HasOne(x => x.ParentCustodyLot).WithMany().HasForeignKey(x => x.ParentCustodyLotId)
            .OnDelete(DeleteBehavior.Restrict);

        var movement = modelBuilder.Entity<CustodyMovement>();
        movement.ToTable("CustodyMovements", table =>
        {
            table.HasCheckConstraint("CK_CustodyMovements_PositiveQuantity", "\"QuantityMt\" > 0");
        });
        movement.Property(x => x.QuantityMt).HasColumnType("numeric(18,4)");
        movement.HasIndex(x => new { x.CustodyLotId, x.TerminalId, x.StorageTankId });
        movement.HasIndex(x => new { x.TerminalId, x.StorageTankId });
        movement.HasIndex(x => x.OperationKey);
        // یک حرکت فقط یک‌بار لغو می‌شود — گارد نهایی در سطح دیتابیس.
        movement.HasIndex(x => x.ReversalOfCustodyMovementId).IsUnique();
        movement.HasOne(x => x.CustodyLot).WithMany(x => x.Movements).HasForeignKey(x => x.CustodyLotId)
            .OnDelete(DeleteBehavior.Restrict);
        movement.HasOne(x => x.Terminal).WithMany().HasForeignKey(x => x.TerminalId)
            .OnDelete(DeleteBehavior.Restrict);
        movement.HasOne(x => x.StorageTank).WithMany().HasForeignKey(x => x.StorageTankId)
            .OnDelete(DeleteBehavior.Restrict);
        movement.HasOne(x => x.ReversalOfCustodyMovement).WithMany().HasForeignKey(x => x.ReversalOfCustodyMovementId)
            .OnDelete(DeleteBehavior.Restrict);

        var hold = modelBuilder.Entity<CustodyHold>();
        hold.ToTable("CustodyHolds", table =>
        {
            table.HasCheckConstraint("CK_CustodyHolds_PositiveQuantity", "\"QuantityMt\" > 0");
        });
        hold.Property(x => x.QuantityMt).HasColumnType("numeric(18,4)");
        hold.HasIndex(x => x.CustodyLotId);
        hold.HasOne(x => x.CustodyLot).WithMany(x => x.Holds).HasForeignKey(x => x.CustodyLotId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<Terminal>().HasIndex(x => new { x.CustodianPartyType, x.CustodianPartyId });

        var sale = modelBuilder.Entity<CustodySale>();
        sale.ToTable("CustodySales", table =>
        {
            table.HasCheckConstraint("CK_CustodySales_Quantity", "\"QuantityMt\" > 0");
            table.HasCheckConstraint("CK_CustodySales_Amounts",
                "\"AmountUsd\" > 0 AND \"CommissionUsd\" >= 0 AND \"CommissionUsd\" <= \"AmountUsd\""
                + " AND \"CommissionRecognizedAtSaleUsd\" >= 0 AND \"CommissionRecognizedAtSaleUsd\" <= \"CommissionUsd\"");
        });
        sale.Property(x => x.QuantityMt).HasColumnType("numeric(18,4)");
        sale.Property(x => x.UnitPriceUsd).HasColumnType("numeric(18,4)");
        sale.Property(x => x.AmountUsd).HasColumnType("numeric(18,4)");
        sale.Property(x => x.CommissionRate).HasColumnType("numeric(18,6)");
        sale.Property(x => x.CommissionUsd).HasColumnType("numeric(18,4)");
        sale.Property(x => x.CommissionRecognizedAtSaleUsd).HasColumnType("numeric(18,4)");
        sale.HasIndex(x => x.SaleNumber).IsUnique();
        sale.HasIndex(x => x.StockOperationKey).IsUnique();
        sale.HasIndex(x => new { x.OwnerPartyType, x.OwnerPartyId });
        sale.HasIndex(x => x.CustomerId);
        sale.HasIndex(x => x.CustodyLotId);
        sale.HasOne(x => x.CustodyLot).WithMany().HasForeignKey(x => x.CustodyLotId).OnDelete(DeleteBehavior.Restrict);
        sale.HasOne(x => x.Customer).WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);

        var finance = modelBuilder.Entity<CustodyFinanceEntry>();
        finance.ToTable("CustodyFinanceEntries", table =>
        {
            table.HasCheckConstraint("CK_CustodyFinanceEntries_Amounts",
                "\"AmountUsd\" > 0 AND \"CommissionUsd\" >= 0 AND \"CommissionUsd\" <= \"AmountUsd\"");
        });
        finance.Property(x => x.AmountUsd).HasColumnType("numeric(18,4)");
        finance.Property(x => x.CommissionUsd).HasColumnType("numeric(18,4)");
        finance.HasIndex(x => new { x.OwnerPartyType, x.OwnerPartyId });
        finance.HasIndex(x => x.CustodySaleId);
        finance.HasIndex(x => x.CustodyLotId);
        // یک پرداخت نقدی فقط به یک سند امانی تعلق دارد.
        finance.HasIndex(x => x.PaymentTransactionId).IsUnique();
        finance.HasIndex(x => x.ReversalPaymentTransactionId).IsUnique();
        finance.HasOne(x => x.CustodySale).WithMany().HasForeignKey(x => x.CustodySaleId).OnDelete(DeleteBehavior.Restrict);
        finance.HasOne(x => x.CustodyLot).WithMany().HasForeignKey(x => x.CustodyLotId).OnDelete(DeleteBehavior.Restrict);
        finance.HasOne(x => x.CashAccount).WithMany().HasForeignKey(x => x.CashAccountId).OnDelete(DeleteBehavior.Restrict);
        finance.HasOne(x => x.PaymentTransaction).WithMany().HasForeignKey(x => x.PaymentTransactionId).OnDelete(DeleteBehavior.Restrict);
        finance.HasOne<PaymentTransaction>().WithMany().HasForeignKey(x => x.ReversalPaymentTransactionId).OnDelete(DeleteBehavior.Restrict);
        finance.HasOne(x => x.ExpenseType).WithMany().HasForeignKey(x => x.ExpenseTypeId).OnDelete(DeleteBehavior.Restrict);
    }
}
