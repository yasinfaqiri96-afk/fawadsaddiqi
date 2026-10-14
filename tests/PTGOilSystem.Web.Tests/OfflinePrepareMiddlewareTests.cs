using System.Text;
using Microsoft.AspNetCore.Http;
using PTGOilSystem.Web.Middleware;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// آماده‌سازی پس‌زمینهٔ کار بدون سرور: صفحهٔ بدون تغییر با ETag پاسخ 304 بدون بدنه می‌گیرد (توکن‌های یک‌بارمصرف فورم
/// در مقایسه نیستند)؛ درخواست‌های عادی کاربر دست نمی‌خورند.
/// </summary>
public sealed class OfflinePrepareMiddlewareTests
{
    private static OfflinePrepareMiddleware Middleware(string html, string contentType = "text/html; charset=utf-8")
        => new(async context =>
        {
            context.Response.StatusCode = 200;
            context.Response.ContentType = contentType;
            await context.Response.WriteAsync(html);
        });

    private static DefaultHttpContext Request(bool prepare, string? ifNoneMatch = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = "/Sales/Create";
        if (prepare) context.Request.Headers[OfflinePrepareMiddleware.HeaderName] = "1";
        if (ifNoneMatch is not null) context.Request.Headers.IfNoneMatch = ifNoneMatch;
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static string Body(HttpContext context)
    {
        context.Response.Body.Position = 0;
        return new StreamReader(context.Response.Body, Encoding.UTF8).ReadToEnd();
    }

    private static string Form(string antiforgery, string formToken, string amount)
        => $"<main><form method=\"post\"><input name=\"__RequestVerificationToken\" type=\"hidden\" value=\"{antiforgery}\" />"
            + $"<input type=\"hidden\" name=\"__FormToken\" value=\"{formToken}\" /><input name=\"Amount\" value=\"{amount}\" /></form>"
            + "<script nonce=\"abc\">x()</script></main>";

    [Fact]
    public void Page_Render_Time_Marker_Is_Not_Content()
    {
        static byte[] Page(string renderedAt, string amount)
            => Encoding.UTF8.GetBytes($"<main><span hidden data-ptg-rendered=\"{renderedAt}\"></span><input name=\"Amount\" value=\"{amount}\" /></main>");

        Assert.Equal(
            OfflinePrepareMiddleware.ComputeETag(Page("2026-10-14T09:00:00.0000000Z", "5")),
            OfflinePrepareMiddleware.ComputeETag(Page("2026-10-14T11:30:00.0000000Z", "5")));
        Assert.NotEqual(
            OfflinePrepareMiddleware.ComputeETag(Page("2026-10-14T09:00:00.0000000Z", "5")),
            OfflinePrepareMiddleware.ComputeETag(Page("2026-10-14T09:00:00.0000000Z", "6")));
    }

    [Fact]
    public async Task Unchanged_Page_Gets_304_Even_With_Fresh_One_Time_Tokens()
    {
        var first = Request(prepare: true);
        await Middleware(Form("tok-1", "ft-1", "5")).InvokeAsync(first);
        var etag = first.Response.Headers.ETag.ToString();
        Assert.StartsWith("W/\"p-", etag);
        Assert.Equal("no-store", first.Response.Headers.CacheControl.ToString());
        Assert.Contains("Amount", Body(first));

        var second = Request(prepare: true, etag);
        await Middleware(Form("tok-2", "ft-2", "5")).InvokeAsync(second);
        Assert.Equal(StatusCodes.Status304NotModified, second.Response.StatusCode);
        Assert.Equal(string.Empty, Body(second));
    }

    [Fact]
    public async Task Changed_Content_Is_Sent_With_A_New_ETag()
    {
        var first = Request(prepare: true);
        await Middleware(Form("tok-1", "ft-1", "5")).InvokeAsync(first);
        var etag = first.Response.Headers.ETag.ToString();

        var second = Request(prepare: true, etag);
        await Middleware(Form("tok-1", "ft-1", "6")).InvokeAsync(second);
        Assert.Equal(StatusCodes.Status200OK, second.Response.StatusCode);
        Assert.NotEqual(etag, second.Response.Headers.ETag.ToString());
        Assert.Contains("value=\"6\"", Body(second));
    }

    [Fact]
    public async Task Normal_User_Requests_Pass_Through_Untouched()
    {
        var context = Request(prepare: false, ifNoneMatch: "W/\"p-x\"");
        await Middleware(Form("tok-1", "ft-1", "5")).InvokeAsync(context);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.False(context.Response.Headers.ContainsKey("ETag"));
        Assert.Contains("tok-1", Body(context));
    }
}
