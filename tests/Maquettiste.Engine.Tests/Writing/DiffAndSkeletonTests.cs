using System.Text;
using Maquettiste.Engine.Hashing;
using Maquettiste.Engine.Writing;

namespace Maquettiste.Engine.Tests.Writing;

public sealed class DiffAndSkeletonTests
{
    private static string Diff(string before, string after, int context = 3) =>
        new DiffGenerator().Unified("db/a.sql", Encoding.UTF8.GetBytes(before), Encoding.UTF8.GetBytes(after), context);

    [Fact]
    public void Identical_inputs_give_an_empty_diff() => Assert.Equal("", Diff("a\nb\n", "a\nb\n"));

    [Fact]
    public void Added_file()
    {
        Assert.Equal("--- a/db/a.sql\n+++ b/db/a.sql\n@@ -0,0 +1,2 @@\n+x\n+y\n", Diff("", "x\ny\n"));
    }

    [Fact]
    public void Deleted_file()
    {
        Assert.Equal("--- a/db/a.sql\n+++ b/db/a.sql\n@@ -1 +0,0 @@\n-x\n", Diff("x\n", ""));
    }

    [Fact]
    public void Modification_with_context()
    {
        var before = "1\n2\n3\n4\n5\n6\n7\n8\n9\n";
        var after = "1\n2\n3\n4\nfive\n6\n7\n8\n9\n";

        Assert.Equal(
            "--- a/db/a.sql\n+++ b/db/a.sql\n@@ -2,7 +2,7 @@\n 2\n 3\n 4\n-5\n+five\n 6\n 7\n 8\n",
            Diff(before, after));
    }

    [Fact]
    public void Distant_changes_make_separate_hunks_and_close_ones_merge()
    {
        var lines = Enumerable.Range(1, 30).Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToList();
        var before = string.Join("\n", lines) + "\n";
        var afterLines = lines.ToList();
        afterLines[1] = "two";
        afterLines[27] = "twenty-eight";
        var separate = Diff(before, string.Join("\n", afterLines) + "\n", context: 2);
        afterLines[5] = "six";
        var merged = Diff(before, string.Join("\n", afterLines) + "\n", context: 2);

        Assert.Equal(2, CountHunks(separate));
        Assert.Contains("@@ -1,4 +1,4 @@", separate, StringComparison.Ordinal);
        Assert.Contains("@@ -26,5 +26,5 @@", separate, StringComparison.Ordinal);
        Assert.Equal(2, CountHunks(merged));
        Assert.Contains("@@ -1,8 +1,8 @@", merged, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_final_newline_is_marked()
    {
        Assert.Equal(
            "--- a/db/a.sql\n+++ b/db/a.sql\n@@ -1,2 +1,2 @@\n a\n-b\n\\ No newline at end of file\n+b\n",
            Diff("a\nb", "a\nb\n"));
    }

    [Fact]
    public void Insertions_and_deletions_are_minimal()
    {
        var diff = Diff("a\nb\nc\nd\n", "a\nx\nc\nd\ny\n", context: 0);

        Assert.Equal("--- a/db/a.sql\n+++ b/db/a.sql\n@@ -2 +2 @@\n-b\n+x\n@@ -4,0 +5 @@\n+y\n", diff);
    }

    [Fact]
    public void Diff_applies_back_to_the_new_text()
    {
        var random = new Random(7);
        for (var round = 0; round < 50; round++)
        {
            var before = Enumerable.Range(0, random.Next(0, 40)).Select(_ => ((char)('a' + random.Next(0, 5))).ToString()).ToList();
            var after = before.ToList();
            for (var edit = random.Next(0, 8); edit > 0; edit--)
            {
                if (after.Count > 0 && random.Next(2) == 0)
                    after.RemoveAt(random.Next(after.Count));
                else
                    after.Insert(random.Next(after.Count + 1), ((char)('a' + random.Next(0, 5))).ToString());
            }

            var beforeText = before.Count == 0 ? "" : string.Join("\n", before) + "\n";
            var afterText = after.Count == 0 ? "" : string.Join("\n", after) + "\n";
            var diff = Diff(beforeText, afterText, context: 1);

            Assert.Equal(afterText, Apply(beforeText, diff));
        }
    }

    [Fact]
    public void Skeleton_drops_region_bodies_only()
    {
        var text = "a\n// maquettiste:keep id=body\nhand written\nmore\n// maquettiste:end-keep\nb\n";
        var edited = "a\n// maquettiste:keep id=body\nsomething else entirely\n// maquettiste:end-keep\nb\n";
        var outside = "A\n// maquettiste:keep id=body\nhand written\nmore\n// maquettiste:end-keep\nb\n";

        var skeleton = Encoding.UTF8.GetString(ManifestHashes.Skeleton(Encoding.UTF8.GetBytes(text)));

        Assert.Equal("a\n// maquettiste:keep id=body\n// maquettiste:end-keep\nb\n", skeleton);
        Assert.Equal(Comparable(text), Comparable(edited));
        Assert.NotEqual(Comparable(text), Comparable(outside));
        Assert.StartsWith("r:", Comparable(text), StringComparison.Ordinal);
    }

    [Fact]
    public void Skeleton_handles_other_comment_syntaxes_and_unclosed_regions()
    {
        var sql = "-- maquettiste:keep id=a\nx\n-- maquettiste:end-keep\n<!-- maquettiste:keep\tid=b -->\ny\n";

        Assert.Equal("-- maquettiste:keep id=a\n-- maquettiste:end-keep\n<!-- maquettiste:keep\tid=b -->\n",
            Encoding.UTF8.GetString(ManifestHashes.Skeleton(Encoding.UTF8.GetBytes(sql))));
    }

    private static string Comparable(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        return ManifestHashes.Comparable("r:x", bytes, ContentHash.Of(bytes));
    }

    private static int CountHunks(string diff) => diff.Split('\n').Count(l => l.StartsWith("@@", StringComparison.Ordinal));

    /// <summary>Applies a unified diff (no "no newline" markers) to a text.</summary>
    private static string Apply(string before, string diff)
    {
        var source = before.Length == 0 ? [] : before[..^1].Split('\n');
        var result = new List<string>();
        var position = 0;
        var lines = diff.Split('\n');
        for (var i = 2; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                var old = line.Split(' ')[1][1..].Split(',');
                var start = int.Parse(old[0], System.Globalization.CultureInfo.InvariantCulture);
                var count = old.Length > 1 ? int.Parse(old[1], System.Globalization.CultureInfo.InvariantCulture) : 1;
                var target = count == 0 ? start : start - 1;
                while (position < target)
                    result.Add(source[position++]);
            }
            else if (line.StartsWith(' '))
            {
                result.Add(source[position++]);
            }
            else if (line.StartsWith('-'))
            {
                position++;
            }
            else if (line.StartsWith('+'))
            {
                result.Add(line[1..]);
            }
        }

        while (position < source.Length)
            result.Add(source[position++]);
        return result.Count == 0 ? "" : string.Join("\n", result) + "\n";
    }
}
