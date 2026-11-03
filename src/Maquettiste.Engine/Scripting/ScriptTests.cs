using Maquettiste.Engine.Diagnostics;

namespace Maquettiste.Engine.Scripting;

/// <summary>A helper test a test file registered with <c>maquettiste.test(name, fn)</c> (next-phase-2-design.md section 5.4).</summary>
/// <param name="File">The test file's script path.</param>
/// <param name="Name">The test name, unique in its file.</param>
/// <param name="Order">Its position among every registered test: test files in load order, tests in registration order.</param>
internal sealed record ScriptTest(string File, string Name, int Order);

/// <summary>A test file that did not load: a syntax error, a throw, a limit, a registration other than a test, or a repeated name.</summary>
/// <param name="File">The test file's script path.</param>
/// <param name="Diagnostic">Why, at its position (MQ6016 or MQ6007 as the sandbox reports it; the runner reports MQ6053).</param>
internal sealed record ScriptTestFailure(string File, Diagnostic Diagnostic);

/// <summary>The result of one helper test call.</summary>
/// <param name="Passed">Whether every assertion held and the test returned.</param>
/// <param name="Message">Why it failed: the assertion's text (<c>expected &lt;JSON&gt;, got &lt;JSON&gt;</c> after the optional message),
/// the error a test or helper threw, or the limit it hit.</param>
/// <param name="File">The script of the failing statement.</param>
/// <param name="Line">The line of the failing statement.</param>
/// <param name="Column">The column of the failing statement.</param>
/// <param name="Limit">Whether a sandbox limit stopped it.</param>
/// <param name="Malformed">Whether it did not run: an engine built again no longer registered it (MQ6053, not a failure).</param>
internal sealed record ScriptTestResult(bool Passed, string? Message, string? File, int? Line, int? Column, bool Limit = false, bool Malformed = false)
{
    /// <summary>A passing test.</summary>
    public static ScriptTestResult Pass { get; } = new(true, null, null, null, null);
}

/// <summary>
/// The assert API of helper tests (next-phase-2-design.md section 5.4): a small script prelude every test engine runs once, under the
/// sandbox's own source name, so a failure points at the test's call rather than at the prelude. <c>equal</c> compares with
/// <c>===</c>, <c>NaN</c> equal to itself; <c>deepEqual</c> compares plain values, arrays by position and objects by key whatever the
/// key order; <c>ok</c>, <c>throws</c> (returns the error's message) and <c>fail</c>. A failure throws an <c>AssertionError</c> that reads
/// <c>expected &lt;JSON&gt;, got &lt;JSON&gt;</c> after the optional message.
/// </summary>
internal static class ScriptTests
{
    /// <summary>The name of the error an assertion throws.</summary>
    public const string AssertionError = "AssertionError";

    /// <summary>The prelude: an expression whose value is the assert object.</summary>
    public const string Prelude = """
        (() => {
          class AssertionError extends Error {}
          Object.defineProperty(AssertionError.prototype, 'name', { value: 'AssertionError' });
          const json = value => {
            if (value === undefined) return 'undefined';
            if (typeof value === 'number' && !Number.isFinite(value)) return String(value);
            if (typeof value === 'function') return 'function';
            try { const text = JSON.stringify(value); return text === undefined ? String(value) : text; } catch (e) { return String(value); }
          };
          const fail = (message, text) => {
            throw new AssertionError(message === undefined ? text : String(message) + ': ' + text);
          };
          const same = (a, b) => a === b || (a !== a && b !== b);
          const deep = (a, b) => {
            if (same(a, b)) return true;
            if (typeof a !== 'object' || typeof b !== 'object' || a === null || b === null) return false;
            if (Array.isArray(a) !== Array.isArray(b)) return false;
            if (Array.isArray(a)) return a.length === b.length && a.every((item, i) => deep(item, b[i]));
            const keys = Object.keys(a);
            return keys.length === Object.keys(b).length
              && keys.every(key => Object.prototype.hasOwnProperty.call(b, key) && deep(a[key], b[key]));
          };
          return {
            equal(actual, expected, message) {
              if (!same(actual, expected)) fail(message, 'expected ' + json(expected) + ', got ' + json(actual));
            },
            deepEqual(actual, expected, message) {
              if (!deep(actual, expected)) fail(message, 'expected ' + json(expected) + ', got ' + json(actual));
            },
            ok(value, message) {
              if (!value) fail(message, 'expected a truthy value, got ' + json(value));
            },
            throws(fn, message) {
              if (typeof fn !== 'function') fail(message, 'expected a function, got ' + json(fn));
              try { fn(); } catch (e) { return e instanceof Error ? e.message : String(e); }
              return fail(message, 'expected the function to throw');
            },
            fail(message) {
              throw new AssertionError(message === undefined ? 'failed' : String(message));
            },
          };
        })()
        """;

    /// <summary>Whether a script path is a helper test file: <c>tests/&lt;name&gt;.test.js</c> directly in a pack's tests folder.</summary>
    /// <param name="packRelative">The pack-relative path.</param>
    /// <returns><see langword="true"/> for a test file.</returns>
    public static bool IsTestFile(string packRelative) =>
        packRelative.StartsWith("tests/", StringComparison.Ordinal) && packRelative.IndexOf('/', 6) < 0
        && packRelative.EndsWith(".test.js", StringComparison.Ordinal) && packRelative.Length > "tests/.test.js".Length;
}
