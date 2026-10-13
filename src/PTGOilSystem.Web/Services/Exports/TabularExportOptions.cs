namespace PTGOilSystem.Web.Services.Exports;

public sealed class TabularExportOptions
{
    public const string SectionName = "Exports";

    public int ExcelMaxRows { get; set; } = 50_000;
    public int PdfMaxRows { get; set; } = 10_000;
    // برند خروجی‌ها از یک مرجع: ExcelDesignSystem. هیچ نام شرکت قبلی یا تماس آن پیش‌فرض نیست.
    public string CompanyNameFa { get; set; } = ExcelDesignSystem.OrganizationName;
    public string CompanyNameEn { get; set; } = ExcelDesignSystem.OrganizationNameEn;
    public string CompanyLogoPath { get; set; } = ExcelDesignSystem.LogoRelativePath;
    public string CompanyPhone { get; set; } = "";
    public string CompanyEmail { get; set; } = "";
    public string CompanyWebsite { get; set; } = "";
    // سطر اول هر فایل Excel (دری و انگلیسی)؛ نصب‌های دیگر با Exports__OrganizationName عوضش می‌کنند.
    public string OrganizationName { get; set; } = ExcelDesignSystem.OrganizationName;
    public string OrganizationNameEn { get; set; } = ExcelDesignSystem.OrganizationNameEn;
    public string QuestPdfLicense { get; set; } = "Community";
}

