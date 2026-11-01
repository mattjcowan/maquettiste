using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Tests.Store;

/// <summary><see cref="ModelStore.PatchAsync"/>: an RFC 6902 patch saved as a one-operation batch update (next-phase-1-design.md section 5.2).</summary>
public sealed class ModelStorePatchTests
{
    private const string NotesTable = "01K6BND0000000000000000001";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_patch_saves_through_the_conflict_check_and_mints_a_new_columns_id()
    {
        await using var s = await BillingStore.OpenAsync(validate: true);
        var notes = s.Model.GetDocument(NotesTable)!;
        var patch = """
            [ { "op": "test", "path": "/columns/3/name", "value": "body" },
              { "op": "add", "path": "/columns/-", "value": { "name": "pinned", "type": "bool", "nullable": false } } ]
            """u8.ToArray();

        var saved = await s.Store.PatchAsync(NotesTable, patch, notes.Hash, ChangeSource.Cli, Ct);

        Assert.True(saved.Outcome == SaveOutcome.Saved, string.Join("; ", saved.Items.SelectMany(i => i.Diagnostics).Select(d => $"{d.Rule} {d.JsonPointer} {d.Message}")));
        var item = Assert.Single(saved.Items);
        var minted = Assert.Single(saved.Ids.Minted);
        Assert.Equal((0, "/columns/5", "column"), (minted.Operation, minted.Pointer, minted.Kind));
        var table = s.Model.Get<Table>(NotesTable)!;
        Assert.Equal(("pinned", minted.Id), (table.Columns[5].Name, table.Columns[5].Id));
        Assert.Equal(s.Model.GetDocument(NotesTable)!.Hash, item.Hash);
        Assert.NotEqual(notes.Hash, item.Hash);
        Assert.NotNull(saved.Facts);

        // The same patch with the hash read before the save: a conflict carrying the disk version, and nothing is written.
        var files = s.Files();
        var conflict = await s.Store.PatchAsync(NotesTable, patch, notes.Hash, ChangeSource.Cli, Ct);
        Assert.Equal(SaveOutcome.Conflict, conflict.Outcome);
        var refused = Assert.Single(conflict.Items);
        Assert.Equal((item.Hash, item.Hash), (refused.Hash, refused.Current!.Hash));
        Assert.Empty(conflict.Ids.Minted);
        Assert.Equal(files, s.Files());

        // A failed test refuses the patch with MQ1016 at the operation; nothing is written.
        var failed = await s.Store.PatchAsync(NotesTable, """[ { "op": "test", "path": "/name", "value": "memos" } ]"""u8.ToArray(), item.Hash!, ChangeSource.Cli, Ct);
        Assert.Equal(SaveOutcome.Invalid, failed.Outcome);
        var error = Assert.Single(Assert.Single(failed.Items).Diagnostics);
        Assert.Equal(("MQ1016", "/0", NotesTable), (error.Rule, error.JsonPointer, error.ElementId));
        Assert.Equal(files, s.Files());

        // A patch whose result is invalid is refused like a save, with the planner's diagnostics.
        var broken = await s.Store.PatchAsync(NotesTable, """[ { "op": "replace", "path": "/database", "value": "01J92P0V0000000000000000ZZ" } ]"""u8.ToArray(),
            item.Hash!, ChangeSource.Cli, Ct);
        Assert.Equal(SaveOutcome.Invalid, broken.Outcome);
        Assert.NotEmpty(broken.Items[0].Diagnostics);
        Assert.Equal(files, s.Files());
    }

    [Fact]
    public async Task A_patch_that_renames_a_column_carries_its_facts()
    {
        await using var s = await BillingStore.OpenAsync(validate: true);
        var notes = s.Model.GetDocument(NotesTable)!;

        var saved = await s.Store.PatchAsync(NotesTable, """[ { "op": "replace", "path": "/columns/3/name", "value": "content" } ]"""u8.ToArray(),
            notes.Hash, ChangeSource.Cli, Ct);

        Assert.True(saved.Outcome == SaveOutcome.Saved, string.Join("; ", saved.Items.SelectMany(i => i.Diagnostics).Select(d => $"{d.Rule} {d.JsonPointer} {d.Message}")));
        var fact = Assert.Single(saved.Facts!);
        Assert.Equal(("rename", "complete", "rename column billing.notes.body to content"), (fact.Fact, fact.State, fact.Summary));
        Assert.True(s.Model.GetDocument(fact.Id) is { Element: Migration });
        Assert.Null(Assert.Single(saved.Items).Facts); // a batch item carries none; the batch does
        Assert.Equal("content", s.Model.Get<Table>(NotesTable)!.Columns[3].Name);
        Assert.Empty(saved.Ids.Minted);
    }

    [Fact]
    public async Task A_patch_reports_name_failures_at_document_pointers()
    {
        await using var s = await BillingStore.OpenAsync(validate: true);
        var notes = s.Model.GetDocument(NotesTable)!;
        var files = s.Files();

        // A name inside the patch that resolves to nothing is refused by the batch's resolver; its pointer is in the document, not
        // under /operations/0/element, so patch_element answers in the patch (MQ1016) or in the document only.
        var result = await s.Store.PatchAsync(NotesTable, """
            [ { "op": "add", "path": "/columns/-", "value": { "name": "pinned", "type": "bool", "nullable": false } },
              { "op": "replace", "path": "/database", "value": "$nowhere" } ]
            """u8.ToArray(), notes.Hash, ChangeSource.Cli, Ct);

        Assert.Equal(SaveOutcome.Invalid, result.Outcome);
        var error = Assert.Single(Assert.Single(result.Items).Diagnostics);
        Assert.Equal(("MQ1012", "/database"), (error.Rule, error.JsonPointer));
        Assert.Equal(files, s.Files());
    }

    [Fact]
    public async Task A_patch_by_name_is_not_found()
    {
        await using var s = await BillingStore.OpenAsync(validate: true);
        var notes = s.Model.GetDocument(NotesTable)!;
        var files = s.Files();
        var patch = """[ { "op": "replace", "path": "/description", "value": "Patched." } ]"""u8.ToArray();

        // The target is an id, never a name: a qualified name, a missing id and a sub-element id are all not found.
        foreach (var id in new[] { "@table:billing.notes", "01K6BND00000000000000000ZZ", "01K6BND0000000000000000005" })
        {
            var result = await s.Store.PatchAsync(id, patch, notes.Hash, ChangeSource.Cli, Ct);
            Assert.Equal(SaveOutcome.NotFound, result.Outcome);
            Assert.Equal((SaveOutcome.NotFound, id), (Assert.Single(result.Items).Outcome, result.Items[0].Id));
        }

        Assert.Equal(files, s.Files());
        Assert.Equal(SaveOutcome.Saved, (await s.Store.PatchAsync(NotesTable, patch, notes.Hash, ChangeSource.Cli, Ct)).Outcome);
    }
}
