using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Payments;

namespace PTGOilSystem.Web.Services.OfflineSync;

/// <summary>نقشهٔ ثبت: کدام اکشن موجود، با کدام فیلدهای فرم.</summary>
public sealed record OfflineApplyPlan(
    string Controller,
    string Action,
    IReadOnlyDictionary<string, string?> Fields,
    string ReferenceType,
    bool UsesFormToken);

/// <summary>نتیجهٔ ساخت نقشه: یا نقشه، یا «سند لازم نیست» (یادداشت)، یا خطای قابل اصلاح، یا «پشتیبانی نمی‌شود».</summary>
public sealed record OfflinePlanResult(OfflineApplyPlan? Plan, bool NoDocument, string? Error, bool Unsupported);

/// <summary>
/// تبدیلِ عملیات آفلاین به همان فرم POSTِ موجودِ هر ماژول. این کلاس فقط «نگاشت فیلد» است: هیچ مبلغ، موجودی،
/// نرخ یا سند حسابداری اینجا محاسبه نمی‌شود؛ همهٔ اعتبارسنجی و ثبت در اکشن موجود انجام می‌شود.
/// <list type="bullet">
///   <item>Sale.Request → <c>Sales/Create</c> (فروش از مخزن؛ قیمت خالی = «قیمت بعداً» همان گردش کار موجود)</item>
///   <item>CustomerReceipt / SupplierPayment → <c>Payments/Create</c> (روزنامچه، نقد/بانک، منبع: شرکت)</item>
///   <item>Expense.Request → <c>Expenses/Create</c></item>
///   <item>Loading.Note → <c>Loading/Create</c> (یک سطر موتر)</item>
///   <item>Receipt.Note → <c>LoadingReceipts/Create</c> (رسید به مخزن)</item>
///   <item>Customer.Draft → <c>Customers/Create</c></item>
///   <item>General.Note → بدون سند</item>
///   <item>MoneyTransfer.Request → مشعل فرم «انتقال میان حساب‌ها» ندارد؛ ثبت خودکار ممکن نیست.</item>
/// </list>
/// </summary>
public sealed class OfflineOperationApplier(ApplicationDbContext db)
{
    public const string OperationReferencePrefix = "op:";

    public static string FormTokenFor(Guid clientOperationId) => $"offline:{clientOperationId:N}";

    public async Task<OfflinePlanResult> BuildAsync(OfflineSyncOperation operation, CancellationToken ct)
    {
        JsonElement payload;
        try
        {
            using var document = JsonDocument.Parse(operation.PayloadJson);
            payload = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return Error("محتوای عملیات JSON معتبر نیست.");
        }

        var date = (operation.BusinessDate ?? operation.ClientCreatedAtUtc).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var resolver = new ReferenceResolver(db, operation.UserId, payload);

        switch (operation.OperationType)
        {
            case OfflineOperationTypes.GeneralNote:
                return new OfflinePlanResult(null, NoDocument: true, null, false);

            case OfflineOperationTypes.MoneyTransferRequest:
                return new OfflinePlanResult(null, false,
                    "مشعل فرم «انتقال پول میان حساب‌ها» ندارد؛ این عملیات را در روزنامچه به‌شکل پرداخت و دریافت جدا ثبت کنید.",
                    Unsupported: true);

            case OfflineOperationTypes.SaleRequest:
            {
                var customerId = await resolver.IdAsync("customerId", ct);
                if (customerId.Error is not null) return Error(customerId.Error);
                var tankId = Number(payload, "tankId");
                int? terminalId = null;
                if (int.TryParse(tankId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tank))
                {
                    terminalId = await db.StorageTanks.AsNoTracking().Where(t => t.Id == tank).Select(t => (int?)t.TerminalId).FirstOrDefaultAsync(ct);
                }

                var unitPrice = Number(payload, "unitPrice");
                var pricePending = string.IsNullOrWhiteSpace(unitPrice);
                return Plan("Sales", "Create", nameof(SalesTransaction), usesFormToken: true, new()
                {
                    ["SaleStage"] = nameof(SaleStage.TerminalStock),
                    ["CompanyId"] = Number(payload, "companyId") ?? (await OwnerCompanyIdAsync(ct))?.ToString(CultureInfo.InvariantCulture),
                    ["CustomerId"] = customerId.Value,
                    ["ProductId"] = Number(payload, "productId"),
                    ["SourceTerminalId"] = terminalId?.ToString(CultureInfo.InvariantCulture),
                    ["SourceStorageTankId"] = tankId,
                    ["QuantityMt"] = Number(payload, "quantity"),
                    ["Currency"] = Text(payload, "currency"),
                    ["IsSalePricePending"] = pricePending ? "true" : "false",
                    ["UnitPriceInCurrency"] = pricePending ? "0" : unitPrice,
                    ["AppliedFxRateToUsd"] = pricePending ? null : Number(payload, "fxRate"),
                    ["TicketSerialNumber"] = Text(payload, "reference"),
                    ["SaleDate"] = date,
                    ["Notes"] = OfflineNote(payload, operation)
                });
            }

            case OfflineOperationTypes.CustomerReceiptRequest:
            case OfflineOperationTypes.SupplierPaymentRequest:
            {
                var isReceipt = operation.OperationType == OfflineOperationTypes.CustomerReceiptRequest;
                var partyField = isReceipt ? "customerId" : "supplierId";
                var party = await resolver.IdAsync(partyField, ct);
                if (party.Error is not null) return Error(party.Error);
                return Plan("Payments", "Create", nameof(PaymentTransaction), usesFormToken: true, new()
                {
                    ["PaymentDate"] = date,
                    ["Direction"] = isReceipt ? nameof(PaymentDirection.In) : nameof(PaymentDirection.Out),
                    ["PaymentKind"] = isReceipt ? nameof(PaymentKind.CustomerReceipt) : nameof(PaymentKind.SupplierPayment),
                    ["CounterpartyType"] = isReceipt ? nameof(PaymentCounterpartyType.Customer) : nameof(PaymentCounterpartyType.Supplier),
                    ["PaymentMethod"] = nameof(PaymentMethod.CashBank),
                    ["FundingSource"] = nameof(PaymentFundingSource.Company),
                    [isReceipt ? "CustomerId" : "SupplierId"] = party.Value,
                    ["CashAccountId"] = Number(payload, "cashAccountId"),
                    ["Amount"] = Number(payload, "amount"),
                    ["Currency"] = Text(payload, "currency"),
                    ["AppliedFxRateToUsd"] = Number(payload, "fxRate"),
                    ["Reference"] = Text(payload, "reference"),
                    ["Description"] = OfflineNote(payload, operation)
                });
            }

            case OfflineOperationTypes.ExpenseRequest:
                return Plan("Expenses", "Create", nameof(ExpenseTransaction), usesFormToken: true, new()
                {
                    ["ExpenseTypeId"] = Number(payload, "expenseTypeId"),
                    ["ExpenseDate"] = date,
                    ["Amount"] = Number(payload, "amount"),
                    ["Currency"] = Text(payload, "currency"),
                    ["AppliedFxRateToUsd"] = Number(payload, "fxRate"),
                    ["CashAccountId"] = Number(payload, "cashAccountId"),
                    ["Description"] = Text(payload, "description") ?? OfflineNote(payload, operation)
                });

            case OfflineOperationTypes.LoadingNote:
                return Plan("Loading", "Create", nameof(LoadingRegister), usesFormToken: true, new()
                {
                    ["ContractId"] = Number(payload, "contractId"),
                    ["ProductId"] = Number(payload, "productId"),
                    ["TransportType"] = TransportType(payload),
                    ["TruckId"] = Number(payload, "truckId"),
                    ["LoadingDate"] = date,
                    ["Notes"] = OfflineNote(payload, operation),
                    ["Rows[0].RowKey"] = "offline-1",
                    ["Rows[0].ContractId"] = Number(payload, "contractId"),
                    ["Rows[0].LoadingDate"] = date,
                    ["Rows[0].TruckId"] = Number(payload, "truckId"),
                    ["Rows[0].ImportedTransportReference"] = Text(payload, "truckPlate"),
                    ["Rows[0].BillOfLadingNumber"] = Text(payload, "reference"),
                    ["Rows[0].DriverName"] = Text(payload, "driverName"),
                    ["Rows[0].LoadedQuantityMt"] = Number(payload, "quantity")
                });

            case OfflineOperationTypes.ReceiptNote:
            {
                var tankId = Number(payload, "tankId");
                int? terminalId = null;
                if (int.TryParse(tankId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tank))
                {
                    terminalId = await db.StorageTanks.AsNoTracking().Where(t => t.Id == tank).Select(t => (int?)t.TerminalId).FirstOrDefaultAsync(ct);
                }

                return Plan("LoadingReceipts", "Create", nameof(LoadingReceipt), usesFormToken: true, new()
                {
                    ["LoadingRegisterId"] = Number(payload, "loadingId"),
                    ["ReceiptDestination"] = nameof(LoadingReceiptDestination.ToInventory),
                    ["ReceiptDate"] = date,
                    ["ArrivalDate"] = date,
                    ["ReceivedQuantityMt"] = Number(payload, "quantity"),
                    ["ReferenceDocument"] = Text(payload, "reference"),
                    ["TerminalId"] = terminalId?.ToString(CultureInfo.InvariantCulture),
                    ["StorageTankId"] = tankId,
                    ["Notes"] = OfflineNote(payload, operation)
                });
            }

            case OfflineOperationTypes.CustomerDraft:
                return Plan("Customers", "Create", nameof(Customer), usesFormToken: false, new()
                {
                    ["Name"] = Text(payload, "name"),
                    ["Phone"] = Text(payload, "phone"),
                    ["Notes"] = Text(payload, "description"),
                    ["IsActive"] = "true"
                });

            default:
                return new OfflinePlanResult(null, false, "نوع عملیات پشتیبانی نمی‌شود.", Unsupported: true);
        }
    }

    /// <summary>نوع حمل از فرم آفلاین (موتر پیش‌فرض)، همان مقادیر <see cref="LoadingTransportType"/>.</summary>
    private static string TransportType(JsonElement payload)
        => Text(payload, "transportType") is { } value && Enum.TryParse<LoadingTransportType>(value, out var parsed)
            && parsed != LoadingTransportType.Unspecified
            ? parsed.ToString()
            : nameof(LoadingTransportType.Truck);

    private async Task<int?> OwnerCompanyIdAsync(CancellationToken ct)
        => await db.Companies.AsNoTracking().Where(c => c.IsSystemOwner).Select(c => (int?)c.Id).FirstOrDefaultAsync(ct);

    private static OfflinePlanResult Plan(
        string controller, string action, string referenceType, bool usesFormToken, Dictionary<string, string?> fields)
        => new(new OfflineApplyPlan(controller, action, fields, referenceType, usesFormToken), false, null, false);

    private static OfflinePlanResult Error(string message) => new(null, false, message, false);

    private static string OfflineNote(JsonElement payload, OfflineSyncOperation operation)
    {
        var description = Text(payload, "description");
        var tag = $"[ثبت آفلاین {operation.ClientOperationId.ToString("N")[..8]}]";
        return string.IsNullOrWhiteSpace(description) ? tag : $"{description} {tag}";
    }

    internal static string? Text(JsonElement payload, string name)
        => payload.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!.Trim()
            : null;

    internal static string? Number(JsonElement payload, string name)
    {
        if (!payload.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDecimal(out var number) => number.ToString(CultureInfo.InvariantCulture),
            JsonValueKind.String when decimal.TryParse(value.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
                => parsed.ToString(CultureInfo.InvariantCulture),
            _ => null
        };
    }

    /// <summary>
    /// شناسهٔ مرجع، یا <c>op:{ClientOperationId}</c> یعنی «رکوردی که عملیات پیش‌نیازِ همین کاربر ساخت»
    /// (مثلاً مشتری تازه‌ای که آفلاین ثبت شد و فروش به آن وابسته است).
    /// </summary>
    private sealed class ReferenceResolver(ApplicationDbContext db, int userId, JsonElement payload)
    {
        public async Task<(string? Value, string? Error)> IdAsync(string field, CancellationToken ct)
        {
            if (payload.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String
                && value.GetString() is { } text && text.StartsWith(OperationReferencePrefix, StringComparison.Ordinal))
            {
                if (!Guid.TryParse(text[OperationReferencePrefix.Length..], out var dependencyId))
                {
                    return (null, "شناسهٔ عملیات پیش‌نیاز معتبر نیست.");
                }

                var dependency = await db.OfflineSyncOperations.AsNoTracking()
                    .Where(o => o.ClientOperationId == dependencyId && o.UserId == userId)
                    .Select(o => new { o.Status, o.AppliedReferenceId })
                    .FirstOrDefaultAsync(ct);
                return dependency is { Status: OfflineSyncStatus.Applied, AppliedReferenceId: not null }
                    ? (dependency.AppliedReferenceId, null)
                    : (null, "رکورد پیش‌نیاز (مثلاً مشتری تازه) هنوز ثبت قطعی نشده است.");
            }

            return (Number(payload, field), null);
        }
    }
}
