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
using Maquettiste.Engine.Resolution;

namespace Maquettiste.Engine.Scripting;

internal sealed partial class ScriptSandbox
{
    /// <summary>A registered helper, selector, filter or transform.</summary>
    /// <param name="Kind">What it is.</param>
    /// <param name="Name">The registered name.</param>
    /// <param name="DeclaredIn">The script that registered it.</param>
    /// <param name="Function">The function.</param>
    /// <param name="Plain">
    /// Whether a helper takes plain copies of its arguments (<c>{ plain: true }</c>, <see cref="ScriptValues.ToPlainJs"/>) rather than
    /// proxies.
    /// </param>
    private sealed record Registered(ScriptRegistrationKind Kind, string Name, string DeclaredIn, Function Function, bool Plain = false);

    /// <summary>A registered validation rule.</summary>
    /// <param name="Id">The rule id (without <c>x/</c>).</param>
    /// <param name="Severity">The declared severity.</param>
    /// <param name="Kinds">The kinds an element rule checks, or <see langword="null"/> for every kind.</param>
    /// <param name="Check">The check function.</param>
    /// <param name="DeclaredIn">The rule script.</param>
    /// <param name="WholeModel">Whether it was registered with <c>scope: "model"</c>: <c>check(model, report)</c>, once per whole-model
    /// validation.</param>
    /// <param name="Resolved">Whether it was registered with <c>scope: "resolved"</c>: <c>check(element, model, report)</c> on each
    /// resolved object of its kinds, in the checks after resolution.</param>
    private sealed record RuleSpec(string Id, DiagnosticSeverity Severity, FrozenSet<string>? Kinds, Function Check, string DeclaredIn, bool WholeModel = false,
        bool Resolved = false);

    /// <summary>What is being called, for diagnostics.</summary>
    /// <param name="Label">For example <c>Helper 'money'</c>.</param>
    /// <param name="DeclaredIn">The script that registered it (the fallback file of a diagnostic).</param>
    /// <param name="ElementId">The element the call is about, if any.</param>
    /// <param name="Rule">Whether it is a validation rule (MQ5002/MQ5003) rather than a pack script (MQ6016/MQ6007).</param>
    private sealed record CallSite(string Label, string DeclaredIn, string? ElementId, bool Rule);

    [GeneratedRegex(@"at (?:[^()\r\n]*\()?(?<file>[^()\r\n]+?):(?<line>\d+):(?<col>\d+)\)?\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex StackFrame();

    /// <summary>The running call's context (for <c>maquettiste.use</c> calls); <see langword="null"/> between calls and while loading.</summary>
    private ScriptCallContext? _callContext;

    private T Run<T>(CallSite site, ScriptCallContext ctx, Func<T> body)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ObjectDisposedException.ThrowIf(_disposed, this);
        ctx.CancellationToken.ThrowIfCancellationRequested();
        _runToken.ThrowIfCancellationRequested();
        if (_inCall)
            throw new InvalidOperationException("A script sandbox runs one call at a time.");
        _inCall = true;
        _callContext = ctx;
        _state.Begin(ctx);
        _values.BeginCall();
        _random.Seed(ctx.Seed ?? "");
        try
        {
            _engine.Constraints.Reset();
            var result = body();
            // A read of a failed calculated value fails the call even when the script caught it (next-phase-2-design.md section 5.5).
            if (_values.FailedRead is { } failed)
                throw new CalculatedValueException(failed);
            return result;
        }
        catch (CalculatedValueException)
        {
            _engine.Advanced.ResetCallStack();
            throw;
        }
        catch (ScriptLimitException ex) when (_values.FailedRead is { } failed
            && !ctx.CancellationToken.IsCancellationRequested && !_runToken.IsCancellationRequested)
        {
            _engine.Advanced.ResetCallStack();
            throw new CalculatedValueException(failed, ex);
        }
        catch (ScriptLimitException ex) when (ctx.CancellationToken.IsCancellationRequested || _runToken.IsCancellationRequested)
        {
            // A limit that trips while a cancellation is pending (a regular expression's match timeout, the statement budget)
            // is how the script stopped, not why: the caller asked for cancellation, so that is what it gets.
            Faulted = true;
            throw ctx.CancellationToken.IsCancellationRequested
                ? new OperationCanceledException("The script was cancelled.", ex, ctx.CancellationToken)
                : new OperationCanceledException("The script was cancelled.", ex, _runToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not ScriptErrorException and not ScriptLimitException)
        {
            if (_values.FailedRead is { } failed && !ctx.CancellationToken.IsCancellationRequested && !_runToken.IsCancellationRequested)
            {
                _engine.Advanced.ResetCallStack();
                throw new CalculatedValueException(failed, ex);
            }

            throw Translate(ex, site, ctx.CancellationToken);
        }
        catch (Exception ex) when (ex is ScriptErrorException or ScriptLimitException && _callContext?.Libraries is not null)
        {
            // A used pack's failure (library packs) keeps its own script and position; this engine only unwinds its stack.
            _engine.Advanced.ResetCallStack();
            throw;
        }
        catch (OperationCanceledException)
        {
            Faulted = true;
            throw;
        }
        finally
        {
            _state.End();
            _values.EndCall();
            _callContext = null;
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
                return Limit(site, $"exceeded the statement limit ({StatementLimit(_limits)})", stack, ex);
            case RegexMatchTimeoutException:
                return Limit(site, "ran a regular expression past its time limit", stack, ex);
            case TimeoutException:
                return Limit(site, $"exceeded the time limit ({_limits.ScriptTimeoutMs} ms)", stack, ex);
            case MemoryLimitExceededException:
                return Limit(site, $"exceeded the memory limit ({_limits.ScriptMemoryBytes} bytes): {ex.Message}", stack, ex);
            case RecursionDepthOverflowException:
                return Limit(site, $"exceeded the recursion limit ({_limits.ScriptRecursion})", stack, ex);
            case JavaScriptException js:
                var (file, line, column) = InCode(Locate(js, stack, site));
                // A read of a calculated value the object's kind does not declare is MQ5012 at the reading line (section 5.5).
                if (_values.IsUndeclaredRead(js.Error))
                {
                    return new ScriptErrorException(RuleCatalog.Create("MQ5012", $"{site.Label} threw {ErrorText(js)}", site.ElementId, file)
                        with { Line = line, Column = column }, ex);
                }

                return new ScriptErrorException(new Diagnostic(site.Rule ? "MQ5002" : "MQ6016", DiagnosticSeverity.Error,
                    $"{site.Label} threw {ErrorText(js)}", site.ElementId, file, null, line, column), ex);
            default:
                var (f, l, c) = InCode(FromStack(stack) ?? (site.DeclaredIn, null, null));
                return new ScriptErrorException(new Diagnostic(site.Rule ? "MQ5002" : "MQ6016", DiagnosticSeverity.Error,
                    $"{site.Label} failed: {ex.Message}", site.ElementId, f, null, l, c), ex);
        }
    }

    private ScriptLimitException Limit(CallSite site, string what, string? stack, Exception ex)
    {
        var (file, line, column) = InCode(FromStack(stack) ?? (site.DeclaredIn, null, null));
        return new ScriptLimitException(new Diagnostic(site.Rule ? "MQ5003" : "MQ6007", DiagnosticSeverity.Error,
            $"{site.Label} {what}.", site.ElementId, file, null, line, column), ex);
    }

    /// <summary>A position in this engine's prepared text, mapped to the script's own code (<see cref="PreparedScript.Map"/>).</summary>
    private (string File, int? Line, int? Column) InCode((string File, int? Line, int? Column) position)
    {
        if (!_scripts.TryGetValue(position.File, out var script))
            return position;
        var (line, column) = script.Map(position.Line, position.Column);
        return (position.File, line, column);
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
    /// The read-only model a validation rule sees (engine-design.md section 6; next-phase-1-design.md section 5.7): <c>get(id)</c>,
    /// <c>all(kind)</c>, <c>referencesTo(id)</c>, the index functions <c>withStereotype(key)</c>, <c>withTag(tag)</c> and
    /// <c>withProperty(key)</c>, and <c>byQualifiedName(name)</c>, each returning frozen copies of canonical JSON. One view per
    /// snapshot per engine. Every function records the keys it reads (<see cref="Validation.RuleKeyHasher"/> hashes them), so a
    /// rule's result can be reused while they are unchanged.
    /// </summary>
    private JsValue RuleModel(ModelSnapshot model)
    {
        if (_ruleModel is not null && ReferenceEquals(_ruleSnapshot, model))
            return _ruleModel;
        _ruleSnapshot = model;
        _ruleModel = _values.FrozenObject(
        [
            Function("get", args => RuleGet(model, IdArgument(args, "get"))),
            Function("all", args => RuleAll(model, IdArgument(args, "all"))),
            Function("referencesTo", args => RuleReferencesTo(model, IdArgument(args, "referencesTo"))),
            Function("withStereotype", args => RuleIndexed(model, Validation.RuleModelIndex.Stereotype, IdArgument(args, "withStereotype"))),
            Function("withTag", args => RuleIndexed(model, Validation.RuleModelIndex.Tag, IdArgument(args, "withTag"))),
            Function("withProperty", args => RuleIndexed(model, Validation.RuleModelIndex.Property, IdArgument(args, "withProperty"))),
            Function("byQualifiedName", args => RuleByQualifiedName(model, IdArgument(args, "byQualifiedName"))),
        ]);
        return _ruleModel;

        KeyValuePair<string, JsValue> Function(string name, Func<JsValue[], JsValue> body) =>
            KeyValuePair.Create(name, (JsValue)new ClrFunction(_engine, name, (_, args) => body(args), 1, PropertyFlag.Configurable));
    }

    private string IdArgument(JsValue[] args, string function) =>
        args.Length > 0 && args[0] is JsString s ? s.ToString() : throw TypeError($"model.{function}() takes a string.");

    /// <summary><c>get(id)</c>: the document, or the part of it, holding an element or sub-element id; records <c>e:&lt;id&gt;</c>.</summary>
    private JsValue RuleGet(ModelSnapshot model, string id)
    {
        _state.Record("e:" + id);
        if (!model.TryGetEntry(id, out var entry) || model.GetDocument(id) is not { } document)
            return JsValue.Null;
        if (string.Equals(entry.OwnerId, id, StringComparison.Ordinal) || entry.JsonPointer.Length == 0)
            return _values.FromJson(document.Json);
        return Navigate(document.Json, entry.JsonPointer) is { } sub ? _values.FromJson(sub) : JsValue.Null;
    }

    /// <summary>
    /// <c>all(kind)</c>: every document of a kind from the snapshot's sorted kind index, ordinal by (name, id); records <c>k:&lt;kind&gt;</c>
    /// and <c>e:</c> per document.
    /// </summary>
    private JsValue RuleAll(ModelSnapshot model, string kind)
    {
        if (!KindInfo.TryGet(kind, out var info))
            throw TypeError($"model.all(): unknown kind '{kind}'.");
        _state.Record("k:" + info.Name);
        return Documents(model.DocumentsOf(info.Kind));
    }

    /// <summary>
    /// <c>withStereotype</c>, <c>withTag</c> and <c>withProperty</c>: the documents an index of <see cref="Validation.RuleModelIndex"/> holds
    /// under a value, ordinal by (name, id); records <c>ix:&lt;index&gt;:&lt;value&gt;</c> and <c>e:</c> per document.
    /// </summary>
    private JsValue RuleIndexed(ModelSnapshot model, string index, string value)
    {
        _state.Record(Validation.RuleModelIndex.Key(index, value));
        return Documents(Validation.RuleModelIndex.Of(model).Find(index, value));
    }

    /// <summary>
    /// <c>byQualifiedName(name)</c>: what <see cref="RuleGet"/> returns for each id a qualified name (<c>@entity:Billing.Invoice</c>,
    /// next-phase-1-design.md section 5.1) finds in the snapshot's name index, ordinal by id; records <c>ix:qn:&lt;canonical name&gt;</c>
    /// and <c>e:</c> per id. A malformed name, or a kind without names, is a <c>TypeError</c>.
    /// </summary>
    private JsValue RuleByQualifiedName(ModelSnapshot model, string text)
    {
        if (!Loading.QualifiedName.TryParse(text, out var name, out var error))
            throw TypeError($"model.byQualifiedName() takes a qualified name such as '@entity:Billing.Invoice': '{text}' is malformed ({error}).");
        _state.Record(Validation.RuleModelIndex.Key(Validation.RuleModelIndex.Qualified, name.Text));
        var ids = Validation.RuleModelIndex.Of(model).FindQualified(name);
        var items = new JsValue[ids.Count];
        for (var i = 0; i < items.Length; i++)
            items[i] = RuleGet(model, ids[i]);
        return _values.FrozenArray(items);
    }

    /// <summary>Frozen copies of documents, recording <c>e:</c> for each.</summary>
    private JsValue Documents(IReadOnlyList<ElementDocument> documents)
    {
        var items = new JsValue[documents.Count];
        for (var i = 0; i < items.Length; i++)
        {
            _state.Record("e:" + documents[i].Element.Id);
            items[i] = _values.FromJson(documents[i].Json);
        }

        return _values.FrozenArray(items);
    }

    /// <summary>
    /// <c>referencesTo(id)</c>: every reference to an id, in (path, pointer) order, its own file and diagrams included; records
    /// <c>rt:&lt;id&gt;</c>, whose hash covers every entry (<c>r:</c> skips the id's own file and diagrams).
    /// </summary>
    private JsValue RuleReferencesTo(ModelSnapshot model, string id)
    {
        _state.Record("rt:" + id);
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
