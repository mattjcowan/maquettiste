using System.Globalization;
using System.Runtime.CompilerServices;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Resolution;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Resolution;

/// <summary>A resolution keeps nothing reachable once its result is dropped (a long-lived host re-resolves repeatedly).</summary>
public sealed class ResolverRetentionTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void A_dropped_resolved_model_is_reclaimed_by_the_next_collection(int parallelism)
    {
        // The objects with a dependency set are gathered from the resolver's parallel phases in a bag of per-thread lists, which live
        // in thread statics of the threads that added to them; unless the run empties them, one collection does not reclaim the model
        // (only the bag's finalizer releases them, for a later collection).
        var weak = ResolveAndDrop(parallelism);
        Assert.NotEmpty(weak);
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: false);
        Assert.Equal(0, weak.Count(w => w.IsAlive));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] ResolveAndDrop(int parallelism)
    {
        var b = new ModelBuilder(11);
        b.Database("main", Dialect.PostgreSql);
        for (var i = 0; i < 100; i++)
            b.Entity("Thing" + i.ToString(CultureInfo.InvariantCulture)).Key("id", "uuid").Attr("name", "string");
        var resolver = new ModelResolver(ResolutionKit.Options with { MaxDegreeOfParallelism = parallelism });
        // Synchronously: an async test method's state machine could itself keep the result reachable.
        var resolved = resolver.ResolveAsync(b.Build(), null, CancellationToken.None).GetAwaiter().GetResult();
        return [.. resolved.Entities.Select(e => new WeakReference(e)), .. resolved.Db("main").Tables.Select(t => new WeakReference(t))];
    }
}
