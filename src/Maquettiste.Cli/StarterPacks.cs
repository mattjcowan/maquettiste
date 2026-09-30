using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;

namespace Maquettiste.Cli;

/// <summary>
/// The starter packs for <c>init</c> and <c>pack new --from</c>: the example packs embedded from <c>packs/sql-ddl</c> and
/// <c>packs/csharp-dapper</c> as <c>Maquettiste.Cli.Packs/&lt;pack&gt;/&lt;path&gt;</c>. While a pack folder is empty in this build (the
/// example packs are W10's), a small built-in version of the pack stands in, so <c>init</c> always leaves a working pack.
/// </summary>
internal static class StarterPacks
{
    /// <summary>
    /// The three standard reference-data storage strategies the sql-ddl starter implements (packs/sql-ddl/README.md), which
    /// <c>init --pack sql-ddl</c> declares under <c>referenceData.strategies</c>: the engine gives none of these names a meaning, the
    /// declaration is the project's, and the editor's Settings › Reference data offers the same set in one click.
    /// </summary>
    /// <returns>A new <c>strategies</c> object.</returns>
    public static JsonObject StandardReferenceStrategies() => new()
    {
        ["lookup-table"] = new JsonObject
        {
            ["description"] = "Table keyed by code, FK from each column",
            ["collections"] = true,
            ["options"] = new JsonObject
            {
                ["schema"] = new JsonObject { ["type"] = "string" },
                ["tableName"] = new JsonObject { ["type"] = "string" },
            },
        },
        ["check"] = new JsonObject { ["description"] = "CHECK (col IN (...codes))" },
        ["native"] = new JsonObject
        {
            ["description"] = "CREATE TYPE ... AS ENUM on PostgreSQL",
            ["collections"] = new JsonObject { ["*"] = false, ["postgresql"] = true },
        },
    };

    /// <summary>The embedded resource prefix.</summary>
    public const string ResourcePrefix = "Maquettiste.Cli.Packs/";

    /// <summary>The pack names <c>init --pack</c> and <c>pack new --from</c> accept, besides <c>none</c> and <c>empty</c>.</summary>
    public static readonly IReadOnlyList<string> Names = ["sql-ddl", "csharp-dapper"];

    /// <summary>The output folder each starter pack gets in <c>maquettiste.json</c>, matching the default output roots.</summary>
    /// <param name="pack">The pack name.</param>
    /// <returns>The folder.</returns>
    public static string OutputFolder(string pack) => pack == "csharp-dapper" ? "src/Generated" : "db";

    /// <summary>Returns a starter pack's files: pack-relative path (with <c>/</c>) to bytes, ordinal by path.</summary>
    /// <param name="pack">The pack name.</param>
    /// <param name="embedded">Whether the files came from the embedded example pack (else the built-in fallback).</param>
    /// <returns>The files.</returns>
    public static IReadOnlyList<KeyValuePair<string, byte[]>> Files(string pack, out bool embedded)
    {
        var assembly = typeof(StarterPacks).Assembly;
        var prefix = ResourcePrefix + pack + "/";
        var files = new List<KeyValuePair<string, byte[]>>();
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith(prefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            var relative = name[prefix.Length..].Replace('\\', '/');
            files.Add(new(relative, Read(assembly, name)));
        }

        embedded = files.Count > 0;
        if (!embedded)
            files.AddRange(Fallback(pack).Select(f => new KeyValuePair<string, byte[]>(f.Key, Encoding.UTF8.GetBytes(f.Value))));
        return [.. files.OrderBy(f => f.Key, StringComparer.Ordinal)];
    }

    /// <summary>The files of an empty pack for <c>pack new</c>: one template, a helpers file; the pack.json is written by the caller.</summary>
    /// <returns>Pack-relative path to text.</returns>
    public static IReadOnlyList<KeyValuePair<string, string>> Empty() =>
    [
        new("entity.scriban", """
            {{ banner "//" }}
            // {{ entity.name }} ({{ entity.attributes.size }} attributes): {{ shout entity.name }}
            {{- for a in entity.attributes }}
            //   {{ a.name }}
            {{- end }}

            """),
        new("helpers.js", """
            // Pack helpers, callable from templates. Helpers must be pure: no state kept between calls (D12).
            maquettiste.helper("shout", (text) => String(text).toUpperCase());

            """),
    ];

    /// <summary>The built-in stand-ins for the example packs.</summary>
    private static IReadOnlyList<KeyValuePair<string, string>> Fallback(string pack) => pack switch
    {
        "sql-ddl" =>
        [
            new("pack.json", """
                {
                  "name": "sql-ddl",
                  "version": "0.1.0",
                  "engine": ">=1.0 <2.0",
                  "description": "Starter pack: one CREATE TABLE script per table.",
                  "units": [
                    {
                      "id": "table",
                      "template": "table.scriban",
                      "for": "each table",
                      "output": "{{ kebab table.database.name }}/{{ if table.schema }}{{ table.schema }}/{{ end }}{{ table.name }}.sql"
                    }
                  ]
                }

                """),
            new("table.scriban", """
                {{ banner "--" }}
                CREATE TABLE {{ if table.schema }}{{ sql_quote table.schema table.database }}.{{ end }}{{ sql_quote table }} (
                {{- for c in table.columns }}
                    {{ sql_quote c }} {{ type_of c table.database.dialect }}{{ if !c.nullable }} NOT NULL{{ end }}{{ if !for.last || table.primary_key }},{{ end }}
                {{- end }}
                {{- if table.primary_key }}
                    CONSTRAINT {{ sql_quote table.primary_key.name table.database }} PRIMARY KEY ({{ for c in table.primary_key.columns }}{{ sql_quote c }}{{ if !for.last }}, {{ end }}{{ end }})
                {{- end }}
                );

                """),
        ],
        "csharp-dapper" =>
        [
            new("pack.json", """
                {
                  "name": "csharp-dapper",
                  "version": "0.1.0",
                  "engine": ">=1.0 <2.0",
                  "description": "Starter pack: one partial C# record per entity and value object, one C# enum per enum.",
                  "parameters": {
                    "namespace": "App.Model"
                  },
                  "units": [
                    {
                      "id": "entity",
                      "template": "entity.scriban",
                      "for": "each entity",
                      "output": "{{ if entity.package }}{{ pascal entity.package.name }}/{{ end }}{{ pascal entity.name }}.g.cs"
                    },
                    {
                      "id": "value-object",
                      "template": "value-object.scriban",
                      "for": "each value object",
                      "output": "{{ if value_object.package }}{{ pascal value_object.package.name }}/{{ end }}{{ pascal value_object.name }}.g.cs"
                    },
                    {
                      "id": "enum",
                      "template": "enum.scriban",
                      "for": "each enum",
                      "output": "{{ if enum.package }}{{ pascal enum.package.name }}/{{ end }}{{ pascal enum.name }}.g.cs"
                    }
                  ]
                }

                """),
            new("entity.scriban", """
                {{ banner "//" }}
                #nullable enable
                {{ include "usings.scriban" -}}
                namespace {{ pack.params.namespace }}{{ if entity.package }}.{{ pascal entity.package.name }}{{ end }};

                public partial record {{ pascal entity.name }}
                {
                {{- for a in entity.attributes }}
                    {{ include "property.scriban" }}
                {{- end }}
                }

                """),
            new("enum.scriban", """
                {{ banner "//" }}
                namespace {{ pack.params.namespace }}{{ if enum.package }}.{{ pascal enum.package.name }}{{ end }};

                {{ if enum.flags }}[System.Flags]
                {{ end }}public enum {{ pascal enum.name }}
                {
                {{- for m in enum.members }}
                    {{ pascal m.name }}{{ if m.value != null }} = {{ m.value }}{{ end }},
                {{- end }}
                }

                """),
            new("property.scriban", """
                public {{ if a.required }}required {{ end }}{{ type_of a "csharp" }} {{ pascal a.name }} { get; init; }{{ if a.collection && !a.required }} = [];{{ end }}
                """),
            new("usings.scriban", """
                {{ for p in model.packages }}using {{ pack.params.namespace }}.{{ pascal p.name }};
                {{ end }}{{ if model.packages.size > 0 }}
                {{ end }}
                """),
            new("value-object.scriban", """
                {{ banner "//" }}
                #nullable enable
                {{ include "usings.scriban" -}}
                namespace {{ pack.params.namespace }}{{ if value_object.package }}.{{ pascal value_object.package.name }}{{ end }};

                public partial record {{ pascal value_object.name }}
                {
                {{- for a in value_object.attributes }}
                    {{ include "property.scriban" }}
                {{- end }}
                }

                """),
            new("types/csharp.json", """
                {
                  "binary": "byte[]",
                  "bool": "bool",
                  "collection": "IReadOnlyList<{type}>",
                  "date": "DateOnly",
                  "datetime": "DateTime",
                  "datetimeoffset": "DateTimeOffset",
                  "decimal": "decimal",
                  "double": "double",
                  "duration": "TimeSpan",
                  "float": "float",
                  "int16": "short",
                  "int32": "int",
                  "int64": "long",
                  "json": "System.Text.Json.JsonElement",
                  "nullable": "{type}?",
                  "string": "string",
                  "text": "string",
                  "time": "TimeOnly",
                  "ulid": "string",
                  "uuid": "Guid"
                }

                """),
        ],
        _ => throw new UsageException($"Unknown starter pack '{pack}'."),
    };

    private static byte[] Read(Assembly assembly, string name)
    {
        using var stream = assembly.GetManifestResourceStream(name) ?? throw new InvalidOperationException("Missing resource " + name);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
