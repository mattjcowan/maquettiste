using System.Collections.Frozen;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jint.Native;
using Jint.Native.Function;
using Jint.Runtime;
using Jint.Runtime.Descriptors;
using Jint.Runtime.Interop;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Scripting;

internal sealed partial class ScriptSandbox
{
    /// <summary>A registered helper, selector, filter or transform.</summary>
    private sealed record Registered(ScriptRegistrationKind Kind, string Name, string DeclaredIn, Function Function);

    /// <summary>A registered validation rule.</summary>
    private sealed record RuleSpec(string Id, DiagnosticSeverity Severity, FrozenSet<string>? Kinds, Function Check, string DeclaredIn);

    /// <summary>What is being called, for diagnostics.</summary>
    /// <param name="Label">For example <c>Helper 'money'</c>.</param>
    /// <param name="DeclaredIn">The script that registered it (the fallback file of a diagnostic).</param>
    /// <param name="ElementId">The element the call is about, if any.</param>
    /// <param name="Rule">Whether it is a validation rule (MQ5002/MQ5003) rather than a pack script (MQ6016/MQ6007).</param>
    private sealed record CallSite(string Label, string DeclaredIn, string? ElementId, bool Rule);

    [GeneratedRegex(@"at (?:[^()\r\n]*\()?(?<file>[^()\r\n]+?):(?<line>\d+):(?<col>\d+)\)?\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex StackFrame();

    private T Run<T>(CallSite site, ScriptCallContext ctx, Func<T> body)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ObjectDisposedException.ThrowIf(_disposed, this);
        ctx.CancellationToken.ThrowIfCancellationRequested();
        _runToken.ThrowIfCancellationRequested();
        if (_inCall)
            throw new InvalidOperationException("A script sandbox runs one call at a time.");
        _inCall = true;
        _state.Begin(ctx);
        _random.Seed(ctx.Seed ?? "");
        try
        {
            _engine.Constraints.Reset();
            return body();
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not ScriptErrorException and not ScriptLimitException)
        {
            throw Translate(ex, site, ctx.CancellationToken);
        }
        catch (OperationCanceledException)
        {
            Faulted = true;
            throw;
        }
        finally
        {
            _state.End();
            _inCall = false;
        }
    }

    /// <summary>Maps an exception raised while a script ran to the exception the caller sees.</summary>
    private Exception Translate(Exception ex, CallSite site, CancellationToken callToken)
    {
        var stack = SafeStackTrace();
        if (ex is not JavaScriptException)
        {
            Faulted = true;
            _engine.Advanced.ResetCallStack();
        }

        // A regular expression that was running when a token was cancelled ends with a timeout; cancellation wins.
        if (ex is not JavaScriptException && (callToken.IsCancellationRequested || _runToken.IsCancellationRequested))
            ex = new ExecutionCanceledException();

        switch (ex)
        {
            case ExecutionCanceledException:
                return callToken.IsCancellationRequested ? new OperationCanceledException("The script was cancelled.", ex, callToken)
                    : _runToken.IsCancellationRequested ? new OperationCanceledException("The script was cancelled.", ex, _runToken)
                    : new OperationCanceledException("The script was cancelled.", ex);
            case StatementsCountOverflowException:
                return Limit(site, $"exceeded the statement limit ({_limits.ScriptStatements})", stack, ex);
            case RegexMatchTimeoutException:
                return Limit(site, "ran a regular expression past its time limit", stack, ex);
            case TimeoutException:
                return Limit(site, $"exceeded the time limit ({_limits.ScriptTimeoutMs} ms)", stack, ex);
            case MemoryLimitExceededException:
                return Limit(site, $"exceeded the memory limit ({_limits.ScriptMemoryBytes} bytes): {ex.Message}", stack, ex);
            case RecursionDepthOverflowException:
                return Limit(site, $"exceeded the recursion limit ({_limits.ScriptRecursion})", stack, ex);
            case JavaScriptException js:
                var (file, line, column) = Locate(js, stack, site);
                return new ScriptErrorException(new Diagnostic(site.Rule ? "MQ5002" : "MQ6016", DiagnosticSeverity.Error,
                    $"{site.Label} threw {ErrorText(js)}", site.ElementId, file, null, line, column), ex);
            default:
                var (f, l, c) = FromStack(stack) ?? (site.DeclaredIn, null, null);
                return new ScriptErrorException(new Diagnostic(site.Rule ? "MQ5002" : "MQ6016", DiagnosticSeverity.Error,
                    $"{site.Label} failed: {ex.Message}", site.ElementId, f, null, l, c), ex);
        }
    }

    private ScriptLimitException Limit(CallSite site, string what, string? stack, Exception ex)
    {
        var (file, line, column) = FromStack(stack) ?? (site.DeclaredIn, null, null);
        return new ScriptLimitException(new Diagnostic(site.Rule ? "MQ5003" : "MQ6007", DiagnosticSeverity.Error,
            $"{site.Label} {what}.", site.ElementId, file, null, line, column), ex);
    }

    private string? SafeStackTrace()
    {
        try
        {
            return _engine.Advanced.StackTrace;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static (string File, int? Line, int? Column) Locate(JavaScriptException js, string? engineStack, CallSite site)
    {
        var location = js.Location;
        if (!string.IsNullOrEmpty(location.SourceFile) && location.Start.Line > 0 && !IsInternal(location.SourceFile))
            return (location.SourceFile, location.Start.Line, location.Start.Column + 1);
        return FromStack(js.JavaScriptStackTrace) ?? FromStack(engineStack) ?? (site.DeclaredIn, null, null);
    }

    /// <summary>The innermost frame of a Jint stack trace (<c>at fn (file:line:column)</c>); columns there are 1-based.</summary>
    private static (string File, int? Line, int? Column)? FromStack(string? stack)
    {
        if (string.IsNullOrEmpty(stack))
            return null;
        foreach (Match match in StackFrame().Matches(stack))
        {
            var file = match.Groups["file"].Value.Trim();
            if (file.Length == 0 || string.Equals(file, "<anonymous>", StringComparison.Ordinal) || IsInternal(file))
                continue;
            return (file, int.Parse(match.Groups["line"].Value, CultureInfo.InvariantCulture), int.Parse(match.Groups["col"].Value, CultureInfo.InvariantCulture));
        }

        return null;
    }

    /// <summary>Whether a source file is one of the sandbox's own setup scripts (<c>maquettiste:*</c>), never blamed in a diagnostic.</summary>
    private static bool IsInternal(string file) => file.StartsWith(InternalSourcePrefix, StringComparison.Ordinal);

    private static string ErrorText(JavaScriptException js)
    {
        var name = js.Error is Jint.Native.Object.ObjectInstance error && error.Get("name") is JsString n ? n.ToString() : null;
        return name is null ? js.Message : $"{name}: {js.Message}";
    }

    /// <summary>
    /// The read-only model a validation rule sees (engine-design.md section 6): <c>get(id)</c>, <c>all(kind)</c> and
    /// <c>referencesTo(id)</c>, each returning frozen copies of canonical JSON. One view per snapshot per engine.
    /// </summary>
    private JsValue RuleModel(ModelSnapshot model)
    {
        if (_ruleModel is not null && ReferenceEquals(_ruleSnapshot, model))
            return _ruleModel;
        var get = new ClrFunction(_engine, "get", (_, args) => RuleGet(model, IdArgument(args, "get")), 1, PropertyFlag.Configurable);
        var all = new ClrFunction(_engine, "all", (_, args) => RuleAll(model, IdArgument(args, "all")), 1, PropertyFlag.Configurable);
        var referencesTo = new ClrFunction(_engine, "referencesTo", (_, args) => RuleReferencesTo(model, IdArgument(args, "referencesTo")), 1, PropertyFlag.Configurable);
        _ruleSnapshot = model;
        _ruleModel = _values.FrozenObject(
        [
            KeyValuePair.Create("get", (JsValue)get),
            KeyValuePair.Create("all", (JsValue)all),
            KeyValuePair.Create("referencesTo", (JsValue)referencesTo),
        ]);
        return _ruleModel;
    }

    private string IdArgument(JsValue[] args, string function) =>
        args.Length > 0 && args[0] is JsString s ? s.ToString() : throw TypeError($"model.{function}() takes a string.");

    private JsValue RuleGet(ModelSnapshot model, string id)
    {
        _state.Record("e:" + id);
        if (!model.TryGetEntry(id, out var entry) || model.GetDocument(id) is not { } document)
            return JsValue.Null;
        if (string.Equals(entry.OwnerId, id, StringComparison.Ordinal) || entry.JsonPointer.Length == 0)
            return _values.FromJson(document.Json);
        return Navigate(document.Json, entry.JsonPointer) is { } sub ? _values.FromJson(sub) : JsValue.Null;
    }

    private JsValue RuleAll(ModelSnapshot model, string kind)
    {
        if (!KindInfo.TryGet(kind, out var info))
            throw TypeError($"model.all(): unknown kind '{kind}'.");
        _state.Record("k:" + info.Name);
        var documents = model.Documents
            .Where(d => d.Element.Kind == info.Kind)
            .OrderBy(d => d.Element.Name, StringComparer.Ordinal)
            .ThenBy(d => d.Element.Id, StringComparer.Ordinal)
            .ToList();
        var items = new JsValue[documents.Count];
        for (var i = 0; i < items.Length; i++)
        {
            _state.Record("e:" + documents[i].Element.Id);
            items[i] = _values.FromJson(documents[i].Json);
        }

        return _values.FrozenArray(items);
    }

    private JsValue RuleReferencesTo(ModelSnapshot model, string id)
    {
        _state.Record("r:" + id);
        var references = model.ReferencesTo(id);
        var items = new JsValue[references.Count];
        for (var i = 0; i < items.Length; i++)
        {
            var r = references[i];
            items[i] = _values.FrozenObject(
            [
                KeyValuePair.Create("fromElementId", (JsValue)new JsString(r.FromElementId)),
                KeyValuePair.Create("fromId", (JsValue)new JsString(r.FromId)),
                KeyValuePair.Create("jsonPointer", (JsValue)new JsString(r.JsonPointer)),
                KeyValuePair.Create("field", (JsValue)new JsString(r.Field)),
                KeyValuePair.Create("toId", (JsValue)new JsString(r.ToId)),
            ]);
        }

        return _values.FrozenArray(items);
    }

    private static JsonElement? Navigate(JsonElement root, string pointer)
    {
        var current = root;
        foreach (var raw in pointer.Split('/').Skip(1))
        {
            var segment = raw.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            if (current.ValueKind == JsonValueKind.Object && current.TryGetProperty(segment, out var child))
                current = child;
            else if (current.ValueKind == JsonValueKind.Array && int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                && index < current.GetArrayLength())
                current = current[index];
            else
                return null;
        }

        return current;
    }
}
