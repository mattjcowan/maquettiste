using System.Globalization;
using System.Text;
using Maquettiste.Engine.Pipeline;

namespace Maquettiste.Engine.Writing;

/// <summary>
/// Unified diffs for dry runs and plans (W7): the common prefix and suffix are trimmed, the middle is diffed with Myers' algorithm,
/// and a middle whose edit distance exceeds <see cref="MaxEditDistance"/> is shown as one replacement, so memory stays bounded. The
/// output is deterministic: <c>--- a/&lt;path&gt;</c>, <c>+++ b/&lt;path&gt;</c>, then GNU-style hunks with <c>\ No newline at end
/// of file</c> markers. Identical inputs give an empty string.
/// </summary>
internal sealed class DiffGenerator : IDiffGenerator
{
    /// <summary>Above this many inserted plus deleted lines the middle is shown as a whole replacement.</summary>
    internal const int MaxEditDistance = 2000;

    private enum Op
    {
        Equal,
        Delete,
        Insert,
    }

    /// <inheritdoc/>
    public string Unified(string path, ReadOnlySpan<byte> before, ReadOnlySpan<byte> after, int context = 3)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (context < 0)
            throw new ArgumentOutOfRangeException(nameof(context));
        if (before.SequenceEqual(after))
            return "";
        var a = SplitLines(Encoding.UTF8.GetString(before), out var aNewline);
        var b = SplitLines(Encoding.UTF8.GetString(after), out var bNewline);
        var script = EditScript(a, b, aNewline, bNewline);

        var sb = new StringBuilder();
        sb.Append("--- a/").Append(path).Append('\n');
        sb.Append("+++ b/").Append(path).Append('\n');
        foreach (var (start, end) in Hunks(script, context))
            AppendHunk(sb, script, start, end, a, b, aNewline, bNewline);
        return sb.ToString();
    }

    private static string[] SplitLines(string text, out bool endsWithNewline)
    {
        endsWithNewline = text.Length == 0 || text[^1] == '\n';
        if (text.Length == 0)
            return [];
        var lines = text.Split('\n');
        return endsWithNewline ? lines[..^1] : lines;
    }

    /// <summary>The edit script as (op, index in a, index in b) steps.</summary>
    private static List<(Op Op, int A, int B)> EditScript(string[] a, string[] b, bool aNewline, bool bNewline)
    {
        // A last line without a trailing newline differs from the same text with one.
        bool Same(int i, int j) =>
            string.Equals(a[i], b[j], StringComparison.Ordinal)
            && (i == a.Length - 1 && !aNewline) == (j == b.Length - 1 && !bNewline);

        var prefix = 0;
        while (prefix < a.Length && prefix < b.Length && Same(prefix, prefix))
            prefix++;
        var suffix = 0;
        while (suffix < a.Length - prefix && suffix < b.Length - prefix && Same(a.Length - 1 - suffix, b.Length - 1 - suffix))
            suffix++;

        var script = new List<(Op, int, int)>(a.Length + b.Length);
        for (var i = 0; i < prefix; i++)
            script.Add((Op.Equal, i, i));
        var n = a.Length - prefix - suffix;
        var m = b.Length - prefix - suffix;
        script.AddRange(Middle(n, m, prefix, (i, j) => Same(prefix + i, prefix + j)));
        for (var k = suffix; k > 0; k--)
            script.Add((Op.Equal, a.Length - k, b.Length - k));
        return script;
    }

    private static List<(Op, int, int)> Middle(int n, int m, int offset, Func<int, int, bool> same)
    {
        if (n == 0 || m == 0)
            return Replace(n, m, offset);

        // Myers' greedy algorithm. trace[d] holds V for k in [-d-1, d+1] before step d, for the backtrack; d is capped, so the
        // saved rows stay bounded (about MaxEditDistance² integers).
        var limit = Math.Min(n + m, MaxEditDistance);
        var v = new int[(2 * limit) + 3];
        var center = limit + 1;
        var trace = new List<int[]>();
        var found = -1;
        for (var d = 0; d <= limit && found < 0; d++)
        {
            trace.Add(v.AsSpan(center - d - 1, (2 * d) + 3).ToArray());
            for (var k = -d; k <= d; k += 2)
            {
                var x = k == -d || (k != d && v[center + k - 1] < v[center + k + 1]) ? v[center + k + 1] : v[center + k - 1] + 1;
                var y = x - k;
                while (x < n && y < m && same(x, y))
                {
                    x++;
                    y++;
                }

                v[center + k] = x;
                if (x >= n && y >= m)
                {
                    found = d;
                    break;
                }
            }
        }

        if (found < 0)
            return Replace(n, m, offset);

        var steps = new List<(Op, int, int)>();
        var cx = n;
        var cy = m;
        for (var d = found; d > 0; d--)
        {
            var row = trace[d];
            int Prev(int k) => row[k + d + 1];
            var k = cx - cy;
            var down = k == -d || (k != d && Prev(k - 1) < Prev(k + 1));
            var pk = down ? k + 1 : k - 1;
            var px = Prev(pk);
            var py = px - pk;
            while (cx > px && cy > py)
            {
                cx--;
                cy--;
                steps.Add((Op.Equal, offset + cx, offset + cy));
            }

            if (down)
            {
                cy--;
                steps.Add((Op.Insert, offset + cx, offset + cy));
            }
            else
            {
                cx--;
                steps.Add((Op.Delete, offset + cx, offset + cy));
            }
        }

        while (cx > 0 && cy > 0)
        {
            cx--;
            cy--;
            steps.Add((Op.Equal, offset + cx, offset + cy));
        }

        steps.Reverse();
        return steps;
    }

    private static List<(Op, int, int)> Replace(int n, int m, int offset)
    {
        var steps = new List<(Op, int, int)>(n + m);
        for (var i = 0; i < n; i++)
            steps.Add((Op.Delete, offset + i, offset));
        for (var j = 0; j < m; j++)
            steps.Add((Op.Insert, offset + n, offset + j));
        return steps;
    }

    /// <summary>Groups changes into hunks of script indexes [start, end), merging hunks whose context would touch.</summary>
    private static List<(int Start, int End)> Hunks(List<(Op Op, int A, int B)> script, int context)
    {
        var hunks = new List<(int, int)>();
        var i = 0;
        while (i < script.Count)
        {
            if (script[i].Op == Op.Equal)
            {
                i++;
                continue;
            }

            var start = Math.Max(0, i - context);
            var end = i;
            while (end < script.Count)
            {
                if (script[end].Op != Op.Equal)
                {
                    end++;
                    continue;
                }

                var run = end;
                while (run < script.Count && script[run].Op == Op.Equal)
                    run++;
                if (run < script.Count && run - end <= 2 * context)
                {
                    end = run;
                    continue;
                }

                end = Math.Min(script.Count, end + context);
                break;
            }

            if (hunks.Count > 0 && hunks[^1].Item2 >= start)
                hunks[^1] = (hunks[^1].Item1, end);
            else
                hunks.Add((start, end));
            i = end;
        }

        return hunks;
    }

    private static void AppendHunk(StringBuilder sb, List<(Op Op, int A, int B)> script, int start, int end,
        string[] a, string[] b, bool aNewline, bool bNewline)
    {
        int aStart = -1, bStart = -1, aCount = 0, bCount = 0;
        for (var i = start; i < end; i++)
        {
            var (op, ai, bi) = script[i];
            if (op != Op.Insert)
            {
                if (aStart < 0)
                    aStart = ai;
                aCount++;
            }

            if (op != Op.Delete)
            {
                if (bStart < 0)
                    bStart = bi;
                bCount++;
            }
        }

        // An empty side names the line before the hunk (0 at the top), as GNU diff does.
        var firstA = script[start].A;
        var firstB = script[start].B;
        sb.Append("@@ -").Append(Range(aCount == 0 ? firstA : aStart + 1, aCount))
            .Append(" +").Append(Range(bCount == 0 ? firstB : bStart + 1, bCount)).Append(" @@\n");
        for (var i = start; i < end; i++)
        {
            var (op, ai, bi) = script[i];
            switch (op)
            {
                case Op.Equal:
                    sb.Append(' ').Append(a[ai]).Append('\n');
                    if (ai == a.Length - 1 && !aNewline)
                        sb.Append("\\ No newline at end of file\n");
                    break;
                case Op.Delete:
                    sb.Append('-').Append(a[ai]).Append('\n');
                    if (ai == a.Length - 1 && !aNewline)
                        sb.Append("\\ No newline at end of file\n");
                    break;
                default:
                    sb.Append('+').Append(b[bi]).Append('\n');
                    if (bi == b.Length - 1 && !bNewline)
                        sb.Append("\\ No newline at end of file\n");
                    break;
            }
        }
    }

    private static string Range(int start, int count) =>
        count == 1 ? start.ToString(CultureInfo.InvariantCulture) : string.Create(CultureInfo.InvariantCulture, $"{start},{count}");
}
