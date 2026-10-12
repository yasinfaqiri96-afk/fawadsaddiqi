using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Models.Entities;

namespace PTGOilSystem.Web.Data.Configurations;

public static class AccountingModelConfiguration
{
    public static void ConfigureAccountingCore(this ModelBuilder modelBuilder)
    {
        ConfigureAccount(modelBuilder);
        ConfigureSettings(modelBuilder);
        ConfigureFiscalCalendar(modelBuilder);
        ConfigureJournal(modelBuilder);
        ConfigureCloseRun(modelBuilder);
        ConfigureInventoryAverageCost(modelBuilder);
    }

    private static void ConfigureInventoryAverageCost(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<InventoryAverageCost>();
        entity.ToTable("InventoryAverageCosts", table =>
        {
            // The pool may reach zero but must never go negative: a sale that would overdraw it
            // is skipped rather than valued against stock that is not there.
            table.HasCheckConstraint(
                "CK_InventoryAverageCosts_NonNegative",
                "\"QuantityMt\" >= 0 AND \"TotalValueUsd\" >= 0");
        });
        entity.Ignore(x => x.AverageUnitCostUsd);
        entity.Property(x => x.QuantityMt).HasColumnType("numeric(18,4)");
        entity.Property(x => x.TotalValueUsd).HasColumnType("numeric(18,4)");
        entity.Property(x => x.RowVersion).IsRowVersion();
        entity.HasIndex(x => new { x.CompanyId, x.ProductId, x.TerminalId }).IsUnique();
        entity.HasOne(x => x.Company).WithMany().HasForeignKey(x => x.CompanyId)
            .OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId)
            .OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(x => x.Terminal).WithMany().HasForeignKey(x => x.TerminalId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureAccount(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<Account>();
        entity.ToTable("Accounts");
        entity.Property(x => x.RowVersion).IsRowVersion();
        entity.HasIndex(x => new { x.CompanyId, x.Code }).IsUnique();
        entity.HasIndex(x => x.CompanyId);
        entity.HasIndex(x => x.ParentAccountId);
        entity.HasIndex(x => new { x.CompanyId, x.IsActive });
        entity.HasOne(x => x.Company).WithMany().HasForeignKey(x => x.CompanyId)
            .OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(x => x.ParentAccount).WithMany(x => x.ChildAccounts)
            .HasForeignKey(x => x.ParentAccountId).OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureSettings(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<AccountingSettings>();
        entity.ToTable("AccountingSettings");
        entity.Property(x => x.RowVersion).IsRowVersion();
        entity.HasIndex(x => x.CompanyId).IsUnique();
        entity.HasOne(x => x.Company).WithMany().HasForeignKey(x => x.CompanyId)
            .OnDelete(DeleteBehavior.Restrict);

        entity.HasOne(x => x.CashBankControlAccount).WithMany()
            .HasForeignKey(x => x.CashBankControlAccountId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(x => x.AccountsReceivableAccount).WithMany()
            .HasForeignKey(x => x.AccountsReceivableAccountId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(x => x.AccountsPayableAccount).WithMany()
            .HasForeignKey(x => x.AccountsPayableAccountId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(x => x.InventoryAccount).WithMany()
            .HasForeignKey(x => x.InventoryAccountId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(x => x.InventoryInTransitAccount).WithMany()
            .HasForeignKey(x => x.InventoryInTransitAccountId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(x => x.SupplierPrepaymentAccount).WithMany()
            .HasForeignKey(x => x.SupplierPrepaymentAccountId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(x => x.CustomerAdvanceAccount).WithMany()
            .HasForeignKey(x => x.CustomerAdvanceAccountId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(x => x.FreightPayableAccount).WithMany()
            .HasForeignKey(x => x.FreightPayableAccountId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(x => x.CommissionPayableAccount).WithMany()
            .HasForeignKey(x => x.CommissionPayableAccountId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(x => x.EmployeeAdvanceAccount).WithMany()
            .HasForeignKey(x => x.EmployeeAdvanceAccountId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(x => x.EmployeePayableAccount).WithMany()
            .HasForeignKey(x => x.EmployeePayableAccountId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(x => x.AccruedExpenseAccount).WithMany()
            .HasForeignKey(x => x.AccruedExpenseAccountId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(x => x.SalesRevenueAccount).WithMany()
            .HasForeignKey(x => x.SalesRevenueAccountId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(x => x.CostOfGoodsSoldAccount).WithMany()
            .HasForeignKey(x => x.CostOfGoodsSoldAccountId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(x => x.GeneralExpenseAccount).WithMany()
            .HasForeignKey(x => x.GeneralExpenseAccountId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(x => x.ExchangeGainAccount).WithMany()
            .HasForeignKey(x => x.ExchangeGainAccountId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(x => x.ExchangeLossAccount).WithMany()
            .HasForeignKey(x => x.ExchangeLossAccountId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(x => x.InventoryLossAccount).WithMany()
            .HasForeignKey(x => x.InventoryLossAccountId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(x => x.CurrentYearProfitLossAccount).WithMany()
            .HasForeignKey(x => x.CurrentYearProfitLossAccountId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(x => x.RetainedEarningsAccount).WithMany()
            .HasForeignKey(x => x.RetainedEarningsAccountId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(x => x.PartnerCurrentAccount).WithMany()
            .HasForeignKey(x => x.PartnerCurrentAccountId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(x => x.FixedAssetAccount).WithMany()
            .HasForeignKey(x => x.FixedAssetAccountId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(x => x.AccumulatedDepreciationAccount).WithMany()
            .HasForeignKey(x => x.AccumulatedDepreciationAccountId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(x => x.DepreciationExpenseAccount).WithMany()
            .HasForeignKey(x => x.DepreciationExpenseAccountId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(x => x.AssetRentalRevenueAccount).WithMany()
            .HasForeignKey(x => x.AssetRentalRevenueAccountId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(x => x.InternalAssetRecoveryAccount).WithMany()
            .HasForeignKey(x => x.InternalAssetRecoveryAccountId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(x => x.AssetOperatingExpenseAccount).WithMany()
            .HasForeignKey(x => x.AssetOperatingExpenseAccountId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(x => x.SalaryExpenseAccount).WithMany()
            .HasForeignKey(x => x.SalaryExpenseAccountId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(x => x.CustodyFundsPayableAccount).WithMany()
            .HasForeignKey(x => x.CustodyFundsPayableAccountId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(x => x.CustodyPendingCollectionAccount).WithMany()
            .HasForeignKey(x => x.CustodyPendingCollectionAccountId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(x => x.CustodyCommissionRevenueAccount).WithMany()
            .HasForeignKey(x => x.CustodyCommissionRevenueAccountId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(x => x.CustodyServiceRevenueAccount).WithMany()
            .HasForeignKey(x => x.CustodyServiceRevenueAccountId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(x => x.DeliveredPendingPricingAccount).WithMany()
            .HasForeignKey(x => x.DeliveredPendingPricingAccountId).OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureFiscalCalendar(ModelBuilder modelBuilder)
    {
        var year = modelBuilder.Entity<FiscalYear>();
        year.ToTable("FiscalYears", table => table.HasCheckConstraint(
            "CK_FiscalYears_DateRange", "\"StartDate\" <= \"EndDate\""));
        year.Property(x => x.RowVersion).IsRowVersion();
        year.Property(x => x.StartDate).HasColumnType("date");
        year.Property(x => x.EndDate).HasColumnType("date");
        year.HasIndex(x => x.CompanyId);
        year.HasIndex(x => new { x.CompanyId, x.Status });
        year.HasIndex(x => new { x.CompanyId, x.IsCurrent }).IsUnique()
            .HasFilter("\"IsCurrent\" = TRUE");
        year.HasOne(x => x.Company).WithMany().HasForeignKey(x => x.CompanyId)
            .OnDelete(DeleteBehavior.Restrict);
        year.HasOne(x => x.PreviousFiscalYear).WithMany().HasForeignKey(x => x.PreviousFiscalYearId)
            .OnDelete(DeleteBehavior.Restrict);
        year.HasOne(x => x.OpeningJournalEntry).WithMany().HasForeignKey(x => x.OpeningJournalEntryId)
            .OnDelete(DeleteBehavior.Restrict);
        year.HasOne(x => x.ClosingJournalEntry).WithMany().HasForeignKey(x => x.ClosingJournalEntryId)
            .OnDelete(DeleteBehavior.Restrict);
        year.HasOne(x => x.OpenedByUser).WithMany().HasForeignKey(x => x.OpenedByUserId)
            .OnDelete(DeleteBehavior.Restrict);
        year.HasOne(x => x.ClosedByUser).WithMany().HasForeignKey(x => x.ClosedByUserId)
            .OnDelete(DeleteBehavior.Restrict);

        var period = modelBuilder.Entity<FiscalPeriod>();
        period.ToTable("FiscalPeriods", table =>
        {
            table.HasCheckConstraint("CK_FiscalPeriods_DateRange", "\"StartDate\" <= \"EndDate\"");
            table.HasCheckConstraint("CK_FiscalPeriods_PeriodNumber", "\"PeriodNumber\" BETWEEN 1 AND 12");
        });
        period.Property(x => x.RowVersion).IsRowVersion();
        period.Property(x => x.StartDate).HasColumnType("date");
        period.Property(x => x.EndDate).HasColumnType("date");
        period.HasIndex(x => x.CompanyId);
        period.HasIndex(x => x.FiscalYearId);
        period.HasIndex(x => new { x.FiscalYearId, x.PeriodNumber }).IsUnique();
        period.HasIndex(x => new { x.CompanyId, x.Status });
        period.HasOne(x => x.Company).WithMany().HasForeignKey(x => x.CompanyId)
            .OnDelete(DeleteBehavior.Restrict);
        period.HasOne(x => x.FiscalYear).WithMany(x => x.Periods).HasForeignKey(x => x.FiscalYearId)
            .OnDelete(DeleteBehavior.Restrict);
        period.HasOne(x => x.LockedByUser).WithMany().HasForeignKey(x => x.LockedByUserId)
            .OnDelete(DeleteBehavior.Restrict);

        var history = modelBuilder.Entity<FiscalYearStatusHistory>();
        history.ToTable("FiscalYearStatusHistories");
        history.HasIndex(x => new { x.FiscalYearId, x.ChangedAt });
        history.HasOne(x => x.FiscalYear).WithMany().HasForeignKey(x => x.FiscalYearId)
            .OnDelete(DeleteBehavior.Restrict);
        history.HasOne(x => x.ChangedByUser).WithMany().HasForeignKey(x => x.ChangedByUserId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureJournal(ModelBuilder modelBuilder)
    {
        var journal = modelBuilder.Entity<JournalEntry>();
        journal.ToTable("JournalEntries", table => table.HasCheckConstraint(
            "CK_JournalEntries_ReversalReference",
            "(\"IsReversal\" = TRUE AND \"ReversalOfJournalEntryId\" IS NOT NULL) OR (\"IsReversal\" = FALSE AND \"ReversalOfJournalEntryId\" IS NULL)"));
        journal.Property(x => x.RowVersion).IsRowVersion();
        journal.Property(x => x.CreatedAtUtc).HasColumnName("CreatedAt");
        journal.Property(x => x.AccountingDate).HasColumnType("date");
        journal.Property(x => x.DocumentDate).HasColumnType("date");
        journal.Property(x => x.OperationDate).HasColumnType("date");
        journal.HasIndex(x => new { x.CompanyId, x.JournalNumber }).IsUnique();
        journal.HasIndex(x => x.CompanyId);
        journal.HasIndex(x => x.AccountingDate);
        journal.HasIndex(x => x.FiscalYearId);
        journal.HasIndex(x => x.FiscalPeriodId);
        journal.HasIndex(x => x.SourceEventId);
        journal.HasIndex(x => new { x.CompanyId, x.SourceModule, x.SourceEventId }).IsUnique()
            .HasFilter("\"SourceEventId\" IS NOT NULL AND \"SourceEventId\" <> ''");
        journal.HasIndex(x => x.ReversalOfJournalEntryId).IsUnique()
            .HasFilter("\"ReversalOfJournalEntryId\" IS NOT NULL AND \"Status\" = 1");
        journal.HasOne(x => x.Company).WithMany().HasForeignKey(x => x.CompanyId)
            .OnDelete(DeleteBehavior.Restrict);
        journal.HasOne(x => x.FiscalYear).WithMany().HasForeignKey(x => x.FiscalYearId)
            .OnDelete(DeleteBehavior.Restrict);
        journal.HasOne(x => x.FiscalPeriod).WithMany().HasForeignKey(x => x.FiscalPeriodId)
            .OnDelete(DeleteBehavior.Restrict);
        journal.HasOne(x => x.ReversalOfJournalEntry).WithMany(x => x.Reversals)
            .HasForeignKey(x => x.ReversalOfJournalEntryId).OnDelete(DeleteBehavior.Restrict);
        journal.HasOne(x => x.PostedByUser).WithMany().HasForeignKey(x => x.PostedByUserId)
            .OnDelete(DeleteBehavior.Restrict);

        var line = modelBuilder.Entity<JournalEntryLine>();
        line.ToTable("JournalEntryLines", table =>
        {
            table.HasCheckConstraint(
                "CK_JournalEntryLines_DebitCredit",
                "\"Debit\" >= 0 AND \"Credit\" >= 0 AND ((\"Debit\" > 0 AND \"Credit\" = 0) OR (\"Credit\" > 0 AND \"Debit\" = 0))");
            table.HasCheckConstraint(
                "CK_JournalEntryLines_Transaction",
                "\"TransactionAmount\" >= 0 AND \"ExchangeRate\" > 0");
            table.HasCheckConstraint(
                "CK_JournalEntryLines_Party",
                "(\"PartyType\" IS NULL AND \"PartyId\" IS NULL) OR (\"PartyType\" IS NOT NULL AND \"PartyId\" IS NOT NULL)");
        });
        line.Property(x => x.Debit).HasColumnType("numeric(18,4)");
        line.Property(x => x.Credit).HasColumnType("numeric(18,4)");
        line.Property(x => x.TransactionAmount).HasColumnType("numeric(18,4)");
        line.Property(x => x.ExchangeRate).HasColumnType("numeric(24,12)");
        line.HasIndex(x => new { x.JournalEntryId, x.LineNumber }).IsUnique();
        line.HasIndex(x => x.AccountId);
        line.HasIndex(x => x.ContractId);
        line.HasIndex(x => x.ShipmentId);
        line.HasIndex(x => x.TankId);
        line.HasIndex(x => x.ProductId);
        line.HasIndex(x => x.OperationalAssetId);
        line.HasIndex(x => x.CashAccountId);
        line.HasOne(x => x.JournalEntry).WithMany(x => x.Lines).HasForeignKey(x => x.JournalEntryId)
            .OnDelete(DeleteBehavior.Restrict);
        line.HasOne(x => x.Account).WithMany().HasForeignKey(x => x.AccountId)
            .OnDelete(DeleteBehavior.Restrict);
        line.HasOne(x => x.Contract).WithMany().HasForeignKey(x => x.ContractId)
            .OnDelete(DeleteBehavior.Restrict);
        line.HasOne(x => x.Shipment).WithMany().HasForeignKey(x => x.ShipmentId)
            .OnDelete(DeleteBehavior.Restrict);
        line.HasOne(x => x.Tank).WithMany().HasForeignKey(x => x.TankId)
            .OnDelete(DeleteBehavior.Restrict);
        line.HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId)
            .OnDelete(DeleteBehavior.Restrict);
        line.HasOne(x => x.OperationalAsset).WithMany().HasForeignKey(x => x.OperationalAssetId)
            .OnDelete(DeleteBehavior.Restrict);
        line.HasOne(x => x.CashAccount).WithMany().HasForeignKey(x => x.CashAccountId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureCloseRun(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<FiscalYearCloseRun>();
        entity.ToTable("FiscalYearCloseRuns");
        entity.Property(x => x.RowVersion).IsRowVersion();
        entity.HasIndex(x => x.CompanyId);
        entity.HasIndex(x => x.FiscalYearId);
        entity.HasIndex(x => new { x.FiscalYearId, x.Status });
        entity.HasOne(x => x.Company).WithMany().HasForeignKey(x => x.CompanyId)
            .OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(x => x.FiscalYear).WithMany().HasForeignKey(x => x.FiscalYearId)
            .OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(x => x.StartedByUser).WithMany().HasForeignKey(x => x.StartedByUserId)
            .OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(x => x.CompletedByUser).WithMany().HasForeignKey(x => x.CompletedByUserId)
            .OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(x => x.ClosingJournalEntry).WithMany().HasForeignKey(x => x.ClosingJournalEntryId)
            .OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(x => x.OpeningJournalEntry).WithMany().HasForeignKey(x => x.OpeningJournalEntryId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
