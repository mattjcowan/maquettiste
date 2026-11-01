using System.Text.Json.Nodes;

namespace Maquettiste.Cli.Tests;

/// <summary><c>maquettiste migration list|fold</c> (next-phase-1-design.md section 3.2).</summary>
public sealed class MigrationTests
{
    private const string MainDatabaseId = "01J92P0V1QRN2181XM2ZWE02W4";
    private const string NotesTableId = "01K6BND0000000000000000001";
    private const string NotesBodyColumnId = "01K6BND0000000000000000005";
    private const string First = "01K9MGR0000000000000000001";
    private const string Second = "01K9MGR0000000000000000002";
    private const string Folder = ".maquettiste/model/databases/main/migrations/open/";

    [Fact]
    public async Task Migration_list_with_an_unknown_database_exits_1()
    {
        using var repo = CliRepo.Billing();
        var none = await repo.RunAsync("migration", "list");
        Assert.True(none.ExitCode == 0, none.ToString());
        Assert.Equal("", none.Out);

        // Two renames of the notes body column, text to content to body (the name the model shows), as a merge would leave them.
        WriteFacts(repo);
        var text = await repo.RunAsync("migration", "list");
        Assert.True(text.ExitCode == 0, text.ToString());
        Assert.Equal(
            [
                $"main 1 {First} rename complete: rename column billing.notes.text to content",
                $"main 2 {Second} rename complete: rename column billing.notes.content to body",
            ],
            Text.Lines(text.Out));
        var json = JsonNode.Parse((await repo.RunAsync("migration", "list", "--database", MainDatabaseId, "--format", "json")).Out)!;
        Assert.Equal(2, (int)json["total"]!);
        Assert.Equal([First, Second], json["facts"]!.AsArray().Select(f => (string)f!["id"]!));
        Assert.Equal([1, 2], json["facts"]!.AsArray().Select(f => (int)f!["order"]!));

        var unknown = await repo.RunAsync("migration", "list", "--database", "nowhere");
        Assert.Equal(1, unknown.ExitCode);
        Assert.Contains("nowhere", unknown.Error, StringComparison.Ordinal);
        Assert.Equal(4, (await repo.RunAsync("migration")).ExitCode);
        Assert.Equal(4, (await repo.RunAsync("migration", "squash")).ExitCode);
    }

    [Fact]
    public async Task Migration_fold_joins_the_facts_a_baseline_lacks()
    {
        using var repo = CliRepo.Billing();
        var id = (string)JsonNode.Parse((await repo.RunAsync("snapshot", "create", "Base", "--format", "json")).Out)!["id"]!;
        WriteFacts(repo);
        var first = repo.Read(Folder + First.ToLowerInvariant() + ".json");

        // Without a baseline or a committed snapshot, only pending facts are folded: these are complete.
        var plain = await repo.RunAsync("migration", "fold");
        Assert.True(plain.ExitCode == 0, plain.ToString());
        Assert.Equal("", plain.Out);

        var dry = await repo.RunAsync("migration", "fold", "--against", id, "--dry-run");
        Assert.True(dry.ExitCode == 0, dry.ToString());
        Assert.Equal(
            [
                $"joined rename {First} into {Second}",
                "D " + Folder + First.ToLowerInvariant() + ".json",
                "M " + Folder + Second.ToLowerInvariant() + ".json",
            ],
            Text.Lines(dry.Out));
        Assert.Equal(first, repo.Read(Folder + First.ToLowerInvariant() + ".json"));

        var folded = await repo.RunAsync("migration", "fold", "--against", id, "--format", "json");
        Assert.True(folded.ExitCode == 0, folded.ToString());
        var result = JsonNode.Parse(folded.Out)!;
        Assert.Equal("saved", (string)result["outcome"]!);
        Assert.Equal(Second, (string)result["joined"]![0]!["kept"]!);
        Assert.False(File.Exists(repo.PathOf(Folder + First.ToLowerInvariant() + ".json")));
        var kept = JsonNode.Parse(repo.Read(Folder + Second.ToLowerInvariant() + ".json"))!;
        Assert.Equal(("text", "body", "content"), ((string)kept["from"]!["name"]!, (string)kept["to"]!["name"]!, (string)kept["via"]![0]!));

        Assert.Equal(4, (await repo.RunAsync("migration", "fold", "--against", "no-such-snapshot")).ExitCode);
        Assert.Equal(1, (await repo.RunAsync("migration", "fold", "--database", "nowhere")).ExitCode);
    }

    private static void WriteFacts(CliRepo repo)
    {
        repo.Write(Folder + First.ToLowerInvariant() + ".json", Rename(First, "text", "content", null));
        repo.Write(Folder + Second.ToLowerInvariant() + ".json", Rename(Second, "content", "body", First));
    }

    private static string Rename(string id, string from, string to, string? after) => $$"""
        {
          "$schema": "../../../../../.schema/v1/migration.json",
          "kind": "migration",
          "id": "{{id}}",
          "database": "{{MainDatabaseId}}",
          "fact": "rename",
        """ + (after is null ? "" : $$"""

          "after": [
            "{{after}}"
          ],
        """) + $$"""

          "object": {
            "kind": "column",
            "table": "{{NotesTableId}}",
            "key": "{{NotesBodyColumnId}}",
            "schema": "billing",
            "tableName": "notes",
            "name": "{{from}}"
          },
          "from": {
            "name": "{{from}}"
          },
          "to": {
            "name": "{{to}}"
          }
        }

        """;
}
