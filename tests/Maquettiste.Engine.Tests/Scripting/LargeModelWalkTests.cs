using System.Collections.Frozen;
using System.Diagnostics;
using Maquettiste.Engine.Resolution;

namespace Maquettiste.Engine.Tests.Scripting;

/// <summary>
/// A script that walks a large list (the packs' case: one property of every table of a database whose table list carries a
/// membership key per model file) records each key list once per call, so it runs well inside the sandbox limits and records
/// exactly the keys it read.
/// </summary>
public sealed class LargeModelWalkTests
{
    [Fact]
    public void Walking_every_item_of_a_large_list_is_fast_and_records_the_same_keys()
    {
        const int Items = 5000;
        var membership = Enumerable.Range(0, 20_000).Select(i => "e:M" + i.ToString("D5", System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        var entities = Enumerable.Range(0, Items).Select(i => new REntity
        {
            Id = "E" + i.ToString("D5", System.Globalization.CultureInfo.InvariantCulture),
            Name = "Thing" + i.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Dependencies = ["e:E" + i.ToString("D5", System.Globalization.CultureInfo.InvariantCulture), "k:entity"],
        }).ToList();
        var model = new ResolvedModel
        {
            Entities = new RList<REntity>(entities, membership),
            ById = entities.ToFrozenDictionary(e => e.Id, e => (IResolvedObject)e, StringComparer.Ordinal),
        };
        using var pool = Scripts.Pool("""
            maquettiste.filter('walk', (e, model) => {
              let total = 0;
              for (let i = 0; i < model.entities.length; i++) total += model.entities[i].name.length;
              return total > 0;
            });
            """);
        var reads = new ListRecorder();
        var clock = Stopwatch.StartNew();
        using (var lease = pool.Rent())
            Assert.True(lease.Sandbox.Filter("walk", entities[0], model, Scripts.Ctx(reads)) is true);

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), "walking took " + clock.Elapsed);
        var expected = membership.Concat(entities.SelectMany(e => e.Dependencies)).ToHashSet(StringComparer.Ordinal);
        Assert.True(expected.SetEquals(reads.Keys), "the recorded keys differ from the keys read");

        // A second call records its reads again (the once-per-list rule is per call).
        var again = new ListRecorder();
        using (var lease = pool.Rent())
            lease.Sandbox.Filter("walk", entities[0], model, Scripts.Ctx(again));
        Assert.True(expected.SetEquals(again.Keys));
    }
}
