using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.PartyStatements;
using PTGOilSystem.Web.Services.CompanyFlow;
using PTGOilSystem.Web.Services.Exports;
using PTGOilSystem.Web.Services.Parties;
using PTGOilSystem.Web.Services.PartyStatements;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// صورت‌حساب تجارتی: شرح از سند اصلی، ستون‌های «معامله / دریافت / پرداخت» و این‌که بیلانس
/// و جمع‌های قبلی (رسید/برد) دقیقاً همان می‌مانند.
/// </summary>
public sealed class PartyStatementTradeViewTests
{
    [Fact]
    public async Task Customer_SalesAndReceipts_ReadLoadByLoad_WithVehicleQuantityRateAndRunningBalance()
    {
        await using var db = CreateDb();
        var customer = new Customer { Name = "Haji Ezat", NamePersian = "حاجی عزت" };
        var diesel = new Product { Code = "D", Name = "Diesel", NamePersian = "دیزل" };
        var truckA = new Truck { PlateNumber = "12345" };
        var truckB = new Truck { PlateNumber = "67890" };
        var truckC = new Truck { PlateNumber = "55555" };
        db.AddRange(customer, diesel, truckA, truckB, truckC);
        await db.SaveChangesAsync();

        var dispatchA = new TruckDispatch { TruckId = truckA.Id, ProductId = diesel.Id, LoadedQuantityMt = 20m };
        db.Add(dispatchA);
        await db.SaveChangesAsync();

        var sale1 = Sale(customer.Id, diesel.Id, "INV001", new DateTime(2026, 10, 1), 20m, 728m);
        sale1.TruckDispatchId = dispatchA.Id;
        var sale2 = Sale(customer.Id, diesel.Id, "INV002", new DateTime(2026, 10, 3), 10m, 730m);
        db.AddRange(sale1, sale2);
        await db.SaveChangesAsync();
        // بار دوم دو موتر دارد؛ دیسپچ لغوشده نباید نمایش داده شود.
        db.AddRange(
            new TruckDispatch { TruckId = truckB.Id, ProductId = diesel.Id, SalesTransactionId = sale2.Id },
            new TruckDispatch { TruckId = truckC.Id, ProductId = diesel.Id, SalesTransactionId = sale2.Id, Status = DispatchStatus.Cancelled });

        db.LedgerEntries.Add(SaleLedger(sale1, customer.Id));
        await db.SaveChangesAsync();
        await AddCustomerReceiptAsync(db, customer.Id, new DateTime(2026, 10, 2), 7_560m, "P-4", "تصفیه حساب");
        db.LedgerEntries.Add(SaleLedger(sale2, customer.Id));
        await db.SaveChangesAsync();
        await AddCustomerReceiptAsync(db, customer.Id, new DateTime(2026, 10, 3), 5_000m, "P-5");

        var statement = await BuildService(db).GetStatementAsync(
            new PartyRef(PartyStatementPartyType.Customer, customer.Id),
            new PartyStatementFilter());
        var rows = statement.Rows.Where(r => !r.IsOpeningBalance).ToList();

        // ترتیب واقعی کار: بار → پول → بار → پول.
        Assert.Equal(new[] { "فروش دیزل", "دریافت از حاجی عزت", "فروش دیزل", "دریافت از حاجی عزت" },
            rows.Select(r => r.TitleFor(false)));
        Assert.Equal("موتر 12345", rows[0].Detail);
        Assert.Equal(20m, rows[0].TradeQuantity);
        Assert.Equal(728m, rows[0].TradeUnitPrice);
        Assert.Equal("USD", rows[0].TradeUnitPriceCurrency);
        Assert.Equal("فاکتور INV001", rows[0].DocumentLabel);
        Assert.Equal("موتر 67890", rows[2].Detail);
        Assert.Equal(730m, rows[2].TradeUnitPrice);
        Assert.Equal("تصفیه حساب", rows[1].Detail);
        Assert.Equal("رسید P-4", rows[1].DocumentLabel);

        var amounts = rows.Select(r => PartyStatementPresentation.AmountsFor(r, PartyStatementPartyType.Customer)).ToList();
        Assert.Equal(14_560m, amounts[0].Trade);
        Assert.Equal(7_560m, amounts[1].Received);
        Assert.Null(amounts[1].Trade);
        Assert.Equal(new[] { 14_560m, 7_000m, 14_300m, 9_300m }, rows.Select(r => r.RunningBalance));

        // جمع‌های تجارتی فقط تفکیک همان رسید/برد هستند.
        Assert.Equal(21_860m, statement.Summary.TotalTrade);
        Assert.Equal(12_560m, statement.Summary.TotalReceived);
        Assert.Equal(0m, statement.Summary.TotalPaid);
        Assert.Equal(statement.Summary.TotalOutflow, statement.Summary.TotalTrade + statement.Summary.TotalPaid);
        Assert.Equal(statement.Summary.TotalReceipt, statement.Summary.TotalReceived);
        Assert.Equal(9_300m, statement.Summary.ClosingBalance);
        Assert.Equal(30m, statement.Summary.TradeQuantity);
        Assert.Equal("MT", statement.Summary.TradeQuantityUnit);

        // ترتیب پایدار در اجرای دوباره.
        var again = await BuildService(db).GetStatementAsync(
            new PartyRef(PartyStatementPartyType.Customer, customer.Id),
            new PartyStatementFilter());
        Assert.Equal(rows.Select(r => r.LedgerEntryId), again.Rows.Where(r => !r.IsOpeningBalance).Select(r => r.LedgerEntryId));
    }

    [Fact]
    public async Task Supplier_PurchaseAndPayment_ShowPurchaseValueAndPaymentColumns_WithSameBalance()
    {
        await using var db = CreateDb();
        var supplier = new Supplier { Name = "Company X" };
        var petrol = new Product { Code = "P", Name = "Petrol", NamePersian = "پترول" };
        db.AddRange(supplier, petrol);
        await db.SaveChangesAsync();
        var contract = new Contract { ContractNumber = "P-77", ContractType = ContractType.Purchase, SupplierId = supplier.Id, ProductId = petrol.Id };
        db.Add(contract);
        await db.SaveChangesAsync();
        var loading = new LoadingRegister
        {
            ContractId = contract.Id,
            ProductId = petrol.Id,
            LoadingDate = new DateTime(2026, 9, 1),
            LoadedQuantityMt = 25m,
            LoadingPriceUsd = 650m,
            WagonNumber = "W-9001"
        };
        db.Add(loading);
        await db.SaveChangesAsync();
        db.LedgerEntries.AddRange(
            new LedgerEntry
            {
                EntryDate = loading.LoadingDate, Side = LedgerSide.Credit, AmountUsd = 16_250m, Currency = "USD",
                SupplierId = supplier.Id, ContractId = contract.Id, SourceType = "Loading", SourceId = loading.Id,
                Reference = "LD-1", Description = "ثبت بارگیری"
            },
            new LedgerEntry
            {
                EntryDate = new DateTime(2026, 9, 2), Side = LedgerSide.Debit, AmountUsd = 10_000m, Currency = "USD",
                SupplierId = supplier.Id, SourceType = "SupplierPayment", SourceId = 0,
                Reference = "PAY-1", Description = "پرداخت"
            });
        await db.SaveChangesAsync();

        var statement = await BuildService(db).GetStatementAsync(
            new PartyRef(PartyStatementPartyType.Supplier, supplier.Id),
            new PartyStatementFilter());
        var rows = statement.Rows.Where(r => !r.IsOpeningBalance).ToList();

        Assert.Equal("خرید پترول", rows[0].Title);
        Assert.Equal("واگن W-9001", rows[0].Detail);
        Assert.Equal(25m, rows[0].TradeQuantity);
        Assert.Equal(650m, rows[0].TradeUnitPrice);
        Assert.Equal("قرارداد P-77", rows[0].DocumentLabel);
        var purchase = PartyStatementPresentation.AmountsFor(rows[0], PartyStatementPartyType.Supplier);
        var payment = PartyStatementPresentation.AmountsFor(rows[1], PartyStatementPartyType.Supplier);
        Assert.Equal(16_250m, purchase.Trade);
        Assert.Equal(10_000m, payment.Paid);
        Assert.Equal(16_250m, statement.Summary.TotalTrade);
        Assert.Equal(10_000m, statement.Summary.TotalPaid);
        // بیلانس همان فرمول قبلی: اول دوره + Σبرد − Σرسید.
        Assert.Equal(-6_250m, statement.Summary.ClosingBalance);
        Assert.Equal(
            statement.Summary.ClosingBalance,
            statement.Summary.OpeningBalance - statement.Summary.TotalTrade + statement.Summary.TotalPaid - statement.Summary.TotalReceived);
    }

    [Fact]
    public async Task PartySettlement_ShowsWhoPaidWhomDirectly()
    {
        await using var db = CreateDb();
        var payer = new Customer { Name = "Haji Ezat", NamePersian = "حاجی عزت" };
        var receiver = new Customer { Name = "Haji Hussain", NamePersian = "حاجی حسین" };
        db.AddRange(payer, receiver);
        await db.SaveChangesAsync();
        var settlement = new PartySettlement
        {
            SettlementDate = new DateTime(2026, 10, 4),
            FromPartyType = AccountingPartyType.Customer, FromPartyId = payer.Id,
            ToPartyType = AccountingPartyType.Customer, ToPartyId = receiver.Id,
            Amount = 7_560m, Currency = "USD", AmountUsd = 7_560m
        };
        db.Add(settlement);
        await db.SaveChangesAsync();
        db.LedgerEntries.Add(new LedgerEntry
        {
            EntryDate = settlement.SettlementDate, Side = LedgerSide.Debit, AmountUsd = 7_560m, Currency = "USD",
            SourceAmount = 7_560m, SourceCurrencyCode = "USD",
            CustomerId = payer.Id, SourceType = "PartySettlement", SourceId = settlement.Id,
            Reference = $"PS-{settlement.Id}-P", Description = "تسویه بین طرف‌حساب‌ها"
        });
        await db.SaveChangesAsync();

        var statement = await BuildService(db).GetStatementAsync(
            new PartyRef(PartyStatementPartyType.Customer, payer.Id),
            new PartyStatementFilter());
        var row = Assert.Single(statement.Rows.Where(r => !r.IsOpeningBalance));

        Assert.Equal("7,560.00 USD از حساب حاجی عزت، مستقیم به حاجی حسین پرداخت شد", row.Title);
        Assert.Equal($"سند تسویه PS-{settlement.Id}", row.DocumentLabel);
        Assert.Equal(7_560m, PartyStatementPresentation.AmountsFor(row, PartyStatementPartyType.Customer).Received);
        Assert.Equal(-7_560m, statement.Summary.ClosingBalance);
    }

    [Fact]
    public async Task CancelledReceipt_SitsInSameColumnAsNegative_SoNothingIsCountedTwice()
    {
        await using var db = CreateDb();
        var customer = new Customer { Name = "Haji Ezat", NamePersian = "حاجی عزت" };
        db.Add(customer);
        await db.SaveChangesAsync();
        var payment = await AddCustomerReceiptAsync(db, customer.Id, new DateTime(2026, 10, 1), 1_000m, "P-9");
        db.LedgerEntries.Add(new LedgerEntry
        {
            EntryDate = new DateTime(2026, 10, 2), Side = LedgerSide.Credit, AmountUsd = 1_000m, Currency = "USD",
            CustomerId = customer.Id, SourceType = "CustomerReceipt", SourceId = payment.Id,
            Reference = "P-9" + CompanyFlowSourceTypes.ReversalReferenceSuffix, Description = "برگشت"
        });
        await db.SaveChangesAsync();

        var statement = await BuildService(db).GetStatementAsync(
            new PartyRef(PartyStatementPartyType.Customer, customer.Id),
            new PartyStatementFilter());
        var rows = statement.Rows.Where(r => !r.IsOpeningBalance).ToList();

        Assert.Equal("لغو دریافت از حاجی عزت", rows[1].Title);
        Assert.Equal(1_000m, PartyStatementPresentation.AmountsFor(rows[0], PartyStatementPartyType.Customer).Received);
        Assert.Equal(-1_000m, PartyStatementPresentation.AmountsFor(rows[1], PartyStatementPartyType.Customer).Received);
        Assert.Equal(0m, statement.Summary.TotalReceived);
        Assert.Equal(0m, statement.Summary.TotalPaid);
        Assert.Equal(0m, statement.Summary.ClosingBalance);
    }

    [Fact]
    public void SaleReversal_NetsToZeroInTradeColumn_AndColumnsReproduceBalance()
    {
        var sale = new PartyStatementRow { SourceType = "Sale", OutflowBase = 500m, FlowDirection = CompanyFlowDirection.Outflow };
        var reversal = new PartyStatementRow { SourceType = "Sale", ReceiptBase = 500m, FlowDirection = CompanyFlowDirection.Receipt, IsReversalRow = true };
        var refund = new PartyStatementRow { SourceType = "CustomerPayment", OutflowBase = 40m, FlowDirection = CompanyFlowDirection.Outflow };

        var a = PartyStatementPresentation.AmountsFor(sale, PartyStatementPartyType.Customer);
        var b = PartyStatementPresentation.AmountsFor(reversal, PartyStatementPartyType.Customer);
        var c = PartyStatementPresentation.AmountsFor(refund, PartyStatementPartyType.Customer);

        Assert.Equal(500m, a.Trade);
        Assert.Equal(-500m, b.Trade);
        Assert.Equal(40m, c.Paid);
        // s × Σمعامله + Σپرداخت − Σدریافت = Σ(برد − رسید)
        Assert.Equal(
            sale.SignedAmount + reversal.SignedAmount + refund.SignedAmount,
            (a.Trade!.Value + b.Trade!.Value) + c.Paid!.Value);
    }

    [Fact]
    public async Task OpeningBalance_NegativeAndZeroBalances_FollowTheExistingFormula()
    {
        await using var db = CreateDb();
        var customer = new Customer { Name = "C" };
        var product = new Product { Code = "D", Name = "Diesel" };
        db.AddRange(customer, product);
        await db.SaveChangesAsync();
        var oldSale = Sale(customer.Id, product.Id, "OLD", new DateTime(2026, 8, 1), 1m, 300m);
        db.Add(oldSale);
        await db.SaveChangesAsync();
        db.LedgerEntries.Add(SaleLedger(oldSale, customer.Id));
        await db.SaveChangesAsync();
        await AddCustomerReceiptAsync(db, customer.Id, new DateTime(2026, 9, 2), 300m, "R-1");
        await AddCustomerReceiptAsync(db, customer.Id, new DateTime(2026, 9, 3), 200m, "R-2");

        var statement = await BuildService(db).GetStatementAsync(
            new PartyRef(PartyStatementPartyType.Customer, customer.Id),
            new PartyStatementFilter { FromDate = new DateTime(2026, 9, 1) });

        var opening = statement.Rows.First();
        Assert.True(opening.IsOpeningBalance);
        Assert.Equal(300m, opening.RunningBalance);
        Assert.Equal(default, PartyStatementPresentation.AmountsFor(opening, PartyStatementPartyType.Customer));
        Assert.Equal(new[] { 0m, -200m }, statement.Rows.Skip(1).Select(r => r.RunningBalance));
        Assert.Equal(-200m, statement.Summary.ClosingBalance);
        Assert.Equal(0m, statement.Summary.TotalTrade);
        Assert.Null(statement.Summary.TradeQuantity);
    }

    [Fact]
    public async Task ForeignCurrencyRows_ShowRealCurrency_AndRateOnlyWhenItReproducesTheAmount()
    {
        await using var db = CreateDb();
        var customer = new Customer { Name = "C" };
        var product = new Product { Code = "G", Name = "Gas", NamePersian = "گاز" };
        db.AddRange(customer, product);
        await db.SaveChangesAsync();
        // فروش افغانی: 2 MT × 50,000 AFN = 100,000 AFN ≈ 1,428.57 USD.
        var afnSale = new SalesTransaction
        {
            CustomerId = customer.Id, ProductId = product.Id, InvoiceNumber = "AF-1", SaleDate = new DateTime(2026, 10, 1),
            QuantityMt = 2m, Currency = "AFN", UnitPriceInCurrency = 50_000m, UnitPriceUsd = 714.2857m,
            TotalInCurrency = 100_000m, TotalUsd = 1_428.57m
        };
        // فروشی که مبلغش از چند جزء ساخته شده؛ نرخ ساده با مبلغ سطر جور نمی‌آید.
        var mixedSale = Sale(customer.Id, product.Id, "MX-1", new DateTime(2026, 10, 2), 3m, 100m);
        db.AddRange(afnSale, mixedSale);
        await db.SaveChangesAsync();
        db.LedgerEntries.AddRange(
            new LedgerEntry
            {
                EntryDate = afnSale.SaleDate, Side = LedgerSide.Credit, AmountUsd = 1_428.57m, Currency = "USD",
                SourceAmount = 100_000m, SourceCurrencyCode = "AFN", AppliedFxRateToUsd = 0.0142857m,
                CustomerId = customer.Id, SourceType = "Sale", SourceId = afnSale.Id, Reference = "AF-1", Description = "ثبت فروش"
            },
            new LedgerEntry
            {
                EntryDate = mixedSale.SaleDate, Side = LedgerSide.Credit, AmountUsd = 345m, Currency = "USD",
                CustomerId = customer.Id, SourceType = "Sale", SourceId = mixedSale.Id, Reference = "MX-1", Description = "ثبت فروش"
            });
        await db.SaveChangesAsync();
        db.LedgerEntries.Add(new LedgerEntry
        {
            EntryDate = new DateTime(2026, 10, 3), Side = LedgerSide.Debit, AmountUsd = 700m, Currency = "USD",
            SourceAmount = 49_000m, SourceCurrencyCode = "AFN", AppliedFxRateToUsd = 1m / 70m,
            CustomerId = customer.Id, SourceType = "CustomerReceipt", SourceId = 0, Reference = "R-AF", Description = "دریافت"
        });
        await db.SaveChangesAsync();

        var statement = await BuildService(db).GetStatementAsync(
            new PartyRef(PartyStatementPartyType.Customer, customer.Id),
            new PartyStatementFilter());
        var rows = statement.Rows.Where(r => !r.IsOpeningBalance).ToList();

        Assert.Null(rows[0].Detail);
        Assert.Equal("2.000 تن × 50,000.00 AFN/تن", PartyStatementPresentation.DescriptionSecondLine(rows[0], "USD"));
        Assert.Equal("49,000.00 AFN", PartyStatementPresentation.DescriptionSecondLine(rows[2], "USD"));
        Assert.Equal("AFN", rows[0].TradeUnitPriceCurrency);
        Assert.Null(rows[1].Detail);
        Assert.Equal(3m, rows[1].TradeQuantity);
        Assert.Null(rows[1].TradeUnitPrice);
        Assert.Equal(345m, PartyStatementPresentation.AmountsFor(rows[1], PartyStatementPartyType.Customer).Trade);
        // سطر بدون سند پرداخت: شرح دفتر می‌ماند و حدس زده نمی‌شود.
        Assert.Null(rows[2].Title);
        Assert.Equal("دریافت", rows[2].TitleFor(false));
        Assert.Equal(1_073.57m, statement.Summary.ClosingBalance);
    }

    [Fact]
    public async Task LongDescription_IsKeptWhole_WithoutEllipsis()
    {
        await using var db = CreateDb();
        var customer = new Customer { Name = "C" };
        db.Add(customer);
        await db.SaveChangesAsync();
        var text = string.Join(' ', Enumerable.Repeat("شرح بسیار طولانی معامله برای بررسی شکستن خط", 6));
        db.LedgerEntries.Add(new LedgerEntry
        {
            EntryDate = new DateTime(2026, 10, 1), Side = LedgerSide.Debit, AmountUsd = 10m, Currency = "USD",
            CustomerId = customer.Id, SourceType = "ManualReceipt", SourceId = 0, Reference = "LONG-REFERENCE-0000000000001",
            Description = text
        });
        await db.SaveChangesAsync();

        var statement = await BuildService(db).GetStatementAsync(
            new PartyRef(PartyStatementPartyType.Customer, customer.Id),
            new PartyStatementFilter());
        var row = Assert.Single(statement.Rows.Where(r => !r.IsOpeningBalance));

        Assert.Equal(text, row.Description);
        Assert.Equal("LONG-REFERENCE-0000000000001", row.Reference);
        Assert.DoesNotContain("…", row.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pdf_RendersMultiPageTradeStatement_WithLongTextAndVeryLargeAmounts()
    {
        await using var db = CreateDb();
        var customer = new Customer { Name = "Haji Ezat", NamePersian = "حاجی عزت" };
        var diesel = new Product { Code = "D", Name = "Diesel", NamePersian = "دیزل" };
        var truck = new Truck { PlateNumber = "KBL-12345" };
        db.AddRange(customer, diesel, truck);
        await db.SaveChangesAsync();

        for (var day = 0; day < 45; day++)
        {
            var dispatch = new TruckDispatch { TruckId = truck.Id, ProductId = diesel.Id };
            db.Add(dispatch);
            await db.SaveChangesAsync();
            var sale = Sale(customer.Id, diesel.Id, $"INV{day:000}", new DateTime(2026, 6, 1).AddDays(day), 20m + day, 728m);
            if (day == 7)
            {
                // مبلغ بسیار بزرگ.
                sale.QuantityMt = 12_345_678.123m;
                sale.TotalUsd = sale.QuantityMt * sale.UnitPriceUsd;
                sale.TotalInCurrency = sale.TotalUsd;
            }
            sale.TruckDispatchId = dispatch.Id;
            db.Add(sale);
            await db.SaveChangesAsync();
            db.LedgerEntries.Add(SaleLedger(sale, customer.Id));
            await db.SaveChangesAsync();
            await AddCustomerReceiptAsync(
                db,
                customer.Id,
                sale.SaleDate,
                day % 3 == 0 ? sale.TotalUsd : 5_000m,
                $"P-{day}",
                day % 5 == 0 ? string.Join(' ', Enumerable.Repeat("بابت تصفیهٔ حساب بارهای قبلی و کرایهٔ موتر", 4)) : null);
        }

        var statement = await BuildService(db).GetStatementAsync(
            new PartyRef(PartyStatementPartyType.Customer, customer.Id),
            new PartyStatementFilter { FromDate = new DateTime(2026, 6, 1) });

        await using var output = new MemoryStream();
        await CreateExportService().WritePartyStatementPdfAsync(statement, false, output, CancellationToken.None);
        var bytes = output.ToArray();
        Assert.StartsWith("%PDF", Encoding.ASCII.GetString(bytes, 0, 4));
        Assert.True(CountPages(bytes) > 1, "statement should span several pages");

        // نمونه برای بررسی چشمیِ چیدمان (اختیاری).
        var sampleDirectory = Environment.GetEnvironmentVariable("PTG_STATEMENT_SAMPLE_DIR");
        if (!string.IsNullOrWhiteSpace(sampleDirectory))
        {
            Directory.CreateDirectory(sampleDirectory);
            await File.WriteAllBytesAsync(Path.Combine(sampleDirectory, "customer-trade-statement.pdf"), bytes);
        }
    }

    private static int CountPages(byte[] pdf)
    {
        var text = Encoding.Latin1.GetString(pdf);
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf("/Type /Page", index, StringComparison.Ordinal)) >= 0)
        {
            if (!text.AsSpan(index + "/Type /Page".Length).StartsWith("s"))
            {
                count++;
            }
            index++;
        }
        return count;
    }

    private static SalesTransaction Sale(int customerId, int productId, string invoice, DateTime date, decimal quantity, decimal rate)
        => new()
        {
            CustomerId = customerId,
            ProductId = productId,
            InvoiceNumber = invoice,
            SaleDate = date,
            QuantityMt = quantity,
            Currency = "USD",
            UnitPriceInCurrency = rate,
            UnitPriceUsd = rate,
            TotalInCurrency = quantity * rate,
            TotalUsd = quantity * rate
        };

    private static LedgerEntry SaleLedger(SalesTransaction sale, int customerId)
        => new()
        {
            EntryDate = sale.SaleDate,
            Side = LedgerSide.Credit,
            AmountUsd = sale.TotalUsd,
            Currency = "USD",
            CustomerId = customerId,
            SourceType = "Sale",
            SourceId = sale.Id,
            Reference = sale.InvoiceNumber,
            Description = "ثبت فروش فروش از مخزن فاکتور " + sale.InvoiceNumber
        };

    private static async Task<PaymentTransaction> AddCustomerReceiptAsync(
        ApplicationDbContext db,
        int customerId,
        DateTime date,
        decimal amount,
        string reference,
        string? note = null)
    {
        var ledger = new LedgerEntry
        {
            EntryDate = date,
            Side = LedgerSide.Debit,
            AmountUsd = amount,
            Currency = "USD",
            CustomerId = customerId,
            SourceType = nameof(PaymentKind.CustomerReceipt),
            SourceId = 0,
            Reference = reference,
            Description = "دریافت از مشتری | صندوق مرکزی"
        };
        db.LedgerEntries.Add(ledger);
        await db.SaveChangesAsync();
        var payment = new PaymentTransaction
        {
            PaymentDate = date,
            Direction = PaymentDirection.In,
            PaymentKind = PaymentKind.CustomerReceipt,
            CustomerId = customerId,
            Amount = amount,
            Currency = "USD",
            AmountUsd = amount,
            Reference = reference,
            Description = note,
            LedgerEntryId = ledger.Id
        };
        db.PaymentTransactions.Add(payment);
        await db.SaveChangesAsync();
        ledger.SourceId = payment.Id;
        await db.SaveChangesAsync();
        return payment;
    }

    private static PartyStatementReadService BuildService(ApplicationDbContext db)
        => new(
            db,
            new PartyStatementPolicyResolver(),
            new CompanyFlowDirectionResolver(),
            new CompanyFlowBalanceService(),
            Options.Create(new PartyStatementOptions()),
            new PartyDirectory(db));

    private static ApplicationDbContext CreateDb()
        => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static TabularExportService CreateExportService()
    {
        var webRoot = FindWebRoot();
        var environment = new TestWebHostEnvironment
        {
            WebRootPath = webRoot,
            ContentRootPath = Directory.GetParent(webRoot)!.FullName
        };
        return new TabularExportService(
            Options.Create(new TabularExportOptions
            {
                ExcelMaxRows = 1_000,
                PdfMaxRows = 1_000,
                CompanyLogoPath = "/invoice-templates/fawad/logo.png",
                QuestPdfLicense = "Community"
            }),
            environment);
    }

    private static string FindWebRoot([CallerFilePath] string sourceFilePath = "")
    {
        var current = new DirectoryInfo(Path.GetDirectoryName(sourceFilePath)!);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "src", "PTGOilSystem.Web", "wwwroot");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Repository root was not found from the test output directory.");
    }

    private sealed class TestWebHostEnvironment : IWebHostEnvironment
    {
        public string WebRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ApplicationName { get; set; } = "PTGOilSystem.Web.Tests";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public string EnvironmentName { get; set; } = "Testing";
    }
}
