using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using PTGOilSystem.Web.Services.OfflineSync;
using Xunit;

namespace PTGOilSystem.Web.Tests.OfflineSync;

/// <summary>
/// هر اکشن POSTِ MVC دقیقاً یک تعیین تکلیف دارد: یا آفلاین (<see cref="OfflineFormRegistry.All"/>) یا فقط‌آنلاین با دلیل
/// (<see cref="OfflineFormRegistry.OnlineOnly"/>). اکشن تازه بدون تصمیم، این تست را می‌شکند.
/// </summary>
public sealed class OfflineFormRegistryCoverageTests
{
    private static readonly IReadOnlyList<(string Controller, string Action, MethodInfo Method)> PostActions = typeof(PTGOilSystem.Web.Controllers.HomeController).Assembly
        .GetTypes()
        .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(Controller).IsAssignableFrom(t) && t.Name.EndsWith("Controller", StringComparison.Ordinal))
        .SelectMany(t => t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttributes<HttpPostAttribute>(true).Any() && m.GetCustomAttribute<NonActionAttribute>() is null)
            .Select(m => (t.Name[..^"Controller".Length], ActionName(m), m)))
        .ToList();

    private static string ActionName(MethodInfo method)
        => method.GetCustomAttribute<ActionNameAttribute>()?.Name
            ?? (method.Name.EndsWith("Async", StringComparison.Ordinal) ? method.Name[..^"Async".Length] : method.Name);

    private static string Key(string controller, string action) => $"{controller}.{action}".ToLowerInvariant();

    [Fact]
    public void Every_Mvc_Post_Action_Is_Either_Offline_Capable_Or_Online_Only_With_A_Reason()
    {
        var offline = OfflineFormRegistry.All.Select(t => Key(t.Controller, t.Action)).ToHashSet();
        var onlineOnly = OfflineFormRegistry.OnlineOnly.Select(o => Key(o.Controller, o.Action)).ToHashSet();
        var actions = PostActions.Select(a => Key(a.Controller, a.Action)).ToHashSet();

        var undecided = actions.Where(a => !offline.Contains(a) && !onlineOnly.Contains(a)).OrderBy(a => a).ToList();
        var both = offline.Intersect(onlineOnly).OrderBy(a => a).ToList();
        var unknownOffline = offline.Where(a => !actions.Contains(a)).OrderBy(a => a).ToList();
        var unknownOnline = onlineOnly.Where(a => !actions.Contains(a)).OrderBy(a => a).ToList();

        Assert.True(undecided.Count == 0, "POST actions without an offline decision: " + string.Join(", ", undecided));
        Assert.True(both.Count == 0, "Actions in both lists: " + string.Join(", ", both));
        Assert.True(unknownOffline.Count == 0, "Offline targets that are not POST actions: " + string.Join(", ", unknownOffline));
        Assert.True(unknownOnline.Count == 0, "Online-only entries that are not POST actions: " + string.Join(", ", unknownOnline));
        Assert.All(OfflineFormRegistry.OnlineOnly, o => Assert.False(string.IsNullOrWhiteSpace(o.Reason)));
        Assert.Equal(OfflineFormRegistry.All.Count, OfflineFormRegistry.All.Select(t => t.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Record_Targets_Bind_An_Id_And_Find_Resolves_Both_Route_And_Field_Forms()
    {
        foreach (var target in OfflineFormRegistry.All.Where(t => t.RouteId))
        {
            var methods = PostActions.Where(a => Key(a.Controller, a.Action) == Key(target.Controller, target.Action)).ToList();
            Assert.True(methods.Any(m => m.Method.GetParameters().Any(p => string.Equals(p.Name, "id", StringComparison.OrdinalIgnoreCase))),
                $"{target.Controller}.{target.Action} is registered with a record id but its POST action has no 'id' parameter.");
        }

        var cancel = OfflineFormRegistry.Find("/sales/cancel/42", out var routeId);
        Assert.Equal("Sales", cancel?.Controller);
        Assert.Equal(42, routeId);
        Assert.Equal(OfflineApplyMode.Ambient, cancel!.Mode);

        var byField = OfflineFormRegistry.Find("/expenses/cancel", out var noRouteId);
        Assert.Equal("Expenses", byField?.Controller);
        Assert.Null(noRouteId);

        Assert.Null(OfflineFormRegistry.Find("/sales/create/42", out _));
        Assert.Null(OfflineFormRegistry.Find("/users/delete/3", out _));
        Assert.Null(OfflineFormRegistry.Find("/sales/cancel/op:abc", out _));
        Assert.Equal(OfflineApplyMode.Token, OfflineFormRegistry.Find("/sales/create")!.Mode);
    }
}
