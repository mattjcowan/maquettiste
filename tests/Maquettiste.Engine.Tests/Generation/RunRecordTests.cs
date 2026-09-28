using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Generation;
using Maquettiste.Engine.Loading;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Planning;
using Maquettiste.Engine.Writing;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Generation;

/// <summary>The last-run record's file format, and the unit state store leaving an unchanged state file alone.</summary>
public sealed class RunRecordTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void A_record_round_trips_and_its_outputs_can_be_encoded_in_chunks()
    {
        var record = Sample();
        var bytes = record.Encode();
        Assert.True(RunRecord.Intact(bytes));
        var decoded = RunRecord.Decode(bytes);
        Assert.NotNull(decoded);
        Assert.Equal(record.Key, decoded.Key);
        Assert.Equal(record.UnitsSkipped, decoded.UnitsSkipped);
        Assert.Equal(record.Diagnostics, decoded.Diagnostics);
        Assert.Equal(record.ModelFiles, decoded.ModelFiles);
        Assert.Equal(record.Sidecars, decoded.Sidecars);
        Assert.Equal(record.MissingSidecars, decoded.MissingSidecars);
        Assert.Equal(record.TemplatesHash, decoded.TemplatesHash);
        Assert.Equal(record.EngineFolders.Select(f => f.Folder), decoded.EngineFolders.Select(f => f.Folder));
        Assert.Equal(record.EngineFolders.SelectMany(f => f.Files), decoded.EngineFolders.SelectMany(f => f.Files));
        Assert.Equal(record.CommittedHash, decoded.CommittedHash);
        Assert.Equal(record.Outputs, decoded.Outputs);

        // Outputs split into chunks encode to the same bytes; the inputs decode without them.
        Assert.Equal(bytes, record.Encode([OutputChunk.Of(record.Outputs.Take(1)), OutputChunk.Of(record.Outputs.Skip(1))]));
        var inputs = RunRecord.Decode(bytes, outputs: false);
        Assert.NotNull(inputs);
        Assert.Empty(inputs.Outputs);
        Assert.Equal(record.ModelFiles, inputs.ModelFiles);
        Assert.Equal(record.CommittedHash, inputs.CommittedHash);
    }

    [Fact]
    public void A_record_from_another_engine_build_is_not_used()
    {
        // The contract version stays 1.0.0 across builds that change a validation or resolution rule; the build identity does not.
        var record = Sample();
        var other = record.Encode([OutputChunk.Of(record.Outputs)], build: "another build");
        Assert.True(RunRecord.Intact(other));
        Assert.Null(RunRecord.Decode(other));
        Assert.Null(RunRecord.Decode(other, outputs: false));
        Assert.NotNull(RunRecord.Decode(other, build: "another build"));
        Assert.Null(RunRecord.Decode(record.Encode(), build: "another build"));

        // The identity names this build: the engine's module version id is part of it, and it is stable within a process.
        Assert.Equal(RunRecord.CurrentBuild, RunRecord.CurrentBuild);
        Assert.Matches("^[0-9a-f]{64}$", RunRecord.CurrentBuild);
        Assert.NotEqual(Maquettiste.Engine.Hashing.HashBuilder.Of("mq-build-1", EngineVersion.Value), RunRecord.CurrentBuild);
    }

    [Fact]
    public void A_damaged_truncated_or_foreign_record_is_not_used()
    {
        var bytes = Sample().Encode();
        for (var i = 0; i < bytes.Length; i += 7)
        {
            var damaged = (byte[])bytes.Clone();
            damaged[i] ^= 0x5A;
            Assert.False(RunRecord.Intact(damaged), "byte " + i);
        }

        Assert.Null(RunRecord.Decode(bytes[..^40]));
        Assert.False(RunRecord.Intact(bytes[..^1]));
        Assert.Null(RunRecord.Decode([]));
        Assert.Null(RunRecord.Decode("MQLRxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx"u8.ToArray()));
    }

    [Fact]
    public async Task Saving_the_states_the_store_last_read_leaves_the_file_alone()
    {
        using var repo = new TempRepo();
        var store = new UnitStateStore(repo.Options, new OutputPathPolicy(repo.Options, null));
        UnitState[] states = [new("p/a", "h1", ["e:1", "k:entity"], [new UnitOutput("out/a.txt", "m1", 3, 4)]), new("p/b", "h2", ["e:2"], [])];
        await store.SaveAsync("p", states, Ct);
        var file = store.FileOf("p");
        var past = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(file, past);

        var loaded = await store.LoadAsync("p", Ct);
        await store.SaveAsync("p", [.. loaded.Values], Ct);
        Assert.Equal(past, File.GetLastWriteTimeUtc(file));
        Assert.Equal(loaded.Keys.Order(StringComparer.Ordinal), store.Remembered("p")!.Keys.Order(StringComparer.Ordinal));

        // Other states, or the same bytes after the file changed length behind the store, are written.
        await store.SaveAsync("p", [states[0]], Ct);
        Assert.NotEqual(past, File.GetLastWriteTimeUtc(file));
        var one = UnitStateStore.Encode([states[0]]);
        Assert.Equal(one, await File.ReadAllBytesAsync(file, Ct));
        await File.WriteAllBytesAsync(file, [.. one, 0], Ct);
        File.SetLastWriteTimeUtc(file, past);
        await store.SaveAsync("p", [states[0]], Ct);
        Assert.Equal(one, await File.ReadAllBytesAsync(file, Ct));
    }

    private static RunRecord Sample() => new(
        "key",
        3,
        [
            new Diagnostic("MQ1003", DiagnosticSeverity.Warning, "Not canonical.", "01J92P0V0ETQKXXP951CMMNHH3", ".maquettiste/model/a.json", "/name", 2, 5),
            new Diagnostic("x/rule", DiagnosticSeverity.Info, "Note.", null, null, null, null, null),
        ],
        [new FileStamp("maquettiste.json", 10, 20), new FileStamp("model/entities/é.json", 11, 21)],
        [new FileStamp("model/entities/notes.md", 5, 6)],
        ["model/entities/missing.md"],
        "templates-hash",
        [new FolderStamp("/cache/units", [new FileStamp("e2e.v1.bin", 100, 200)]), new FolderStamp("/cache/manifest", [])],
        "committed-hash",
        [new OutputStamp("db/a.sql", false, 12, 34), new OutputStamp("src/b.cs", true, 0, 0), new OutputStamp("src/c.cs", false, 1, 2)]);
}
