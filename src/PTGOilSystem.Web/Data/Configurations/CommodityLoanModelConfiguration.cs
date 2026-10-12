using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Models.Entities;

namespace PTGOilSystem.Web.Data.Configurations;

public static class CommodityLoanModelConfiguration
{
    public static void ConfigureCommodityLoans(this ModelBuilder modelBuilder)
    {
        var loan = modelBuilder.Entity<CommodityLoan>();
        loan.ToTable("CommodityLoans", table =>
        {
            table.HasCheckConstraint("CK_CommodityLoans_Quantity", "\"QuantityMt\" > 0");
            table.HasCheckConstraint("CK_CommodityLoans_Due", "\"DueDate\" >= \"LoanDate\"");
            // قرض‌دادن فقط به بهای دفتری؛ قرض‌گرفتن فقط به ارزش توافقیِ مثبت با مرجع و مبنای قرض.
            table.HasCheckConstraint("CK_CommodityLoans_Terms",
                "(\"Direction\" <> 1 OR \"ValuationPolicy\" IS NULL OR \"ValuationPolicy\" = 1)"
                + " AND (\"Direction\" <> 2 OR \"BorrowBasis\" IS NOT NULL)"
                + " AND (\"Direction\" <> 2 OR \"ValuationPolicy\" IS NULL OR (\"ValuationPolicy\" = 2 AND \"AgreedUnitValueUsd\" > 0 AND \"ValuationReference\" IS NOT NULL))"
                + " AND (NOT \"CashSettlementAllowed\" OR (\"CashSettlementUnitPriceUsd\" > 0 AND \"CashSettlementReference\" IS NOT NULL))"
                + " AND (\"QuantityTolerancePercent\" IS NULL OR (\"QuantityTolerancePercent\" >= 0 AND \"QuantityTolerancePercent\" <= 10))");
        });
        loan.Property(x => x.QuantityMt).HasColumnType("numeric(18,4)");
        loan.Property(x => x.AgreedUnitValueUsd).HasColumnType("numeric(18,4)");
        loan.Property(x => x.CarryingValueUsd).HasColumnType("numeric(18,4)");
        loan.Property(x => x.QuantityTolerancePercent).HasColumnType("numeric(9,4)");
        loan.Property(x => x.CashSettlementUnitPriceUsd).HasColumnType("numeric(18,4)");
        loan.HasIndex(x => x.LoanNumber).IsUnique();
        loan.HasIndex(x => x.InventoryMovementId).IsUnique();
        loan.HasIndex(x => new { x.CounterpartyType, x.CounterpartyId });
        loan.HasIndex(x => x.ProductId);
        loan.HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
        loan.HasOne<Terminal>().WithMany().HasForeignKey(x => x.TerminalId).OnDelete(DeleteBehavior.Restrict);
        loan.HasOne<StorageTank>().WithMany().HasForeignKey(x => x.StorageTankId).OnDelete(DeleteBehavior.Restrict);
        loan.HasOne<InventoryMovement>().WithMany().HasForeignKey(x => x.InventoryMovementId).OnDelete(DeleteBehavior.Restrict);
        loan.HasOne<Account>().WithMany().HasForeignKey(x => x.LoanAccountId).OnDelete(DeleteBehavior.Restrict);
        loan.HasOne<Account>().WithMany().HasForeignKey(x => x.DifferenceAccountId).OnDelete(DeleteBehavior.Restrict);

        var settlement = modelBuilder.Entity<CommodityLoanSettlement>();
        settlement.ToTable("CommodityLoanSettlements", table =>
        {
            table.HasCheckConstraint("CK_CommodityLoanSettlements_Quantity", "\"QuantityMt\" > 0");
            table.HasCheckConstraint("CK_CommodityLoanSettlements_Kind",
                "(\"Kind\" <> 1 OR (\"TerminalId\" IS NOT NULL AND \"InventoryMovementId\" IS NOT NULL))"
                + " AND (\"Kind\" <> 2 OR (\"CashAmountUsd\" > 0 AND \"CashAccountId\" IS NOT NULL))");
        });
        settlement.Property(x => x.QuantityMt).HasColumnType("numeric(18,4)");
        settlement.Property(x => x.CarryingValueUsd).HasColumnType("numeric(18,4)");
        settlement.Property(x => x.InventoryValueUsd).HasColumnType("numeric(18,4)");
        settlement.Property(x => x.CashAmountUsd).HasColumnType("numeric(18,4)");
        settlement.Property(x => x.DifferenceUsd).HasColumnType("numeric(18,4)");
        settlement.HasIndex(x => x.CommodityLoanId);
        settlement.HasIndex(x => x.InventoryMovementId).IsUnique();
        settlement.HasIndex(x => x.PaymentTransactionId).IsUnique();
        settlement.HasIndex(x => x.ReversalPaymentTransactionId).IsUnique();
        settlement.HasOne(x => x.CommodityLoan).WithMany(x => x.Settlements).HasForeignKey(x => x.CommodityLoanId).OnDelete(DeleteBehavior.Restrict);
        settlement.HasOne<Terminal>().WithMany().HasForeignKey(x => x.TerminalId).OnDelete(DeleteBehavior.Restrict);
        settlement.HasOne<StorageTank>().WithMany().HasForeignKey(x => x.StorageTankId).OnDelete(DeleteBehavior.Restrict);
        settlement.HasOne<InventoryMovement>().WithMany().HasForeignKey(x => x.InventoryMovementId).OnDelete(DeleteBehavior.Restrict);
        settlement.HasOne<CashAccount>().WithMany().HasForeignKey(x => x.CashAccountId).OnDelete(DeleteBehavior.Restrict);
        settlement.HasOne<PaymentTransaction>().WithMany().HasForeignKey(x => x.PaymentTransactionId).OnDelete(DeleteBehavior.Restrict);
        settlement.HasOne<PaymentTransaction>().WithMany().HasForeignKey(x => x.ReversalPaymentTransactionId).OnDelete(DeleteBehavior.Restrict);
    }
}
