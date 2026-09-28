using Maquettiste.Engine.Resolution;

namespace Maquettiste.Engine.Tests.SchemaDiff;

/// <summary>Builds resolved physical objects directly (the resolver is another workstream); the setters are internal.</summary>
internal static class PhysicalBuilder
{
    public const string DatabaseId = "01JB2Q0M8X4T5V6W7Y8Z9A0B1C";

    public static RDatabase Database(string name, IEnumerable<RTable> tables, IEnumerable<RView>? views = null, IEnumerable<RSequence>? sequences = null,
        string dialect = "postgresql")
    {
        var database = new RDatabase { Id = DatabaseId, Name = name, Dialect = dialect };
        var tableList = tables.ToList();
        var viewList = (views ?? []).ToList();
        foreach (var table in tableList)
            table.Database = database;
        foreach (var view in viewList)
            view.Database = database;
        database.Tables = new RList<RTable>(tableList, ["k:table"]);
        database.Views = new RList<RView>(viewList, ["k:view"]);
        database.Sequences = new RList<RSequence>(sequences ?? [], ["k:sequence"]);
        return database;
    }

    public static RTable Table(string key, string name, params RColumn[] columns)
    {
        var table = new RTable { Id = key, Key = key, Name = name, Schema = "public" };
        for (var i = 0; i < columns.Length; i++)
        {
            columns[i].Table = table;
            columns[i].Position = i;
        }

        table.Columns = new RList<RColumn>(columns, []);
        table.PrimaryKey = columns.Length > 0 ? new RPrimaryKey { Name = "pk_" + name, Columns = [columns[0]] } : null;
        return table;
    }

    public static RColumn Column(string key, string name, string type = "int64", bool nullable = false) =>
        new() { Id = key, Key = key, Name = name, Type = type, NativeType = type == "int64" ? "bigint" : "text", Nullable = nullable };

    public static RColumn Col(this RTable table, string key) => table.Columns.Single(c => c.Key == key);

    public static RForeignKey ForeignKey(RTable from, string column, RTable to, string? name = null) => new()
    {
        Name = name ?? "fk_" + from.Name + "_" + to.Name,
        Columns = [from.Col(column)],
        ReferencedTable = to,
        ReferencedColumns = [to.Columns[0]],
    };

    public static RView View(string id, string name, string body) => new() { Id = id, Name = name, Schema = "public", Body = body };

    public static RSequence Sequence(string id, string name, long start = 1) => new() { Id = id, Name = name, Schema = "public", Start = start, NativeType = "bigint" };
}
