using System.ComponentModel.DataAnnotations;

namespace PTGOilSystem.Web.Models.Sales;

/// <summary>
/// PTG-P2-03 — ورودیِ صفحهٔ «ابطال / اصلاح فروش».
///
/// این مدل عمداً هیچ فیلد مالی‌ای برای ویرایش ندارد. اصلاحِ یک فروشِ ثبت‌شده با
/// بازنویسیِ مبلغ یا مقدارِ همان سند انجام نمی‌شود؛ سند اصلی ابطال و یک سندِ تازه ثبت
/// می‌شود. آنچه کاربر اینجا وارد می‌کند فقط «چرا» و «آیا جایگزین لازم است» است.
/// </summary>
public sealed class SaleCorrectionViewModel
{
    public int SaleId { get; set; }

    /// <summary>PTG-P1-05 — نسخه‌ای که کاربر هنگام بازکردن فرم دید.</summary>
    public long Version { get; set; }

    // ---- فقط برای نمایش؛ هیچ‌کدام از فرم پذیرفته نمی‌شوند ----
    public string InvoiceNumber { get; set; } = "";
    public DateTime SaleDate { get; set; }
    public string CustomerName { get; set; } = "";
    public decimal QuantityMt { get; set; }
    public string Currency { get; set; } = "USD";
    public decimal TotalInCurrency { get; set; }
    public decimal TotalUsd { get; set; }
    public bool IsSalePricePending { get; set; }

    [Display(Name = "دلیل ابطال")]
    [Required(ErrorMessage = "نوشتن دلیل ابطال الزامی است.")]
    [MaxLength(500)]
    public string? CancelReason { get; set; }

    [Display(Name = "ثبت فروش جایگزین پس از ابطال")]
    public bool CreateReplacement { get; set; } = true;

    public string? ReturnUrl { get; set; }
}
