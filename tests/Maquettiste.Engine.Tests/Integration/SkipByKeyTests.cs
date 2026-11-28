using Maquettiste.Engine.Planning;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Integration;

/// <summary>
/// The skip check by key (change-driven-edits-design.md section 4, step 3): what it trusts from the previous run, and what it still
/// checks. Outputs are checked for every unit whose inputs are unchanged, accepted by key or not (ruling R1 is open), and states the
/// process did not compute itself (a new host, a state file rewritten behind it) take the full check once.
/// </summary>
[Collection(IntegrationCollection.Name)]
public sealed class SkipByKeyTests
{
    [Fact]
    public async Task Units_are_accepted_by_key_only_from_states_this_host_computed_and_their_outputs_are_still_checked()
    {
        await using var repo = E2ERepo.Create();
        var cold = await repo.ApplyAsync();
        var units = cold.UnitsRendered;

        // Every unit the cold run rendered is current under its hashes: the next run accepts them all by key.
        var again = await repo.ApplyAsync();
        Assert.Equal(0, again.UnitsRendered);
        Assert.Equal(units, Stats(repo).ByKey);
        Assert.Equal(0, Stats(repo).Checked);

        // A deleted output is still found, though its unit's inputs are accepted by key.
        File.Delete(repo.Repo.PathOf("db/e2e/index.txt"));
        var restored = await repo.ApplyAsync();
        Assert.Equal(1, restored.UnitsRendered);
        Assert.True(repo.Repo.Exists("db/e2e/index.txt"));
        Assert.Equal(units, Stats(repo).ByKey);
        Assert.True(Stats(repo).OutputsChecked >= units - 1, Stats(repo).ToString());

        // An edit: the units that read the edited entity take the full check, the rest are accepted by key.
        await repo.WidenCustomerNameAsync();
        var edited = await repo.ApplyAsync();
        Assert.True(edited.UnitsRendered > 0);
        Assert.Equal(units - Stats(repo).ByKey, Stats(repo).Checked);
        Assert.True(Stats(repo).KeysChanged > 0 && Stats(repo).ByKey > units / 2, Stats(repo).ToString());

        // A new host reads the states from disk: every unit takes the full check once, then is accepted by key.
        await repo.RestartAsync();
        Assert.Equal(0, (await repo.ApplyAsync()).UnitsRendered);
        Assert.Equal(0, Stats(repo).ByKey);
        Assert.Equal(units, Stats(repo).Checked);
        Assert.Equal(0, (await repo.ApplyAsync()).UnitsRendered);
        Assert.Equal(units, Stats(repo).ByKey);

        // A state file rewritten behind the host (here without one unit's state) is decoded again and its units checked in full.
        var store = (UnitStateStore)repo.Service.Services.UnitState;
        var file = store.FileOf("e2e");
        var states = UnitStateStore.Decode(File.ReadAllBytes(file))!;
        var dropped = states.Keys.Order(StringComparer.Ordinal).First();
        states.Remove(dropped);
        File.WriteAllBytes(file, UnitStateStore.Encode([.. states.Values]));
        var rewritten = await repo.ApplyAsync();
        Assert.Equal(1, rewritten.UnitsRendered);
        Assert.Equal(states.Count, Stats(repo).Checked); // the pack's other units; the other pack's file is still trusted
        Assert.Equal(units - states.Count - 1, Stats(repo).ByKey);
        await IncrementalTests.AssertIncrementalEqualsForcedAsync(repo);
    }

    [Fact]
    public async Task A_template_saved_between_the_skip_check_and_a_render_renders_its_readers_in_the_next_run()
    {
        // One run can see a pack file twice: the skip check hashes the keys its candidates read, and rendering hashes them again for
        // the units it renders. A partial saved in between must not let the next run take the newer hash as the one its readers'
        // states were checked against.
        await using var repo = E2ERepo.Create();
        await repo.ApplyAsync();
        await repo.ApplyAsync();
        Assert.Equal(0, Stats(repo).Checked);

        // A new entity, so the next run renders a unit that reads the partial; the partial is saved once the skip check has accepted
        // its other readers by key.
        repo.Repo.WriteFile(".maquettiste/model/entities/coupon.json", """
            {
              "kind": "entity",
              "id": "01J92P0V2K0000000000000002",
              "name": "Coupon",
              "package": "01J92P0V01KDRN8GX5PGYCNKSX",
              "key": { "attributes": ["01J92P0V2K0000000000000004"], "strategy": "uuid-v7" },
              "attributes": [
                { "id": "01J92P0V2K0000000000000004", "name": "id", "type": "uuid", "required": true },
                { "id": "01J92P0V2K0000000000000003", "name": "code", "type": "string", "length": 20, "required": true }
              ]
            }

            """);
        var partial = Path.Combine(repo.Repo.ModelRoot, "templates", "billing-demo", "_property.scriban");
        var detector = (ChangeDetector)repo.Service.Services.ChangeDetector;
        detector.AfterSelect = () =>
        {
            detector.AfterSelect = null;
            File.WriteAllText(partial, File.ReadAllText(partial).Replace("{ get; init; }", "{ get; set; }", StringComparison.Ordinal));
        };
        await repo.ApplyAsync();
        Assert.Contains("{ get; set; }", repo.Repo.ReadFile("src/Generated/demo/src/Billing/Coupon.g.cs"), StringComparison.Ordinal);
        Assert.Contains("{ get; init; }", repo.Repo.ReadFile("src/Generated/demo/src/Billing/Customer.g.cs"), StringComparison.Ordinal);

        // Nothing else changes: the partial's other readers still render, as the full check would have them.
        var next = await repo.ApplyAsync();
        Assert.True(next.UnitsRendered >= 8, Stats(repo).ToString());
        Assert.Contains("{ get; set; }", repo.Repo.ReadFile("src/Generated/demo/src/Billing/Customer.g.cs"), StringComparison.Ordinal);
        await IncrementalTests.AssertIncrementalEqualsForcedAsync(repo);
    }

    private static SkipStats Stats(E2ERepo repo) => ((ChangeDetector)repo.Service.Services.ChangeDetector).LastStats!;
}
