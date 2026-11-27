using System.Runtime.CompilerServices;
using Maquettiste.Testing;
using Xunit.v3;

namespace Maquettiste.Cli.Tests;

/// <summary>Points the tests of a class or method with <see cref="UsesProjectedFixturesAttribute"/> at the projected fixture copies (temporary).</summary>
internal static class ProjectedFixtures
{
    [ModuleInitializer]
    internal static void Register() =>
        Fixtures.UsesProjectedFixtures = () =>
            (TestContext.Current.TestClass is IXunitTestClass test && test.Class.IsDefined(typeof(UsesProjectedFixturesAttribute), false))
            || (TestContext.Current.TestMethod is IXunitTestMethod method && method.Method.IsDefined(typeof(UsesProjectedFixturesAttribute), false));
}
