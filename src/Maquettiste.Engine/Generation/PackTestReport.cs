using System.Text.Json.Serialization;
using Maquettiste.Engine.Diagnostics;

namespace Maquettiste.Engine;

/// <summary>The result of testing packs (<see cref="GenerationService.TestPackAsync"/>, next-phase-2-design.md section 5.4).</summary>
/// <param name="Packs">Each tested pack, ordinal by name.</param>
/// <param name="Passed">Cases and helper tests that passed.</param>
/// <param name="Failed">Cases and helper tests that failed or errored, plus each pack that did not load and each test file that is
/// malformed.</param>
/// <param name="Updated">Cases whose expected tree was rewritten.</param>
public sealed record PackTestReport(IReadOnlyList<PackTestResult> Packs, int Passed, int Failed, int Updated);

/// <summary>The tests of one pack.</summary>
/// <param name="Pack">The pack name.</param>
/// <param name="Cases">Its cases, ordinal by name.</param>
/// <param name="Tests">Its helper tests: test files in ordinal order, tests in registration order.</param>
/// <param name="Diagnostics">What is not one case's: a load error of the pack, MQ6053 for a malformed test file, MQ6052 for each
/// failing helper test.</param>
public sealed record PackTestResult(string Pack, IReadOnlyList<PackCaseResult> Cases, IReadOnlyList<PackHelperTestResult> Tests,
    IReadOnlyList<Diagnostic> Diagnostics);

/// <summary>One test case of a pack: a small project and the files it must produce.</summary>
/// <param name="Name">The case folder's name.</param>
/// <param name="Description">The <c>case.json</c> description, if any.</param>
/// <param name="Outcome">The outcome.</param>
/// <param name="Files">The files that differ from <c>expected/</c> (or that an update rewrote), ordinal by path.</param>
/// <param name="Diagnostics">MQ6051 when the output differs, MQ6053 when the case is malformed, or the diagnostics of a run that
/// did not succeed.</param>
public sealed record PackCaseResult(string Name, string? Description, PackTestOutcome Outcome, IReadOnlyList<PackCaseFile> Files,
    IReadOnlyList<Diagnostic> Diagnostics);

/// <summary>One file of a case's output that differs from its expected tree.</summary>
/// <param name="Path">The path under the output folder (and under <c>expected/</c>), <c>/</c> separators.</param>
/// <param name="Change">How it differs.</param>
/// <param name="Diff">A unified diff for <c>differs</c> on text files; null otherwise.</param>
public sealed record PackCaseFile(string Path, PackCaseFileChange Change, string? Diff);

/// <summary>One helper test: a <c>maquettiste.test(name, fn)</c> of a <c>tests/*.test.js</c> file.</summary>
/// <param name="File">The test file, pack-relative (<c>tests/helpers.test.js</c>).</param>
/// <param name="Name">The test name.</param>
/// <param name="Outcome">The outcome.</param>
/// <param name="Message">Why it failed; null when it passed.</param>
/// <param name="Line">The line of the failing statement, if known.</param>
/// <param name="Column">The column of the failing statement, if known.</param>
public sealed record PackHelperTestResult(string File, string Name, PackTestOutcome Outcome, string? Message, int? Line, int? Column);

/// <summary>The outcome of a case or a helper test.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PackTestOutcome>))]
public enum PackTestOutcome
{
    /// <summary>The output equals the expected tree, or the test's assertions held: <c>passed</c>.</summary>
    [JsonStringEnumMemberName("passed")] Passed,

    /// <summary>The output differs (MQ6051), or an assertion failed (MQ6052): <c>failed</c>.</summary>
    [JsonStringEnumMemberName("failed")] Failed,

    /// <summary>The expected tree was rewritten from the output: <c>updated</c>.</summary>
    [JsonStringEnumMemberName("updated")] Updated,

    /// <summary>The case's run did not succeed, or the case or test file is malformed (MQ6053): <c>error</c>.</summary>
    [JsonStringEnumMemberName("error")] Error,
}

/// <summary>How a file of a case's output differs from its expected tree.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PackCaseFileChange>))]
public enum PackCaseFileChange
{
    /// <summary>Produced, not expected: <c>added</c>.</summary>
    [JsonStringEnumMemberName("added")] Added,

    /// <summary>Expected, not produced: <c>missing</c>.</summary>
    [JsonStringEnumMemberName("missing")] Missing,

    /// <summary>Produced with other bytes: <c>differs</c>.</summary>
    [JsonStringEnumMemberName("differs")] Differs,
}
