using System.Globalization;
using Maquettiste.Engine.Branding;
using Maquettiste.Engine.Hashing;
using Maquettiste.Engine.Loading;

namespace Maquettiste.Engine;

/// <summary>The result of <see cref="ModelStore.SaveBrandingIconAsync"/>.</summary>
/// <param name="Saved">Whether the file was written.</param>
/// <param name="Icon">The model-relative path written (the value for <c>branding.icon</c>), when saved.</param>
/// <param name="Hash">The stored file's content hash, when saved.</param>
/// <param name="Removed">What was removed from an SVG before it was stored; empty when nothing was.</param>
/// <param name="Problem">Why the file was refused, when not saved.</param>
/// <param name="TooLarge">Whether it was refused for its size.</param>
public sealed record BrandingIconWrite(bool Saved, string? Icon, string? Hash, IReadOnlyList<string> Removed, string? Problem, bool TooLarge);

/// <summary>The icon named by <c>branding.icon</c>, ready to serve.</summary>
/// <param name="Icon">The model-relative path.</param>
/// <param name="ContentType"><c>image/svg+xml</c> or <c>image/png</c>.</param>
/// <param name="Bytes">The file, or the sanitized SVG when the file has unsafe parts (MQ8003).</param>
/// <param name="Hash">The content hash of <paramref name="Bytes"/>.</param>
public sealed record BrandingIconFile(string Icon, string ContentType, byte[] Bytes, string Hash);

public sealed partial class ModelStore
{
    /// <summary>
    /// Stores the project icon under a content-addressed name, <c>&lt;model root&gt;/branding/icon-&lt;hash&gt;.svg</c> or <c>.png</c>:
    /// an SVG without its scripts, event handlers and external references, or a PNG, at most 512 KB. It never replaces the file the
    /// saved settings name, and it does not change <c>maquettiste.json</c>; the caller saves <c>branding.icon</c> with the returned
    /// path, and that settings save removes the uploads it no longer names.
    /// </summary>
    /// <param name="contentType"><c>image/svg+xml</c> or <c>image/png</c>.</param>
    /// <param name="bytes">The file.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>Saved with the path and hash, or refused with the reason.</returns>
    public async Task<BrandingIconWrite> SaveBrandingIconAsync(string contentType, byte[] bytes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(contentType);
        ArgumentNullException.ThrowIfNull(bytes);
        var inspection = BrandingIcons.Inspect(contentType, bytes);
        if (!inspection.Usable)
            return new BrandingIconWrite(false, null, null, [], string.Join(" ", inspection.Problems), bytes.Length > BrandingIcons.MaxBytes);

        await LoadedAsync(ct).ConfigureAwait(false);
        var paths = _paths.Value;
        var safe = inspection.Safe!;
        var hash = ContentHash.Of(safe);
        var icon = BrandingIcons.PathFor(contentType, hash);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var batchId = Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + "-" + Interlocked.Increment(ref _batchCounter).ToString(CultureInfo.InvariantCulture);
            var failure = await Documents.ApplyAsync([(icon, safe)], [], batchId, ct).ConfigureAwait(false);
            if (failure is { Refused: true })
                return new BrandingIconWrite(false, null, null, [], $"The write to {failure.Path} was refused: {failure.Reason}", false);
            if (failure is not null)
                throw new IOException($"The icon could not be written ({failure.Path}); nothing was changed. {failure.Reason}");
        }
        finally
        {
            _gate.Release();
        }

        return new BrandingIconWrite(true, icon, hash, inspection.Problems, null, false);
    }

    /// <summary>
    /// After a settings save that changed <c>branding.icon</c>: removes the uploaded icons (content-addressed names only) the settings
    /// no longer name. Best effort; a file that cannot be removed stays.
    /// </summary>
    private void RemoveUnnamedUploads(string? before, string? after)
    {
        if (string.Equals(before, after, StringComparison.Ordinal))
            return;
        var folder = Path.Combine(_paths.Value.ModelRoot, BrandingIcons.Folder);
        if (!Directory.Exists(folder))
            return;
        foreach (var file in Directory.EnumerateFiles(folder))
        {
            var relative = BrandingIcons.Folder + "/" + Path.GetFileName(file);
            if (!BrandingIcons.IsUploadPath(relative) || string.Equals(relative, after, StringComparison.Ordinal))
                continue;
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>
    /// Reads the icon named by <c>branding.icon</c>. <see langword="null"/> when none is set, or when it breaks MQ8002 or cannot be used
    /// (MQ8003 on a file that is not SVG or PNG, or too large); an SVG with unsafe parts comes back without them.
    /// </summary>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The icon, or <see langword="null"/>.</returns>
    public async Task<BrandingIconFile?> ReadBrandingIconAsync(CancellationToken ct)
    {
        var snapshot = await GetSnapshotAsync(ct).ConfigureAwait(false);
        var icon = snapshot.Settings.Branding.Icon;
        if (!BrandingIcons.IsIconPath(icon))
            return null;
        var file = new FileInfo(_paths.Value.FullPath(icon!));
        if (!file.Exists || file.LinkTarget is not null || file.Length > BrandingIcons.MaxBytes)
            return null;
        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(file.FullName, ct).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            return null;
        }

        var contentType = BrandingIcons.ContentTypeOf(icon)!;
        var inspection = BrandingIcons.Inspect(contentType, bytes);
        return inspection.Usable ? new BrandingIconFile(icon!, contentType, inspection.Safe!, ContentHash.Of(inspection.Safe!)) : null;
    }
}
