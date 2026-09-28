using System.Text.Json;

namespace Maquettiste.Engine.Model;

/// <summary>One loaded model file.</summary>
/// <param name="Element">The deserialized element.</param>
/// <param name="Path">The repo-relative path with <c>/</c> separators, for example <c>.maquettiste/model/entities/invoice.json</c>.</param>
/// <param name="Hash">The SHA-256 of the file bytes, lowercase hex: the element's ETag (host-contracts requirement 11).</param>
/// <param name="DependencyHash"><c>H(Hash, sidecar hash)</c>: what <c>e:&lt;id&gt;</c> dependency keys hash to.</param>
/// <param name="Json">The parsed file, as read.</param>
/// <param name="SidecarText">The sidecar description text, when the description is a file reference that was found.</param>
public sealed record ElementDocument(Element Element, string Path, string Hash, string DependencyHash, JsonElement Json, string? SidecarText);

/// <summary>An entry in the id index: an element or a sub-element.</summary>
/// <param name="Id">The id.</param>
/// <param name="OwnerId">The id of the top-level element whose file holds it (equal to <paramref name="Id"/> for an element).</param>
/// <param name="Kind">An element kind name, or <c>attribute</c>, <c>enum-member</c>, <c>end</c>, <c>column</c>, <c>category</c>, <c>schema</c> or <c>key</c>.</param>
/// <param name="JsonPointer">The pointer to the object in its owner's file (empty for an element).</param>
public sealed record IndexEntry(string Id, string OwnerId, string Kind, string JsonPointer);

/// <summary>One reference from a model file to an id.</summary>
/// <param name="FromElementId">The id of the top-level element whose file holds the reference.</param>
/// <param name="FromId">The id of the innermost element or sub-element holding the reference.</param>
/// <param name="JsonPointer">The pointer to the reference value (including the list index for list references).</param>
/// <param name="Field">The JSON property name holding the reference.</param>
/// <param name="ToId">The referenced id.</param>
public sealed record ReferenceInfo(string FromElementId, string FromId, string JsonPointer, string Field, string ToId);

/// <summary>A summary of one element, for explorers and indexes.</summary>
/// <param name="Id">The element id.</param>
/// <param name="Kind">The kind name.</param>
/// <param name="Name">The element name.</param>
/// <param name="Package">The id of the owning (or, for a package, parent) package, when it has one.</param>
/// <param name="Tags">The element's tag keys.</param>
/// <param name="Hash">The file hash (ETag).</param>
/// <param name="Path">The repo-relative file path.</param>
public sealed record ElementSummary(string Id, string Kind, string Name, string? Package, IReadOnlyList<string> Tags, string Hash, string Path);

/// <summary>A loaded extension schema file.</summary>
/// <param name="Schema">The extension.</param>
/// <param name="Path">The repo-relative file path.</param>
/// <param name="Hash">The file hash.</param>
public sealed record ExtensionDocument(ExtensionSchema Schema, string Path, string Hash);
