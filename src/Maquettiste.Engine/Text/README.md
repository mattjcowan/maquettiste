# Text

**Owner:** W3 Resolver. See docs/engineering/engine-design.md section 18.

Casing (`Casing`, D26) and the inflector (`Inflector`), shared with the renderer's helpers (W5).

- Implements (complete):
  - `Casing.Words(name)`: split on non-alphanumerics, at lower→upper and at acronym→word boundaries; digits join the preceding
    word; words lowercased (`HTTPServer2Id` → `http`, `server2`, `id`). `Casing.Apply(name, CaseStyle)`, `Casing.Join(words, style)`
    and the shortcuts `Snake`, `Kebab`, `Pascal`, `Camel`, `UpperSnake`, `Capitalize`. `Preserve` returns the name unchanged.
    Culture-invariant (tested under tr-TR and de-DE).
  - `Inflector(InflectionSettings? settings = null)`: `Pluralize` and `Singularize` with fixed English rules (irregulars such as
    person/people, uncountables such as money/series/data, suffix rules for -y, -s/-x/-ch/-sh, -us, -is, -f/-fe, -o, -ix/-ex).
    Singularizing keeps words in -ss and -is and an explicit list of singular -us words (status, bus, campus, virus, bonus, …,
    whose plurals -uses lose their `es`); every other word in -s just loses the `s` (`houses` → `house`, `menus` → `menu`,
    `uses` → `use`), as the Rails inflector does. A singular -us word missing from the list needs an `inflection.plurals` entry.
    Rules apply to the last word of a name and keep its case shape (`SalesPerson` → `SalesPeople`, `PERSON` → `PEOPLE`).
    `inflection.plurals` (singular → plural) and `inflection.uncountable` win over the built-in lists, matched case-insensitively
    on the whole name, then on its last word; overrides are applied in ordinal key order. Results are memoized per instance in a
    `ConcurrentDictionary`, so an instance is thread-safe and meant to live for one run (there is no static cache).
    `MemoizedWords` (internal) counts the memoized results, so an owner that keeps an instance across runs can bound it
    (`ModelResolver` does).
- Deviations: `Inflector` gained the optional `InflectionSettings` constructor parameter (it had only the implicit parameterless
  constructor); `Casing` gained the members above. Both types stay `internal`. A plural input is left as is by `Pluralize`
  (`people`, `invoices`), and `index` pluralizes to `indices` (override with `inflection.plurals` for `indexes`). Overrides
  are stored lowercase and take the input's case shape, so `"SalesPerson": "SalesTeam"` gives `SalesPerson` → `Salesteam`
  (inner word boundaries of an override value are not kept).
- Consumes: nothing.

Tests: `tests/Maquettiste.Engine.Tests/Text/`.
