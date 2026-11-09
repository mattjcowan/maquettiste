using Maquettiste.Engine.Model;
using Maquettiste.Engine.Resolution;

namespace Maquettiste.Engine.Tests.Resolution;

/// <summary>
/// The join paths of a relation with an end of several types (several-types-design.md decision 48), built from hand-made storage as step 2
/// leaves it: a Comment attached to a Part or an Assembly. One step when an end's rows hold the link, two through a link table; the type
/// column's codes, the link table's constants and what picks a target's rows as conditions on the step they belong to; <c>from_type</c> and
/// <c>to_type</c> only on an end with several types; paths ordered by the target at the from end, then at the to end.
/// </summary>
public sealed class RelationTypeJoinTests
{
    private static readonly DependencyKeyCache Keys = new();

    private static REntity Entity(string name) => new() { Id = "id-" + name, Name = name };

    private static Placement Placed(REntity entity) => new(entity, new Entity { Id = entity.Id, Name = entity.Name }, null);

    private static (TableBuild Build, Dictionary<string, RColumn> Columns) Table(string name, params string[] columns)
    {
        var table = new RTable { Id = name, Key = name, Name = name };
        var build = new TableBuild(Keys, table, null, null, name);
        var byName = new Dictionary<string, RColumn>(StringComparer.Ordinal);
        foreach (var c in columns)
        {
            var column = new RColumn { Id = c, Key = name + "." + c, Name = c, Table = table };
            build.Columns.Add(column);
            byName[c] = column;
        }

        return (build, byName);
    }

    private sealed class Model
    {
        public readonly REnd CommentEnd = new() { Id = "end-comment", Role = "comment" };
        public readonly REnd AttachedTo = new() { Id = "end-attached", Role = "attachedTo", HasSeveralTypes = true };
        public readonly REntity Comment = Entity("Comment");
        public readonly REntity Part = Entity("Part");
        public readonly REntity Assembly = Entity("Assembly");
        public readonly Placement CommentP;
        public readonly Placement PartP;
        public readonly Placement AssemblyP;
        public readonly (TableBuild Build, Dictionary<string, RColumn> Columns) Comments = Table("comments", "id", "attached_type", "attached_id");
        public readonly (TableBuild Build, Dictionary<string, RColumn> Columns) Parts = Table("parts", "id");
        public readonly (TableBuild Build, Dictionary<string, RColumn> Columns) Assemblies = Table("assemblies", "id");
        public readonly (TableBuild Build, Dictionary<string, RColumn> Columns) Links = Table("links", "comment_id", "target_type", "target_id", "relation_code");

        public Model()
        {
            CommentP = Placed(Comment);
            PartP = Placed(Part);
            AssemblyP = Placed(Assembly);
            CommentP.Table = Comments.Build;
            PartP.Table = Parts.Build;
            AssemblyP.Table = Assemblies.Build;
        }

        public TypeLinks Links3(bool holderResult = true)
        {
            // Way 3: comments.attached_type and comments.attached_id; the comment end's rows hold the link.
            var links = Storage();
            foreach (var (target, codes) in new[] { (PartP, "P"), (AssemblyP, "A") })
            {
                var referenced = target == PartP ? Parts : Assemblies;
                var pointer = new TypeLinkPointer
                {
                    End = AttachedTo, Target = target, IdColumn = "attached_id", TypeColumn = "attached_type",
                    Result = new RRelationLinkEnd
                    {
                        End = AttachedTo, Type = target.Entity, Columns = [Comments.Columns["attached_id"]], ReferencedTable = referenced.Build.Table,
                        ReferencedColumns = [referenced.Columns["id"]], TypeColumn = Comments.Columns["attached_type"], Codes = [codes],
                    },
                };
                var holder = new TypeLinkPointer
                {
                    End = CommentEnd, Target = CommentP, IsHolder = true,
                    Result = holderResult ? new RRelationLinkEnd { End = CommentEnd, Type = Comment, IsHolder = true } : null,
                };
                links.Pairs.Add(new TypeLink { Table = Comments.Build, Ends = [holder, pointer], Result = new RRelationLink { Table = Comments.Build.Table } });
            }

            return links;
        }

        public TypeLinks Links9()
        {
            // A link table: links.comment_id refers to comments.id; links.target_type and links.target_id point at a part or an assembly.
            var links = Storage();
            foreach (var target in new[] { PartP, AssemblyP })
            {
                var referenced = target == PartP ? Parts : Assemblies;
                var comment = new TypeLinkPointer
                {
                    End = CommentEnd, Target = CommentP, IdColumn = "comment_id",
                    Result = new RRelationLinkEnd
                    {
                        End = CommentEnd, Type = Comment, Columns = [Links.Columns["comment_id"]], ReferencedTable = Comments.Build.Table,
                        ReferencedColumns = [Comments.Columns["id"]],
                    },
                };
                var attached = new TypeLinkPointer
                {
                    End = AttachedTo, Target = target, IdColumn = "target_id", TypeColumn = "target_type",
                    Result = new RRelationLinkEnd
                    {
                        End = AttachedTo, Type = target.Entity, Columns = [Links.Columns["target_id"]], ReferencedTable = referenced.Build.Table,
                        ReferencedColumns = [referenced.Columns["id"]], TypeColumn = Links.Columns["target_type"], Codes = [target.Entity.Name],
                    },
                };
                links.Pairs.Add(new TypeLink { Table = Links.Build, Ends = [comment, attached], Result = new RRelationLink { Table = Links.Build.Table } });
            }

            return links;
        }

        private TypeLinks Storage()
        {
            var commentEnd = new TypeLinkEnd { End = CommentEnd };
            commentEnd.Targets.Add(CommentP);
            var attachedEnd = new TypeLinkEnd { End = AttachedTo };
            attachedEnd.Targets.AddRange([PartP, AssemblyP]);
            return new TypeLinks { Mapping = new Mapping { Id = "m", Name = "m", Database = "db" }, Ends = [commentEnd, attachedEnd] };
        }
    }

    private static IReadOnlyList<RColumnCondition> NoRows(Placement target, RTable table) => [];

    private static string Describe(RJoinPath path) => string.Join(" | ", path.Steps.Select(s =>
        $"{s.FromTable.Name}({string.Join(',', s.FromColumns.Select(c => c.Name))}) > {s.ToTable.Name}({string.Join(',', s.ToColumns.Select(c => c.Name))})"
        + (s.ViaJunction ? " via" : "")
        + string.Concat(s.Conditions.Select(c => $" [{c.Column.Name} in {string.Join(',', c.Values)}]"))))
        + $" from={path.FromType?.Name} to={path.ToType?.Name}";

    [Fact]
    public void The_holding_end_reads_one_step_to_each_target_with_the_type_codes()
    {
        var m = new Model();
        var paths = TypeJoinPaths.Of(m.Links3(), m.CommentEnd, m.AttachedTo, [], NoRows);
        Assert.Equal(
            [
                "comments(attached_id) > parts(id) [attached_type in P] from= to=Part",
                "comments(attached_id) > assemblies(id) [attached_type in A] from= to=Assembly",
            ],
            paths.Select(p => Describe(p.Path)));
        Assert.All(paths, p => Assert.Same(m.CommentP, p.From));
    }

    [Fact]
    public void Each_target_reads_one_step_into_the_holding_table()
    {
        var m = new Model();
        var paths = TypeJoinPaths.Of(m.Links3(holderResult: false), m.AttachedTo, m.CommentEnd, [], NoRows);
        Assert.Equal(
            [
                "parts(id) > comments(attached_id) [attached_type in P] from=Part to=",
                "assemblies(id) > comments(attached_id) [attached_type in A] from=Assembly to=",
            ],
            paths.Select(p => Describe(p.Path)));
        Assert.Equal([m.PartP, m.AssemblyP], paths.Select(p => p.From));
    }

    [Fact]
    public void A_link_table_takes_two_steps_with_its_constants_on_the_way_in()
    {
        var m = new Model();
        RColumnCondition[] constants = [new() { Column = m.Links.Columns["relation_code"], Values = ["AttachedTo"] }];
        var forth = TypeJoinPaths.Of(m.Links9(), m.CommentEnd, m.AttachedTo, constants, NoRows);
        Assert.Equal(
            [
                "comments(id) > links(comment_id) via [relation_code in AttachedTo] | links(target_id) > parts(id) via [target_type in Part] from= to=Part",
                "comments(id) > links(comment_id) via [relation_code in AttachedTo] | links(target_id) > assemblies(id) via [target_type in Assembly] from= to=Assembly",
            ],
            forth.Select(p => Describe(p.Path)));
        var back = TypeJoinPaths.Of(m.Links9(), m.AttachedTo, m.CommentEnd, constants, NoRows);
        Assert.Equal(
            "parts(id) > links(target_id) via [target_type in Part] [relation_code in AttachedTo] | links(comment_id) > comments(id) via from=Part to=",
            Describe(back[0].Path));
    }

    [Fact]
    public void Paths_follow_the_target_order_and_leave_out_a_pair_that_did_not_resolve()
    {
        var m = new Model();
        var links = m.Links3();
        links.Pairs.Reverse();
        links.Pairs[0].Result = null; // the assembly's pair, refused by step 2
        var paths = TypeJoinPaths.Of(links, m.CommentEnd, m.AttachedTo, [], NoRows);
        Assert.Equal(["Part"], paths.Select(p => p.To.Entity.Name));

        var both = m.Links3();
        both.Pairs.Reverse();
        Assert.Equal(["Part", "Assembly"], TypeJoinPaths.Of(both, m.CommentEnd, m.AttachedTo, [], NoRows).Select(p => p.To.Entity.Name));
    }

    [Fact]
    public void What_picks_a_targets_rows_goes_on_the_step_that_enters_its_table()
    {
        var m = new Model();
        var kind = new RColumn { Id = "kind", Key = "parts.kind", Name = "kind" };
        var seen = new List<(string, string)>();
        IReadOnlyList<RColumnCondition> Rows(Placement target, RTable table)
        {
            seen.Add((target.Entity.Name, table.Name));
            return target == m.PartP ? [new RColumnCondition { Column = kind, Values = ["Part"] }] : [];
        }

        var paths = TypeJoinPaths.Of(m.Links3(), m.CommentEnd, m.AttachedTo, [], Rows);
        Assert.Equal("comments(attached_id) > parts(id) [attached_type in P] [kind in Part] from= to=Part", Describe(paths[0].Path));
        Assert.Equal([("Part", "parts"), ("Assembly", "assemblies")], seen);
    }
}
