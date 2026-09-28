using System.Collections.Immutable;
using System.Globalization;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Text;

namespace Maquettiste.Engine.Tests.Text;

public sealed class InflectorTests
{
    private readonly Inflector _inflector = new();

    [Theory]
    [InlineData("invoice", "invoices")]
    [InlineData("category", "categories")]
    [InlineData("query", "queries")]
    [InlineData("day", "days")]
    [InlineData("box", "boxes")]
    [InlineData("match", "matches")]
    [InlineData("address", "addresses")]
    [InlineData("dish", "dishes")]
    [InlineData("status", "statuses")]
    [InlineData("bus", "buses")]
    [InlineData("alias", "aliases")]
    [InlineData("analysis", "analyses")]
    [InlineData("axis", "axes")]
    [InlineData("index", "indices")]
    [InlineData("matrix", "matrices")]
    [InlineData("knife", "knives")]
    [InlineData("wolf", "wolves")]
    [InlineData("half", "halves")]
    [InlineData("roof", "roofs")]
    [InlineData("hero", "heroes")]
    [InlineData("photo", "photos")]
    [InlineData("quiz", "quizzes")]
    [InlineData("person", "people")]
    [InlineData("child", "children")]
    [InlineData("man", "men")]
    [InlineData("mouse", "mice")]
    [InlineData("criterion", "criteria")]
    [InlineData("movie", "movies")]
    [InlineData("money", "money")]
    [InlineData("series", "series")]
    [InlineData("news", "news")]
    [InlineData("data", "data")]
    public void Pluralizes_and_singularizes_words(string singular, string plural)
    {
        Assert.Equal(plural, _inflector.Pluralize(singular));
        Assert.Equal(singular, _inflector.Singularize(plural));
    }

    [Theory]
    [InlineData("Person", "People")]
    [InlineData("PERSON", "PEOPLE")]
    [InlineData("SalesPerson", "SalesPeople")]
    [InlineData("InvoiceLine", "InvoiceLines")]
    [InlineData("invoice_line", "invoice_lines")]
    [InlineData("LineItem", "LineItems")]
    [InlineData("OrderStatus", "OrderStatuses")]
    [InlineData("ProductCategory", "ProductCategories")]
    [InlineData("Inbox", "Inboxes")]
    public void Inflects_the_last_word_and_keeps_its_case(string singular, string plural)
    {
        Assert.Equal(plural, _inflector.Pluralize(singular));
        Assert.Equal(singular, _inflector.Singularize(plural));
    }

    [Theory]
    [InlineData("people")]
    [InlineData("invoices")]
    [InlineData("categories")]
    public void Pluralizing_a_plural_keeps_it(string plural) => Assert.Equal(plural, _inflector.Pluralize(plural));

    [Theory]
    [InlineData("databases", "database")]
    [InlineData("cases", "case")]
    [InlineData("responses", "response")]
    [InlineData("ties", "tie")]
    [InlineData("status", "status")]
    [InlineData("class", "class")]
    [InlineData("houses", "house")]
    [InlineData("warehouses", "warehouse")]
    [InlineData("causes", "cause")]
    [InlineData("clauses", "clause")]
    [InlineData("uses", "use")]
    [InlineData("menus", "menu")]
    [InlineData("courses", "course")]
    [InlineData("OrderStatuses", "OrderStatus")]
    [InlineData("campuses", "campus")]
    [InlineData("viruses", "virus")]
    [InlineData("bonus", "bonus")]
    [InlineData("focus", "focus")]
    [InlineData("atlas", "atlas")]
    [InlineData("alias", "alias")]
    [InlineData("gas", "gas")]
    [InlineData("sagas", "saga")]
    [InlineData("theses", "thesis")]
    [InlineData("addresses", "address")]
    public void Singularizes_common_words(string plural, string singular) => Assert.Equal(singular, _inflector.Singularize(plural));

    [Fact]
    public void Project_overrides_win()
    {
        var settings = new InflectionSettings
        {
            Plurals = ImmutableDictionary<string, string>.Empty.Add("Cactus", "Cactuses").Add("regex", "regexen").Add("staff", "staves"),
            Uncountable = ["Kudos", "Equipment"],
        };
        var inflector = new Inflector(settings);
        Assert.Equal("cactuses", inflector.Pluralize("cactus"));
        Assert.Equal("Cactus", inflector.Singularize("Cactuses"));
        Assert.Equal("Regexen", inflector.Pluralize("Regex"));
        Assert.Equal("UserRegexen", inflector.Pluralize("UserRegex"));
        Assert.Equal("regex", inflector.Singularize("regexen"));
        Assert.Equal("staves", inflector.Pluralize("staff"));
        Assert.Equal("Kudos", inflector.Pluralize("Kudos"));
        Assert.Equal("kudos", inflector.Singularize("kudos"));
        Assert.Equal("cacti", new Inflector().Pluralize("cactus"));
    }

    [Fact]
    public void Edge_cases_pass_through()
    {
        Assert.Equal("", _inflector.Pluralize(""));
        Assert.Equal("__", _inflector.Pluralize("__"));
        Assert.Equal("Item2s", _inflector.Pluralize("Item2"));
        Assert.Equal("invoices_", _inflector.Pluralize("invoice_"));
    }

    [Fact]
    public void Inflection_is_culture_invariant()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
            Assert.Equal("Titles", _inflector.Pluralize("Title"));
            Assert.Equal("INVOICES", _inflector.Pluralize("INVOICE"));
            Assert.Equal("Indices", _inflector.Pluralize("Index"));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
