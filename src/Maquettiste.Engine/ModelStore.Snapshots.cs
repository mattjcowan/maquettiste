using System.Globalization;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Loading;

namespace Maquettiste.Engine;

/// <summary>The store's part in snapshots (docs/engineering/snapshots.md): the read-only refusal and the whole-model replacement of a restore.</summary>
public sealed partial class ModelStore
{
    /// <summary>The diagnostic of a write against a snapshot opened read-only.</summary>
    /// <returns>MQ6029.</returns>
    internal static Diagnostic ReadOnlyRefusal() =>
        RuleCatalog.Create("MQ6029", "This model is a snapshot opened read-only; nothing was written. Restore the snapshot to change it.", null, null, null);

    /// <summary>
    /// Replaces documents of the working model in one all-or-nothing write (a restore), then reloads the whole model and publishes one
    /// change set to every subscriber, so realtime clients see the restored model. Nothing is validated: a snapshot is restored as it
    /// was taken.
    /// </summary>
    /// <param name="writes">Model-relative paths and their bytes.</param>
    /// <param name="deletes">Model-relative paths to delete.</param>
    /// <param name="source">What the change is recorded as.</param>
    /// <param name="ct">Cancellation, observed until the first document is replaced.</param>
    /// <returns>The change set, or the failure when the write was refused (nothing changed then).</returns>
    /// <exception cref="IOException">The write failed and was rolled back.</exception>
    internal async Task<(ChangeSet? Changes, FileSetFailure? Refused)> ReplaceDocumentsAsync(
        IReadOnlyList<(string Path, byte[] Bytes)> writes, IReadOnlyList<string> deletes, ChangeSource source, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsReadOnly)
            return (null, new FileSetFailure(Documents.Description, Snapshots.ReadOnlyPathPolicy.Reason, true));
        await LoadedAsync(ct).ConfigureAwait(false);
        ChangeSet changes;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (writes.Count > 0 || deletes.Count > 0)
            {
                var batchId = Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + "-" + Interlocked.Increment(ref _batchCounter).ToString(CultureInfo.InvariantCulture);
                var failure = await Documents.ApplyAsync(writes, deletes, batchId, ct).ConfigureAwait(false);
                if (failure is { Refused: true })
                    return (null, failure);
                if (failure is not null)
                    throw new IOException($"The snapshot could not be restored ({failure.Path}); nothing was changed. {failure.Reason}");
            }

            // The files are on disk: index them even if the caller has given up.
            changes = await ReloadAsync(null, false, source, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        await NotifyAsync(changes, CancellationToken.None).ConfigureAwait(false);
        return (changes, null);
    }
}
