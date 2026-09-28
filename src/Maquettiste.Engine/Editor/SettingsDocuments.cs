using System.Text.Json;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine;

/// <summary><c>maquettiste.json</c> as the editor's Settings workspace reads it (E3, phase2-design.md section 3.8).</summary>
/// <param name="Settings">The typed settings the loaded snapshot uses.</param>
/// <param name="Path">The repo-relative path (<c>.maquettiste/maquettiste.json</c>).</param>
/// <param name="Hash">The SHA-256 of the file bytes, lowercase hex: the ETag a save sends back (the hash of no bytes when the file is
/// missing).</param>
/// <param name="Json">The file as read; when it is missing or does not parse, the canonical form of <paramref name="Settings"/>.</param>
public sealed record SettingsDocument(ProjectSettings Settings, string Path, string Hash, JsonElement Json);

/// <summary>The result of <see cref="ModelStore.SaveSettingsAsync"/> (E3).</summary>
/// <param name="Outcome"><see cref="SaveOutcome.Saved"/>, <see cref="SaveOutcome.Conflict"/> (the file changed since it was loaded) or
/// <see cref="SaveOutcome.Invalid"/> (nothing was written).</param>
/// <param name="Hash">The new hash when saved; the disk hash on conflict.</param>
/// <param name="Current">The settings now on disk: the saved document, or the disk version on conflict.</param>
/// <param name="Diagnostics">Why the save is invalid, or the warnings and infos it introduced.</param>
public sealed record SettingsSaveResult(SaveOutcome Outcome, string? Hash, SettingsDocument? Current, IReadOnlyList<Diagnostic> Diagnostics);
