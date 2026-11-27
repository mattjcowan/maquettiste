namespace Maquettiste.Testing;

/// <summary>
/// The test class (or test method) tests projection (tables laid out from entities by conventions, entity mappings and overlays), so it reads the
/// projected copies of the model fixtures (<c>tests/fixtures/models/&lt;name&gt;-projected</c>, see
/// <see cref="Fixtures.Path(string[])"/>) instead of the fixtures themselves, whose tables are designed tables with bindings since
/// 0.29.0. Temporary: pull request 3 of retiring projection deletes projection, these classes' projection tests, the copies and this
/// attribute (docs/engineering/retire-projection-design.md section 9).
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = false)]
public sealed class UsesProjectedFixturesAttribute : Attribute
{
}
