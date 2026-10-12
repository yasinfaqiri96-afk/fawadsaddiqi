namespace PTGOilSystem.Web.Models.ShipmentPnl;

public sealed class ShipmentContractBreakdownLine
{
    public string ContractNumber { get; init; } = "-";
    public decimal QuantityMt { get; init; }
    public decimal AmountUsd { get; init; }
    public string? Description { get; init; }
}

public sealed class ShipmentSaleDisplayRow
{
    public int Id { get; init; }
    public DateTime SaleDate { get; init; }
    public string InvoiceNumber { get; init; } = string.Empty;
    public string? CustomerName { get; init; }
    public decimal QuantityMt { get; init; }
    public decimal UnitPriceUsd { get; init; }
    public decimal TotalUsd { get; init; }
    /// <summary>دست‌کم یکی از فروش‌های این ردیف هنوز قیمت ندارد؛ مبلغ ردیف نامشخص است.</summary>
    public bool IsSalePricePending { get; init; }
    public bool IsDirectShipmentSale { get; init; }
    /// <summary>نمبر وسیله؛ اگر سطر گروهی چند وسیله داشته باشد «چند وسیله».</summary>
    public string? VehicleNumber { get; init; }
    public IReadOnlyList<ShipmentContractBreakdownLine> ContractBreakdownLines { get; init; } = [];
}

public sealed class ShipmentExpenseDisplayRow
{
    public int Id { get; init; }
    public DateTime ExpenseDate { get; init; }
    public string ExpenseTypeName { get; init; } = string.Empty;
    public string? Description { get; init; }
    public decimal AmountUsd { get; init; }
    public bool IsCustoms { get; init; }
    /// <summary>ستون ساخت‌یافتهٔ ExpenseType.Category از دیتابیس (Transport/Storage/…).</summary>
    public string? ExpenseTypeCategory { get; init; }
    /// <summary>دستهٔ نمایشی واحد — منبع مشترک KPI و جدول‌های صفحهٔ پروندهٔ محموله.</summary>
    public ShipmentExpenseCategory Category => ShipmentExpenseCategorizer.Categorize(this);
    /// <summary>نمبر وسیله؛ اگر سطر گروهی چند وسیله داشته باشد «چند وسیله».</summary>
    public string? VehicleNumber { get; init; }
    public IReadOnlyList<ShipmentContractBreakdownLine> ContractBreakdownLines { get; init; } = [];
}

public sealed class ShipmentLossDisplayRow
{
    public int Id { get; init; }
    public DateTime EventDate { get; init; }
    public decimal QuantityMt { get; init; }
    public decimal EstimatedValueUsd { get; init; }
    public string ResponsibilityTypeName { get; init; } = "-";
    public string? Description { get; init; }
    /// <summary>نمبر وسیله؛ اگر سطر گروهی چند وسیله داشته باشد «چند وسیله».</summary>
    public string? VehicleNumber { get; init; }
    public IReadOnlyList<ShipmentContractBreakdownLine> ContractBreakdownLines { get; init; } = [];
}

public sealed class ShipmentTransportDisplayRow
{
    public string GroupKey { get; init; } = string.Empty;
    public DateTime LoadedDate { get; init; }
    public string TransportTypeName { get; init; } = "-";
    public string TransportStatusName { get; init; } = "-";
    public bool IsOriginalVesselMovement { get; init; }
    public string SourceName { get; init; } = "-";
    public string DestinationName { get; init; } = "-";
    public decimal QuantityMt { get; init; }
    public decimal ReceivedQuantityMt { get; init; }
    public decimal SoldQuantityMt { get; init; }
    public decimal ShortageQuantityMt { get; init; }
    public IReadOnlyList<ShipmentPnlTransportLegItemViewModel> Items { get; init; } = [];
}

public static class ShipmentPnlDisplayGrouping
{
    // وقتی چند رکورد با وسیله‌های مختلف در یک سطر گروه می‌شوند، عدد واحدی وجود ندارد.
    private const string MixedVehicleText = "چند وسیله";

    public static IReadOnlyList<ShipmentTransportDisplayRow> GroupTransports(
        IReadOnlyList<ShipmentPnlTransportLegItemViewModel> transports)
        => transports
            .GroupBy(
                item => !string.IsNullOrWhiteSpace(item.TransportGroupKey)
                    ? item.TransportGroupKey.Trim()
                    : $"LEG:{item.Id}",
                StringComparer.OrdinalIgnoreCase)
            .Select(group => new ShipmentTransportDisplayRow
            {
                GroupKey = group.Key,
                LoadedDate = group.Min(item => item.LoadedDate),
                TransportTypeName = ResolveSingleOrMixed(
                    group.Select(item => item.TransportTypeName),
                    "چند نوع وسیله") ?? "-",
                TransportStatusName = ResolveSingleOrMixed(
                    group.Select(item => item.TransportStatusName),
                    "وضعیت ترکیبی") ?? "-",
                IsOriginalVesselMovement = group.All(item => item.IsOriginalVesselMovement),
                SourceName = ResolveSingleOrMixed(
                    group.Select(item => item.SourceName),
                    "چند مبدأ") ?? "-",
                DestinationName = ResolveSingleOrMixed(
                    group.Select(item => item.DestinationName),
                    "چند مقصد") ?? "-",
                QuantityMt = RoundQuantity(group.Sum(item => item.QuantityMt)),
                ReceivedQuantityMt = RoundQuantity(group.Sum(item => item.ReceivedQuantityMt)),
                SoldQuantityMt = RoundQuantity(group.Sum(item => item.SoldQuantityMt)),
                ShortageQuantityMt = RoundQuantity(group.Sum(item => item.ShortageQuantityMt)),
                Items = group.OrderBy(item => item.Id).ToList()
            })
            .OrderByDescending(row => row.LoadedDate)
            .ThenBy(row => row.GroupKey)
            .ToList();

    public static IReadOnlyList<ShipmentSaleDisplayRow> GroupSales(
        IReadOnlyList<ShipmentPnlSalesItemViewModel> sales)
        => sales
            .GroupBy(
                sale => string.IsNullOrWhiteSpace(sale.InvoiceNumber)
                    ? $"SALE:{sale.Id}"
                    : $"INVOICE:{sale.InvoiceNumber.Trim()}",
                StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var ordered = group
                    .OrderByDescending(sale => sale.SaleDate)
                    .ThenByDescending(sale => sale.Id)
                    .ToList();
                var quantityMt = RoundQuantity(ordered.Sum(sale => sale.QuantityMt));
                var totalUsd = RoundMoney(ordered.Sum(sale => sale.TotalUsd));
                var breakdown = AggregateBreakdown(
                    ordered.SelectMany(sale =>
                        sale.ContractBreakdownLines.Count > 0
                            ? sale.ContractBreakdownLines
                            : string.IsNullOrWhiteSpace(sale.ContractNumber)
                                ? []
                                :
                                [
                                    new ShipmentContractBreakdownLine
                                    {
                                        ContractNumber = sale.ContractNumber!,
                                        QuantityMt = sale.QuantityMt,
                                        AmountUsd = sale.TotalUsd,
                                        Description = sale.InvoiceNumber
                                    }
                                ]));

                return new ShipmentSaleDisplayRow
                {
                    Id = ordered.Min(sale => sale.Id),
                    SaleDate = ordered.Max(sale => sale.SaleDate),
                    InvoiceNumber = ordered.First().InvoiceNumber,
                    CustomerName = ResolveSingleOrMixed(
                        ordered.Select(sale => sale.CustomerName),
                        "چند مشتری"),
                    QuantityMt = quantityMt,
                    UnitPriceUsd = quantityMt > 0m
                        ? RoundMoney(totalUsd / quantityMt)
                        : RoundMoney(ordered.First().UnitPriceUsd),
                    TotalUsd = totalUsd,
                    IsSalePricePending = ordered.Any(sale => sale.IsSalePricePending),
                    IsDirectShipmentSale = ordered.Any(sale => sale.IsDirectShipmentSale),
                    VehicleNumber = ResolveSingleOrMixed(
                        ordered.Select(sale => sale.VehicleNumber),
                        MixedVehicleText),
                    ContractBreakdownLines = breakdown
                };
            })
            .OrderByDescending(row => row.SaleDate)
            .ThenByDescending(row => row.Id)
            .ToList();

    public static IReadOnlyList<ShipmentExpenseDisplayRow> GroupExpenses(
        IReadOnlyList<ShipmentPnlExpenseItemViewModel> expenses)
        => expenses
            .GroupBy(BuildExpenseGroupKey, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var ordered = group
                    .OrderByDescending(expense => expense.ExpenseDate)
                    .ThenByDescending(expense => expense.Id)
                    .ToList();
                var first = ordered[0];
                var breakdown = AggregateBreakdown(
                    ordered
                        .Where(expense => !string.IsNullOrWhiteSpace(expense.ContractNumber))
                        .Select(expense => new ShipmentContractBreakdownLine
                        {
                            ContractNumber = expense.ContractNumber!,
                            QuantityMt = expense.AllocationQuantityMt,
                            AmountUsd = expense.AmountUsd,
                            Description = CleanExpenseDescription(expense.Description)
                        }));

                return new ShipmentExpenseDisplayRow
                {
                    Id = ordered.Min(expense => expense.Id),
                    ExpenseDate = ordered.Max(expense => expense.ExpenseDate),
                    ExpenseTypeName = ResolveSingleOrMixed(
                        ordered.Select(expense => expense.ExpenseTypeName),
                        "چند نوع مصرف") ?? "-",
                    Description = ResolveSingleOrMixed(
                        ordered.Select(expense => CleanExpenseDescription(expense.Description)),
                        "مصرف تخصیص‌یافته بین چند قرارداد"),
                    AmountUsd = RoundMoney(ordered.Sum(expense => expense.AmountUsd)),
                    IsCustoms = ordered.All(expense => expense.IsCustoms),
                    ExpenseTypeCategory = ordered
                        .Select(expense => expense.ExpenseTypeCategory)
                        .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)),
                    VehicleNumber = ResolveSingleOrMixed(
                        ordered.Select(expense => expense.VehicleNumber),
                        MixedVehicleText),
                    ContractBreakdownLines = breakdown
                };
            })
            .OrderByDescending(row => row.ExpenseDate)
            .ThenByDescending(row => row.Id)
            .ToList();

    public static IReadOnlyList<ShipmentLossDisplayRow> GroupLosses(
        IReadOnlyList<ShipmentJourneyLossItem> losses,
        IReadOnlyList<ShipmentContractLineViewModel> contractLines)
        => losses
            .GroupBy(BuildLossGroupKey, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var ordered = group
                    .OrderByDescending(loss => loss.EventDate)
                    .ThenByDescending(loss => loss.Id)
                    .ToList();
                var breakdown = AggregateBreakdown(
                    ordered.SelectMany(loss => BuildLossBreakdown(loss, contractLines)));
                var descriptions = ordered
                    .Select(loss => loss.Notes)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => value!.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                return new ShipmentLossDisplayRow
                {
                    Id = ordered.Min(loss => loss.Id),
                    EventDate = ordered.Max(loss => loss.EventDate),
                    QuantityMt = RoundQuantity(ordered.Sum(loss => loss.DifferenceQuantityMt)),
                    EstimatedValueUsd = RoundMoney(breakdown.Sum(line => line.AmountUsd)),
                    ResponsibilityTypeName = ResolveSingleOrMixed(
                        ordered.Select(loss => loss.ResponsibilityTypeName),
                        "تقسیم بین چند طرف") ?? "-",
                    Description = descriptions.Count switch
                    {
                        0 => null,
                        1 => descriptions[0],
                        _ => string.Join("؛ ", descriptions)
                    },
                    VehicleNumber = ResolveSingleOrMixed(
                        ordered.Select(loss => loss.VehicleNumber),
                        MixedVehicleText),
                    ContractBreakdownLines = breakdown
                };
            })
            .OrderByDescending(row => row.EventDate)
            .ThenByDescending(row => row.Id)
            .ToList();

    private static string BuildExpenseGroupKey(ShipmentPnlExpenseItemViewModel expense)
    {
        var groupKey = ExtractTaggedValue(expense.Description, "GroupKey:");
        if (string.IsNullOrWhiteSpace(groupKey))
        {
            return string.IsNullOrWhiteSpace(expense.SourceKey)
                ? $"EXPENSE:{expense.Id}"
                : expense.SourceKey;
        }

        return $"GROUP:{groupKey}";
    }

    private static string BuildLossGroupKey(ShipmentJourneyLossItem loss)
    {
        if (!string.IsNullOrWhiteSpace(loss.Reference))
        {
            return $"REFERENCE:{loss.Reference.Trim()}:{loss.EventDate:yyyyMMdd}:{loss.StageName}";
        }

        if (!string.IsNullOrWhiteSpace(loss.AllocationGroupKey))
        {
            return $"GROUP:{loss.AllocationGroupKey.Trim()}:{loss.EventDate:yyyyMMdd}:{loss.StageName}";
        }

        return $"LOSS:{loss.Id}";
    }

    private static IReadOnlyList<ShipmentContractBreakdownLine> BuildLossBreakdown(
        ShipmentJourneyLossItem loss,
        IReadOnlyList<ShipmentContractLineViewModel> contractLines)
    {
        if (loss.ContractId.HasValue)
        {
            var contract = contractLines.FirstOrDefault(line => line.ContractId == loss.ContractId.Value);
            var contractNumber = !string.IsNullOrWhiteSpace(loss.ContractDisplayLabel)
                ? loss.ContractDisplayLabel
                : contract?.DisplayLabel
                ?? $"#{loss.ContractId.Value}";
            var amountUsd = contract?.UnitPriceUsd is > 0m
                ? RoundMoney(loss.DifferenceQuantityMt * contract.UnitPriceUsd.Value)
                : 0m;

            return
            [
                new ShipmentContractBreakdownLine
                {
                    ContractNumber = contractNumber,
                    QuantityMt = RoundQuantity(loss.DifferenceQuantityMt),
                    AmountUsd = amountUsd,
                    Description = loss.Notes
                }
            ];
        }

        var weightedContracts = contractLines
            .Select(line => new
            {
                Line = line,
                Weight = line.LoadedAvailableBeforeDirectLossQuantityMt > 0m
                    ? line.LoadedAvailableBeforeDirectLossQuantityMt
                    : line.UsedQuantityMt > 0m
                        ? line.UsedQuantityMt
                        : line.AllocatedQuantityMt
            })
            .Where(row => row.Weight > 0m)
            .OrderBy(row => row.Line.ContractNumber)
            .ToList();

        if (weightedContracts.Count == 0)
        {
            return
            [
                new ShipmentContractBreakdownLine
                {
                    ContractNumber = "-",
                    QuantityMt = RoundQuantity(loss.DifferenceQuantityMt),
                    AmountUsd = 0m,
                    Description = loss.Notes
                }
            ];
        }

        var totalWeight = weightedContracts.Sum(row => row.Weight);
        var allocatedQuantity = 0m;
        var result = new List<ShipmentContractBreakdownLine>(weightedContracts.Count);
        for (var index = 0; index < weightedContracts.Count; index++)
        {
            var row = weightedContracts[index];
            var quantityMt = index == weightedContracts.Count - 1
                ? RoundQuantity(loss.DifferenceQuantityMt - allocatedQuantity)
                : RoundQuantity(loss.DifferenceQuantityMt * row.Weight / totalWeight);
            allocatedQuantity += quantityMt;

            result.Add(new ShipmentContractBreakdownLine
            {
                ContractNumber = row.Line.ContractNumber,
                QuantityMt = quantityMt,
                AmountUsd = row.Line.UnitPriceUsd is > 0m
                    ? RoundMoney(quantityMt * row.Line.UnitPriceUsd.Value)
                    : 0m,
                Description = loss.Notes
            });
        }

        return result;
    }

    private static IReadOnlyList<ShipmentContractBreakdownLine> AggregateBreakdown(
        IEnumerable<ShipmentContractBreakdownLine> lines)
        => lines
            .GroupBy(
                line => string.IsNullOrWhiteSpace(line.ContractNumber)
                    ? "-"
                    : line.ContractNumber.Trim(),
                StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var descriptions = group
                    .Select(line => line.Description)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => value!.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                return new ShipmentContractBreakdownLine
                {
                    ContractNumber = group.Key,
                    QuantityMt = RoundQuantity(group.Sum(line => line.QuantityMt)),
                    AmountUsd = RoundMoney(group.Sum(line => line.AmountUsd)),
                    Description = descriptions.Count switch
                    {
                        0 => null,
                        1 => descriptions[0],
                        _ => string.Join("؛ ", descriptions)
                    }
                };
            })
            .OrderBy(line => line.ContractNumber)
            .ToList();

    private static string? CleanExpenseDescription(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return null;
        }

        var firstPart = description
            .Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
        return string.IsNullOrWhiteSpace(firstPart) ? null : firstPart.Trim();
    }

    private static string? ExtractTaggedValue(string? text, string tag)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var taggedPart = text
            .Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(part => part.StartsWith(tag, StringComparison.OrdinalIgnoreCase));

        return taggedPart is null
            ? null
            : taggedPart[tag.Length..].Trim();
    }

    private static string? ResolveSingleOrMixed(
        IEnumerable<string?> values,
        string mixedText)
    {
        var distinct = values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(2)
            .ToList();

        return distinct.Count switch
        {
            0 => null,
            1 => distinct[0],
            _ => mixedText
        };
    }

    private static decimal RoundMoney(decimal value)
        => decimal.Round(value, 4, MidpointRounding.AwayFromZero);

    private static decimal RoundQuantity(decimal value)
        => decimal.Round(value, 4, MidpointRounding.AwayFromZero);
}
