using System.Text;
using System.Text.Json;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Validation;

namespace Maquettiste.Engine.Tests.Validation;

public sealed class SarifWriterTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Diagnostic[] Sample =
    [
        new("MQ2001", DiagnosticSeverity.Error, "'package' references 'X', which does not exist.", "01JENT00000000000000000001",
            ".maquettiste/model/entities/order.json", "/package", 6, 14),
        new("MQ4006", DiagnosticSeverity.Warning, "Native type 'varchar2' is unknown.", "01JCMN00000000000000000001",
            ".maquettiste/model/databases/main db/tables/orders.json", "/columns/3/nativeType", null, null),
        new("x/no-draft", DiagnosticSeverity.Info, "Draft.", "01JENT00000000000000000002", ".maquettiste/model/entities/draft.json", "/tags", 3, 3),
        new("MQ5002", DiagnosticSeverity.Error, "Loading the validation rule scripts failed.", null, null, null, null, null),
    ];

    private static async Task<JsonElement> WriteAsync(IReadOnlyList<Diagnostic> diagnostics, Stream? stream = null)
    {
        var buffer = new MemoryStream();
        await SarifWriter.WriteAsync(stream ?? buffer, diagnostics, "1.2.3", Ct);
        var bytes = stream is AsyncOnlyStream a ? a.Written : buffer.ToArray();
        using var doc = JsonDocument.Parse(bytes);
        return doc.RootElement.Clone();
    }

    [Fact]
    public async Task Writes_sarif_2_1_0_with_tool_rules_and_results()
    {
        var log = await WriteAsync(Sample);

        Assert.Equal("2.1.0", log.GetProperty("version").GetString());
        var run = Assert.Single(log.GetProperty("runs").EnumerateArray());
        var driver = run.GetProperty("tool").GetProperty("driver");
        Assert.Equal("maquettiste", driver.GetProperty("name").GetString());
        Assert.Equal("1.2.3", driver.GetProperty("version").GetString());

        var rules = driver.GetProperty("rules").EnumerateArray().ToList();
        Assert.Equal(RuleCatalog.All.Count + 1, rules.Count);
        Assert.Equal(RuleCatalog.All.Select(r => r.Id).Append("x/no-draft"), rules.Select(r => r.GetProperty("id").GetString()));
        Assert.Equal("note", rules[^1].GetProperty("defaultConfiguration").GetProperty("level").GetString());
        Assert.Equal("warning", rules.Single(r => r.GetProperty("id").GetString() == "MQ1003").GetProperty("defaultConfiguration").GetProperty("level").GetString());

        var results = run.GetProperty("results").EnumerateArray().ToList();
        Assert.Equal(Sample.Select(d => d.Rule), results.Select(r => r.GetProperty("ruleId").GetString()));
        Assert.Equal(["error", "warning", "note", "error"], results.Select(r => r.GetProperty("level").GetString()));
        foreach (var result in results)
            Assert.Equal(result.GetProperty("ruleId").GetString(), rules[result.GetProperty("ruleIndex").GetInt32()].GetProperty("id").GetString());

        var first = results[0];
        Assert.Equal(Sample[0].Message, first.GetProperty("message").GetProperty("text").GetString());
        var location = Assert.Single(first.GetProperty("locations").EnumerateArray()).GetProperty("physicalLocation");
        Assert.Equal(".maquettiste/model/entities/order.json", location.GetProperty("artifactLocation").GetProperty("uri").GetString());
        Assert.Equal("%SRCROOT%", location.GetProperty("artifactLocation").GetProperty("uriBaseId").GetString());
        Assert.Equal(6, location.GetProperty("region").GetProperty("startLine").GetInt32());
        Assert.Equal(14, location.GetProperty("region").GetProperty("startColumn").GetInt32());
        Assert.Equal("01JENT00000000000000000001", first.GetProperty("properties").GetProperty("elementId").GetString());
        Assert.Equal("/package", first.GetProperty("properties").GetProperty("jsonPointer").GetString());

        var noLine = Assert.Single(results[1].GetProperty("locations").EnumerateArray()).GetProperty("physicalLocation");
        Assert.False(noLine.TryGetProperty("region", out _));
        Assert.Equal(".maquettiste/model/databases/main%20db/tables/orders.json", noLine.GetProperty("artifactLocation").GetProperty("uri").GetString());

        Assert.False(results[3].TryGetProperty("locations", out _));
        Assert.False(results[3].TryGetProperty("properties", out _));
    }

    [Fact]
    public async Task Writes_only_asynchronously_so_it_can_stream_to_a_response_body()
    {
        var many = Enumerable.Range(0, 2000).Select(i => Sample[i % Sample.Length]).ToList();
        var stream = new AsyncOnlyStream();

        var log = await WriteAsync(many, stream);

        Assert.Equal(2000, log.GetProperty("runs")[0].GetProperty("results").GetArrayLength());
        Assert.True(stream.AsyncWrites > 1, "large logs are flushed in chunks");
    }

    [Fact]
    public async Task Output_is_deterministic_utf8_with_lf()
    {
        var a = new MemoryStream();
        var b = new MemoryStream();
        await SarifWriter.WriteAsync(a, Sample, "1.0.0", Ct);
        await SarifWriter.WriteAsync(b, Sample, "1.0.0", Ct);

        Assert.Equal(a.ToArray(), b.ToArray());
        var text = Encoding.UTF8.GetString(a.ToArray());
        Assert.DoesNotContain("\r", text, StringComparison.Ordinal);
        Assert.False(a.ToArray().AsSpan().StartsWith(Encoding.UTF8.Preamble));
    }

    [Fact]
    public async Task Honors_cancellation()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SarifWriter.WriteAsync(new MemoryStream(), Sample, "1", cts.Token));
    }

    [Fact]
    public async Task An_empty_log_has_the_catalog_and_no_results()
    {
        var log = await WriteAsync([]);

        var run = log.GetProperty("runs")[0];
        Assert.Equal(0, run.GetProperty("results").GetArrayLength());
        Assert.Equal(RuleCatalog.All.Count, run.GetProperty("tool").GetProperty("driver").GetProperty("rules").GetArrayLength());
    }

    /// <summary>A stream that refuses synchronous writes and flushes, like an ASP.NET Core response body by default.</summary>
    private sealed class AsyncOnlyStream : Stream
    {
        private readonly MemoryStream _inner = new();

        public int AsyncWrites { get; private set; }

        public byte[] Written => _inner.ToArray();

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() => throw new InvalidOperationException("Synchronous operations are disallowed.");

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new InvalidOperationException("Synchronous operations are disallowed.");

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            AsyncWrites++;
            _inner.Write(buffer.Span);
            return ValueTask.CompletedTask;
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }
}
