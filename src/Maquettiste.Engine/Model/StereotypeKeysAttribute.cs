namespace Maquettiste.Engine.Model;

/// <summary>
/// Marks a list of stereotype keys (next-phase-1-design.md section 4.2): an element's or a process node's <c>stereotypes</c>, and
/// a stereotype's <c>requires</c>, <c>conflicts</c> and <c>implies</c>. The index resolves each key to the stereotype's id and records
/// it as a reference, so a stereotype a list names cannot be deleted plainly; an unknown key is no reference (validation reports it).
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class StereotypeKeysAttribute : Attribute;
