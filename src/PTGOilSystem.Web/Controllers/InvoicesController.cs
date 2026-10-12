using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Models.Documents;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Models.Sales;

namespace PTGOilSystem.Web.Controllers;

[Authorize]
public class InvoicesController : Controller
{
    private readonly ApplicationDbContext _db;

    public InvoicesController(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<IActionResult> Contract(int id)
    {
        var contract = await _db.Contracts
            .Include(c => c.Company)
            .Include(c => c.Product)
            .Include(c => c.Unit)
            .Include(c => c.Supplier)
            .Include(c => c.Customer)
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == id);

        if (contract is null)
        {
            return NotFound();
        }

        return View("Document", BuildContractInvoice(contract));
    }

    public async Task<IActionResult> Sale(int id)
    {
        var sale = await _db.SalesTransactions
            .Include(s => s.Company)
            .Include(s => s.Customer)
            .Include(s => s.Product)
            .Include(s => s.Contract)
            .Include(s => s.DestinationLocation)
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == id);

        if (sale is null)
        {
            return NotFound();
        }

        // فاکتور بدون قیمت چاپ نمی‌شود؛ پس از «تعیین قیمت فروش» در دسترس است.
        if (sale.IsSalePricePending)
        {
            TempData["err"] = "قیمت فروش هنوز تعیین نشده است؛ فاکتور پس از تعیین قیمت قابل چاپ است.";
            return RedirectToAction("Details", "Sales", new { id });
        }

        return View("Document", BuildSaleInvoice(sale));
    }

    public async Task<IActionResult> Payment(int id)
    {
        var payment = await _db.PaymentTransactions
            .Include(p => p.CashAccount)
            .Include(p => p.Customer)
            .Include(p => p.Supplier)
            .Include(p => p.Driver)
            .Include(p => p.Employee)
            .Include(p => p.Contract)
                .ThenInclude(c => c!.Company)
            .Include(p => p.Contract)
                .ThenInclude(c => c!.Product)
            .Include(p => p.SalesTransaction)
                .ThenInclude(s => s!.Company)
            .Include(p => p.SalesTransaction)
                .ThenInclude(s => s!.Product)
            .Include(p => p.SalesTransaction)
                .ThenInclude(s => s!.Customer)
            .Include(p => p.ExpenseTransaction)
                .ThenInclude(e => e!.ExpenseType)
            .Include(p => p.LedgerEntry)
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == id);

        if (payment is null)
        {
            return NotFound();
        }

        return View("Document", BuildPaymentInvoice(payment));
    }

    private static DocumentInvoiceViewModel BuildContractInvoice(Contract contract)
    {
        var isPurchase = contract.ContractType == ContractType.Purchase;
        var finalUnitUsd = ContractPricingAdapter.GetCanonicalFinalPrice(contract);
        var displayCurrency = ResolveContractDisplayCurrency(contract);
        var displayUnitPrice = ResolveContractDisplayUnitPrice(contract, finalUnitUsd);
        var displayTotal = displayUnitPrice.HasValue ? displayUnitPrice.Value * contract.QuantityMt : (decimal?)null;
        var estimatedTotalUsd = finalUnitUsd.HasValue ? finalUnitUsd.Value * contract.QuantityMt : (decimal?)null;
        var productName = DisplayName(contract.Product?.NamePersian, contract.Product?.Name, "محصول");
        var unitText = DisplayUnit(contract.Unit);
        var counterparty = isPurchase
            ? PartyFromSupplier(contract.Supplier, "از", "From")
            : PartyFromCustomer(contract.Customer, "به", "To");
        var companyParty = PartyFromCompany(contract.Company, isPurchase ? "به" : "از", isPurchase ? "To" : "From");

        var totals = new List<DocumentInvoiceTotalRowViewModel>
        {
            TotalRow("جمع مقدار", "Total Quantity", $"{contract.QuantityMt:N4} {unitText}"),
            TotalRow("ارزش قرارداد", "Contract Value", FormatOptionalMoney(displayTotal, displayCurrency), isGrandTotal: true)
        };

        if (estimatedTotalUsd.HasValue && !string.Equals(displayCurrency, "USD", StringComparison.OrdinalIgnoreCase))
        {
            totals.Add(TotalRow("ارزش تخمینی USD", "Estimated Value USD", FormatMoney(estimatedTotalUsd.Value, "USD")));
        }

        return new DocumentInvoiceViewModel
        {
            TitleFa = isPurchase ? "سند قرارداد خرید" : "سند قرارداد فروش",
            TitleEn = isPurchase ? "Purchase Contract Invoice" : "Sale Contract Invoice",
            BreadcrumbFa = "قراردادها",
            BreadcrumbEn = "Contracts",
            DocumentNumber = contract.ContractNumber,
            DocumentDate = contract.ContractDate,
            StatusFa = ContractStatusFa(contract.Status),
            StatusEn = contract.Status.ToString(),
            Tone = isPurchase ? "purchase" : "sale",
            BrandName = contract.Company is null ? "PTG Oil System" : DisplayName(contract.Company.NamePersian, contract.Company.Name, "PTG Oil System"),
            BrandSubtitleFa = isPurchase ? "قرارداد خرید" : "قرارداد فروش",
            BrandSubtitleEn = isPurchase ? "Purchase Contract" : "Sale Contract",
            FromParty = isPurchase ? counterparty : companyParty,
            ToParty = isPurchase ? companyParty : counterparty,
            PaymentBox = new DocumentInvoicePaymentBoxViewModel
            {
                HeadingFa = "خلاصه سند",
                HeadingEn = "Document Summary",
                AmountText = FormatOptionalMoney(displayTotal, displayCurrency),
                ReferenceText = contract.ContractNumber,
                NoteFa = "ارزش قرارداد برای نمایش سند است؛ مانده رسمی از دفتر کل محاسبه می‌شود.",
                NoteEn = "Contract value is informational; official balance is ledger-based."
            },
            Lines =
            [
                new DocumentInvoiceLineViewModel
                {
                    Number = "1",
                    Item = productName,
                    Description = $"{ContractTypeFa(contract.ContractType)} / {ContractPricingAdapter.GetPricingDisplayLabel(contract)}",
                    UnitCost = displayUnitPrice.HasValue ? $"{displayUnitPrice.Value:N2} {displayCurrency}" : "در انتظار نرخ",
                    Quantity = $"{contract.QuantityMt:N4} {unitText}",
                    Total = FormatOptionalMoney(displayTotal, displayCurrency)
                }
            ],
            Totals = totals,
            NotesFa = contract.Notes,
            NotesEn = contract.Notes,
            SourceReference = contract.ContractNumber,
            BackController = "ContractJourney",
            BackAction = "Details",
            BackRouteValues = new Dictionary<string, string>
            {
                ["contractId"] = contract.Id.ToString(),
                ["lockContract"] = "true"
            }
        };
    }

    private static DocumentInvoiceViewModel BuildSaleInvoice(SalesTransaction sale)
    {
        var productName = DisplayName(sale.Product?.NamePersian, sale.Product?.Name, "محصول");
        var totals = new List<DocumentInvoiceTotalRowViewModel>
        {
            TotalRow("جمع فروش", "Subtotal", FormatMoney(sale.TotalInCurrency, sale.Currency)),
            TotalRow("نرخ تبدیل", "FX Rate", sale.AppliedFxRateToUsd.HasValue ? sale.AppliedFxRateToUsd.Value.ToString("0.######") : "-"),
            TotalRow("جمع نهایی USD", "Total USD", FormatMoney(sale.TotalUsd, "USD"), isGrandTotal: true)
        };

        return new DocumentInvoiceViewModel
        {
            TitleFa = "فاکتور فروش",
            TitleEn = "Sales Invoice",
            BreadcrumbFa = "فروش",
            BreadcrumbEn = "Sales",
            DocumentNumber = sale.InvoiceNumber,
            DocumentDate = sale.SaleDate,
            StatusFa = sale.IsCancelled ? "لغوشده" : SaleStageLabels.ToPersian(sale.SaleStage),
            StatusEn = sale.IsCancelled ? "Cancelled" : sale.SaleStage.ToString(),
            Tone = "sale",
            BrandName = sale.Company is null ? "PTG Oil System" : DisplayName(sale.Company.NamePersian, sale.Company.Name, "PTG Oil System"),
            BrandSubtitleFa = "فاکتور فروش",
            BrandSubtitleEn = "Sales Invoice",
            FromParty = PartyFromCompany(sale.Company, "از", "From"),
            ToParty = PartyFromCustomer(sale.Customer, "به", "To"),
            PaymentBox = new DocumentInvoicePaymentBoxViewModel
            {
                HeadingFa = "مبلغ قابل دریافت",
                HeadingEn = "Amount Receivable",
                AmountText = FormatMoney(sale.TotalInCurrency, sale.Currency),
                ReferenceText = sale.InvoiceNumber,
                NoteFa = sale.AppliedFxRateToUsd.HasValue ? $"نرخ تبدیل به USD: {sale.AppliedFxRateToUsd.Value:0.######}" : null,
                NoteEn = sale.AppliedFxRateToUsd.HasValue ? $"FX to USD: {sale.AppliedFxRateToUsd.Value:0.######}" : null
            },
            Lines =
            [
                new DocumentInvoiceLineViewModel
                {
                    Number = "1",
                    Item = productName,
                    Description = $"فروش / {sale.Contract?.ContractNumber ?? SalesContractText.WithoutSalesContract}",
                    UnitCost = FormatMoney(sale.UnitPriceInCurrency, sale.Currency),
                    Quantity = $"{sale.QuantityMt:N4} MT",
                    Total = FormatMoney(sale.TotalInCurrency, sale.Currency)
                }
            ],
            Totals = totals,
            NotesFa = sale.Notes,
            NotesEn = sale.Notes,
            SourceReference = sale.InvoiceNumber,
            BackController = "Sales",
            BackAction = "Details",
            BackRouteValues = new Dictionary<string, string> { ["id"] = sale.Id.ToString() }
        };
    }

    private static DocumentInvoiceViewModel BuildPaymentInvoice(PaymentTransaction payment)
    {
        var isReceipt = payment.Direction == PaymentDirection.In;
        var company = payment.Contract?.Company ?? payment.SalesTransaction?.Company;
        var counterparty = PartyFromPaymentCounterparty(payment, isReceipt ? "از" : "به", isReceipt ? "From" : "To");
        var companyParty = PartyFromCompany(company, isReceipt ? "به" : "از", isReceipt ? "To" : "From");
        var sourceLabel = BuildPaymentSourceLabel(payment);
        var documentNumber = string.IsNullOrWhiteSpace(payment.Reference) ? $"PAY-{payment.Id}" : payment.Reference!;

        return new DocumentInvoiceViewModel
        {
            TitleFa = isReceipt ? "رسید دریافت" : "سند پرداخت",
            TitleEn = isReceipt ? "Receipt Voucher" : "Payment Voucher",
            BreadcrumbFa = "پرداخت‌ها و دریافت‌ها",
            BreadcrumbEn = "Payments",
            DocumentNumber = documentNumber,
            DocumentDate = payment.PaymentDate,
            StatusFa = "ثبت‌شده",
            StatusEn = "Recorded",
            Tone = isReceipt ? "receipt" : "payment",
            BrandName = company is null ? "PTG Oil System" : DisplayName(company.NamePersian, company.Name, "PTG Oil System"),
            BrandSubtitleFa = isReceipt ? "رسید دریافت" : "سند پرداخت",
            BrandSubtitleEn = isReceipt ? "Receipt Voucher" : "Payment Voucher",
            FromParty = isReceipt ? counterparty : companyParty,
            ToParty = isReceipt ? companyParty : counterparty,
            PaymentBox = new DocumentInvoicePaymentBoxViewModel
            {
                HeadingFa = isReceipt ? "مبلغ دریافت" : "مبلغ پرداخت",
                HeadingEn = isReceipt ? "Received Amount" : "Paid Amount",
                AmountText = FormatMoney(payment.Amount, payment.Currency),
                ReferenceText = documentNumber,
                // نرخ ۱ (سند USD) چیزی به سند اضافه نمی‌کند و همان نرخ در جدول
                // جمع‌ها هم هست؛ فقط نرخ واقعی ارز غیر USD در سربرگ نوشته می‌شود.
                NoteFa = ShowFxNote(payment) ? $"نرخ تبدیل به USD: {payment.AppliedFxRateToUsd!.Value:0.######}" : null,
                NoteEn = ShowFxNote(payment) ? $"FX to USD: {payment.AppliedFxRateToUsd!.Value:0.######}" : null
            },
            Lines =
            [
                new DocumentInvoiceLineViewModel
                {
                    Number = "1",
                    Item = PaymentKindFa(payment.PaymentKind),
                    Description = sourceLabel,
                    UnitCost = FormatMoney(payment.Amount, payment.Currency),
                    Quantity = "1",
                    Total = FormatMoney(payment.Amount, payment.Currency)
                }
            ],
            Totals =
            [
                TotalRow("مبلغ اصلی", "Original Amount", FormatMoney(payment.Amount, payment.Currency)),
                TotalRow("نرخ تبدیل", "FX Rate", payment.AppliedFxRateToUsd.HasValue ? payment.AppliedFxRateToUsd.Value.ToString("0.######") : "-"),
                TotalRow("مبلغ USD", "Amount USD", FormatMoney(payment.AmountUsd, "USD"), isGrandTotal: true)
            ],
            NotesFa = payment.Description,
            NotesEn = payment.Description,
            SourceReference = payment.LedgerEntryId.HasValue ? $"Ledger #{payment.LedgerEntryId}" : documentNumber,
            BackController = "Payments",
            BackAction = "Details",
            BackRouteValues = new Dictionary<string, string> { ["id"] = payment.Id.ToString() }
        };
    }

    private static bool ShowFxNote(PaymentTransaction payment)
        => payment.AppliedFxRateToUsd.HasValue && payment.AppliedFxRateToUsd.Value != 1m;

    private static string ResolveContractDisplayCurrency(Contract contract)
        => contract.PricingMethod == PricingMethod.Fixed
            ? NormalizeCurrency(contract.Currency)
            : "USD";

    private static decimal? ResolveContractDisplayUnitPrice(Contract contract, decimal? finalUnitUsd)
    {
        if (contract.PricingMethod == PricingMethod.Fixed && contract.UnitPriceInCurrency.HasValue)
        {
            return contract.UnitPriceInCurrency.Value;
        }

        return finalUnitUsd;
    }

    private static DocumentInvoicePartyViewModel PartyFromCompany(Company? company, string headingFa, string headingEn)
        => new()
        {
            HeadingFa = headingFa,
            HeadingEn = headingEn,
            Name = company is null ? "PTG Oil System" : DisplayName(company.NamePersian, company.Name, "PTG Oil System"),
            Details = CleanDetails(
                company?.Code,
                company?.Address,
                company?.Country,
                company?.Notes)
        };

    private static DocumentInvoicePartyViewModel PartyFromSupplier(Supplier? supplier, string headingFa, string headingEn)
        => new()
        {
            HeadingFa = headingFa,
            HeadingEn = headingEn,
            Name = supplier is null ? "-" : DisplayName(supplier.NamePersian, supplier.Name, "-"),
            Details = CleanDetails(
                supplier?.Code,
                supplier?.ContactPerson is null ? null : $"تماس: {supplier.ContactPerson}",
                supplier?.Address,
                supplier?.Country,
                supplier?.Phone is null ? null : $"تلفن: {supplier.Phone}")
        };

    private static DocumentInvoicePartyViewModel PartyFromCustomer(Customer? customer, string headingFa, string headingEn)
        => new()
        {
            HeadingFa = headingFa,
            HeadingEn = headingEn,
            Name = customer is null ? "-" : DisplayName(customer.NamePersian, customer.Name, "-"),
            Details = CleanDetails(
                customer?.Code,
                customer?.ContactPerson is null ? null : $"تماس: {customer.ContactPerson}",
                customer?.Address,
                customer?.Country,
                customer?.Phone is null ? null : $"تلفن: {customer.Phone}")
        };

    private static DocumentInvoicePartyViewModel PartyFromPaymentCounterparty(PaymentTransaction payment, string headingFa, string headingEn)
    {
        if (payment.Customer is not null)
        {
            return PartyFromCustomer(payment.Customer, headingFa, headingEn);
        }

        if (payment.Supplier is not null)
        {
            return PartyFromSupplier(payment.Supplier, headingFa, headingEn);
        }

        if (payment.Employee is not null)
        {
            return new DocumentInvoicePartyViewModel
            {
                HeadingFa = headingFa,
                HeadingEn = headingEn,
                Name = payment.Employee.FullName,
                Details = CleanDetails("کارمند")
            };
        }

        if (payment.Driver is not null)
        {
            return new DocumentInvoicePartyViewModel
            {
                HeadingFa = headingFa,
                HeadingEn = headingEn,
                Name = payment.Driver.FullName,
                Details = CleanDetails("راننده", payment.Driver.Phone)
            };
        }

        return new DocumentInvoicePartyViewModel
        {
            HeadingFa = headingFa,
            HeadingEn = headingEn,
            Name = "طرف حساب",
            Details = CleanDetails(PaymentKindFa(payment.PaymentKind))
        };
    }

    private static string BuildPaymentSourceLabel(PaymentTransaction payment)
    {
        var parts = new List<string>
        {
            PaymentDirectionFa(payment.Direction),
            payment.CashAccount is null ? "حساب نقد / بانک" : payment.CashAccount.Name
        };

        if (payment.Contract is not null)
        {
            parts.Add($"قرارداد {payment.Contract.ContractNumber}");
        }

        if (payment.SalesTransaction is not null)
        {
            parts.Add($"فاکتور {payment.SalesTransaction.InvoiceNumber}");
        }

        if (payment.ExpenseTransaction is not null)
        {
            parts.Add(payment.ExpenseTransaction.ExpenseType?.NamePersian ?? payment.ExpenseTransaction.ExpenseType?.Name ?? "مصرف");
        }

        return string.Join(" / ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
    }

    private static IReadOnlyList<string> CleanDetails(params string?[] values)
        => values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .ToList();

    private static DocumentInvoiceTotalRowViewModel TotalRow(string fa, string en, string value, bool isGrandTotal = false)
        => new()
        {
            LabelFa = fa,
            LabelEn = en,
            Value = value,
            IsGrandTotal = isGrandTotal
        };

    private static string DisplayName(string? persian, string? english, string fallback)
        => !string.IsNullOrWhiteSpace(persian)
            ? persian.Trim()
            : !string.IsNullOrWhiteSpace(english)
                ? english.Trim()
                : fallback;

    private static string DisplayUnit(Unit? unit)
        => !string.IsNullOrWhiteSpace(unit?.Symbol)
            ? unit.Symbol!
            : !string.IsNullOrWhiteSpace(unit?.Code)
                ? unit.Code!
                : "MT";

    private static string FormatMoney(decimal amount, string currency)
        => $"{amount:N2} {NormalizeCurrency(currency)}";

    private static string FormatOptionalMoney(decimal? amount, string currency)
        => amount.HasValue ? FormatMoney(amount.Value, currency) : "-";

    private static string NormalizeCurrency(string? currency)
        => string.IsNullOrWhiteSpace(currency) ? "USD" : currency.Trim().ToUpperInvariant();

    private static string ContractTypeFa(ContractType type)
        => type == ContractType.Purchase ? "خرید" : "فروش";

    private static string ContractStatusFa(ContractStatus status)
        => status switch
        {
            ContractStatus.Draft => "پیش‌نویس",
            ContractStatus.Active => "فعال",
            ContractStatus.Closed => "بسته‌شده",
            ContractStatus.Cancelled => "لغوشده",
            _ => status.ToString()
        };

    private static string PaymentDirectionFa(PaymentDirection direction)
        => direction == PaymentDirection.In ? "دریافت" : "پرداخت";

    private static string PaymentKindFa(PaymentKind paymentKind)
        => paymentKind switch
        {
            PaymentKind.CustomerReceipt => "دریافت از مشتری",
            PaymentKind.SupplierPayment => "پرداخت به تأمین‌کننده",
            PaymentKind.ExpensePayment => "پرداخت مصرف",
            PaymentKind.TruckPayment => "پرداخت موتر",
            PaymentKind.ManualPayment => "پرداخت دستی",
            PaymentKind.ManualReceipt => "دریافت دستی",
            PaymentKind.EmployeeSalaryPayment => "پرداخت معاش کارمند",
            PaymentKind.EmployeeSalaryAdvance => "پیش‌پرداخت کارمند",
            PaymentKind.SupplierReceipt => "دریافت از تأمین‌کننده",
            PaymentKind.CustomerPayment => "پرداخت به مشتری",
            PaymentKind.EmployeeReturn => "برگشت از کارمند",
            _ => paymentKind.ToString()
        };
}
