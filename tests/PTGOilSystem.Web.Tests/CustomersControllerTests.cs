using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Controllers;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Customers;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.PartyStatements;
using PTGOilSystem.Web.Models.Reports;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.DeleteSafety;
using PTGOilSystem.Web.Services.PartyStatements;
using Xunit;

namespace PTGOilSystem.Web.Tests;

public class CustomersControllerTests
{
    [Fact]
    public async Task Details_Statement_Shows_ThreeWaySettlement_Labels_And_Source_Details_Links()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        db.Customers.Add(new Customer { Id = 1, Name = "Customer A" });
        db.LedgerEntries.AddRange(
            new LedgerEntry
            {
                Id = 1,
                EntryDate = new DateTime(2026, 6, 6),
                Side = LedgerSide.Debit,
                AmountUsd = 1000m,
                Currency = "USD",
                SourceType = ThreeWaySettlementController.LedgerSourceType,
                SourceId = 10,
                Reference = "HW-10",
                Description = "Three-way settlement",
                CustomerId = 1
            },
            new LedgerEntry
            {
                Id = 2,
                EntryDate = new DateTime(2026, 6, 7),
                Side = LedgerSide.Credit,
                AmountUsd = 1000m,
                Currency = "USD",
                SourceType = ThreeWaySettlementController.CancellationLedgerSourceType,
                SourceId = 10,
                Reference = "HW-10",
                Description = "Three-way settlement cancellation",
                CustomerId = 1
            });
        await db.SaveChangesAsync();

        var controller = BuildController(db);

        var result = await controller.Details(1);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<CustomerProfileViewModel>(view.Model);

        Assert.Collection(
            model.StatementRows,
            row =>
            {
                Assert.Equal("تسویه سه‌طرفه / حواله", row.Type);
                Assert.Equal("ThreeWaySettlement", row.SourceDetailsController);
                Assert.Equal("Details", row.SourceDetailsAction);
                Assert.Equal(10, row.SourceDetailsRouteId);
            },
            row =>
            {
                Assert.Equal("برگشت تسویه سه‌طرفه", row.Type);
                Assert.Equal("ThreeWaySettlement", row.SourceDetailsController);
                Assert.Equal("Details", row.SourceDetailsAction);
                Assert.Equal(10, row.SourceDetailsRouteId);
            });
    }

    [Fact]
    public async Task Index_Shows_Official_Customer_Balance_Instead_Of_Contact_Person()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new ApplicationDbContext(options);
        db.Customers.Add(new Customer { Id = 1, Name = "Customer A", ContactPerson = "Legacy contact" });
        await db.SaveChangesAsync();

        var view = Assert.IsType<ViewResult>(await BuildController(db, new FixedCustomerBalanceReader(750m)).Index(null));
        var rows = Assert.IsAssignableFrom<IEnumerable<CustomerIndexItemViewModel>>(view.Model);

        Assert.Equal(750m, Assert.Single(rows).BalanceUsd);
    }

    private static CustomersController BuildController(
        ApplicationDbContext db,
        IPartyBalanceReadService? balances = null)
        => new(db, new AuditService(db), new MasterDataDeleteSafetyService(db), partyBalances: balances);

    private sealed class FixedCustomerBalanceReader(decimal balance) : IPartyBalanceReadService
    {
        public Task<IReadOnlyList<PartyBalanceSnapshot>> GetBalancesAsync(
            ManagementReportFilterViewModel filter,
            CancellationToken ct = default,
            IReadOnlyCollection<PartyStatementPartyType>? partyTypes = null)
            => Task.FromResult<IReadOnlyList<PartyBalanceSnapshot>>
            ([
                new(PartyStatementPartyType.Customer, 1, "Customer A", 0m, 0m, 0m, 0m,
                    balance, null, "", "Customers")
            ]);

        public Task<IReadOnlyDictionary<int, ContractBalanceSummary>> GetContractBalancesAsync(
            IReadOnlyCollection<int> contractIds,
            DateTime? asOfDate = null,
            CancellationToken ct = default,
            bool resolveNames = true)
            => Task.FromResult<IReadOnlyDictionary<int, ContractBalanceSummary>>(
                new Dictionary<int, ContractBalanceSummary>());
    }
}
