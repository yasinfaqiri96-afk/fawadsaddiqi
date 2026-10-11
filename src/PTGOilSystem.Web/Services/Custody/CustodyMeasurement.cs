using PTGOilSystem.Web.Services.Exceptions;

namespace PTGOilSystem.Web.Services.Custody;

/// <summary>No density defaults or thermal correction: volume and certified density must share a temperature.</summary>
public sealed record CustodyMeasurement(decimal Liters, decimal DensityKgPerM3,
    decimal VolumeTemperatureC, decimal DensityTemperatureC, string Reference)
{
    public decimal ToMt()
    {
        if (Liters <= 0m || Liters > 99999999999999.9999m || decimal.Round(Liters, 4) != Liters)
            throw Rule("CUSTODY_LITERS_INVALID", "مقدار لیتر باید مثبت و حداکثر چهار رقم اعشار باشد.");
        if (DensityKgPerM3 <= 0m || DensityKgPerM3 > 999999999999.999999m || decimal.Round(DensityKgPerM3, 6) != DensityKgPerM3)
            throw Rule("CUSTODY_DENSITY_INVALID", "چگالی معتبر به kg/m³، مثبت و حداکثر شش رقم اعشار لازم است.");
        if (VolumeTemperatureC != DensityTemperatureC)
            throw Rule("CUSTODY_MEASUREMENT_TEMPERATURE", "دمای حجم و چگالی یکسان نیست؛ حجم یا چگالیِ معتبر در همان دما را ثبت کنید. تصحیح حرارتی خودکار انجام نمی‌شود.");
        if (VolumeTemperatureC < -273.15m || VolumeTemperatureC > 99999.999m || decimal.Round(VolumeTemperatureC, 3) != VolumeTemperatureC)
            throw Rule("CUSTODY_MEASUREMENT_TEMPERATURE", "دمای اندازه‌گیری معتبر با حداکثر سه رقم اعشار لازم است.");
        if (string.IsNullOrWhiteSpace(Reference) || Reference.Trim().Length > 500)
            throw Rule("CUSTODY_MEASUREMENT_REFERENCE", "مرجع معتبر اندازه‌گیری و چگالی همین محصول الزامی است.");
        var mass = decimal.Round(Liters * DensityKgPerM3 / 1_000_000m, 4, MidpointRounding.AwayFromZero);
        if (mass <= 0m || mass > 99999999999999.9999m)
            throw Rule("CUSTODY_MEASUREMENT_MASS", "مقدار تبدیل‌شده در دقت موجودی معتبر نیست.");
        return mass;
    }
    private static BusinessRuleException Rule(string code, string message) => new(code, message);
}
