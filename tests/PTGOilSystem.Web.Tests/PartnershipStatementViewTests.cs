using System.Runtime.CompilerServices;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// نمایشِ «مانده نهایی» — یک عدد، نه دو.
///
/// مانده هر شریک در پروفایل خودش دست‌نخورده می‌ماند (بدهی بدهکار و طلب طلبکار به اندازهٔ
/// باقیماندهٔ تطبیق‌نشده فرق دارند)، ولی صورت‌حساب مشترک فقط «مبلغ قابل تسویه» را به‌عنوان
/// نتیجه نشان می‌دهد و آن اختلاف را با متن کوچک گزارش می‌کند — نه به‌عنوان مانده دوم.
/// </summary>
public sealed class PartnershipStatementViewTests
{
    [Fact]
    public void CombinedStatement_ShowsOneSettlementAmount_AndReportsTheResidualAsSmallText()
    {
        var view = ReadRepoFile("src/PTGOilSystem.Web/Views/PartnershipStatement/Index.cshtml");

        var summaryStart = view.IndexOf("statement.DebtorPartnerId.HasValue", StringComparison.Ordinal);
        Assert.True(summaryStart >= 0);
        var summary = view[summaryStart..view.IndexOf("حساب هر شریک", summaryStart, StringComparison.Ordinal)];

        Assert.Contains("مبلغ قابل تسویه", summary);
        Assert.Contains("statement.AmountDueUsd", summary);
        Assert.Contains("تفاوت تطبیق با دفتر", summary);
        Assert.Contains("statement.UnreconciledResidualUsd", summary);

        // طلبِ شریکِ طلبکار دیگر به‌عنوان یک «مانده نهایی» موازی چاپ نمی‌شود.
        Assert.DoesNotContain("statement.CreditorClaimUsd", summary);
        Assert.DoesNotContain("<dt>مانده نهایی</dt>", summary);

        // مبلغ قابل تسویه فقط یک بار می‌آید، تا دو عدد کنار هم دیده نشوند.
        Assert.Equal(1, Count(summary, "statement.AmountDueUsd"));
    }

    [Fact]
    public void NoBalanceIsProducedByAveragingTheTwoPartnerPositions()
    {
        var service = ReadRepoFile("src/PTGOilSystem.Web/Services/PartyStatements/PartnershipStatementService.cs");
        var view = ReadRepoFile("src/PTGOilSystem.Web/Views/PartnershipStatement/Index.cshtml");
        var profile = ReadRepoFile("src/PTGOilSystem.Web/Views/Partners/Details.cshtml");

        // سیاست حسابداریِ صریحی برای تقسیم باقیماندهٔ تطبیق‌نشده وجود ندارد، پس هیچ‌جا
        // میانگین/نصفِ دو مانده به‌عنوان مانده ساخته نمی‌شود.
        Assert.DoesNotContain("Average(t => t.NetPositionUsd", service);
        Assert.DoesNotContain("NetPositionUsd) / 2", service);
        Assert.DoesNotContain("UnreconciledResidualUsd / 2", service);
        Assert.DoesNotContain("/ 2", view);
        Assert.DoesNotContain("/ 2", profile);

        // میانگین فقط برای درصد سهم مجاز است، نه برای مبلغ.
        foreach (var line in service.Split('\n').Where(l => l.Contains(".Average(", StringComparison.Ordinal)))
        {
            Assert.Contains("SharePercent", line);
        }
    }

    private static int Count(string value, string token)
        => (value.Length - value.Replace(token, string.Empty, StringComparison.Ordinal).Length) / token.Length;

    private static string ReadRepoFile(string relativePath, [CallerFilePath] string sourceFilePath = "")
    {
        var normalizedPath = relativePath.Replace('/', Path.DirectorySeparatorChar);
        foreach (var start in new[]
                 {
                     Environment.CurrentDirectory,
                     AppContext.BaseDirectory,
                     Path.GetDirectoryName(sourceFilePath) ?? string.Empty
                 })
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null)
            {
                var candidate = Path.Combine(directory.FullName, normalizedPath);
                if (File.Exists(candidate))
                {
                    return File.ReadAllText(candidate).Replace("\r\n", "\n").Replace("\r", "\n");
                }

                directory = directory.Parent;
            }
        }

        throw new FileNotFoundException($"Repo file not found: {relativePath}");
    }
}
