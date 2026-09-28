using Maquettiste.Engine.Model;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Resolution;

/// <summary>SPEC Section 7's default physical mapping table, one test per shape (engine-design.md section 7.4).</summary>
public sealed class RelationShapeTests
{
    private static (ModelBuilder B, EntityBuilder A, EntityBuilder C) TwoEntities()
    {
        var b = new ModelBuilder(seed: 11);
        var a = b.Entity("User").Key("id", "uuid").Attr("name", "string");
        var c = b.Entity("Team").Key("id", "int64", IdentityStrategy.DatabaseIdentity).Attr("title", "string");
        b.Database("main", Dialect.PostgreSql);
        return (b, a, c);
    }

    [Fact]
    public void One_to_one_is_a_unique_foreign_key_in_the_dependent_table()
    {
        var (b, user, team) = TwoEntities();
        var profile = b.Entity("Profile").Key("id", "uuid");
        b.Relation("has profile", user, profile, fromMax: MaxCardinality.One, toMax: MaxCardinality.One, fromMin: 1, fromRole: "owner", toRole: "profile");
        var db = ResolutionKit.Resolve(b).Db("main");

        var profiles = db.Table("profiles");
        var fk = Assert.Single(profiles.ForeignKeys);
        Assert.Equal(["owner_id"], fk.Columns.Names());
        Assert.Equal("users", fk.ReferencedTable.Name);
        Assert.False(profiles.Column("owner_id").Nullable);
        Assert.Contains(profiles.Uniques, u => u.Columns.Names().SequenceEqual(["owner_id"]) && u.Name == "uq_profiles_owner_id");
        Assert.Empty(db.Table("users").ForeignKeys);
        _ = team;
    }

    [Fact]
    public void One_to_one_tie_puts_the_key_in_the_second_end_unless_the_mapping_names_the_first()
    {
        var (b, user, team) = TwoEntities();
        var rel = b.Relation("pairs with", user, team, fromMax: MaxCardinality.One, toMax: MaxCardinality.One, fromRole: "user", toRole: "team");
        var db = ResolutionKit.Resolve(b).Db("main");
        Assert.Equal("user_id", Assert.Single(db.Table("teams").ForeignKeys).Columns.Single().Name);
        Assert.True(db.Table("teams").Column("user_id").Nullable);

        var main = b.BuildElements().OfType<Database>().Single();
        b.Add(new Mapping { Id = b.NewId(), Name = "pairs", Database = main.Id, Relation = rel.Id, ForeignKeyEnd = rel.EndIds[0] });
        db = ResolutionKit.Resolve(b).Db("main");
        Assert.Empty(db.Table("teams").ForeignKeys);
        Assert.Equal("team_id", Assert.Single(db.Table("users").ForeignKeys).Columns.Single().Name);
    }

    [Fact]
    public void One_to_many_is_a_foreign_key_on_the_many_side_nullable_when_the_one_end_is_optional()
    {
        var (b, user, team) = TwoEntities();
        b.Relation("leads", team, user, fromMax: MaxCardinality.One, fromRole: "team", toRole: "member", toNavigation: "members");
        var model = ResolutionKit.Resolve(b);
        var users = model.Db("main").Table("users");
        var fk = Assert.Single(users.ForeignKeys);
        Assert.Equal("fk_users_team_id", fk.Name);
        Assert.Equal("teams", fk.ReferencedTable.Name);
        Assert.Equal("no-action", fk.OnDelete);
        var column = users.Column("team_id");
        Assert.True(column.Nullable);
        Assert.True(column.IsForeignKey);
        Assert.Equal("int64", column.Type);
        Assert.False(column.Identity);
        Assert.Equal("foreign-key", model.Relation("leads").Mappings["main"].Shape);
        Assert.Same(fk, model.Relation("leads").Mappings["main"].ForeignKey);
    }

    [Fact]
    public void Many_to_many_without_attributes_is_a_junction_with_a_composite_key()
    {
        var (b, user, team) = TwoEntities();
        b.Relation("is member of", user, team, fromRole: "member", toRole: "team", fromNavigation: "members", toNavigation: "teams");
        var model = ResolutionKit.Resolve(b);
        var junction = model.Db("main").Table("user_team");
        Assert.True(junction.IsJunction);
        Assert.Equal(["member_id", "team_id"], junction.Columns.Names());
        Assert.Equal(["member_id", "team_id"], junction.PrimaryKey!.Columns.Names());
        Assert.All(junction.ForeignKeys, fk => Assert.Equal("cascade", fk.OnDelete));
        Assert.Same(junction, model.Relation("is member of").Mappings["main"].JunctionTable);
        var join = model.Entity("User").Mappings["main"].Joins["teams"];
        Assert.Equal(2, join.Steps.Count);
        Assert.All(join.Steps, s => Assert.True(s.ViaJunction));
        Assert.Equal("users", join.Steps[0].FromTable.Name);
        Assert.Equal("teams", join.Steps[1].ToTable.Name);
    }

    [Fact]
    public void Relation_with_attributes_is_a_junction_holding_them_by_default()
    {
        var (b, user, team) = TwoEntities();
        b.Relation("is member of", user, team, fromRole: "member", toRole: "team")
            .Attr("role", "string", a => a.Length(40)).Attr("joinedOn", "date", a => a.Required());
        var junction = ResolutionKit.Resolve(b).Db("main").Table("user_team");
        Assert.Equal(["member_id", "team_id", "role", "joined_on"], junction.Columns.Names());
        Assert.True(junction.Column("role").Nullable);
        Assert.False(junction.Column("joined_on").Nullable);
        Assert.Equal(["member_id", "team_id"], junction.PrimaryKey!.Columns.Names());
    }

    [Fact]
    public void Relation_with_attributes_is_promoted_when_conventions_say_so()
    {
        var (b, user, team) = TwoEntities();
        var rel = b.Relation("is member of", user, team, fromRole: "member", toRole: "team", fromNavigation: "members", toNavigation: "teams")
            .Attr("role", "string", a => a.Length(40));
        b.Settings(s => s with { Conventions = new Conventions { RelationsWithAttributes = RelationShape.Promoted } });
        var model = ResolutionKit.Resolve(b);

        var promoted = model.Entity("UserTeam");
        Assert.True(promoted.IsPromoted);
        Assert.Equal(rel.Id, promoted.Id);
        Assert.Same(model.Relation("is member of"), promoted.PromotedFrom);
        // The key is the composite of the end references: one synthesized attribute per end key attribute.
        Assert.Equal(["memberId", "teamId", "role"], promoted.Attributes.Select(a => a.Name));
        Assert.Equal(["memberId", "teamId"], promoted.Key!.Attributes.Select(a => a.Name));
        Assert.Equal("application", promoted.Key.Strategy);
        Assert.All(promoted.Key.Attributes, a => Assert.True(a.Required && ReferenceEquals(a.Owner, promoted)));
        Assert.Equal("uuid", promoted.Key.Attributes[0].Type.Builtin);
        Assert.Same(promoted.Key.Attributes[0], model.Find(promoted.Key.Attributes[0].Id));
        var table = model.Db("main").Table("user_teams");
        Assert.False(table.IsJunction);
        Assert.Same(promoted, table.Entity);
        Assert.Equal(["member_id", "team_id", "role"], table.Columns.Names());
        Assert.Same(table, promoted.Mappings["main"].Table);
        Assert.Equal(["member_id", "team_id", "role"], promoted.Mappings["main"].Columns.Select(c => c.Column.Name));
        Assert.Same(promoted.Key.Attributes[1], table.Column("team_id").Attribute);
        Assert.Equal(table.PrimaryKey!.Columns, promoted.Key.Attributes.Select(a => promoted.Mappings["main"].Columns.Single(c => c.AttributePath == a.Id).Column));
        Assert.Equal("promoted", model.Relation("is member of").Mappings["main"].Shape);
        Assert.Same(promoted, model.Relation("is member of").Mappings["main"].PromotedEntity);

        // Two many-to-one relations with ids <relationId>.<endId>, each realized by one foreign key of the promoted table.
        var ids = rel.EndIds.Select(e => rel.Id + "." + e).ToList();
        foreach (var id in ids)
        {
            var promotion = Assert.IsType<Maquettiste.Engine.Resolution.RRelation>(model.Find(id));
            Assert.Equal("one-to-many", promotion.Cardinality);
            Assert.Same(promoted, promotion.Ends[1].Entity);
            Assert.Contains(table.ForeignKeys, fk => ReferenceEquals(fk, promotion.Mappings["main"].ForeignKey));
        }

        Assert.Equal(["member", "team"], promoted.Navigations.Select(n => n.Name));
        Assert.Equal(2, model.Entity("User").Mappings["main"].Joins["teams"].Steps.Count);

        // Each end entity also takes part in its promotion relation.
        Assert.Contains(model.Entity("User").Relations, r => r.Id == ids[0]);
        Assert.Contains(model.Entity("Team").Relations, r => r.Id == ids[1]);
        Assert.DoesNotContain(model.Entity("User").Relations, r => r.Id == ids[1]);
    }

    [Fact]
    public void Promoted_relation_with_duplicates_has_a_surrogate_key_attribute()
    {
        var (b, user, team) = TwoEntities();
        var rel = b.Relation("visits", user, team, fromRole: "visitor", toRole: "team").AllowDuplicates().Attr("at", "datetime");
        b.Mapping(b.Database("other", Dialect.Sqlite), rel).Shape(RelationShape.Promoted);
        var model = ResolutionKit.Resolve(b);
        var promoted = model.Entities.Single(e => e.IsPromoted);
        Assert.Equal(["id", "at"], promoted.Attributes.Select(a => a.Name));
        var key = Assert.Single(promoted.Key!.Attributes);
        Assert.Equal(("id", "int64", "database-identity"), (key.Name, key.Type.Builtin, promoted.Key.Strategy));
        var mapping = promoted.Mappings["other"];
        var id = mapping.Columns.Single(c => ReferenceEquals(c.Column.Attribute, key)).Column;
        Assert.Equal("id", id.Name);
        Assert.True(id.IsPrimaryKey && id.Identity);
        Assert.Equal(["id", "at"], mapping.Columns.Select(c => c.Column.Name));
        Assert.DoesNotContain("main", promoted.Mappings.Keys); // "main" keeps the junction
    }

    [Fact]
    public void N_ary_relation_is_a_junction_with_one_foreign_key_per_end()
    {
        var (b, user, team) = TwoEntities();
        var project = b.Entity("Project").Key("id", "uuid");
        b.NAryRelation("assignment").End(user, "assignee").End(team, "team").End(project, "project");
        var model = ResolutionKit.Resolve(b);
        var junction = model.Db("main").Table("user_team_project");
        Assert.Equal(["assignee_id", "team_id", "project_id"], junction.Columns.Names());
        Assert.Equal(3, junction.ForeignKeys.Count);
        Assert.Equal(["users", "teams", "projects"], junction.ForeignKeys.Select(f => f.ReferencedTable.Name));
        Assert.Equal("n-ary", model.Relation("assignment").Cardinality);
    }

    [Fact]
    public void Duplicates_and_ordered_ends_give_the_junction_a_surrogate_key_and_position()
    {
        var (b, user, team) = TwoEntities();
        b.Relation("visited", user, team, fromRole: "visitor", toRole: "team").AllowDuplicates()
            .WithEnd(1, e => e with { Ordered = true });
        var junction = ResolutionKit.Resolve(b).Db("main").Table("user_team");
        Assert.Equal(["id", "visitor_id", "team_id", "position"], junction.Columns.Names());
        Assert.Equal(["id"], junction.PrimaryKey!.Columns.Names());
        Assert.True(junction.Column("id").Identity);
        Assert.Empty(junction.Uniques);
    }

    [Fact]
    public void Self_relation_is_distinguished_by_role()
    {
        var b = new ModelBuilder(seed: 12);
        var category = b.Entity("Category").Key("id", "int32");
        b.Relation("has parent", category, category, toMax: MaxCardinality.One, fromRole: "child", toRole: "parent",
            fromNavigation: "children", toNavigation: "parent");
        b.Database("main", Dialect.PostgreSql);
        var model = ResolutionKit.Resolve(b);
        var fk = Assert.Single(model.Db("main").Table("categories").ForeignKeys);
        Assert.Equal(["parent_id"], fk.Columns.Names());
        var joins = model.Entity("Category").Mappings["main"].Joins;
        Assert.Equal("parent_id", joins["parent"].Steps[0].FromColumns.Single().Name);
        Assert.Equal("parent_id", joins["children"].Steps[0].ToColumns.Single().Name);
    }

    [Fact]
    public void Composition_cascades_and_end_intents_map_to_actions()
    {
        var (b, user, team) = TwoEntities();
        b.Relation("owns", team, user, fromMax: MaxCardinality.One, fromMin: 1, fromRole: "team", toRole: "user").Kind(RelationKind.Composition);
        var project = b.Entity("Project").Key("id", "uuid");
        b.Relation("sponsors", team, project, fromMax: MaxCardinality.One, fromRole: "sponsor", toRole: "project")
            .WithEnd(0, e => e with { OnDelete = ReferentialIntent.SetNull });
        var db = ResolutionKit.Resolve(b).Db("main");
        Assert.Equal("cascade", db.Table("users").ForeignKeys.Single().OnDelete);
        Assert.Equal("set-null", db.Table("projects").ForeignKeys.Single().OnDelete);
    }
}
