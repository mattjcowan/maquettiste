using Maquettiste.Engine.Expressions;

namespace Maquettiste.Engine.Tests.Expressions;

/// <summary>The portable subset of regular expressions (next-phase-3-design.md section 3.1, task H2).</summary>
public sealed class PortableRegexTests
{
    [Theory]
    [InlineData("^[A-Z]{3}$")]
    [InlineData("^[0-9]+(\\.[0-9]{1,2})?$")]
    [InlineData("^INV-[0-9]{4,}$")]
    [InlineData("a|b|")]
    [InlineData("(?:ab)+?c*?d??e{2}?")]
    [InlineData("[^a-z_]")]
    [InlineData("[-a]")]
    [InlineData("[a-]")]
    [InlineData("[a-z-]")]
    [InlineData("[-a-z]")]
    [InlineData("[a-z\\-9]")]
    [InlineData("[\\]\\-\\\\]")]
    [InlineData("[.*+?(){}|$^]")]
    [InlineData(".*")]
    [InlineData("x{0,255}")]
    [InlineData("\\.\\*\\+\\?\\(\\)\\[\\]\\{\\}\\|\\^\\$\\/\\\\")]
    [InlineData("\\n\\r\\t")]
    [InlineData("é😀[😀-😂]")]
    [InlineData("")]
    public void The_subset_is_accepted(string pattern)
    {
        Assert.True(PortableRegex.TryValidate(pattern, out var problem), problem);
        Assert.Null(problem);
    }

    [Theory]
    [InlineData("^\\d{3}$", "the shorthand \\d")]
    [InlineData("\\w+", "the shorthand \\w")]
    [InlineData("\\s", "the shorthand \\s")]
    [InlineData("\\D", "the shorthand \\D")]
    [InlineData("[\\d]", "the shorthand \\d")]
    [InlineData("\\bword", "the word boundary")]
    [InlineData("(a)\\1", "a back-reference")]
    [InlineData("a(?=b)", "lookaround")]
    [InlineData("a(?!b)", "lookaround")]
    [InlineData("(?<=a)b", "lookaround")]
    [InlineData("(?<!a)b", "lookaround")]
    [InlineData("(?<name>a)", "a named group")]
    [InlineData("(?i)a", "only '(?:' is portable")]
    [InlineData("[[:alpha:]]", "a POSIX class")]
    [InlineData("[a[:digit:]]", "a POSIX class")]
    [InlineData("a**", "a quantifier on a quantifier")]
    [InlineData("*a", "nothing to repeat")]
    [InlineData("^*", "nothing to repeat")]
    [InlineData("(|+)", "nothing to repeat")]
    [InlineData("[z-a]", "end comes before its start")]
    [InlineData("[a-z-9]", "a '-' right after a range")]
    [InlineData("[0-9-_]", "a '-' right after a range")]
    [InlineData("[a-c-e]", "a '-' right after a range")]
    [InlineData("[a-z--]", "a '-' right after a range")]
    [InlineData("x{256}", "a bound above 255")]
    [InlineData("x{3,2}", "maximum is below its minimum")]
    [InlineData("x{2", "an unclosed bound")]
    [InlineData("{", "nothing to repeat")]
    [InlineData("a{,3}", "a '{' that starts no bound")]
    [InlineData("(a", "an unclosed group")]
    [InlineData("a)", "an unmatched ')'")]
    [InlineData("[a", "an unclosed bracket class")]
    [InlineData("[]a]", "an empty bracket class")]
    [InlineData("a]", "an unescaped ']'")]
    [InlineData("a}", "an unescaped '}'")]
    [InlineData("\\q", "the escape \\q")]
    [InlineData("\\-", "the escape \\-")]
    [InlineData("\\x41", "the escape \\x")]
    [InlineData("\\u0041", "the escape \\u")]
    [InlineData("a\\", "ends with '\\'")]
    [InlineData("[a[b]", "an unescaped '['")]
    public void Each_refused_form_says_why(string pattern, string words)
    {
        Assert.False(PortableRegex.TryValidate(pattern, out var problem));
        Assert.Contains(words, problem, StringComparison.Ordinal);
        Assert.Contains("outside the portable subset", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void The_problem_names_the_position()
    {
        Assert.False(PortableRegex.TryValidate("^[A-Z]\\d", out var problem));
        Assert.Contains("at position 7", problem, StringComparison.Ordinal);
    }
}
