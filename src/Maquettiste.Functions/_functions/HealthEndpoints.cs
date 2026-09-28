using System.Reflection;
using Maquettiste.Engine;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Maquettiste.Functions;

/// <summary>The editor's readiness (<c>GET /api/health</c>, admitted without credentials).</summary>
/// <param name="Status"><c>ok</c>, <c>starting</c> until the first load finished, or <c>degraded</c> when the model folder cannot be
/// read or a background service is not running.</param>
/// <param name="EngineVersion"><see cref="Engine.EngineVersion.Value"/>, the engine's format-level version.</param>
/// <param name="EngineBuild">The loaded engine assembly's informational version without any <c>+</c> suffix (the image's per-build
/// package version, PD24).</param>
/// <param name="ModelLoaded">Whether the model is indexed.</param>
/// <param name="Elements">How many elements the index holds.</param>
/// <param name="Worker"><c>running</c> or <c>stopped</c>.</param>
/// <param name="Watcher"><c>watching</c>, <c>polling</c> or <c>stopped</c>.</param>
public sealed record EditorHealth(string Status, string EngineVersion, string EngineBuild, bool ModelLoaded, int Elements, string Worker, string Watcher);

/// <summary><c>GET /api/health</c>.</summary>
public static class HealthEndpoints
{
    /// <summary>Whether the functions are loaded, the model is indexed and the background services run; reads no files.</summary>
    /// <param name="context">The request.</param>
    /// <param name="store">The model store.</param>
    /// <param name="events">The background services' state.</param>
    /// <returns>200 with <see cref="EditorHealth"/>.</returns>
    [HttpGet("/api/health")]
    public static IResult Get(HttpContext context, ModelStore store, EditorEvents events) => Api.Guard(context, () =>
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(events);
        var snapshot = store.Current;
        var status = snapshot is null
            ? events.LoadFailed ? "degraded" : "starting"
            : events.Worker == "running" && events.Watcher != "stopped" ? "ok" : "degraded";
        return Api.Json(new EditorHealth(status, EngineVersion.Value, EngineBuild(), snapshot is not null, snapshot?.Documents.Count ?? 0,
            events.Worker, events.Watcher));
    });

    /// <summary>The loaded engine assembly's informational version, without build metadata.</summary>
    /// <returns>The version.</returns>
    public static string EngineBuild()
    {
        var assembly = typeof(ModelStore).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString() ?? "unknown";
        var plus = version.IndexOf('+', StringComparison.Ordinal);
        return plus < 0 ? version : version[..plus];
    }
}
