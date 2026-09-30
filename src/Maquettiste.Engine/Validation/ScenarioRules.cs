using Maquettiste.Engine.Model;
using Maquettiste.Engine.Processes;

namespace Maquettiste.Engine.Validation;

/// <summary>
/// The rules that run the engine interpreter or parse expressions (phase-3-design.md section 3): MQ9501 on a process (every guard and
/// action expression parses, without running), and MQ9301 to MQ9306 with MQ9502 to MQ9507 on a scenario (a replay of the scenario
/// against its process).
/// </summary>
internal static class ScenarioRules
{
    /// <summary>Runs the rules that belong to one element file.</summary>
    /// <param name="context">The validation context.</param>
    /// <param name="report">The report of the document to validate.</param>
    /// <param name="runtime">The validation's process runtime; <see langword="null"/> when no scenario is in scope.</param>
    public static void Validate(ValidationContext context, Report report, ProcessRuntime? runtime)
    {
        switch (report.Document.Element)
        {
            case Process process:
                foreach (var problem in ProcessExpressions.Get(process).Problems)
                {
                    report.Add("MQ9501", $"The expression of {problem.Kind} '{problem.Name}' does not parse: {problem.Message}; fix its syntax, or remove the expression to make the {problem.Kind} a stub.",
                        problem.Pointer, problem.Id);
                }

                break;
            case Scenario scenario when runtime is not null:
                if (ScenarioReplayer.Replay(scenario, runtime) is not { } replay)
                    break;
                foreach (var d in replay.Diagnostics)
                    report.Add(d with { FilePath = report.Document.Path });
                break;
        }
    }
}
