using System.Text.Json;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Revisions;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Revisions;

/// <summary>
/// The revision sequence (revisions-design.md section 6; decisions 3 to 7, 14 and 22): every case of the shared fixture
/// <c>tests/fixtures/revisions/sequence-cases.json</c>, which the editor's copy reads too, then what the fixture can't say: the shapes that
/// make no sequence, the arguments refused, numbers past any integer type, and a walk that checks the order and the written form together.
/// </summary>
public sealed class RevisionSequenceTests
{
    private static readonly string[] Groups = ["start", "isValue", "write", "next", "chosen", "compare", "examples"];

    private static readonly Lazy<(JsonElement Root, IReadOnlyDictionary<string, RevisionSequence> Sequences)> Fixture = new(Load);

    private static (JsonElement Root, IReadOnlyDictionary<string, RevisionSequence> Sequences) Load()
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(Fixtures.Path("revisions", "sequence-cases.json")));
        var root = document.RootElement.Clone();
        var sequences = new Dictionary<string, RevisionSequence>(StringComparer.Ordinal);
        foreach (var sequence in root.GetProperty("sequences").EnumerateObject())
            sequences[sequence.Name] = Sequence(sequence.Value.GetRawText());
        return (root, sequences);
    }

    /// <summary>A sequence from a block's JSON: its levels as the records read them, and its separator.</summary>
    private static RevisionSequence Sequence(string block)
    {
        var revisions = JsonSerializer.Deserialize<EntityRevisions>(block, EngineJson.Options)!;
        return new RevisionSequence(revisions.Levels!, revisions.Separator);
    }

    private static RevisionLevel[] Levels(string json) => JsonSerializer.Deserialize<RevisionLevel[]>(json, EngineJson.Options)!;

    /// <summary>Runs every case of a group, collecting the cases whose answer differs.</summary>
    private static void Run(string group, Func<RevisionSequence, JsonElement, (string Actual, string Expected)> answer)
    {
        var failures = new List<string>();
        foreach (var c in Fixture.Value.Root.GetProperty(group).EnumerateArray())
        {
            var sequence = Fixture.Value.Sequences[c.GetProperty("sequence").GetString()!];
            var (actual, expected) = answer(sequence, c);
            if (!string.Equals(actual, expected, StringComparison.Ordinal))
                failures.Add($"{c.GetRawText()}: got {actual}");
        }

        Assert.True(failures.Count == 0, $"{failures.Count} {group} cases differ:\n" + string.Join("\n", failures));
    }

    private static string Text(JsonElement c, string name) => c.GetProperty(name).GetString()!;

    private static string Show(IReadOnlyList<string>? parts) => parts is null ? "not a value" : JsonSerializer.Serialize(parts);

    // ---- the shared fixture ----

    [Fact]
    public void The_fixture_has_only_the_groups_these_tests_read_and_every_case_names_a_sequence()
    {
        var root = Fixture.Value.Root;
        Assert.Equal(["description", "sequences", .. Groups], root.EnumerateObject().Select(p => p.Name));
        foreach (var group in Groups)
        {
            Assert.NotEmpty(root.GetProperty(group).EnumerateArray());
            Assert.All(root.GetProperty(group).EnumerateArray(), c => Assert.True(Fixture.Value.Sequences.ContainsKey(Text(c, "sequence")), c.GetRawText()));
        }
    }

    [Fact]
    public void Every_start_of_the_fixture() =>
        Run("start", (s, c) => (s.Start, Text(c, "expected")));

    [Fact]
    public void Every_value_and_non_value_of_the_fixture_reads_as_it_says() =>
        Run("isValue", (s, c) =>
        {
            var expected = c.GetProperty("expected").GetBoolean()
                ? Show(c.GetProperty("parts").EnumerateArray().Select(p => p.GetString()!).ToList())
                : Show(null);
            s.TryRead(Text(c, "text"), out var parts);
            Assert.Equal(parts is not null, s.IsValue(Text(c, "text")));
            return (Show(parts), expected);
        });

    [Fact]
    public void Every_write_of_the_fixture_and_its_text_reads_back() =>
        Run("write", (s, c) =>
        {
            var parts = c.GetProperty("parts").EnumerateArray().Select(p => p.GetString()!).ToList();
            var written = s.Write(parts);
            Assert.True(s.TryRead(written, out var read) && read.SequenceEqual(parts), written);
            return (written, Text(c, "expected"));
        });

    [Fact]
    public void Every_next_of_the_fixture_or_its_refusal() =>
        Run("next", (s, c) =>
        {
            var next = s.Next(Text(c, "from"), c.GetProperty("level").GetInt32());
            var expected = c.TryGetProperty("refused", out var refused) && refused.GetBoolean() ? "refused" : Text(c, "expected");
            return (next ?? "refused", expected);
        });

    [Fact]
    public void Every_chosen_name_of_the_fixture() =>
        Run("chosen", (s, c) =>
        {
            var expected = c.GetProperty("ok").GetBoolean() ? RevisionNameCheck.Ok
                : Text(c, "why") switch
                {
                    "not-a-value" => RevisionNameCheck.NotAValue,
                    "not-later" => RevisionNameCheck.NotLater,
                    var why => throw new InvalidOperationException("An unknown why: " + why),
                };
            return (s.CheckChosen(Text(c, "latest"), Text(c, "name")).ToString(), expected.ToString());
        });

    [Fact]
    public void Every_comparison_of_the_fixture_both_ways() =>
        Run("compare", (s, c) =>
        {
            var (a, b) = (Text(c, "a"), Text(c, "b"));
            var expected = c.GetProperty("expected").GetInt32();
            Assert.Equal(-expected, s.Compare(b, a));
            return (s.Compare(a, b).ToString(System.Globalization.CultureInfo.InvariantCulture), expected.ToString(System.Globalization.CultureInfo.InvariantCulture));
        });

    [Fact]
    public void Every_example_run_of_the_fixture() =>
        Run("examples", (s, c) =>
            (JsonSerializer.Serialize(s.Examples(c.GetProperty("level").GetInt32())), JsonSerializer.Serialize(c.GetProperty("expected").EnumerateArray().Select(e => e.GetString()))));

    // ---- what the fixture can't say ----

    [Fact]
    public void A_walk_through_each_sequence_moves_forward_and_writes_values_that_read_back()
    {
        // From the start, move at each level in turn, many times: every revision is a value, writes back to itself, comes after the one
        // before, and its first value at each level comes from FirstValue.
        foreach (var (name, sequence) in Fixture.Value.Sequences)
        {
            var current = sequence.Start;
            Assert.True(sequence.IsValue(current), name);
            for (var step = 0; step < 60; step++)
            {
                var level = step % sequence.Levels.Count + 1;
                if (sequence.Next(current, level) is not { } next)
                    continue;
                Assert.True(sequence.TryRead(next, out var parts), $"{name}: {next}");
                Assert.Equal(next, sequence.Write(parts));
                Assert.True(sequence.Compare(current, next) < 0, $"{name}: {current} then {next}");
                Assert.Equal(RevisionNameCheck.Ok, sequence.CheckChosen(current, next));
                Assert.Equal(RevisionNameCheck.NotLater, sequence.CheckChosen(next, current));
                Assert.Equal(sequence.FirstValue(level), sequence.Examples(level)[0]);
                current = next;
            }
        }
    }

    [Fact]
    public void The_drawing_of_section_6_runs_as_the_design_says()
    {
        var drawing = Sequence("""
            {"levels":[{"name":"major","format":"letters","skip":["I","O","Q","S","X","Z"]},{"name":"minor","format":"numbers","startEmpty":true}]}
            """);
        Assert.Equal(".", drawing.Separator);
        Assert.Equal("A", drawing.Start);
        Assert.Equal("A.1", drawing.Next("A", 2));
        Assert.Equal("A.2", drawing.Next("A.1", 2));
        Assert.Equal("B", drawing.Next("A.2", 1));
        Assert.Equal("B.1", drawing.Next("B", 2));
        Assert.Equal("J", drawing.Next("H", 1));
        Assert.Equal(["1", "2", "3", "4", "5"], drawing.Examples(2));
        Assert.Equal(("A", "1"), (drawing.FirstValue(1), drawing.FirstValue(2)));
    }

    [Fact]
    public void Numbers_have_no_end_and_compare_by_value_past_any_integer_type()
    {
        var numbers = new RevisionSequence(Levels("""[{"format":"numbers","first":99999999999999999999}]"""));
        Assert.Equal("99999999999999999999", numbers.Start);
        Assert.Equal("100000000000000000000", numbers.Next("99999999999999999999", 1));
        Assert.Equal(-1, numbers.Compare("99999999999999999999", "100000000000000000000"));
        Assert.False(numbers.IsValue("9999999999999999999"));

        var padded = new RevisionSequence(Levels("""[{"format":"numbers","first":0,"width":3}]"""));
        Assert.Equal(("000", "001"), (padded.Start, padded.Next("000", 1)));
        Assert.Equal("1000", padded.Next("999", 1));
        Assert.False(padded.IsValue("0999"));
    }

    [Fact]
    public void Shapes_that_make_no_sequence_say_why_and_the_constructor_refuses_them()
    {
        foreach (var (levels, separator, problem) in new (string, string?, string)[]
        {
            ("[]", null, "at least one level"),
            ("""[{"format":"letters","values":["A"]}]""", null, "both a format and values"),
            ("""[{"name":"major"}]""", null, "neither a format nor values"),
            ("""[{"values":["A","A"]}]""", null, "empty, repeated or holds the separator"),
            ("""[{"values":["1.0","2.0"]}]""", null, "empty, repeated or holds the separator"),
            ("""[{"format":"letters","skip":["A","B","C","D","E","F","G","H","I","J","K","L","M","N","O","P","Q","R","S","T","U","V","W","X","Y","Z"]}]""", null, "skips every letter"),
            ("""[{"format":"letters","first":"a"}]""", null, "not a run of its letters"),
            ("""[{"format":"letters","skip":["I"],"first":"AI"}]""", null, "not a run of its letters"),
            ("""[{"format":"letters","first":3}]""", null, "not a run of its letters"),
            ("""[{"format":"numbers","first":"5"}]""", null, "not a whole number"),
            ("""[{"format":"numbers","first":1.5}]""", null, "not a whole number"),
            ("""[{"format":"letters","startEmpty":true},{"format":"numbers"}]""", null, "does not start empty"),
            ("""[{"format":"letters"},{"format":"numbers"}]""", "v", "holds a letter or a digit"),
            ("""[{"format":"letters"},{"format":"numbers"}]""", "-1", "holds a letter or a digit"),
            ("""[{"format":"numbers"},{"format":"numbers"}]""", "", "need a separator"),
            ("""[{"format":"letters"},{"values":["x"]}]""", "", "need a separator"),
        })
        {
            Assert.False(RevisionSequence.TryCreate(Levels(levels), separator, out var sequence, out var why), levels);
            Assert.Null(sequence);
            Assert.Contains(problem, why, StringComparison.Ordinal);
            var refused = Assert.Throws<ArgumentException>(() => new RevisionSequence(Levels(levels), separator));
            Assert.StartsWith(why, refused.Message, StringComparison.Ordinal);
        }

        // Keys that don't apply to a level are not read, and an empty separator is fine with one level or between letters and numbers.
        Assert.True(RevisionSequence.TryCreate(Levels("""[{"format":"letters","width":2},{"format":"numbers","skip":["I"]}]"""), "", out var lenient, out _));
        Assert.Equal("A1", lenient.Start);
        Assert.True(RevisionSequence.TryCreate(Levels("""[{"values":["Pilot"],"first":3,"skip":["I"]}]"""), "", out var list, out _));
        Assert.Equal("Pilot", list.Start);
    }

    [Fact]
    public void Texts_that_are_not_values_and_levels_the_sequence_lacks_are_refused()
    {
        var drawing = Fixture.Value.Sequences["drawing"];
        Assert.Throws<ArgumentException>(() => drawing.Next("I", 1));
        Assert.Throws<ArgumentException>(() => drawing.Compare("A", "A.0"));
        Assert.Throws<ArgumentException>(() => drawing.CheckChosen("Z", "B"));
        Assert.Throws<ArgumentException>(() => drawing.Write(["A", "1", "1"]));
        Assert.Throws<ArgumentOutOfRangeException>(() => drawing.Next("A", 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => drawing.Next("A", 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => drawing.Examples(3));
        Assert.Throws<ArgumentOutOfRangeException>(() => drawing.FirstValue(0));
        Assert.False(drawing.TryRead("A ", out var parts));
        Assert.Null(parts);
        Assert.Equal(RevisionNameCheck.NotAValue, drawing.CheckChosen("A", "a"));
    }

    [Fact]
    public void An_empty_separator_splits_only_where_a_digit_meets_anything_else()
    {
        var joined = Fixture.Value.Sequences["joined"];
        Assert.True(joined.TryRead("ABC123", out var parts));
        Assert.Equal(["ABC", "123"], parts);
        foreach (var text in new[] { "A 1", "A-1", "Ä1", "a1", "A1 ", "A١" })
            Assert.False(joined.IsValue(text), text);
    }
}
