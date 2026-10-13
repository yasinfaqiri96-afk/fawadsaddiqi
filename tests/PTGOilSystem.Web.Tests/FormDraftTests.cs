using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Helpers;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services.FormDrafts;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// پیش‌نویس خودکار فرم‌ها: نسخه‌بندی، جداسازی کاربران، ثبت نهایی یک‌باره و حفظ محتوا در ثبت ناموفق.
/// هیچ‌کدام از این مسیرها سند، موجودی یا حسابداری نمی‌سازند؛ «ثبت موفق» در این تست‌ها فقط با
/// نشستن توکن فرم در ProcessedFormTokens شبیه‌سازی می‌شود (همان نشانه‌ای که اکشن واقعی در تراکنش سند می‌گذارد).
/// </summary>
public sealed class FormDraftTests : IDisposable
{
    private const string FormKey = FormDraftForms.LoadingCreate;
    private readonly SqliteConnection _connection;

    public FormDraftTests()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();
        using var db = NewContext();
        db.Database.EnsureCreated();
        db.Users.Add(new User { Id = 1, Username = "clerk", FullName = "Clerk", PasswordHash = "x" });
        db.Users.Add(new User { Id = 2, Username = "other", FullName = "Other", PasswordHash = "x" });
        db.SaveChanges();
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task Save_Versions_Each_Save_And_Rejects_Stale_Overwrite()
    {
        await using var db = NewContext();
        var service = new FormDraftService(db);
        var key = Guid.NewGuid();

        var first = await service.SaveAsync(1, Request(key, 0, ("Rows.Index", "a"), ("Rows[a].LoadedQuantityMt", "10")));
        Assert.Equal(FormDraftSaveOutcome.Saved, first.Outcome);
        Assert.Equal(1, first.Revision);

        var second = await service.SaveAsync(1, Request(key, 1, ("Rows.Index", "a"), ("Rows[a].LoadedQuantityMt", "12")));
        Assert.Equal(FormDraftSaveOutcome.Saved, second.Outcome);
        Assert.Equal(2, second.Revision);

        // پنجرهٔ قدیمی (Revision 1) با محتوای دیگر نباید نسخهٔ تازه‌تر را بازنویسی کند.
        var stale = await service.SaveAsync(1, Request(key, 1, ("Rows.Index", "a"), ("Rows[a].LoadedQuantityMt", "99")));
        Assert.Equal(FormDraftSaveOutcome.Conflict, stale.Outcome);

        // همان محتوا با Revision کهنه (keepalive + همگام‌سازی نسخهٔ محلی) تعارض نیست و چیزی نمی‌نویسد.
        var replay = await service.SaveAsync(1, Request(key, 1, ("Rows.Index", "a"), ("Rows[a].LoadedQuantityMt", "12")));
        Assert.Equal(FormDraftSaveOutcome.Saved, replay.Outcome);
        Assert.Equal(2, replay.Revision);

        var snapshot = await service.FindOpenAsync(1, key, FormKey);
        Assert.NotNull(snapshot);
        Assert.Equal(2, snapshot!.Revision);
        Assert.Equal("12", snapshot.Value("Rows[a].LoadedQuantityMt"));
    }

    [Fact]
    public async Task Save_Never_Stores_One_Time_Tokens_And_Counts_Rows()
    {
        await using var db = NewContext();
        var service = new FormDraftService(db);
        var key = Guid.NewGuid();

        await service.SaveAsync(1, Request(key, 0,
            ("__RequestVerificationToken", "secret"),
            ("__FormToken", "token"),
            (FormDraftFields.Key, key.ToString()),
            ("Rows.Index", "a"), ("Rows.Index", "b"), ("Rows.Index", "c"),
            ("Rows[b].DriverName", "")));

        var draft = await db.FormDrafts.AsNoTracking().SingleAsync();
        Assert.DoesNotContain("secret", draft.PayloadJson);
        Assert.DoesNotContain("__FormToken", draft.PayloadJson);
        Assert.Equal(3, draft.RowCount);
        Assert.Equal(FormDraftStatus.Open, draft.Status);
    }

    [Fact]
    public async Task Drafts_Are_Isolated_Per_User()
    {
        await using var db = NewContext();
        var service = new FormDraftService(db);
        var key = Guid.NewGuid();
        await service.SaveAsync(1, Request(key, 0, ("Notes", "mine")));

        // کاربر دیگر با همان کلید نه می‌خواند، نه می‌نویسد، نه حذف می‌کند.
        var hijack = await service.SaveAsync(2, Request(key, 1, ("Notes", "theirs")));
        Assert.Equal(FormDraftSaveOutcome.Conflict, hijack.Outcome);
        Assert.Null(await service.FindOpenAsync(2, key, FormKey));
        Assert.Empty(await service.ListAsync(2, FormKey, includeSubmitted: true));
        Assert.False(await service.DeleteAsync(2, key));

        var mine = await service.FindOpenAsync(1, key, FormKey);
        Assert.Equal("mine", mine!.Value("Notes"));
        Assert.Single(await service.ListAsync(1, FormKey, includeSubmitted: false));
    }

    [Fact]
    public async Task Independent_Drafts_Do_Not_Overwrite_Each_Other()
    {
        await using var db = NewContext();
        var service = new FormDraftService(db);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        await service.SaveAsync(1, Request(first, 0, ("Notes", "first")));
        await service.SaveAsync(1, Request(second, 0, ("Notes", "second")));

        Assert.Equal("first", (await service.FindOpenAsync(1, first, FormKey))!.Value("Notes"));
        Assert.Equal("second", (await service.FindOpenAsync(1, second, FormKey))!.Value("Notes"));
        Assert.Equal(2, (await service.ListAsync(1, FormKey, includeSubmitted: false)).Count);

        Assert.True(await service.DeleteAsync(1, first));
        Assert.Null(await service.FindOpenAsync(1, first, FormKey));
        Assert.NotNull(await service.FindOpenAsync(1, second, FormKey));
    }

    [Fact]
    public async Task Unknown_Form_And_Malformed_Fields_Are_Rejected()
    {
        await using var db = NewContext();
        var service = new FormDraftService(db);

        var unknown = Request(Guid.NewGuid(), 0, ("Notes", "x"));
        unknown.FormKey = "Payments.Delete";
        Assert.Equal(FormDraftSaveOutcome.Invalid, (await service.SaveAsync(1, unknown)).Outcome);

        var malformed = Request(Guid.NewGuid(), 0);
        malformed.Fields = [["only-name"]];
        Assert.Equal(FormDraftSaveOutcome.Invalid, (await service.SaveAsync(1, malformed)).Outcome);
        Assert.Equal(0, await db.FormDrafts.CountAsync());
    }

    [Theory]
    [InlineData("/Loading/Create?contractId=5&draft=0b6c8c43-6d0e-4c43-9a5b-3d9b8f1b2a11", "/Loading/Create?contractId=5")]
    [InlineData("/Loading/Create", "/Loading/Create")]
    [InlineData("//evil.example/Loading/Create", "/Loading/Create")]
    [InlineData("/Payments/Create?x=1", "/Loading/Create")]
    public void Resume_Path_Stays_On_The_Same_Form(string requested, string expected)
        => Assert.Equal(expected, FormDraftService.NormalizeResumePath(FormDraftForms.Find(FormKey)!, requested));

    [Fact]
    public async Task Successful_Final_Submit_Marks_Draft_Submitted_And_Blocks_A_Second_Submit()
    {
        var key = Guid.NewGuid();
        await using (var db = NewContext())
        {
            await new FormDraftService(db).SaveAsync(1, Request(key, 0, ("Notes", "ready")));
        }

        var token = Guid.NewGuid().ToString("N");
        var actionRan = await RunSubmitAsync(key, token, commit: true);
        Assert.True(actionRan.Invoked);

        await using (var db = NewContext())
        {
            var draft = await db.FormDrafts.AsNoTracking().SingleAsync();
            Assert.Equal(FormDraftStatus.Submitted, draft.Status);
            Assert.Equal(token, draft.SubmittedFormToken);
            Assert.NotNull(draft.SubmittedAtUtc);

            // ثبت‌شده دیگر قابل ادامه یا ذخیره نیست.
            var service = new FormDraftService(db);
            Assert.Null(await service.FindOpenAsync(1, key, FormKey));
            Assert.Equal(FormDraftSaveOutcome.Submitted, (await service.SaveAsync(1, Request(key, draft.Revision, ("Notes", "again")))).Outcome);
        }

        // همان پیش‌نویس از پنجرهٔ دیگری (با توکن فرم دیگر) دوباره ثبت نمی‌شود: اکشن اصلاً اجرا نمی‌شود.
        var second = await RunSubmitAsync(key, Guid.NewGuid().ToString("N"), commit: true);
        Assert.False(second.Invoked);
        var redirect = Assert.IsType<RedirectToActionResult>(second.Context.Result);
        Assert.Equal("FormDrafts", redirect.ControllerName);
    }

    [Fact]
    public async Task Failed_Final_Submit_Keeps_Draft_Open_With_The_Posted_Values()
    {
        var key = Guid.NewGuid();
        await using (var db = NewContext())
        {
            await new FormDraftService(db).SaveAsync(1, Request(key, 0, ("Rows.Index", "a"), ("Rows[a].LoadedQuantityMt", "1")));
        }

        var run = await RunSubmitAsync(key, Guid.NewGuid().ToString("N"), commit: false,
            ("Rows.Index", "a"), ("Rows.Index", "b"), ("Rows[a].LoadedQuantityMt", "25"), ("Rows[b].LoadedQuantityMt", "30"));
        Assert.True(run.Invoked);

        await using var verify = NewContext();
        var draft = await verify.FormDrafts.AsNoTracking().SingleAsync();
        Assert.Equal(FormDraftStatus.Open, draft.Status);
        Assert.Null(draft.SubmitLockedAtUtc);
        Assert.Equal(2, draft.Revision);
        Assert.Equal(2, draft.RowCount);
        Assert.Contains("\"30\"", draft.PayloadJson);
        Assert.DoesNotContain("__FormToken", draft.PayloadJson);

        // فرمِ دوباره نمایش‌داده‌شده به همان پیش‌نویس و Revision تازه وصل می‌ماند.
        var view = Assert.IsType<ViewResult>(run.Executed!.Result);
        var state = Assert.IsType<FormDraftViewState>(view.ViewData[FormDraftViewState.ViewDataKey]);
        Assert.Equal(key, state.Key);
        Assert.Equal(2, state.Revision);
    }

    [Fact]
    public async Task Submit_Before_First_Autosave_Is_Recorded_So_The_Key_Cannot_Reopen()
    {
        var key = Guid.NewGuid();
        var run = await RunSubmitAsync(key, Guid.NewGuid().ToString("N"), commit: true, ("Notes", "fast"));
        Assert.True(run.Invoked);

        await using var db = NewContext();
        var draft = await db.FormDrafts.AsNoTracking().SingleAsync();
        Assert.Equal(FormDraftStatus.Submitted, draft.Status);
        var service = new FormDraftService(db);
        Assert.Equal(FormDraftSaveOutcome.Submitted, (await service.SaveAsync(1, Request(key, 0, ("Notes", "late")))).Outcome);
    }

    [Fact]
    public async Task Draft_Being_Submitted_Elsewhere_Is_Not_Submitted_Twice()
    {
        var key = Guid.NewGuid();
        await using (var db = NewContext())
        {
            await new FormDraftService(db).SaveAsync(1, Request(key, 0, ("Notes", "x")));
            await db.FormDrafts.ExecuteUpdateAsync(s => s
                .SetProperty(d => d.Status, FormDraftStatus.Submitting)
                .SetProperty(d => d.SubmitLockedAtUtc, DateTime.UtcNow));
        }

        var run = await RunSubmitAsync(key, Guid.NewGuid().ToString("N"), commit: true);
        Assert.False(run.Invoked);
        Assert.IsType<RedirectToActionResult>(run.Context.Result);

        await using var verify = NewContext();
        Assert.Equal(FormDraftStatus.Submitting, (await verify.FormDrafts.AsNoTracking().SingleAsync()).Status);
        Assert.Equal(0, await verify.ProcessedFormTokens.CountAsync());
    }

    [Fact]
    public async Task Post_Without_Draft_Fields_Is_Untouched()
    {
        await using var db = NewContext();
        var http = NewHttpContext(1, new Dictionary<string, StringValues> { ["Notes"] = "plain" });
        var (context, filter) = NewFilterContext(db, http);
        var invoked = false;
        await filter.OnActionExecutionAsync(context, () =>
        {
            invoked = true;
            return Task.FromResult(new ActionExecutedContext(context, [], null!) { Result = new OkResult() });
        });

        Assert.True(invoked);
        Assert.Equal(0, await db.FormDrafts.CountAsync());
    }

    private async Task<SubmitRun> RunSubmitAsync(Guid key, string formToken, bool commit, params (string Name, string Value)[] fields)
    {
        await using var db = NewContext();
        var form = new Dictionary<string, StringValues>
        {
            [FormDraftFields.Key] = key.ToString(),
            [FormDraftFields.Form] = FormKey,
            [FormTokenHtmlHelper.FieldName] = formToken,
            ["__RequestVerificationToken"] = "antiforgery"
        };
        foreach (var group in fields.GroupBy(f => f.Name))
        {
            form[group.Key] = new StringValues(group.Select(f => f.Value).ToArray());
        }

        var http = NewHttpContext(1, form);
        var (context, filter) = NewFilterContext(db, http);
        var run = new SubmitRun(context);
        await filter.OnActionExecutionAsync(context, async () =>
        {
            run.Invoked = true;
            if (commit)
            {
                // اکشن واقعی توکن را در همان تراکنش سند ثبت می‌کند.
                await using var actionDb = NewContext();
                actionDb.ProcessedFormTokens.Add(new ProcessedFormToken { Token = formToken, Purpose = "Loading.Create", ConsumedAtUtc = DateTime.UtcNow });
                await actionDb.SaveChangesAsync();
                run.Executed = new ActionExecutedContext(context, [], null!) { Result = new RedirectToActionResult("Index", "Loading", null) };
            }
            else
            {
                run.Executed = new ActionExecutedContext(context, [], null!)
                {
                    Result = new ViewResult { ViewData = new ViewDataDictionary(new EmptyModelMetadataProvider(), new ModelStateDictionary()) }
                };
            }

            return run.Executed;
        });
        return run;
    }

    private static DefaultHttpContext NewHttpContext(int userId, Dictionary<string, StringValues> form)
    {
        var http = new DefaultHttpContext();
        http.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "Test"));
        http.Request.Method = HttpMethods.Post;
        http.Request.ContentType = "application/x-www-form-urlencoded";
        http.Request.Form = new FormCollection(form);
        return http;
    }

    private static (ActionExecutingContext Context, FormDraftSubmitFilter Filter) NewFilterContext(ApplicationDbContext db, HttpContext http)
    {
        var actionContext = new ActionContext(http, new RouteData(), new ActionDescriptor());
        var context = new ActionExecutingContext(actionContext, [], new Dictionary<string, object?>(), null!);
        var filter = new FormDraftSubmitFilter(db, new TempDataFactory(), NullLogger<FormDraftSubmitFilter>.Instance);
        return (context, filter);
    }

    private static FormDraftSaveRequest Request(Guid key, int baseRevision, params (string Name, string Value)[] fields) => new()
    {
        Key = key,
        FormKey = FormKey,
        ResumePath = "/Loading/Create",
        BaseRevision = baseRevision,
        Fields = fields.Select(f => new[] { f.Name, f.Value }).ToList()
    };

    private ApplicationDbContext NewContext()
        => new(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);

    private sealed class SubmitRun(ActionExecutingContext context)
    {
        public ActionExecutingContext Context { get; } = context;
        public bool Invoked { get; set; }
        public ActionExecutedContext? Executed { get; set; }
    }

    private sealed class TempDataFactory : ITempDataDictionaryFactory
    {
        public ITempDataDictionary GetTempData(HttpContext context)
            => new TempDataDictionary(context, new NullProvider());
    }

    private sealed class NullProvider : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }
}
