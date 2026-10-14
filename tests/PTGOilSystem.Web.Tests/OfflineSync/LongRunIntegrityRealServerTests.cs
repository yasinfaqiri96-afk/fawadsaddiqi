using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PTGOilSystem.Web.Configuration;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.OfflineSync;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Accounting;
using PTGOilSystem.Web.Services.OfflineSync;
using PTGOilSystem.Web.Services.Reporting;
using Xunit;
using Xunit.Abstractions;

namespace PTGOilSystem.Web.Tests.OfflineSync;

/// <summary>
/// شبیه‌سازی چندساله روی برنامهٔ واقعی (Program.cs کامل، کنترلرها، حسابداری دوطرفهٔ فعال) و PostgreSQL موقت.
/// هر سند از مسیر واقعی ثبت می‌شود: صف آفلاین (/api/sync/v1 → همان اکشن Create) یا POSTِ واقعی فورم (لغو، ویرایش).
/// نتیجهٔ مورد انتظار با یک مدل مستقل (decimal دقیق، بدون خواندن منطق سیستم) حساب و در پایان هر ماه با دیتابیس
/// تطبیق داده می‌شود: موجودی هر مخزن، pool میانگین موزون، حساب موجودی دفتر کل، بهای تمام‌شده، درآمد، مانده
/// مشتری/فروشنده، صندوق، توازن هر سند و یکتایی رویداد منبع. عملیات ممنوع (فروش بیش از موجودی، لغو رسیدِ
/// فروخته‌شده) باید بدون هیچ اثر ناقص رد شوند؛ تکرار همان عملیات نباید سند تازه بسازد.
///
/// Seed ثابت و قابل بازتولید: PTG_LONGRUN_SEED (پیش‌فرض 20261014)، تعداد ماه: PTG_LONGRUN_MONTHS (پیش‌فرض 60).
/// </summary>
[Collection(OfflineRealServerCollection.Name)]
public sealed class LongRunIntegrityRealServerTests(OfflineRealServerFixture server, ITestOutputHelper output)
{
    private static readonly DateTime Start = new(2021, 7, 1);

    private sealed record Seed(int CompanyId, int ProductId, int ExpenseTypeId, int CashAccountId, int TruckId, int AdminId,
        int[] TankIds, int[] TerminalIds, int ReversalTankId, int ReversalTerminalId, int[] ContractIds, decimal[] ContractPrices, int[] SupplierIds, int[] CustomerIds);

    private sealed record SaleDoc(int Id, int Tank, int Customer, decimal Qty, decimal TotalUsd, decimal CostUsd, DateTime Date);

    private sealed record ReceiptDoc(int Id, int Tank, int Contract, decimal Qty, decimal ValueUsd);

    /// <summary>مدل مستقل: فقط از ورودی‌های خود تست حساب می‌شود.</summary>
    private sealed class Model(int tanks, int customers, int suppliers)
    {
        public readonly decimal[] Stock = new decimal[tanks];

        /// <summary>موجودیِ برگشتی از ابطال فروش: حرکت برگشت تاریخ روز ابطال (امروز) را دارد، پس برای فروشِ تاریخ‌گذشته در دسترس نیست.</summary>
        public readonly decimal[] ReturnedToday = new decimal[tanks];

        public decimal Available(int tank) => Stock[tank] - ReturnedToday[tank];
        public readonly decimal[] PoolQty = new decimal[tanks];
        public readonly decimal[] PoolValue = new decimal[tanks];
        public readonly decimal[] CustomerSales = new decimal[customers];
        public readonly decimal[] CustomerReceipts = new decimal[customers];
        public readonly decimal[] SupplierLoadings = new decimal[suppliers];
        public readonly decimal[] SupplierPayments = new decimal[suppliers];
        public decimal Cash;
        public decimal Revenue;
        public decimal Cogs;
        public decimal ShortageValue;
        public decimal InTransit;
        public decimal ActiveExpenses;
        public readonly List<SaleDoc> Sales = [];
        public readonly List<ReceiptDoc> Receipts = [];
        public readonly Dictionary<int, (decimal Amount, int Customer)> CustomerPayments = [];
        public readonly Dictionary<int, decimal> Expenses = [];

        /// <summary>میانگین موزون متحرک: میانگین ۶ رقم، هزینه ۴ رقم؛ آخرین مقدار کل ارزش را می‌برد.</summary>
        public decimal Consume(int tank, decimal qty)
        {
            var average = decimal.Round(PoolValue[tank] / PoolQty[tank], 6, MidpointRounding.AwayFromZero);
            var cost = PoolQty[tank] == qty ? PoolValue[tank] : decimal.Round(qty * average, 4, MidpointRounding.AwayFromZero);
            cost = Math.Min(cost, PoolValue[tank]);
            PoolQty[tank] -= qty;
            PoolValue[tank] -= cost;
            return cost;
        }
    }

    [RealServerFact]
    public async Task Five_Years_Of_Real_Operations_Reconcile_Every_Month_Against_An_Independent_Model()
    {
        var seedValue = int.TryParse(Environment.GetEnvironmentVariable("PTG_LONGRUN_SEED"), out var s) ? s : 20261014;
        var months = int.TryParse(Environment.GetEnvironmentVariable("PTG_LONGRUN_MONTHS"), out var m) && m > 0 ? m : 60;
        var random = new Random(seedValue);
        var seed = await SeedAsync();
        var admin = await server.LoginAsync("admin", OfflineRealServerFixture.AdminPassword);
        await admin.RegisterAsync();
        var model = new Model(seed.TankIds.Length, seed.CustomerIds.Length, seed.SupplierIds.Length);
        var findings = new List<string>();
        var counters = new Dictionary<string, int>(StringComparer.Ordinal);
        var sent = new List<object>();
        void Count(string key, int n = 1) => counters[key] = counters.GetValueOrDefault(key) + n;
        decimal Between(decimal min, decimal max, int decimals)
            => decimal.Round(min + (decimal)random.NextDouble() * (max - min), decimals, MidpointRounding.AwayFromZero);
        string D(DateTime d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        async Task<List<OfflineOperationResult>> Push(List<object> ops)
        {
            var results = new List<OfflineOperationResult>();
            foreach (var chunk in ops.Chunk(OfflineSyncLimits.MaxBatchSize))
            {
                results.AddRange((await admin.PushAsync(chunk)).Results);
            }

            sent.AddRange(ops);
            return results;
        }

        for (var month = 0; month < months; month++)
        {
            var monthStart = Start.AddMonths(month);
            var tag = $"{monthStart:yyyyMM}";

            // ── ۱) بارگیری (۴ سند) ──
            var loadPlan = Enumerable.Range(0, 4).Select(i => (Contract: random.Next(seed.ContractIds.Length), Qty: Between(30m, 80m, 3))).ToList();
            var loadOps = loadPlan.Select((p, i) => OfflineClient.Op(Guid.NewGuid(), seed.AdminId, OfflineOperationTypes.LoadingNote,
                new { contractId = seed.ContractIds[p.Contract], productId = seed.ProductId, quantity = p.Qty, truckId = seed.TruckId,
                    truckPlate = "KBL-LR-1", reference = $"BL-LR-{tag}-{i}" }, D(monthStart.AddDays(1)))).ToList();
            var loadResults = await Push(loadOps);
            var loadings = new List<(int Id, int Contract, decimal Qty)>();
            for (var i = 0; i < loadPlan.Count; i++)
            {
                var r = loadResults[i];
                Assert.True(r.Status == nameof(OfflineSyncStatus.Applied), $"{tag} loading {i}: {r.Status} {r.Message}");
                var (contract, qty) = loadPlan[i];
                loadings.Add((int.Parse(r.AppliedReferenceId!, CultureInfo.InvariantCulture), contract, qty));
                var supplier = Array.IndexOf(seed.SupplierIds, seed.SupplierIds[contract]);
                model.SupplierLoadings[supplier] += decimal.Round(qty * seed.ContractPrices[contract], 4, MidpointRounding.AwayFromZero);
                model.InTransit += decimal.Round(qty * seed.ContractPrices[contract], 4, MidpointRounding.AwayFromZero);
                Count("loading");
            }

            // ── ۲) رسید به مخزن با کسری کوچک ──
            var receiptPlan = loadings.Select(l => (l.Id, l.Contract, Tank: random.Next(seed.TankIds.Length),
                Received: random.Next(3) == 0 ? l.Qty : l.Qty - Between(0.001m, 0.4m, 3), l.Qty)).ToList();
            var receiptResults = await Push(receiptPlan.Select((p, i) => OfflineClient.Op(Guid.NewGuid(), seed.AdminId, OfflineOperationTypes.ReceiptNote,
                new { loadingId = p.Id, tankId = seed.TankIds[p.Tank], quantity = p.Received, reference = $"RC-LR-{tag}-{i}" },
                D(monthStart.AddDays(4)))).ToList());
            for (var i = 0; i < receiptPlan.Count; i++)
            {
                var r = receiptResults[i];
                Assert.True(r.Status == nameof(OfflineSyncStatus.Applied), $"{tag} receipt {i}: {r.Status} {r.Message}");
                var p = receiptPlan[i];
                var value = decimal.Round(p.Received * seed.ContractPrices[p.Contract], 4, MidpointRounding.AwayFromZero);
                model.Stock[p.Tank] += p.Received;
                model.PoolQty[p.Tank] += p.Received;
                model.PoolValue[p.Tank] += value;
                model.InTransit -= value;
                model.ShortageValue += decimal.Round(p.Qty * seed.ContractPrices[p.Contract], 4, MidpointRounding.AwayFromZero) - value;
                model.Receipts.Add(new ReceiptDoc(int.Parse(r.AppliedReferenceId!, CultureInfo.InvariantCulture), p.Tank, p.Contract, p.Received, value));
                Count("receipt");
            }

            // ── ۳) فروش از مخزن (۸ سند؛ یکی از چهار به افغانی با نرخ همان روز) ──
            for (var i = 0; i < 8; i++)
            {
                var tank = random.Next(seed.TankIds.Length);
                if (model.Available(tank) < 1m)
                {
                    continue;
                }

                var qty = decimal.Round(model.Available(tank) * Between(0.05m, 0.3m, 4), 3, MidpointRounding.AwayFromZero);
                var customer = random.Next(seed.CustomerIds.Length);
                var usdPrice = Between(700m, 900m, 2);
                var afn = random.Next(4) == 0;
                var perUsd = Between(65m, 80m, 2);
                var fxRate = afn ? FxRateMath.ToUsdFromPerUsd(perUsd) : 1m;
                var unitPrice = afn ? decimal.Round(usdPrice * perUsd, 2, MidpointRounding.AwayFromZero) : usdPrice;
                var reference = $"S-LR-{tag}-{i}";
                var date = monthStart.AddDays(9 + i * 2);
                var result = Assert.Single(await Push([OfflineClient.Op(Guid.NewGuid(), seed.AdminId, OfflineOperationTypes.SaleRequest,
                    afn
                        ? new { customerId = seed.CustomerIds[customer], productId = seed.ProductId, tankId = seed.TankIds[tank], quantity = qty,
                            unitPrice, currency = "AFN", fxRate = (decimal?)fxRate, reference }
                        : new { customerId = seed.CustomerIds[customer], productId = seed.ProductId, tankId = seed.TankIds[tank], quantity = qty,
                            unitPrice, currency = "USD", fxRate = (decimal?)null, reference },
                    D(date))]));
                Assert.True(result.Status == nameof(OfflineSyncStatus.Applied), $"{tag} sale {i} ({(afn ? "AFN" : "USD")}): {result.Status} {result.Message}");
                var expectedTotal = decimal.Round(decimal.Round(qty * unitPrice, 4, MidpointRounding.AwayFromZero) * fxRate, 4, MidpointRounding.AwayFromZero);
                await using (var db = server.CreateDbContext())
                {
                    var sale = await db.SalesTransactions.AsNoTracking().SingleAsync(x => x.TicketSerialNumber == reference);
                    if (sale.TotalUsd != expectedTotal)
                    {
                        findings.Add($"{reference}: TotalUsd {sale.TotalUsd} ≠ model {expectedTotal} (qty {qty} × {unitPrice} {sale.Currency} @ {fxRate})");
                    }

                    var cost = model.Consume(tank, qty);
                    model.Stock[tank] -= qty;
                    model.CustomerSales[customer] += sale.TotalUsd;
                    model.Revenue += sale.TotalUsd;
                    model.Cogs += cost;
                    model.Sales.Add(new SaleDoc(sale.Id, tank, customer, qty, sale.TotalUsd, cost, date));
                }

                Count(afn ? "sale.afn" : "sale.usd");
            }

            // ── ۴) فروش بیش از موجودی: باید رد شود و هیچ اثری نگذارد ──
            {
                var tank = random.Next(seed.TankIds.Length);
                var reference = $"S-LR-{tag}-OVER";
                var over = Assert.Single(await Push([OfflineClient.Op(Guid.NewGuid(), seed.AdminId, OfflineOperationTypes.SaleRequest,
                    new { customerId = seed.CustomerIds[0], productId = seed.ProductId, tankId = seed.TankIds[tank],
                        quantity = model.Stock[tank] + 5m, unitPrice = 800m, currency = "USD", reference }, D(monthStart.AddDays(26)))]));
                await using var db = server.CreateDbContext();
                Assert.NotEqual(nameof(OfflineSyncStatus.Applied), over.Status);
                Assert.False(await db.SalesTransactions.AnyAsync(x => x.TicketSerialNumber == reference), $"{reference}: oversell created a sale");
                Count("reject.oversell");
            }

            // ── ۵) دریافت جزئی از مشتری، پرداخت به فروشنده، مصرف نقدی ──
            var paymentPlan = new List<(string Kind, int Party, decimal Amount, string Reference)>();
            for (var i = 0; i < 4; i++)
            {
                var customer = random.Next(seed.CustomerIds.Length);
                var outstanding = model.CustomerSales[customer] - model.CustomerReceipts[customer];
                paymentPlan.Add(("C", customer, decimal.Round(Math.Max(outstanding, 200m) * Between(0.2m, 0.7m, 4), 2, MidpointRounding.AwayFromZero), $"CR-LR-{tag}-{i}"));
            }

            for (var i = 0; i < 2; i++)
            {
                paymentPlan.Add(("S", random.Next(seed.SupplierIds.Length), Between(3_000m, 25_000m, 2), $"SP-LR-{tag}-{i}"));
            }

            var paymentResults = await Push(paymentPlan.Select(p => p.Kind == "C"
                ? OfflineClient.Op(Guid.NewGuid(), seed.AdminId, OfflineOperationTypes.CustomerReceiptRequest,
                    new { customerId = seed.CustomerIds[p.Party], cashAccountId = seed.CashAccountId, amount = p.Amount, currency = "USD", reference = p.Reference },
                    D(monthStart.AddDays(26)))
                : OfflineClient.Op(Guid.NewGuid(), seed.AdminId, OfflineOperationTypes.SupplierPaymentRequest,
                    new { supplierId = seed.SupplierIds[p.Party], cashAccountId = seed.CashAccountId, amount = p.Amount, currency = "USD", reference = p.Reference },
                    D(monthStart.AddDays(26)))).ToList());
            for (var i = 0; i < paymentPlan.Count; i++)
            {
                var r = paymentResults[i];
                var p = paymentPlan[i];
                Assert.True(r.Status == nameof(OfflineSyncStatus.Applied), $"{p.Reference}: {r.Status} {r.Message}");
                if (p.Kind == "C")
                {
                    model.CustomerReceipts[p.Party] += p.Amount;
                    model.Cash += p.Amount;
                    model.CustomerPayments[int.Parse(r.AppliedReferenceId!, CultureInfo.InvariantCulture)] = (p.Amount, p.Party);
                    Count("payment.customer");
                }
                else
                {
                    model.SupplierPayments[p.Party] += p.Amount;
                    model.Cash -= p.Amount;
                    Count("payment.supplier");
                }
            }

            var expensePlan = Enumerable.Range(0, 2).Select(i => (Amount: Between(10m, 500m, 2), Description: $"مصرف LR {tag}-{i}")).ToList();
            var expenseResults = await Push(expensePlan.Select(p => OfflineClient.Op(Guid.NewGuid(), seed.AdminId, OfflineOperationTypes.ExpenseRequest,
                new { expenseTypeId = seed.ExpenseTypeId, amount = p.Amount, currency = "USD", cashAccountId = seed.CashAccountId, description = p.Description },
                D(monthStart.AddDays(27)))).ToList());
            for (var i = 0; i < expensePlan.Count; i++)
            {
                Assert.True(expenseResults[i].Status == nameof(OfflineSyncStatus.Applied), $"{expensePlan[i].Description}: {expenseResults[i].Status} {expenseResults[i].Message}");
                model.Expenses[int.Parse(expenseResults[i].AppliedReferenceId!, CultureInfo.InvariantCulture)] = expensePlan[i].Amount;
                model.ActiveExpenses += expensePlan[i].Amount;
                Count("expense");
            }

            // ── ۶) لغو فروش (یک ماه آنلاین، یک ماه آفلاین) ──
            if (month % 2 == 0 || month % 5 == 0)
            {
                var candidates = model.Sales.Where(x => x.Date >= monthStart.AddMonths(-2)).ToList();
                if (candidates.Count > 0)
                {
                    var sale = candidates[random.Next(candidates.Count)];
                    var form = (await FormAsync(admin, $"/Sales/Correct/{sale.Id}", $"/sales/cancel/{sale.Id}")).With(("cancelReason", $"ابطال LR {tag}"));
                    if (month % 4 == 0)
                    {
                        await OnlineAsync(admin, form);
                    }
                    else
                    {
                        await OfflineFormAsync(admin, seed.AdminId, form, $"/sales/cancel/{sale.Id}");
                    }

                    model.Sales.Remove(sale);
                    model.Stock[sale.Tank] += sale.Qty;
                    model.ReturnedToday[sale.Tank] += sale.Qty;
                    model.PoolQty[sale.Tank] += sale.Qty;
                    model.PoolValue[sale.Tank] += sale.CostUsd;
                    model.CustomerSales[sale.Customer] -= sale.TotalUsd;
                    model.Revenue -= sale.TotalUsd;
                    model.Cogs -= sale.CostUsd;
                    Count(month % 4 == 0 ? "cancel.sale.online" : "cancel.sale.offline");
                }
            }

            // ── ۷) لغو مصرف (هر سه ماه) و ویرایش مبلغ دریافت (هر چهار ماه) ──
            if (month % 3 == 1)
            {
                var expenseId = model.Expenses.Keys.Max();
                var form = await FormAsync(admin, "/Expenses", "/expenses/cancel", ("id", expenseId.ToString(CultureInfo.InvariantCulture)));
                await OfflineFormAsync(admin, seed.AdminId, form, "/expenses/cancel");
                model.ActiveExpenses -= model.Expenses[expenseId];
                model.Expenses.Remove(expenseId);
                Count("cancel.expense");
            }

            if (month % 4 == 2)
            {
                var paymentId = model.CustomerPayments.Keys.Max();
                var (oldAmount, customer) = model.CustomerPayments[paymentId];
                var newAmount = decimal.Round(oldAmount * 0.9m, 2, MidpointRounding.AwayFromZero);
                var form = (await FormAsync(admin, $"/Payments/Edit/{paymentId}", $"/payments/edit/{paymentId}"))
                    .With(("Amount", newAmount.ToString(CultureInfo.InvariantCulture)));
                await OnlineAsync(admin, form);
                model.CustomerReceipts[customer] += newAmount - oldAmount;
                model.Cash += newAmount - oldAmount;
                model.CustomerPayments[paymentId] = (newAmount, customer);
                Count("edit.payment");
            }

            // ── ۸) لغو رسیدی که کالایش فروخته شده: باید بدون اثر ناقص رد شود ──
            if (month % 6 == 3)
            {
                var blocked = model.Receipts.FirstOrDefault(x => model.PoolQty[x.Tank] < x.Qty || model.PoolValue[x.Tank] < x.ValueUsd);
                if (blocked is not null)
                {
                    var before = await SnapshotAsync(seed);
                    var form = (await FormAsync(admin, $"/LoadingReceipts/Details/{blocked.Id}", $"/loadingreceipts/cancel/{blocked.Id}"))
                        .With(("reason", $"لغو ممنوع LR {tag}"));
                    await PostAsync(admin, form);
                    await using var db = server.CreateDbContext();
                    Assert.False((await db.LoadingReceipts.AsNoTracking().SingleAsync(x => x.Id == blocked.Id)).IsCancelled,
                        $"receipt {blocked.Id} cancelled although its goods were sold");
                    Assert.Equal(before, await SnapshotAsync(seed));
                    Count("reject.receipt-cancel");
                }
            }

            // ── ۹) رسید تازه و لغو مجاز همان رسید (هر شش ماه) ──
            // مخزن جدا: برگشت رسید تاریخ امروز را دارد، پس در گذشته «موجودی شبح» می‌ماند و فروشِ عقب‌تاریخِ
            // همان مخزن را (درست) رد می‌کند؛ این سیاست تاریخ برگشت است و جدا گزارش می‌شود.
            if (month % 6 == 5)
            {
                var loadingResult = Assert.Single(await Push([OfflineClient.Op(Guid.NewGuid(), seed.AdminId, OfflineOperationTypes.LoadingNote,
                    new { contractId = seed.ContractIds[0], productId = seed.ProductId, quantity = 25m, truckId = seed.TruckId, truckPlate = "KBL-LR-1",
                        reference = $"BL-LR-{tag}-RV" }, D(monthStart.AddDays(27)))]));
                Assert.Equal(nameof(OfflineSyncStatus.Applied), loadingResult.Status);
                model.SupplierLoadings[0] += 25m * seed.ContractPrices[0];
                model.InTransit += 25m * seed.ContractPrices[0];
                var receiptResult = Assert.Single(await Push([OfflineClient.Op(Guid.NewGuid(), seed.AdminId, OfflineOperationTypes.ReceiptNote,
                    new { loadingId = int.Parse(loadingResult.AppliedReferenceId!, CultureInfo.InvariantCulture), tankId = seed.ReversalTankId, quantity = 25m,
                        reference = $"RC-LR-{tag}-RV" }, D(monthStart.AddDays(27)))]));
                Assert.Equal(nameof(OfflineSyncStatus.Applied), receiptResult.Status);
                var receiptId = int.Parse(receiptResult.AppliedReferenceId!, CultureInfo.InvariantCulture);
                var form = (await FormAsync(admin, $"/LoadingReceipts/Details/{receiptId}", $"/loadingreceipts/cancel/{receiptId}"))
                    .With(("reason", $"لغو مجاز LR {tag}"));
                await OnlineAsync(admin, form);
                Count("cancel.receipt");
                // رسید لغوشده نباید هیچ اثری بر موجودی و pool بگذارد؛ بارگیری‌اش همچنان بدهی فروشنده است.
            }

            // ── ۱۰) تکرار یک عملیات ارسال‌شده با همان شناسه: هیچ سند تازه ──
            {
                var replay = sent[random.Next(Math.Max(0, sent.Count - 30), sent.Count)];
                var before = await CountsAsync();
                var again = Assert.Single((await admin.PushAsync(replay)).Results);
                Assert.Equal(before, await CountsAsync());
                Count("replay");
                _ = again;
            }

            // ── تطبیق پایان ماه ──
            await ReconcileAsync(seed, model, tag, findings);
            if (month % 12 == 11)
            {
                await YearEndAsync(admin, seed, model, tag, findings, Count);
            }
        }

        foreach (var (key, value) in counters.OrderBy(c => c.Key, StringComparer.Ordinal))
        {
            output.WriteLine($"{key}: {value}");
        }

        output.WriteLine($"total operations: {counters.Values.Sum()} over {months} months (seed {seedValue})");
        output.WriteLine($"model: revenue {model.Revenue}, cogs {model.Cogs}, receipt-shortage value never released from goods-in-transit {model.ShortageValue}, model in-transit {model.InTransit}, cash {model.Cash}");
        foreach (var finding in findings.Distinct().Take(60))
        {
            output.WriteLine("FINDING " + finding);
        }

        Assert.True(findings.Count == 0, $"{findings.Count} reconciliation differences:\n" + string.Join("\n", findings.Distinct().Take(40)));
    }

    // ---------------------------------------------------------------- reconciliation

    private async Task ReconcileAsync(Seed seed, Model model, string tag, List<string> findings)
    {
        await using var db = server.CreateDbContext();
        var settings = await db.AccountingSettings.AsNoTracking().SingleAsync(x => x.CompanyId == seed.CompanyId);
        for (var t = 0; t < seed.TankIds.Length; t++)
        {
            var tankId = seed.TankIds[t];
            var stock = await db.InventoryMovements.AsNoTracking().Where(x => x.StorageTankId == tankId)
                .SumAsync(x => x.Direction == MovementDirection.In ? x.QuantityMt : -x.QuantityMt);
            if (stock != model.Stock[t]) findings.Add($"{tag} tank{t}: movements {stock} ≠ model {model.Stock[t]}");
            if (stock < 0m) findings.Add($"{tag} tank{t}: negative stock {stock}");

            var pool = await db.InventoryAverageCosts.AsNoTracking()
                .SingleOrDefaultAsync(x => x.ProductId == seed.ProductId && x.TerminalId == seed.TerminalIds[t]);
            var poolQty = pool?.QuantityMt ?? 0m;
            var poolValue = pool?.TotalValueUsd ?? 0m;
            if (poolQty != model.PoolQty[t]) findings.Add($"{tag} tank{t}: pool qty {poolQty} ≠ model {model.PoolQty[t]}");
            if (poolValue != model.PoolValue[t]) findings.Add($"{tag} tank{t}: pool value {poolValue} ≠ model {model.PoolValue[t]}");
            if (poolQty != stock) findings.Add($"{tag} tank{t}: pool qty {poolQty} ≠ tank stock {stock}");
            if (poolQty == 0m && poolValue != 0m) findings.Add($"{tag} tank{t}: empty pool keeps value {poolValue}");
        }

        var reversalStock = await db.InventoryMovements.AsNoTracking().Where(x => x.StorageTankId == seed.ReversalTankId)
            .SumAsync(x => x.Direction == MovementDirection.In ? x.QuantityMt : -x.QuantityMt);
        var reversalPool = await db.InventoryAverageCosts.AsNoTracking()
            .Where(x => x.ProductId == seed.ProductId && x.TerminalId == seed.ReversalTerminalId)
            .Select(x => new { x.QuantityMt, x.TotalValueUsd }).SingleOrDefaultAsync();
        if (reversalStock != 0m || (reversalPool is not null && (reversalPool.QuantityMt != 0m || reversalPool.TotalValueUsd != 0m)))
            findings.Add($"{tag} cancelled receipt left stock {reversalStock} / pool {reversalPool?.QuantityMt}/{reversalPool?.TotalValueUsd}");

        // سطر بستانکار موجودیِ سند COGS مخزن ندارد (فقط جنس/قرارداد)؛ پس تطبیق دفتر کل در سطح جنس است.
        var inventoryGl = await db.JournalEntryLines.AsNoTracking()
            .Where(l => l.ProductId == seed.ProductId && l.AccountId == settings.InventoryAccountId && l.JournalEntry!.Status == JournalEntryStatus.Posted)
            .SumAsync(l => l.Debit - l.Credit);
        var poolTotal = await db.InventoryAverageCosts.AsNoTracking().Where(x => x.ProductId == seed.ProductId).SumAsync(x => x.TotalValueUsd);
        if (inventoryGl != poolTotal) findings.Add($"{tag}: GL inventory {inventoryGl} ≠ pool value {poolTotal}");
        if (poolTotal != model.PoolValue.Sum()) findings.Add($"{tag}: pool value {poolTotal} ≠ model {model.PoolValue.Sum()}");

        // رگرسیون: مصرفِ آفلاین با «حساب پرداخت» همان پرداخت فوری است و حساب نقدی‌اش حذف نمی‌شود.
        var expenseIds = model.Expenses.Keys.ToArray();
        var lostCash = await db.ExpenseTransactions.AsNoTracking()
            .CountAsync(x => expenseIds.Contains(x.Id) && (x.CashAccountId != seed.CashAccountId || x.SettlementMode != ExpenseSettlementMode.PaidImmediately));
        if (lostCash > 0) findings.Add($"{tag}: {lostCash} offline cash expenses lost their cash account");

        for (var c = 0; c < seed.CustomerIds.Length; c++)
        {
            var customerId = seed.CustomerIds[c];
            var rows = await db.LedgerEntries.AsNoTracking().Where(x => x.CustomerId == customerId)
                .Select(x => new { x.SourceType, x.Side, x.AmountUsd }).ToListAsync();
            var sales = rows.Where(x => x.SourceType == "Sale").Sum(x => x.Side == LedgerSide.Credit ? x.AmountUsd : -x.AmountUsd);
            var receipts = rows.Where(x => x.SourceType != "Sale").Sum(x => x.Side == LedgerSide.Debit ? x.AmountUsd : -x.AmountUsd);
            if (sales != model.CustomerSales[c]) findings.Add($"{tag} customer{c}: ledger sales {sales} ≠ model {model.CustomerSales[c]}");
            if (receipts != model.CustomerReceipts[c]) findings.Add($"{tag} customer{c}: ledger receipts {receipts} ≠ model {model.CustomerReceipts[c]}");
            var documents = await db.SalesTransactions.AsNoTracking().Where(x => x.CustomerId == customerId && !x.IsCancelled).SumAsync(x => x.TotalUsd);
            if (documents != model.CustomerSales[c]) findings.Add($"{tag} customer{c}: active sale documents {documents} ≠ model {model.CustomerSales[c]}");
        }

        for (var v = 0; v < seed.SupplierIds.Length; v++)
        {
            var supplierId = seed.SupplierIds[v];
            var rows = await db.LedgerEntries.AsNoTracking().Where(x => x.SupplierId == supplierId)
                .Select(x => new { x.Side, x.AmountUsd }).ToListAsync();
            var payable = rows.Sum(x => x.Side == LedgerSide.Credit ? x.AmountUsd : -x.AmountUsd);
            var expected = model.SupplierLoadings[v] - model.SupplierPayments[v];
            if (payable != expected) findings.Add($"{tag} supplier{v}: ledger payable {payable} ≠ model {expected}");
        }

        var cash = Assert.Single(await new CashPositionReader(db).ReadAccountTotalsAsync([seed.CashAccountId], null));
        var expectedCash = model.Cash - model.ActiveExpenses;
        if (cash.BalanceUsd != expectedCash) findings.Add($"{tag} cash: reader {cash.BalanceUsd} ≠ model {expectedCash}");

        var revenue = await db.JournalEntryLines.AsNoTracking()
            .Where(l => l.ProductId == seed.ProductId && l.AccountId == settings.SalesRevenueAccountId && l.JournalEntry!.Status == JournalEntryStatus.Posted)
            .SumAsync(l => l.Credit - l.Debit);
        if (revenue != model.Revenue) findings.Add($"{tag} GL revenue {revenue} ≠ model revenue {model.Revenue}");
        var cogs = await db.JournalEntryLines.AsNoTracking()
            .Where(l => l.ProductId == seed.ProductId && l.AccountId == settings.CostOfGoodsSoldAccountId && l.JournalEntry!.Status == JournalEntryStatus.Posted)
            .SumAsync(l => l.Debit - l.Credit);
        if (cogs != model.Cogs) findings.Add($"{tag} GL COGS {cogs} ≠ model COGS {model.Cogs}");

        var inTransit = await db.JournalEntryLines.AsNoTracking()
            .Where(l => l.ContractId != null && seed.ContractIds.Contains(l.ContractId.Value) && l.AccountId == settings.InventoryInTransitAccountId
                && l.JournalEntry!.Status == JournalEntryStatus.Posted)
            .SumAsync(l => l.Debit - l.Credit);
        if (inTransit != model.InTransit) findings.Add($"{tag} GL goods in transit {inTransit} ≠ model {model.InTransit}");

        var unbalanced = await db.JournalEntryLines.AsNoTracking().GroupBy(l => l.JournalEntryId)
            .Where(g => g.Sum(l => l.Debit) != g.Sum(l => l.Credit)).CountAsync();
        if (unbalanced > 0) findings.Add($"{tag}: {unbalanced} unbalanced journals");
        var duplicates = await db.JournalEntries.AsNoTracking().Where(j => j.SourceEventId != null)
            .GroupBy(j => new { j.CompanyId, j.SourceModule, j.SourceEventId }).Where(g => g.Count() > 1).CountAsync();
        if (duplicates > 0) findings.Add($"{tag}: {duplicates} duplicate journal source events");
    }

    /// <summary>پایان سال: گزارش موجودی StockService و فروش هم‌زمان از موجودی محدود.</summary>
    private async Task YearEndAsync(OfflineClient admin, Seed seed, Model model, string tag, List<string> findings, Action<string, int> count)
    {
        await using (var db = server.CreateDbContext())
        {
            var summary = await new StockService(db).GetStockSummaryAsync(productId: seed.ProductId);
            for (var t = 0; t < seed.TankIds.Length; t++)
            {
                var reported = summary.Where(x => x.TerminalId == seed.TerminalIds[t]).Sum(x => x.FreeQuantityMt);
                if (reported != model.Stock[t]) findings.Add($"{tag} StockService terminal{t}: {reported} ≠ model {model.Stock[t]}");
            }
        }

        // شش دستگاه هم‌زمان هرکدام ۳۰٪ موجودی یک مخزن را می‌فروشند: حداکثر سه فروش مجاز است و موجودی منفی نمی‌شود.
        var tank = model.Available(0) >= model.Available(1) ? 0 : 1;
        var qty = decimal.Round(model.Available(tank) * 0.3m, 3, MidpointRounding.AwayFromZero);
        if (qty <= 0m)
        {
            return;
        }

        var date = DateTime.ParseExact(tag, "yyyyMM", CultureInfo.InvariantCulture).AddDays(27).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var references = Enumerable.Range(0, 6).Select(i => $"S-LR-{tag}-PAR{i}").ToList();
        var results = await Task.WhenAll(references.Select(reference => admin.PushAsync(OfflineClient.Op(Guid.NewGuid(), seed.AdminId,
            OfflineOperationTypes.SaleRequest, new { customerId = seed.CustomerIds[1], productId = seed.ProductId, tankId = seed.TankIds[tank],
                quantity = qty, unitPrice = 750m, currency = "USD", reference }, date))));
        var applied = results.Count(r => r.Results.Single().Status == nameof(OfflineSyncStatus.Applied));
        foreach (var rejected in results.Select(r => r.Results.Single()).Where(r => r.Status != nameof(OfflineSyncStatus.Applied)).Take(2))
        {
            output.WriteLine($"{tag} parallel rejected: {rejected.Status} {rejected.Code} {rejected.Message}");
        }

        await using (var db = server.CreateDbContext())
        {
            var created = await db.SalesTransactions.AsNoTracking()
                .Where(x => references.Contains(x.TicketSerialNumber!)).OrderBy(x => x.Id).ToListAsync();
            Assert.Equal(applied, created.Count);
            Assert.True(created.Count <= 3, $"{tag}: {created.Count} parallel sales of {qty} from {model.Available(tank)} t were accepted");
            foreach (var sale in created)
            {
                var cost = model.Consume(tank, sale.QuantityMt);
                model.Stock[tank] -= sale.QuantityMt;
                model.CustomerSales[1] += sale.TotalUsd;
                model.Revenue += sale.TotalUsd;
                model.Cogs += cost;
                model.Sales.Add(new SaleDoc(sale.Id, tank, 1, sale.QuantityMt, sale.TotalUsd, cost, sale.SaleDate));
            }
        }

        count("sale.parallel.applied", applied);
        count("reject.parallel", references.Count - applied);
        await ReconcileAsync(seed, model, tag + "-parallel", findings);
    }

    private async Task<string> SnapshotAsync(Seed seed)
    {
        await using var db = server.CreateDbContext();
        var movements = await db.InventoryMovements.CountAsync(x => x.ProductId == seed.ProductId);
        var journals = await db.JournalEntries.CountAsync();
        var ledger = await db.LedgerEntries.CountAsync();
        var pool = await db.InventoryAverageCosts.AsNoTracking().Where(x => x.ProductId == seed.ProductId)
            .OrderBy(x => x.TerminalId).Select(x => x.QuantityMt + "/" + x.TotalValueUsd).ToListAsync();
        return $"{movements}|{journals}|{ledger}|{string.Join(",", pool)}";
    }

    private async Task<string> CountsAsync()
    {
        await using var db = server.CreateDbContext();
        return $"{await db.JournalEntries.CountAsync()}|{await db.LedgerEntries.CountAsync()}|{await db.InventoryMovements.CountAsync()}|" +
               $"{await db.SalesTransactions.CountAsync()}|{await db.PaymentTransactions.CountAsync()}|{await db.ExpenseTransactions.CountAsync()}";
    }

    // ---------------------------------------------------------------- real forms

    private sealed record PageForm(string ActionPath, List<KeyValuePair<string, string>> Fields, Dictionary<string, string> Cookies, string RenderedAt)
    {
        public PageForm With(params (string Name, string Value)[] values)
        {
            var fields = Fields.Where(f => values.All(v => !string.Equals(v.Name, f.Key, StringComparison.Ordinal))).ToList();
            fields.AddRange(values.Select(v => new KeyValuePair<string, string>(v.Name, v.Value)));
            return this with { Fields = fields };
        }

        public string[][] OfflineFields => Fields.Where(f => f.Key is not "__RequestVerificationToken" and not "__FormToken")
            .Select(f => new[] { f.Key, f.Value }).ToArray();
    }

    private static async Task<HttpResponseMessage> PostAsync(OfflineClient client, PageForm form)
    {
        using var post = new HttpRequestMessage(HttpMethod.Post, form.ActionPath) { Content = new FormUrlEncodedContent(form.Fields) };
        post.Headers.Add("Cookie", string.Join("; ", form.Cookies.Select(c => $"{c.Key}={c.Value}")));
        return await client.Http.SendAsync(post);
    }

    private static async Task OnlineAsync(OfflineClient client, PageForm form)
    {
        var response = await PostAsync(client, form);
        Assert.True(response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found or HttpStatusCode.SeeOther,
            $"online {form.ActionPath} did not commit: {(int)response.StatusCode}");
        if (response.Headers.Location is { } location)
        {
            using var get = new HttpRequestMessage(HttpMethod.Get, location);
            get.Headers.Add("Cookie", client.Cookie);
            var page = await (await client.Http.SendAsync(get)).Content.ReadAsStringAsync();
            var error = Regex.Match(page, "alert-danger[^>]*>\\s*[^<\\s](.{0,600}?)</div>", RegexOptions.Singleline);
            Assert.False(error.Success, $"online {form.ActionPath}: {WebUtility.HtmlDecode(Regex.Replace(error.Value, "<[^>]+>", " ")).Trim()}");
        }
    }

    private static async Task OfflineFormAsync(OfflineClient client, int userId, PageForm form, string path)
    {
        var op = OfflineClient.Op(Guid.NewGuid(), userId, OfflineOperationTypes.FormSubmit,
            new { path, label = path, fields = form.OfflineFields, page = form.ActionPath },
            DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), baseVersion: "t:" + form.RenderedAt);
        var result = Assert.Single((await client.PushAsync(op)).Results);
        Assert.True(result.Status == nameof(OfflineSyncStatus.Applied), $"offline {path}: {result.Status} {result.Message}");
    }

    private static async Task<PageForm> FormAsync(OfflineClient client, string pagePath, string actionPath, (string Name, string Value)? having = null)
    {
        var cookies = client.Cookie.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(c => c.Split('=', 2)).ToDictionary(c => c[0], c => c.Length > 1 ? c[1] : "");
        using var get = new HttpRequestMessage(HttpMethod.Get, pagePath);
        get.Headers.Add("Cookie", client.Cookie);
        var page = await client.Http.SendAsync(get);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        if (page.Headers.TryGetValues("Set-Cookie", out var setCookies))
        {
            foreach (var pair in setCookies.Select(v => v.Split(';')[0]))
            {
                var index = pair.IndexOf('=');
                cookies[pair[..index]] = pair[(index + 1)..];
            }
        }

        var html = await page.Content.ReadAsStringAsync();
        var renderedAt = Regex.Match(html, "data-ptg-rendered=\"([^\"]+)\"").Groups[1].Value;
        var form = Regex.Matches(html, "<form\\b([^>]*)>(.*?)</form>", RegexOptions.Singleline | RegexOptions.IgnoreCase)
            .Where(m => Regex.Match(m.Groups[1].Value, "action=\"([^\"]*)\"").Groups[1].Value is { Length: > 0 } action
                && string.Equals(WebUtility.HtmlDecode(action).Split('?')[0].TrimEnd('/'), actionPath, StringComparison.OrdinalIgnoreCase))
            .Select(m => m.Groups[2].Value)
            .FirstOrDefault(body => having is not { } h
                || Regex.IsMatch(body, $"name=\"{h.Name}\"[^>]*value=\"{h.Value}\"|value=\"{h.Value}\"[^>]*name=\"{h.Name}\"", RegexOptions.IgnoreCase));
        Assert.True(form is not null, $"{pagePath}: no POST form for {actionPath}");
        form = Regex.Replace(form!, "<template\\b.*?</template>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);

        var fields = new List<KeyValuePair<string, string>>();
        foreach (Match input in Regex.Matches(form, "<input\\b[^>]*>", RegexOptions.IgnoreCase))
        {
            var tag = input.Value;
            var name = WebUtility.HtmlDecode(Regex.Match(tag, "\\bname=\"([^\"]+)\"").Groups[1].Value);
            var type = Regex.Match(tag, "\\btype=\"([^\"]+)\"", RegexOptions.IgnoreCase).Groups[1].Value.ToLowerInvariant();
            if (string.IsNullOrEmpty(name) || type is "submit" or "button" or "file" || Regex.IsMatch(tag, "\\bdisabled\\b")) continue;
            var value = WebUtility.HtmlDecode(Regex.Match(tag, "\\bvalue=\"([^\"]*)\"").Groups[1].Value);
            if (type is "checkbox" or "radio")
            {
                if (Regex.IsMatch(tag, "\\bchecked\\b", RegexOptions.IgnoreCase)) fields.Add(new(name, string.IsNullOrEmpty(value) ? "on" : value));
                continue;
            }

            fields.Add(new(name, value));
        }

        foreach (Match select in Regex.Matches(form, "<select\\b([^>]*)>(.*?)</select>", RegexOptions.Singleline | RegexOptions.IgnoreCase))
        {
            var name = WebUtility.HtmlDecode(Regex.Match(select.Groups[1].Value, "\\bname=\"([^\"]+)\"").Groups[1].Value);
            if (string.IsNullOrEmpty(name) || Regex.IsMatch(select.Groups[1].Value, "\\bdisabled\\b")) continue;
            var options = Regex.Matches(select.Groups[2].Value, "<option\\b([^>]*)>", RegexOptions.IgnoreCase).Select(o => o.Groups[1].Value).ToList();
            var chosen = options.FirstOrDefault(o => Regex.IsMatch(o, "\\bselected\\b", RegexOptions.IgnoreCase)) ?? options.FirstOrDefault();
            fields.Add(new(name, chosen is null ? "" : WebUtility.HtmlDecode(Regex.Match(chosen, "value=\"([^\"]*)\"").Groups[1].Value)));
        }

        foreach (Match area in Regex.Matches(form, "<textarea\\b([^>]*)>(.*?)</textarea>", RegexOptions.Singleline | RegexOptions.IgnoreCase))
        {
            var name = WebUtility.HtmlDecode(Regex.Match(area.Groups[1].Value, "\\bname=\"([^\"]+)\"").Groups[1].Value);
            if (!string.IsNullOrEmpty(name)) fields.Add(new(name, WebUtility.HtmlDecode(area.Groups[2].Value)));
        }

        Assert.Contains(fields, f => f.Key == "__RequestVerificationToken");
        return new PageForm(actionPath, fields, cookies, renderedAt);
    }

    // ---------------------------------------------------------------- seed

    private async Task<Seed> SeedAsync()
    {
        await using var db = server.CreateDbContext();
        var company = await db.Companies.FirstOrDefaultAsync(c => c.IsSystemOwner);
        if (company is null)
        {
            company = new Company { Code = "MSH", Name = "Mashal Test", Country = "AF", IsSystemOwner = true, IsActive = true };
            db.Companies.Add(company);
        }

        foreach (var (code, name) in new[] { ("USD", "US Dollar"), ("AFN", "Afghani") })
        {
            if (!await db.Currencies.AnyAsync(c => c.Code == code))
            {
                db.Currencies.Add(new Currency { Code = code, Name = name, IsActive = true });
            }
        }

        var product = new Product { Code = "DSL-LR", Name = "Diesel Long Run", UnitOfMeasure = "MT" };
        var expenseType = new ExpenseType { Code = "EXP-LR", Name = "Long Run Expense" };
        var truck = new Truck { PlateNumber = "KBL-LR-1" };
        var terminals = Enumerable.Range(0, 3).Select(i => new Terminal { Code = $"T-LR-{i}", Name = $"Long Run Terminal {i}" }).ToArray();
        var suppliers = Enumerable.Range(0, 2).Select(i => new Supplier { Code = $"S-LR-{i}", Name = $"Long Run Supplier {i}" }).ToArray();
        var customers = Enumerable.Range(0, 4).Select(i => new Customer { Code = $"C-LR-{i}", Name = $"Long Run Customer {i}" }).ToArray();
        db.AddRange(product, expenseType, truck);
        db.AddRange(terminals);
        db.AddRange(suppliers);
        db.AddRange(customers);
        await db.SaveChangesAsync();

        var cash = new CashAccount { Code = "CA-LR", Name = "Long Run Cash", CompanyId = company.Id, Currency = "USD", AccountType = CashAccountType.Cash };
        var tanks = terminals.Select((t, i) => new StorageTank { TerminalId = t.Id, TankCode = $"TK-LR-{i}", ProductId = product.Id, CapacityMt = 1_000_000 }).ToArray();
        var prices = new[] { 600m, 640m };
        var contracts = suppliers.Select((supplier, i) => new Contract
        {
            ContractName = $"Long Run Purchase {i}", ContractNumber = $"PC-LR-{i}", ContractType = ContractType.Purchase,
            Status = ContractStatus.Active, CompanyId = company.Id, ProductId = product.Id, SupplierId = supplier.Id,
            ContractDate = Start.AddMonths(-1), QuantityMt = 1_000_000, UnitPriceUsd = prices[i], Currency = "USD",
            ManualFinalPriceUsd = prices[i]
        }).ToArray();
        db.Add(cash);
        db.AddRange(tanks);
        db.AddRange(contracts);
        await db.SaveChangesAsync();

        // سال‌های مالی باز برای کل بازهٔ شبیه‌سازی (فقط در همین دیتابیس موقت).
        for (var year = Start.Year; year <= DateTime.UtcNow.Year; year++)
        {
            var yearStart = new DateTime(year, 1, 1);
            if (await db.FiscalYears.AnyAsync(y => y.CompanyId == company.Id && y.StartDate <= yearStart && y.EndDate >= yearStart))
            {
                continue;
            }

            var fiscalYear = new FiscalYear
            {
                CompanyId = company.Id, Name = year.ToString(CultureInfo.InvariantCulture), StartDate = yearStart,
                EndDate = new DateTime(year, 12, 31), Status = FiscalYearStatus.Open, IsCurrent = year == DateTime.UtcNow.Year
            };
            db.FiscalYears.Add(fiscalYear);
            await db.SaveChangesAsync();
            for (var month = 1; month <= 12; month++)
            {
                var start = new DateTime(year, month, 1);
                db.FiscalPeriods.Add(new FiscalPeriod
                {
                    CompanyId = company.Id, FiscalYearId = fiscalYear.Id, PeriodNumber = month, Name = $"P{month}",
                    StartDate = start, EndDate = start.AddMonths(1).AddDays(-1), Status = FiscalPeriodStatus.Open
                });
            }

            await db.SaveChangesAsync();
        }

        await new AccountingChartSeeder(db, Options.Create(new AccountingOptions { Enabled = true })).SeedAsync();
        var admin = await db.Users.SingleAsync(u => u.Username == "admin");
        return new Seed(company.Id, product.Id, expenseType.Id, cash.Id, truck.Id, admin.Id,
            tanks[..2].Select(t => t.Id).ToArray(), terminals[..2].Select(t => t.Id).ToArray(), tanks[2].Id, terminals[2].Id, contracts.Select(c => c.Id).ToArray(), prices,
            suppliers.Select(x => x.Id).ToArray(), customers.Select(x => x.Id).ToArray());
    }
}
