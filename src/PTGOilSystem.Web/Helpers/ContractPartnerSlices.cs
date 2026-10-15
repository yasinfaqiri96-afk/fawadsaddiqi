namespace PTGOilSystem.Web.Helpers;

/// <summary>
/// PTG-P0-03 — سهم شرکا بازه‌دار است: هر ویرایشِ ترکیب، بازهٔ جاری را می‌بندد و بازهٔ تازه
/// می‌سازد. «ترکیب فعلی» یعنی همهٔ سطرهای بازه‌ای که دیرتر از همه آغاز شده — همان قاعدهٔ
/// <c>ContractsController.ApplyPartnerShareChange</c>.
///
/// «آخرین سطرِ هر شریک» ترکیب فعلی نیست: شریکی که در بازهٔ تازه حذف شده، سطرِ بازهٔ بسته‌اش
/// را دارد و با آن روش دوباره در فرم و کارت‌ها دیده می‌شد و با ذخیرهٔ بعدی برمی‌گشت.
/// </summary>
public static class ContractPartnerSlices
{
    public static List<T> CurrentSlice<T>(this IEnumerable<T> rows, Func<T, DateTime> effectiveFrom)
    {
        var list = rows as IReadOnlyCollection<T> ?? rows.ToList();
        if (list.Count == 0)
        {
            return [];
        }

        var latestStart = list.Max(effectiveFrom);
        return list.Where(row => effectiveFrom(row) == latestStart).ToList();
    }
}
