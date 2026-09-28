using System.Globalization;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Routing;
using StaticSiteHost.Functions;

namespace Maquettiste.Functions.Tests.Support;

/// <summary>
/// Dispatches a request to the functions' handlers the way static-site-hosting 0.2.0's <c>FunctionRouter</c> does (its source at
/// <c>src/StaticSiteHost/Services/FunctionRouter.cs</c>): routes from <c>[Http*]</c> attributes, literal-heavy templates first,
/// <c>{name}</c> segments captured, 405 for a path that matches with another verb, and parameters bound by type (the request, the site,
/// its variables, realtime and AI, the data folder, a logger, the functions' services) or, for simple values, by name from the route
/// then the query string (enums by member name, ignoring case).
/// </summary>
internal static class TestRouter
{
    /// <summary>Every route: verb, template, segments, method.</summary>
    public static IReadOnlyList<(string Verb, string Template, string[] Segments, MethodInfo Method)> Routes { get; } = Discover();

    public static async Task<bool> TryDispatchAsync(HttpContext context, ISite site)
    {
        var segments = (context.Request.Path.Value ?? "/").Split('/', StringSplitOptions.RemoveEmptyEntries);
        var pathMatched = false;
        foreach (var route in Routes)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!TryMatch(route.Segments, segments, values))
                continue;
            pathMatched = true;
            if (!route.Verb.Equals(context.Request.Method, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!TryBind(route.Method, context, site, values, out var arguments, out var error))
            {
                await Results.Text(error, statusCode: StatusCodes.Status400BadRequest).ExecuteAsync(context);
                return true;
            }

            object? returned;
            try
            {
                returned = route.Method.Invoke(null, arguments);
            }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            {
                ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;
            }

            var result = returned switch
            {
                Task<IResult> task => await task,
                IResult value => value,
                _ => throw new InvalidOperationException($"{route.Method.Name} returned {returned?.GetType().Name ?? "null"}; handlers return IResult."),
            };
            await result.ExecuteAsync(context);
            return true;
        }

        if (!pathMatched)
            return false;
        context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
        return true;
    }

    private static List<(string Verb, string Template, string[] Segments, MethodInfo Method)> Discover()
    {
        var routes = new List<(string, string, string[], MethodInfo)>();
        foreach (var type in typeof(Api).Assembly.GetExportedTypes())
        {
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                foreach (var attribute in method.GetCustomAttributes<HttpMethodAttribute>())
                {
                    foreach (var verb in attribute.HttpMethods)
                        routes.Add((verb, attribute.Template!, attribute.Template!.Split('/', StringSplitOptions.RemoveEmptyEntries), method));
                }
            }
        }

        return [.. routes.OrderByDescending(r => r.Item3.Count(s => !s.StartsWith('{')))];
    }

    private static bool TryMatch(string[] template, string[] actual, Dictionary<string, string> values)
    {
        if (actual.Length != template.Length)
            return false;
        for (var i = 0; i < template.Length; i++)
        {
            if (template[i].StartsWith('{') && template[i].EndsWith('}'))
            {
                values[template[i][1..^1]] = actual[i];
                continue;
            }

            if (!template[i].Equals(actual[i], StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    private static bool TryBind(MethodInfo method, HttpContext context, ISite site, Dictionary<string, string> route, out object?[] arguments, out string error)
    {
        var parameters = method.GetParameters();
        arguments = new object?[parameters.Length];
        error = "";
        for (var i = 0; i < parameters.Length; i++)
        {
            var parameter = parameters[i];
            var type = parameter.ParameterType;
            if (type == typeof(HttpContext)) { arguments[i] = context; continue; }
            if (type == typeof(HttpRequest)) { arguments[i] = context.Request; continue; }
            if (type == typeof(HttpResponse)) { arguments[i] = context.Response; continue; }
            if (type == typeof(CancellationToken)) { arguments[i] = context.RequestAborted; continue; }
            if (type == typeof(ISite)) { arguments[i] = site; continue; }
            if (type == typeof(ISiteVariables)) { arguments[i] = site.Variables; continue; }
            if (type == typeof(IRealtime)) { arguments[i] = site.Realtime; continue; }
            if (type == typeof(IAiChat)) { arguments[i] = site.Ai; continue; }
            if (type == typeof(DirectoryInfo)) { arguments[i] = site.Data; continue; }
            if (type == typeof(IServiceProvider)) { arguments[i] = site.Services; continue; }
            if (!IsSimple(type) && site.Services.GetService(type) is { } service) { arguments[i] = service; continue; }

            var raw = route.TryGetValue(parameter.Name!, out var fromRoute) ? fromRoute
                : context.Request.Query.TryGetValue(parameter.Name!, out var fromQuery) ? fromQuery.ToString()
                : null;
            if (raw is null)
            {
                arguments[i] = parameter.HasDefaultValue ? parameter.DefaultValue
                    : type.IsValueType && Nullable.GetUnderlyingType(type) is null ? Activator.CreateInstance(type) : null;
                continue;
            }

            if (!TryConvert(raw, type, out arguments[i]))
            {
                error = $"'{raw}' is not a valid value for '{parameter.Name}'.";
                return false;
            }
        }

        return true;
    }

    private static bool IsSimple(Type type)
    {
        var t = Nullable.GetUnderlyingType(type) ?? type;
        return t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(decimal) || t == typeof(Guid) || t == typeof(DateTime)
            || t == typeof(DateTimeOffset) || t == typeof(TimeSpan);
    }

    private static bool TryConvert(string value, Type target, out object? converted)
    {
        var t = Nullable.GetUnderlyingType(target) ?? target;
        converted = null;
        if (t == typeof(string))
        {
            converted = value;
            return true;
        }

        if (t.IsEnum)
        {
            if (!Enum.TryParse(t, value, ignoreCase: true, out var member))
                return false;
            converted = member;
            return true;
        }

        try
        {
            converted = Convert.ChangeType(value, t, CultureInfo.InvariantCulture);
            return true;
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            return false;
        }
    }
}
