using System.Text.RegularExpressions;
using PTGOilSystem.Web.Helpers;
using Xunit;

namespace PTGOilSystem.Web.Tests.Simulation;

/// <summary>
/// PTG-P0-01 — نگهبان ساختاری: هر فرمِ ثبتِ مالی/عملیاتی که سمت سرور توکن را مصرف می‌کند،
/// باید همان توکن را هم رندر کند. اگر کسی فرم را بازنویسی کند و <c>@Html.FormToken()</c> را
/// بردارد، همین تست می‌شکند — نه یک ماه بعد در دیتابیس مشتری.
/// </summary>
public sealed class FormIdempotencyCoverageTests
{
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ptg-oil-system.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }

    private static string ViewsRoot() => Path.Combine(RepositoryRoot(), "src", "PTGOilSystem.Web", "Views");

    private static string ControllersRoot() => Path.Combine(RepositoryRoot(), "src", "PTGOilSystem.Web", "Controllers");

    public static TheoryData<string> ProtectedCreateViews() =>
        new()
        {
            // مسیرهایی که پیش از PTG-P0-01 هم محافظت داشتند (نباید از دست بروند).
            Path.Combine("Contracts", "Create.cshtml"),
            Path.Combine("Payments", "Create.cshtml"),
            Path.Combine("Sales", "Create.cshtml"),
            Path.Combine("Inventory", "Create.cshtml"),
            Path.Combine("InventoryTransportLegs", "CreateFromInventory.cshtml"),
            // مسیرهایی که با PTG-P0-01 اضافه شدند.
            Path.Combine("Expenses", "Create.cshtml"),
            Path.Combine("Expenses", "CreateWagonRent.cshtml"),
            Path.Combine("Expenses", "CreateGroup.cshtml"),
            Path.Combine("Loading", "Create.cshtml"),
            Path.Combine("LoadingReceipts", "_ReceiptCreateForm.cshtml"),
            Path.Combine("Dispatch", "Create.cshtml"),
            Path.Combine("Dispatch", "CreateDirectFromReceipt.cshtml"),
            Path.Combine("LossEvents", "Create.cshtml"),
            Path.Combine("SupplierBalanceTransfers", "Create.cshtml"),
            Path.Combine("TruckSettlements", "GroupUnload.cshtml"),
            // مسیرهایی که در ممیزی PTG-P3-B پیدا و محافظت شدند.
            Path.Combine("ThreeWaySettlement", "Index.cshtml"),
            Path.Combine("Transports", "FromReceipt.cshtml"),
            Path.Combine("Transports", "Continue.cshtml"),
            Path.Combine("InventoryTransportLegs", "Details.cshtml"),
            Path.Combine("PartySettlements", "Create.cshtml"),
        };

    [Theory]
    [MemberData(nameof(ProtectedCreateViews))]
    public void Protected_Create_Views_Render_The_Idempotency_Token(string relativePath)
    {
        var path = Path.Combine(ViewsRoot(), relativePath);
        Assert.True(File.Exists(path), $"View not found: {path}");

        var markup = File.ReadAllText(path);
        Assert.Contains("Html.FormToken()", markup, StringComparison.Ordinal);
    }

    [Fact]
    public void PartnerSettlement_Modal_Opts_Into_The_Idempotency_Token()
    {
        var index = File.ReadAllText(Path.Combine(ViewsRoot(), "PartnershipStatement", "Index.cshtml"));
        Assert.Contains("ViewData[\"CreateFormIdempotent\"] = true;", index, StringComparison.Ordinal);

        var shell = File.ReadAllText(Path.Combine(ViewsRoot(), "Shared", "_CreateModalShell.cshtml"));
        Assert.Contains("CreateFormIdempotent", shell, StringComparison.Ordinal);
        Assert.Contains("Html.FormToken()", shell, StringComparison.Ordinal);
    }

    public static TheoryData<string, string> StampedPostingPaths() =>
        new()
        {
            { "ExpensesController.cs", "Expense.Create" },
            { "ExpensesController.cs", "Expense.CreateWagonRent" },
            { "ExpensesController.cs", "Expense.CreateGroup" },
            { "LoadingController.cs", "Loading.Create" },
            { "LoadingReceiptsController.cs", "LoadingReceipt.Create" },
            { "DispatchController.cs", "Dispatch.Create" },
            { "DispatchController.cs", "Dispatch.CreateDirectFromReceipt" },
            { "LossEventsController.cs", "LossEvent.Create" },
            { "SupplierBalanceTransfersController.cs", "SupplierBalanceTransfer.Create" },
            { "PartnershipStatementController.cs", "PartnerSettlement.Create" },
            { "TruckSettlementsController.GroupUnload.cs", "TruckSettlement.GroupUnload" },
            { "PaymentsController.cs", "Payment.Create" },
            { "PaymentsController.cs", "Payment.CreateViaSarraf" },
            { "PaymentsController.cs", "Payment.CreateViaSarrafGeneral" },
            { "SalesController.cs", "Sale.Create" },
            { "ContractsController.cs", "Contract.Create" },
            // PTG-P3-B — مسیرهایی که اثر مالی/موجودی دارند و توکن نداشتند.
            { "ThreeWaySettlementController.cs", "ThreeWaySettlement.Confirm" },
            { "TransportsController.cs", "Transport.FromReceipt" },
            { "TransportsController.cs", "Transport.Continue" },
            { "TransportsController.cs", "Transport.SettleFreight" },
            { "PartySettlementsController.cs", "PartySettlement.Create" },
        };

    [Theory]
    [MemberData(nameof(StampedPostingPaths))]
    public void Posting_Paths_Stamp_Their_Idempotency_Token(string controllerFile, string purpose)
    {
        var path = Path.Combine(ControllersRoot(), controllerFile);
        Assert.True(File.Exists(path), $"Controller not found: {path}");

        // برخی کنترلرها گارد را اختیاری (nullable) تزریق می‌کنند و `?.` می‌نویسند؛
        // هر دو شکل یک معنی دارند و هر دو باید شمرده شوند.
        var source = File.ReadAllText(path);
        Assert.Matches(new Regex($@"_formTokens\??\.Stamp\(\s*formToken\s*,\s*""{Regex.Escape(purpose)}""",
            RegexOptions.Singleline), source);
    }

    [Fact]
    public void Every_Stamping_Controller_Also_Handles_The_Duplicate_Exception()
    {
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(ControllersRoot(), "*.cs", SearchOption.AllDirectories))
        {
            var source = File.ReadAllText(file);
            var stamps = source.Contains("_formTokens.Stamp(", StringComparison.Ordinal)
                || source.Contains("_formTokens?.Stamp(", StringComparison.Ordinal);
            if (!stamps)
            {
                continue;
            }

            var handles = source.Contains("_formTokens.IsDuplicate(", StringComparison.Ordinal)
                || source.Contains("_formTokens?.IsDuplicate(", StringComparison.Ordinal);
            if (!handles)
            {
                offenders.Add(Path.GetFileName(file));
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void Form_Token_Field_Name_Is_Stable()
        => Assert.Equal("__FormToken", FormTokenHtmlHelper.FieldName);
}
