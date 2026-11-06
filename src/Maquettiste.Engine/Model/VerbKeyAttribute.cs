namespace Maquettiste.Engine.Model;

/// <summary>
/// Marks a verb key (verbs-design.md decision 7): an event's, a transition's or an operation's <c>verb</c>. The index resolves a key of one
/// of the project's own verbs to that verb's id and records it as a reference; a built-in verb's key, or an unknown one, is no reference
/// (validation reports an unknown one, MQ9701).
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class VerbKeyAttribute : Attribute;
