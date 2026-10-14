using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Net.Http.Headers;

namespace PTGOilSystem.Web.Middleware;

/// <summary>
/// آماده‌سازی خودکار کار بدون سرور (wwwroot/offline/js/prepare.js) صفحات و فورم‌های مجاز کاربر را در پس‌زمینه با هدر
/// <c>X-PTG-Prepare: 1</c> می‌گیرد. برای همین درخواست‌ها (فقط GET):
/// - پاسخ HTML/JSON یک ETag ضعیف از محتوای «عادی‌شده» می‌گیرد (مقدار توکن‌های یک‌بارمصرف فورم —
///   <c>__RequestVerificationToken</c>، <c>__FormToken</c> — و nonce کنار گذاشته می‌شود، چون در هر رندر عوض می‌شوند).
/// - اگر دستگاه همان ETag را در <c>If-None-Match</c> بفرستد، پاسخ <c>304</c> بدون بدنه است: صفحهٔ بدون تغییر دوباره
///   دانلود، رمزگذاری و ذخیره نمی‌شود (مصرف اینترنت موبایل و کار دستگاه کم می‌شود).
/// صلاحیت، اکشن و رندر صفحه هیچ تغییری نمی‌کند؛ درخواست‌های عادی کاربر از این‌جا عبور مستقیم دارند.
/// </summary>
public sealed partial class OfflinePrepareMiddleware
{
    public const string HeaderName = "X-PTG-Prepare";

    private readonly RequestDelegate _next;

    public OfflinePrepareMiddleware(RequestDelegate next) => _next = next;

    public static bool IsPrepareRequest(HttpContext context)
        => HttpMethods.IsGet(context.Request.Method)
            && string.Equals(context.Request.Headers[HeaderName], "1", StringComparison.Ordinal);

    public async Task InvokeAsync(HttpContext context)
    {
        if (!IsPrepareRequest(context))
        {
            await _next(context);
            return;
        }

        var original = context.Response.Body;
        await using var buffer = new MemoryStream();
        context.Response.Body = buffer;
        try
        {
            await _next(context);
        }
        finally
        {
            context.Response.Body = original;
        }

        var response = context.Response;
        response.Headers.CacheControl = "no-store";
        if (response.StatusCode == StatusCodes.Status200OK && buffer.Length > 0 && IsTextual(response.ContentType))
        {
            var etag = ComputeETag(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
            response.Headers.ETag = etag;
            if (Matches(context.Request.Headers.IfNoneMatch, etag))
            {
                response.StatusCode = StatusCodes.Status304NotModified;
                response.ContentLength = null;
                response.Headers.ContentType = Microsoft.Extensions.Primitives.StringValues.Empty;
                return;
            }
        }

        if (buffer.Length > 0)
        {
            response.ContentLength = buffer.Length;
            buffer.Position = 0;
            await buffer.CopyToAsync(original, context.RequestAborted);
        }
    }

    private static bool IsTextual(string? contentType)
        => contentType is not null
            && (contentType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase)
                || contentType.Contains("json", StringComparison.OrdinalIgnoreCase));

    private static bool Matches(Microsoft.Extensions.Primitives.StringValues header, string etag)
    {
        foreach (var value in header)
        {
            if (string.IsNullOrEmpty(value)) continue;
            foreach (var part in value.Split(','))
            {
                if (string.Equals(part.Trim(), etag, StringComparison.Ordinal)) return true;
            }
        }
        return false;
    }

    internal static string ComputeETag(ReadOnlySpan<byte> body)
    {
        var text = Encoding.UTF8.GetString(body);
        text = OneTimeTokenInput().Replace(text, string.Empty);
        text = NonceAttribute().Replace(text, string.Empty);
        // زمان رندرِ صفحه (نسخهٔ پایهٔ ویرایش آفلاین) محتوا نیست.
        text = RenderedAtAttribute().Replace(text, string.Empty);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return "W/\"p-" + Convert.ToHexString(hash, 0, 16).ToLowerInvariant() + "\"";
    }

    [GeneratedRegex("<input\\b[^>]*\\bname=\"(?:__RequestVerificationToken|__FormToken)\"[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex OneTimeTokenInput();

    [GeneratedRegex("\\snonce=\"[^\"]*\"", RegexOptions.IgnoreCase)]
    private static partial Regex NonceAttribute();

    [GeneratedRegex("\\sdata-ptg-rendered=\"[^\"]*\"", RegexOptions.IgnoreCase)]
    private static partial Regex RenderedAtAttribute();
}
