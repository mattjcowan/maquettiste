using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Hashing;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Processes;
using Maquettiste.Engine.Tests.Validation;
using Maquettiste.Engine.Validation;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Processes;

/// <summary>
/// The process rules MQ9001 to MQ9022, MQ9101 to MQ9106 and MQ9201 to MQ9210 (phase-3-design.md section 3, lifecycle-design.md
/// section 6) over the <c>process-basics</c> fixture: the fixture raises none of them but MQ9209, since it names its subject in the
/// one-subject form, each failing case mutates it so one rule fires, and each passing case makes a nearby change that must not raise
/// the rule.
/// </summary>
public sealed class ProcessRuleTests
{
    // process-basics ids.
    private const string P = "01JPRC00000000000000000001";
    private const string Order = "01JENT00000000000000000001";
    private const string Status = "01JATT00000000000000000002";
    private const string Clerk = "01JACT00000000000000000001";
    private const string Manager = "01JACT00000000000000000002";
    private const string Finance = "01JACT00000000000000000003";
    private const string Draft = "01JSTA00000000000000000001";
    private const string Decide = "01JSTA00000000000000000002";
    private const string Review = "01JSTA00000000000000000003";
    private const string Pending = "01JSTA00000000000000000004";
    private const string Escalated = "01JSTA00000000000000000005";
    private const string Resume = "01JSTA00000000000000000006";
    private const string Fulfilment = "01JSTA00000000000000000007";
    private const string Shipping = "01JSTA00000000000000000008";
    private const string Packing = "01JSTA00000000000000000009";
    private const string Shipped = "01JSTA00000000000000000010";
    private const string Invoicing = "01JSTA00000000000000000012";
    private const string Closed = "01JSTA00000000000000000014";
    private const string Submit = "01JPRX00000000000000000001";
    private const string Approve = "01JPRX00000000000000000002";
    private const string Cancel = "01JPRX00000000000000000005";
    private const string IsLarge = "01JPRX00000000000000000011";
    private const string ManagerReview = "01JPRX00000000000000000031";

    // What the binding cases add (lifecycle-design.md decisions 11 to 17).
    private const string StateAttr = "01JATT00000000000000000050";
    private const string OrderState = "01JRTP00000000000000000001";
    private const string RowDraft = "01JRWS00000000000000000001";
    private const string RowClosed = "01JRWS00000000000000000004";
    private const string ReturnEntity = "01JENT00000000000000000005";
    private const string ReturnStatus = "01JATT00000000000000000051";
    private const string RushOrder = "01JENT00000000000000000003";

    // The second process some cases add.
    private const string Q = "01JPRC00000000000000000002";
    private const string QStart = "01JSTA00000000000000000101";
    private const string QEnd = "01JSTA00000000000000000102";
    private const string QGo = "01JPRX00000000000000000101";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static int _next;

    private static string NewId() => "01JNEW" + Interlocked.Increment(ref _next).ToString("D20", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The fixture's files as mutable JSON, keyed by the path under <c>.maquettiste/</c>.</summary>
    private sealed class Mutable
    {
        public SortedDictionary<string, JsonObject> Files { get; } = new(StringComparer.Ordinal);

        /// <summary>Extension documents the model adds (the fixture's own extension is not loaded here).</summary>
        public List<JsonObject> Extensions { get; } = [];

        public JsonObject Process => Files["model/processes/order-lifecycle.json"];

        public JsonObject Entity => Files["model/entities/order.json"];

        public JsonObject Enum => Files["model/enums/order-status.json"];

        public JsonObject Diagram => Files["model/diagrams/order-lifecycle.json"];

        public JsonObject ReferenceType => Files["model/reference-types/order-state.json"];

        public JsonArray Rows => Files["model/seeds/order-state/order-state.json"]["rows"]!.AsArray();

        public JsonObject StateAttribute => Entity["attributes"]!.AsArray().Single(a => (string?)a!["id"] == StateAttr)!.AsObject();

        /// <summary>
        /// Binds the lifecycle to a new attribute 'state' of Order typed by the reference type OrderState, whose rows DRF, REV, FUL and CLS
        /// are the root states' codes (lifecycle-design.md decision 11); the default is DRF. No binding rule fires.
        /// </summary>
        public void BindReference()
        {
            Files["model/reference-types/order-state.json"] = new JsonObject
            {
                ["kind"] = "reference-type", ["id"] = OrderState, ["name"] = "OrderState",
                ["code"] = new JsonObject { ["id"] = "01JRTP00000000000000000002", ["length"] = 8 },
                ["label"] = new JsonObject { ["id"] = "01JRTP00000000000000000003" },
            };
            Files["model/seeds/order-state/order-state.json"] = new JsonObject
            {
                ["kind"] = "seed", ["id"] = "01JSDE00000000000000000001", ["name"] = "OrderState", ["target"] = OrderState,
                ["columns"] = new JsonArray("code", "label"),
                ["rows"] = new JsonArray(
                    new JsonObject { ["id"] = RowDraft, ["values"] = new JsonArray("DRF", "Draft") },
                    new JsonObject { ["id"] = "01JRWS00000000000000000002", ["values"] = new JsonArray("REV", "Review") },
                    new JsonObject { ["id"] = "01JRWS00000000000000000003", ["values"] = new JsonArray("FUL", "Fulfilment") },
                    new JsonObject { ["id"] = RowClosed, ["values"] = new JsonArray("CLS", "Closed") }),
            };
            Bind(new JsonObject { ["ref"] = OrderState }, "DRF");
        }

        /// <summary>Binds the lifecycle to a new attribute 'state' of Order, a plain string of length 3 allowed DRF, REV, FUL and CLS.</summary>
        public void BindPlain()
        {
            Bind("string", "DRF");
            StateAttribute["length"] = 3;
            StateAttribute["validation"] = new JsonObject { ["allowedValues"] = new JsonArray("DRF", "REV", "FUL", "CLS") };
        }

        /// <summary>Codes the root states DRF, REV, FUL and CLS, and binds the lifecycle to a new attribute 'state' of Order.</summary>
        public void Bind(JsonNode type, JsonNode @default)
        {
            Entity["attributes"]!.AsArray().Add(new JsonObject { ["id"] = StateAttr, ["name"] = "state", ["type"] = type, ["default"] = @default });
            Process["boundAttribute"] = StateAttr;
            Codes("DRF", "REV", "FUL", "CLS");
        }

        /// <summary>Gives the root states Draft, Review, Fulfilment and Closed these codes.</summary>
        public void Codes(JsonNode draft, JsonNode review, JsonNode fulfilment, JsonNode closed)
        {
            State(Draft)["code"] = draft;
            State(Review)["code"] = review;
            State(Fulfilment)["code"] = fulfilment;
            State(Closed)["code"] = closed;
        }

        /// <summary>Adds entity Return with an attribute 'status' typed by OrderStatus (default Draft), listed in the subjects with that binding.</summary>
        public void AddReturnSharingTheEnum()
        {
            UseSubjects();
            Files["model/entities/return.json"] = new JsonObject
            {
                ["kind"] = "entity", ["id"] = ReturnEntity, ["name"] = "Return",
                ["attributes"] = new JsonArray(new JsonObject
                {
                    ["id"] = ReturnStatus, ["name"] = "status", ["type"] = new JsonObject { ["ref"] = "01JENM00000000000000000001" }, ["default"] = "Draft",
                }),
            };
            Process["subjects"]!.AsArray().Add(new JsonObject { ["id"] = NewId(), ["entity"] = ReturnEntity, ["boundAttribute"] = ReturnStatus });
        }

        /// <summary>Adds lifecycle Q, listing these subjects.</summary>
        public void AddLifecycleQ(params (string Entity, string? Bound)[] subjects)
        {
            AddQ();
            var q = Files["model/processes/q.json"];
            q["use"] = "lifecycle";
            q["subjects"] = new JsonArray([.. subjects.Select(s =>
            {
                var entry = new JsonObject { ["id"] = NewId(), ["entity"] = s.Entity };
                if (s.Bound is not null)
                    entry["boundAttribute"] = s.Bound;
                return (JsonNode)entry;
            })]);
        }

        /// <summary>Adds entity RushOrder, derived from Order.</summary>
        public void AddRushOrder() =>
            Files["model/entities/rush-order.json"] = new JsonObject { ["kind"] = "entity", ["id"] = RushOrder, ["name"] = "RushOrder", ["base"] = Order };

        /// <summary>Rewrites the process in the subjects form, as use-subjects does: one entry with the subject and its binding.</summary>
        public JsonObject UseSubjects()
        {
            var entry = new JsonObject { ["id"] = NewId(), ["entity"] = Process["subject"]!.GetValue<string>() };
            if (Process["boundAttribute"] is { } bound)
                entry["boundAttribute"] = bound.GetValue<string>();
            Process.Remove("subject");
            Process.Remove("boundAttribute");
            Process["subjects"] = new JsonArray(entry);
            return entry;
        }

        public JsonArray Transitions => Process["transitions"]!.AsArray();

        public JsonObject Transition(int index) => Transitions[index]!.AsObject();

        public JsonObject State(string id) => Find(Process["states"]!.AsArray(), id) ?? throw new KeyNotFoundException(id);

        public JsonObject Actor(string id) => Files.Values.Single(f => (string?)f["id"] == id);

        public JsonObject Gate => Transition(5)["gate"]!.AsObject();

        public JsonObject Event(string id) => Process["events"]!.AsArray().Single(e => (string?)e!["id"] == id)!.AsObject();

        public void AddTransition(string source, string? trigger, string? eventId, params string[] targets)
        {
            var t = new JsonObject { ["id"] = NewId(), ["source"] = source };
            if (trigger is not null)
                t["trigger"] = trigger;
            if (eventId is not null)
                t["event"] = eventId;
            t["targets"] = new JsonArray([.. targets.Select(x => (JsonNode)x)]);
            Transitions.Add(t);
        }

        public void AddQ(bool invokesP = false)
        {
            var start = new JsonObject { ["id"] = QStart, ["name"] = "Start" };
            if (invokesP)
            {
                start["invoke"] = new JsonArray(new JsonObject { ["id"] = NewId(), ["name"] = "runOrder", ["type"] = "process", ["process"] = P });
            }

            Files["model/processes/q.json"] = new JsonObject
            {
                ["kind"] = "process",
                ["id"] = Q,
                ["name"] = "Q",
                ["events"] = new JsonArray(new JsonObject { ["id"] = QGo, ["name"] = "go" }),
                ["states"] = new JsonArray(start, new JsonObject { ["id"] = QEnd, ["name"] = "End", ["type"] = "final" }),
                ["transitions"] = new JsonArray(new JsonObject { ["id"] = NewId(), ["source"] = QStart, ["event"] = QGo, ["targets"] = new JsonArray(QEnd) }),
            };
            if (invokesP)
            {
                var invoke = start["invoke"]![0]!["id"]!.GetValue<string>();
                Files["model/processes/q.json"]!["transitions"]!.AsArray().Add(new JsonObject
                {
                    ["id"] = NewId(), ["source"] = QStart, ["trigger"] = "invoke-done", ["invoke"] = invoke, ["targets"] = new JsonArray(QEnd),
                });
            }
        }

        private static JsonObject? Find(JsonArray states, string id)
        {
            foreach (var node in states)
            {
                var state = node!.AsObject();
                if ((string?)state["id"] == id)
                    return state;
                if (state["states"] is JsonArray children && Find(children, id) is { } found)
                    return found;
            }

            return null;
        }

        public ModelSnapshot Build()
        {
            var root = Fixtures.Path("models", "process-basics", ".maquettiste");
            var settingsBytes = File.ReadAllBytes(Path.Combine(root, "maquettiste.json"));
            var settings = JsonSerializer.Deserialize<ProjectSettings>(settingsBytes, EngineJson.Options)!;
            var documents = new List<ElementDocument>();
            foreach (var (path, json) in Files)
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(json);
                var kind = (string)json["kind"]!;
                Assert.True(KindInfo.TryGet(kind, out var info));
                var element = (Element)JsonSerializer.Deserialize(bytes, info.ClrType, EngineJson.Options)!;
                var hash = ContentHash.Of(bytes);
                using var doc = JsonDocument.Parse(bytes);
                documents.Add(new ElementDocument(element, ".maquettiste/" + path, hash, HashBuilder.Of(hash, null), doc.RootElement.Clone(), null));
            }

            var extensions = Extensions.Select((json, i) =>
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(json);
                return new ExtensionDocument(JsonSerializer.Deserialize<ExtensionSchema>(bytes, EngineJson.Options)!,
                    ".maquettiste/extensions/x" + i.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".json", ContentHash.Of(bytes));
            }).ToList();
            return ModelSnapshot.Create(documents, settings, ContentHash.Of(settingsBytes), extensions, [], 1);
        }
    }

    private static Mutable Load()
    {
        var model = new Mutable();
        var root = Fixtures.Path("models", "process-basics", ".maquettiste");
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "model"), "*.json", SearchOption.AllDirectories))
        {
            var path = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
            model.Files[path] = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        }

        return model;
    }

    private static async Task<IReadOnlyList<Diagnostic>> ValidateAsync(Mutable model, ValidationScope? scope = null)
    {
        var report = await ValidationFixture.Validator().ValidateAsync(model.Build(), scope ?? ValidationScope.All, null, Ct);
        return [.. report.Diagnostics.Where(d => d.Rule.StartsWith("MQ9", StringComparison.Ordinal))];
    }

    private static readonly Dictionary<string, Action<Mutable>> Failing = new(StringComparer.Ordinal)
    {
        ["MQ9001 initial not a direct child"] = m => m.State(Review)["initial"] = Packing,
        ["MQ9001 root initial nested"] = m => m.Process["initial"] = Pending,
        ["MQ9001 initial on a parallel state"] = m => m.State(Fulfilment)["initial"] = Shipping,
        ["MQ9002 atomic with children"] = m => m.State(Draft)["states"] = new JsonArray(new JsonObject { ["id"] = NewId(), ["name"] = "Inner" }),
        ["MQ9002 compound without children"] = m => m.State(Shipping)["states"] = new JsonArray(),
        ["MQ9003 unreachable nested state"] = m => m.State(Review)["states"]!.AsArray().Add(new JsonObject { ["id"] = "01JSTA00000000000000000099", ["name"] = "Parked" }),
        ["MQ9004 dead end"] = m => AddRejected(m, final: false),
        ["MQ9005 no final state"] = m => m.State(Closed).Remove("type"),
        ["MQ9006 unguarded before another"] = m => m.AddTransition(Draft, null, Submit, Closed),
        ["MQ9006 guard used twice"] = m => m.Transitions.Insert(2, new JsonObject
        {
            ["id"] = NewId(), ["source"] = Decide, ["trigger"] = "always", ["guard"] = IsLarge, ["targets"] = new JsonArray(Closed),
        }),
        ["MQ9007 two targets in one region"] = m => m.Transition(5)["targets"] = new JsonArray(Shipping, Packing),
        ["MQ9007 targets not orthogonal"] = m => m.Transition(5)["targets"] = new JsonArray(Packing, Pending),
        ["MQ9007 target in another process"] = m =>
        {
            m.AddQ();
            m.Transition(9)["targets"] = new JsonArray(QEnd);
        },
        ["MQ9008 event trigger without event"] = m => m.Transition(9).Remove("event"),
        ["MQ9008 zero duration"] = m => m.Transition(3)["after"] = "P0D",
        ["MQ9008 malformed duration"] = m => m.Transition(3)["after"] = "2 days",
        ["MQ9008 done on an atomic source"] = m => m.Transition(8)["source"] = Packing,
        ["MQ9008 invoke not on the source"] = m => m.Transition(4)["source"] = Pending,
        ["MQ9008 field of another trigger"] = m => m.Transition(3)["event"] = Submit,
        ["MQ9009 eventless cycle"] = m => m.AddTransition(Pending, "always", null, Decide),
        ["MQ9010 choice with an event transition"] = m =>
        {
            m.Transition(1).Remove("trigger");
            m.Transition(1)["event"] = Submit;
        },
        ["MQ9010 choice without default"] = m => m.Transition(2)["guard"] = IsLarge,
        ["MQ9011 default target outside the parent"] = m => m.State(Resume)["defaultTarget"] = Packing,
        ["MQ9011 history in a parallel state"] = m => m.State(Fulfilment)["states"]!.AsArray().Add(new JsonObject { ["id"] = NewId(), ["name"] = "Back", ["type"] = "history" }),
        ["MQ9012 final with a transition"] = m => m.AddTransition(Closed, null, Cancel, Draft),
        ["MQ9012 final with an invoke"] = m => m.State(Closed)["invoke"] = new JsonArray(new JsonObject { ["id"] = NewId(), ["name"] = "archive", ["type"] = "service" }),
        ["MQ9013 unused event"] = m => m.Process["events"]!.AsArray().Add(new JsonObject { ["id"] = NewId(), ["name"] = "archive" }),
        ["MQ9013 unused guard"] = m => m.Process["guards"]!.AsArray().Add(new JsonObject { ["id"] = NewId(), ["name"] = "isSmall" }),
        ["MQ9013 unused action"] = m => m.Process["actions"]!.AsArray().Add(new JsonObject { ["id"] = NewId(), ["name"] = "audit" }),
        ["MQ9013 unused invoke"] = m => m.Transitions.RemoveAt(4),
        ["MQ9014 event of another process"] = m =>
        {
            m.AddQ();
            m.Transition(9)["event"] = QGo;
        },
        ["MQ9014 source of another process"] = m =>
        {
            m.AddQ();
            m.Transition(9)["source"] = QStart;
        },
        ["MQ9015 process invoke without process"] = m => ManagerReviewInvoke(m)["type"] = "process",
        ["MQ9015 human task without actors"] = m => ManagerReviewInvoke(m).Remove("actors"),
        ["MQ9015 sub-process cycle"] = m =>
        {
            m.AddQ(invokesP: true);
            var invoke = ManagerReviewInvoke(m);
            invoke["type"] = "process";
            invoke.Remove("actors");
            invoke["process"] = Q;
        },
        ["MQ9016 diagram member not a state"] = m => m.Diagram["members"]!.AsArray().Add(new JsonObject { ["element"] = Order }),
        ["MQ9017 parallel with one region"] = m => m.State(Fulfilment)["states"]!.AsArray().RemoveAt(1),
        ["MQ9018 done that never fires"] = m => m.State(Shipped).Remove("type"),
        ["MQ9020 a library that holds what runs"] = m => m.Process["use"] = "library",
        ["MQ9021 a gate policy beside a gate, naming a gate no library holds"] = m => m.Transition(5)["gatePolicy"] = m.Gate["id"]!.DeepClone(),
        ["MQ9022 an invoke naming a library"] = m =>
        {
            m.Files["model/processes/library.json"] = JsonNode.Parse("""{ "kind": "process", "id": "01JSTA000000000000000000M0", "name": "Shared", "use": "library" }""")!.AsObject();
            var invoke = ManagerReviewInvoke(m);
            invoke["type"] = "process";
            invoke.Remove("actors");
            invoke["process"] = "01JSTA000000000000000000M0";
        },
        ["MQ9101 too few person signers"] = m =>
        {
            m.Actor(Manager)["type"] = "person";
            m.Actor(Finance)["type"] = "person";
            m.Gate["required"] = 3;
        },
        ["MQ9102 required actor not a signer"] = m => m.Gate["requiredActors"] = new JsonArray(Clerk),
        ["MQ9103 gate on a non-event trigger"] = m =>
        {
            var gate = m.Gate;
            m.Transition(5).Remove("gate");
            m.Transition(4)["gate"] = gate;
        },
        ["MQ9103 two gates on one source and event"] = m =>
        {
            var copy = m.Transition(5).DeepClone().AsObject();
            copy["id"] = NewId();
            copy["gate"]!["id"] = NewId();
            copy["gate"]!["name"] = "second";
            copy["gate"]!["meanings"] = new JsonArray(new JsonObject { ["id"] = NewId(), ["name"] = "again" });
            copy["guard"] = IsLarge;
            m.Transitions.Insert(5, copy);
        },
        ["MQ9104 gate without meanings"] = m => m.Gate["meanings"] = new JsonArray(),
        ["MQ9105 signer may not raise the event"] = m => m.Event(Approve)["actors"] = new JsonArray(Manager),
        ["MQ9106 unreferenced actor"] = m => m.Files["model/actors/auditor.json"] = new JsonObject
        {
            ["kind"] = "actor", ["id"] = "01JACT00000000000000000009", ["name"] = "Auditor", ["type"] = "role",
        },
        ["MQ9201 lifecycle without subject"] = m => m.Process.Remove("subject"),
        ["MQ9201 subject names no lifecycle"] = m => m.Entity.Remove("lifecycle"),
        ["MQ9201 entity names an orchestration"] = m =>
        {
            m.Process.Remove("use");
            m.Process.Remove("subject");
            m.Process.Remove("boundAttribute");
        },
        ["MQ9201 entity names another entity's lifecycle"] = m => m.Process["subject"] = "01JENT00000000000000000002",
        ["MQ9202 a uuid attribute, which cannot hold a state"] = m => m.Process["boundAttribute"] = "01JATT00000000000000000001",
        ["MQ9202 two lifecycles bind one attribute for an entity both list"] = m => m.AddLifecycleQ((Order, Status)),
        ["MQ9202 a scalar type over a type that cannot hold a state"] = m =>
        {
            m.Files["model/types/order-date.json"] = new JsonObject { ["kind"] = "scalar-type", ["id"] = "01JSCT00000000000000000001", ["name"] = "OrderDate", ["base"] = "date" };
            m.Bind(new JsonObject { ["ref"] = "01JSCT00000000000000000001" }, "2026-01-01");
        },
        ["MQ9202 collection attribute"] = m => m.Entity["attributes"]![1]!["collection"] = true,
        ["MQ9202 bound on an orchestration"] = m =>
        {
            m.Process.Remove("use");
            m.Process.Remove("subject");
            m.Entity.Remove("lifecycle");
        },
        ["MQ9203 members out of order"] = m =>
        {
            var members = m.Enum["members"]!.AsArray();
            var review = members[1]!;
            members.RemoveAt(1);
            members.Insert(2, review);
        },
        ["MQ9203 member missing"] = m => m.Enum["members"]!.AsArray().RemoveAt(3),
        ["MQ9203 extra member"] = m => m.Enum["members"]!.AsArray().Add(new JsonObject { ["id"] = NewId(), ["name"] = "Archived" }),
        ["MQ9204 enum shared with another attribute"] = m => m.Entity["attributes"]!.AsArray().Add(new JsonObject
        {
            ["id"] = NewId(), ["name"] = "previousStatus", ["type"] = new JsonObject { ["ref"] = "01JENM00000000000000000001" },
        }),
        ["MQ9205 default is another state"] = m => m.Entity["attributes"]![1]!["default"] = "Review",
        ["MQ9205 no default"] = m => m.Entity["attributes"]![1]!.AsObject().Remove("default"),
        ["MQ9201 empty subjects"] = m =>
        {
            m.UseSubjects();
            m.Process["subjects"] = new JsonArray();
        },
        ["MQ9201 an entity listed twice"] = m =>
        {
            m.UseSubjects();
            m.Process["subjects"]!.AsArray().Add(new JsonObject { ["id"] = NewId(), ["entity"] = Order });
        },
        ["MQ9201 entity names a lifecycle that does not list it"] = m =>
        {
            m.UseSubjects()["entity"] = "01JENT00000000000000000002";
            m.Files["model/entities/customer.json"] = new JsonObject { ["kind"] = "entity", ["id"] = "01JENT00000000000000000002", ["name"] = "Customer" };
        },
        ["MQ9202 a subjects entry binds a non-enum attribute"] = m => m.UseSubjects()["boundAttribute"] = "01JATT00000000000000000001",
        ["MQ9202 an orchestration's subject binds an attribute"] = m =>
        {
            m.UseSubjects();
            m.Process.Remove("use");
            m.Entity.Remove("lifecycle");
        },
        ["MQ9203 a subjects entry's enum drifts"] = m =>
        {
            m.UseSubjects();
            m.Enum["members"]!.AsArray().RemoveAt(3);
        },
        ["MQ9206 two states share a code"] = m =>
        {
            m.State(Draft)["code"] = "D";
            m.State(Pending)["code"] = "D";
        },
        ["MQ9206 1 and \"1\" are one code"] = m =>
        {
            m.State(Draft)["code"] = 1;
            m.State(Closed)["code"] = "1";
        },
        ["MQ9206 strings and integers mixed"] = m =>
        {
            m.State(Draft)["code"] = "D";
            m.State(Closed)["code"] = 9;
        },
        ["MQ9206 a code on a history state"] = m => m.State(Resume)["code"] = "R",
        ["MQ9206 a code on a choice state"] = m => m.State(Decide)["code"] = "C",
        ["MQ9206 under a binding, a coded state below a coded state"] = m =>
        {
            m.BindReference();
            m.State(Pending)["code"] = "FUL2";
        },
        ["MQ9206 under a binding, a code in a parallel region"] = m =>
        {
            m.BindReference();
            m.State(Fulfilment).Remove("code");
            m.State(Packing)["code"] = "FUL";
        },
        ["MQ9205 a binding by code whose default is another state's code"] = m =>
        {
            m.BindReference();
            m.StateAttribute["default"] = "REV";
        },
        ["MQ9207 an enum binding: a code that is none of the member's identifiers"] = m => m.State(Draft)["code"] = "D",
        ["MQ9207 an enum binding: a code on a nested state"] = m => m.State(Pending)["code"] = "Review",
        ["MQ9207 a reference binding: a code the type does not have"] = m =>
        {
            m.BindReference();
            m.State(Closed)["code"] = "XXX";
        },
        ["MQ9207 a reference binding: a former code"] = m =>
        {
            m.BindReference();
            m.ReferenceType["formerCodes"] = new JsonArray(new JsonObject { ["code"] = "END", ["replacedBy"] = RowClosed });
            m.State(Closed)["code"] = "END";
        },
        ["MQ9207 a plain binding: a code too long"] = m =>
        {
            m.BindPlain();
            m.State(Closed)["code"] = "CLOSED";
        },
        ["MQ9207 a plain binding: a code not among allowedValues"] = m =>
        {
            m.BindPlain();
            m.State(Closed)["code"] = "END";
        },
        ["MQ9207 a plain integer binding: a string code"] = m =>
        {
            m.Bind("int32", 1);
            m.Codes(1, 2, 3, "4");
        },
        ["MQ9208 a row no state has"] = m =>
        {
            m.BindReference();
            m.Rows.Add(new JsonObject { ["id"] = "01JRWS00000000000000000005", ["values"] = new JsonArray("ARC", "Archived") });
        },
        ["MQ9208 an allowed value no state has"] = m =>
        {
            m.BindPlain();
            m.StateAttribute["validation"]!["allowedValues"]!.AsArray().Add("ARC");
        },
        ["MQ9210 a reference binding: a state with no code"] = m =>
        {
            m.BindReference();
            m.State(Review).Remove("code");
        },
        ["MQ9210 a plain binding with no codes"] = m =>
        {
            m.Entity["attributes"]!.AsArray().Add(new JsonObject { ["id"] = StateAttr, ["name"] = "state", ["type"] = "string" });
            m.Process["boundAttribute"] = StateAttr;
        },
        ["MQ9211 a subject entry names a stereotype for actors"] = m =>
        {
            m.UseSubjects();
            m.Process["subjects"]!.AsArray().Add(new JsonObject { ["id"] = "01JPRX00000000000000000291", ["stereotype"] = "persona" });
        },
        ["MQ9209 the one-subject form"] = _ => { },
        ["MQ9209 an orchestration in the one-subject form"] = m =>
        {
            m.Process.Remove("use");
            m.Process.Remove("boundAttribute");
            m.Entity.Remove("lifecycle");
        },
    };

    private static readonly Dictionary<string, Action<Mutable>> Passing = new(StringComparer.Ordinal)
    {
        ["MQ9001 initial a direct child"] = m => m.State(Review)["initial"] = Escalated,
        ["MQ9002 atomic without children"] = m => m.State(Draft)["type"] = "atomic",
        ["MQ9003 nested state with a transition to it"] = m =>
        {
            m.State(Review)["states"]!.AsArray().Add(new JsonObject { ["id"] = "01JSTA00000000000000000099", ["name"] = "Parked" });
            m.Transitions.Add(new JsonObject
            {
                ["id"] = NewId(), ["source"] = Pending, ["trigger"] = "after", ["after"] = "PT1H", ["targets"] = new JsonArray("01JSTA00000000000000000099"),
            });
        },
        ["MQ9004 final end"] = m => AddRejected(m, final: true),
        ["MQ9005 final reachable"] = _ => { },
        ["MQ9006 guarded before unguarded"] = m => m.Transitions.Insert(2, new JsonObject
        {
            ["id"] = NewId(), ["source"] = Decide, ["trigger"] = "always", ["guard"] = AddGuard(m, "isRush"), ["targets"] = new JsonArray(Closed),
        }),
        ["MQ9007 one target per region"] = m => m.Transition(5)["targets"] = new JsonArray(Packing, Invoicing),
        ["MQ9008 positive duration"] = m => m.Transition(3)["after"] = "PT1H30M",
        ["MQ9009 guarded eventless transition"] = m => m.Transitions.Add(new JsonObject
        {
            ["id"] = NewId(), ["source"] = Pending, ["trigger"] = "always", ["guard"] = IsLarge, ["targets"] = new JsonArray(Decide),
        }),
        ["MQ9010 always transitions ending unguarded"] = _ => { },
        ["MQ9011 default target inside the parent"] = m => m.State(Resume)["defaultTarget"] = Escalated,
        ["MQ9012 final without transitions"] = _ => { },
        ["MQ9013 everything used"] = _ => { },
        ["MQ9014 local references"] = m => m.AddQ(),
        ["MQ9015 sub-process without cycle"] = m =>
        {
            m.AddQ();
            var invoke = ManagerReviewInvoke(m);
            invoke["type"] = "process";
            invoke.Remove("actors");
            invoke["process"] = Q;
        },
        ["MQ9016 diagram member a state"] = m => m.Diagram["members"]!.AsArray().Add(new JsonObject { ["element"] = Packing }),
        ["MQ9017 parallel with two regions"] = _ => { },
        ["MQ9018 done with a reachable final"] = _ => { },
        ["MQ9020 a process with states"] = _ => { },
        ["MQ9021 an inline gate alone"] = _ => { },
        ["MQ9022 an invoke naming a process"] = _ => { },
        ["MQ9101 repeat signers allowed"] = m =>
        {
            m.Actor(Manager)["type"] = "person";
            m.Actor(Finance)["type"] = "person";
            m.Gate["required"] = 3;
            m.Gate["allowRepeatSigner"] = true;
        },
        ["MQ9101 a role signer"] = m => m.Gate["required"] = 5,
        ["MQ9102 required actors among signers"] = _ => { },
        ["MQ9103 one gate on an event"] = _ => { },
        ["MQ9104 gate with meanings"] = _ => { },
        ["MQ9105 event open to any actor"] = m => m.Event(Approve).Remove("actors"),
        ["MQ9106 actors referenced"] = _ => { },
        ["MQ9201 both sides agree"] = _ => { },
        ["MQ9201 orchestration without subject"] = m =>
        {
            m.Process.Remove("use");
            m.Process.Remove("subject");
            m.Process.Remove("boundAttribute");
            m.Entity.Remove("lifecycle");
        },
        ["MQ9202 unbound lifecycle"] = m => m.Process.Remove("boundAttribute"),
        ["MQ9203 members in state order"] = _ => { },
        ["MQ9204 enum used once"] = _ => { },
        ["MQ9205 default is the initial state"] = _ => { },
        ["MQ9201 subjects without the entity naming the lifecycle back"] = m =>
        {
            m.UseSubjects();
            m.Entity.Remove("lifecycle");
        },
        ["MQ9201 the entity names a lifecycle that lists it"] = m => m.UseSubjects(),
        ["MQ9202 a subjects entry binds an enum attribute"] = m => m.UseSubjects(),
        ["MQ9203 a subjects entry's enum in state order"] = m => m.UseSubjects(),
        ["MQ9206 unique codes of one kind, nested, with no binding"] = m =>
        {
            m.Process.Remove("boundAttribute");
            m.State(Draft)["code"] = "D";
            m.State(Review)["code"] = "R";
            m.State(Pending)["code"] = "RP";
            m.State(Packing)["code"] = "FP";
            m.State(Closed)["code"] = "C";
        },
        ["MQ9206 integer codes"] = m =>
        {
            m.State(Draft)["code"] = 1;
            m.State(Closed)["code"] = 2;
        },
        ["MQ9206 under a binding, codes that form a map"] = m => m.BindReference(),
        ["MQ9202 a reference attribute"] = m => m.BindReference(),
        ["MQ9202 a plain string attribute"] = m => m.BindPlain(),
        ["MQ9202 an integer attribute through a scalar type"] = m =>
        {
            m.Files["model/types/status-code.json"] = new JsonObject { ["kind"] = "scalar-type", ["id"] = "01JSCT00000000000000000002", ["name"] = "StatusCode", ["base"] = "int16" };
            m.Bind(new JsonObject { ["ref"] = "01JSCT00000000000000000002" }, 1);
            m.Codes(1, 2, 3, 4);
        },
        ["MQ9202 two lifecycles bind one inherited attribute for a base and a derived entity"] = m =>
        {
            m.AddRushOrder();
            m.AddLifecycleQ((RushOrder, Status));
        },
        ["MQ9204 subjects of one lifecycle share their enum"] = m => m.AddReturnSharingTheEnum(),
        ["MQ9205 a binding by code whose default is the initial state's code"] = m => m.BindReference(),
        ["MQ9207 an enum binding: codes name members by key, value or name"] = m =>
        {
            m.Enum["members"]![0]!["code"] = "D";
            m.Enum["members"]![1]!["value"] = 2;
            m.Codes("D", "2", "Fulfilment", "Closed");
        },
        ["MQ9207 an enum binding: integer codes name members by value"] = m =>
        {
            foreach (var (member, value) in m.Enum["members"]!.AsArray().Select((member, i) => (member!, i + 1)))
                member["value"] = value;
            m.Codes(1, 2, 3, 4);
        },
        ["MQ9207 a reference binding's codes are the type's codes"] = m => m.BindReference(),
        ["MQ9207 a reference binding: 1 and \"1\" are one code"] = m =>
        {
            m.BindReference();
            m.ReferenceType["code"]!["type"] = "int32";
            m.ReferenceType["code"]!.AsObject().Remove("length");
            foreach (var (row, value) in m.Rows.Select((row, i) => (row!, i + 1)))
                row["values"]![0] = value;
            m.StateAttribute["default"] = 1;
            m.Codes("1", "2", "3", "4");
        },
        ["MQ9207 a plain binding's codes fit the attribute"] = m => m.BindPlain(),
        ["MQ9208 every row has a state"] = m => m.BindReference(),
        ["MQ9208 every allowed value has a state"] = m => m.BindPlain(),
        ["MQ9210 every state a configuration rests in has a code"] = m => m.BindReference(),
        ["MQ9210 an enum binding with no codes, as before codes"] = _ => { },
        ["MQ9210 an enum binding with one coded state needs no more"] = m => m.State(Draft)["code"] = "Draft",
        ["MQ9211 the subjects form without a stereotype entry"] = m => m.UseSubjects(),
        ["MQ9209 the subjects form"] = m => m.UseSubjects(),
        ["MQ9209 an orchestration with subjects"] = m =>
        {
            m.UseSubjects().Remove("boundAttribute");
            m.Process.Remove("use");
            m.Entity.Remove("lifecycle");
        },
    };

    private static void AddRejected(Mutable m, bool final)
    {
        var rejected = new JsonObject { ["id"] = "01JSTA00000000000000000098", ["name"] = "Rejected" };
        if (final)
            rejected["type"] = "final";
        m.Process["states"]!.AsArray().Add(rejected);
        m.Enum["members"]!.AsArray().Add(new JsonObject { ["id"] = NewId(), ["name"] = "Rejected" });
        m.Transition(9)["targets"] = new JsonArray("01JSTA00000000000000000098");
    }

    private static string AddGuard(Mutable m, string name)
    {
        var id = NewId();
        m.Process["guards"]!.AsArray().Add(new JsonObject { ["id"] = id, ["name"] = name });
        return id;
    }

    private static JsonObject ManagerReviewInvoke(Mutable m) => m.State(Escalated)["invoke"]!.AsArray().Single(i => (string?)i!["id"] == ManagerReview)!.AsObject();

    public static TheoryData<string> FailingCases() => [.. Failing.Keys.Order(StringComparer.Ordinal)];

    public static TheoryData<string> PassingCases() => [.. Passing.Keys.Order(StringComparer.Ordinal)];

    [Fact]
    public async Task The_fixture_raises_no_process_rule()
    {
        // The fixture names its subject in the one-subject form, which is deprecated (lifecycle-design.md decision 6); it is not converted.
        var only = Assert.Single(await ValidateAsync(Load()));
        Assert.Equal(("MQ9209", "/subject", P), (only.Rule, only.JsonPointer, only.ElementId));
    }

    [Theory]
    [InlineData("reference")]
    [InlineData("plain")]
    [InlineData("scalar")]
    [InlineData("subjects")]
    public async Task A_binding_by_code_that_fits_raises_nothing_but_the_deprecation(string kind)
    {
        // The passing cases' bindings are clean through and through, so each passing case proves its own rule only (decisions 11 to 17).
        var model = Load();
        switch (kind)
        {
            case "reference":
                model.BindReference();
                break;
            case "plain":
                model.BindPlain();
                break;
            case "subjects":
                model.BindReference();
                model.UseSubjects();
                break;
            default:
                Passing["MQ9202 an integer attribute through a scalar type"](model);
                break;
        }

        var report = await ValidationFixture.Validator().ValidateAsync(model.Build(), ValidationScope.All, null, Ct);

        Assert.Equal(kind == "subjects" ? [] : ["MQ9209"], report.Diagnostics.Where(d => d.Severity != DiagnosticSeverity.Info).Select(d => d.Rule));
    }

    [Fact]
    public async Task A_one_subject_lifecycle_bound_to_a_plain_attribute_trades_its_error_for_one_warning()
    {
        // lifecycle-design.md section 11: the one error that goes away. One MQ9210 at the binding names each state a configuration rests in,
        // reachable and with no code; Decide (a choice) and Review's children (Review has none coded) are not named.
        var model = Load();
        Failing["MQ9210 a plain binding with no codes"](model);

        var diagnostics = await ValidateAsync(model);

        Assert.DoesNotContain(diagnostics, d => d.Rule == "MQ9202");
        var finding = Assert.Single(diagnostics, d => d.Rule == "MQ9210");
        Assert.Equal((P, "/boundAttribute"), (finding.ElementId, finding.JsonPointer));
        Assert.Equal("4 states of process 'OrderLifecycle' have no code, which binding 'Order.state' needs: Draft, Review, Fulfilment, Closed; give each a code, or run Sync codes.",
            finding.Message);
        Assert.Equal(DiagnosticSeverity.Warning, finding.Severity);
        Assert.All(diagnostics, d => Assert.NotEqual(DiagnosticSeverity.Error, d.Severity));
    }

    [Fact]
    public async Task Under_an_enum_binding_a_code_is_optional_and_one_coded_state_gives_no_MQ9210()
    {
        // The member is the stored value; MQ9207 still checks a code that is given.
        var model = Load();
        model.State(Draft)["code"] = "Draft";

        Assert.DoesNotContain(await ValidateAsync(model), d => d.Rule == "MQ9210");
    }

    [Fact]
    public async Task A_reference_binding_with_three_uncoded_states_gets_one_MQ9210_naming_all_three()
    {
        var model = Load();
        model.BindReference();
        model.State(Review).Remove("code");
        model.State(Fulfilment).Remove("code");
        model.State(Closed).Remove("code");

        var finding = Assert.Single(await ValidateAsync(model), d => d.Rule == "MQ9210");

        Assert.Equal("3 states of process 'OrderLifecycle' have no code, which binding 'Order.state' needs: Review, Fulfilment, Closed; give each a code, or run Sync codes.", finding.Message);
        Assert.Equal((P, "/boundAttribute"), (finding.ElementId, finding.JsonPointer));
    }

    [Fact]
    public async Task One_uncoded_state_reads_in_the_singular()
    {
        var model = Load();
        model.BindReference();
        model.State(Closed).Remove("code");

        var finding = Assert.Single(await ValidateAsync(model), d => d.Rule == "MQ9210");

        Assert.Equal("1 state of process 'OrderLifecycle' has no code, which binding 'Order.state' needs: Closed; give each a code, or run Sync codes.", finding.Message);
    }

    [Fact]
    public async Task More_than_five_uncoded_states_end_with_and_N_more()
    {
        var model = Load();
        model.BindReference();
        foreach (var state in new[] { Draft, Review, Fulfilment, Closed })
            model.State(state).Remove("code");
        for (var i = 1; i <= 3; i++)
        {
            var id = "01JSTA000000000000000000" + (80 + i).ToString(System.Globalization.CultureInfo.InvariantCulture);
            model.Process["states"]!.AsArray().Add(new JsonObject { ["id"] = id, ["name"] = "Extra" + i.ToString(System.Globalization.CultureInfo.InvariantCulture) });
            model.AddTransition(Draft, null, Submit, id);
        }

        var finding = Assert.Single(await ValidateAsync(model), d => d.Rule == "MQ9210");

        Assert.Equal("7 states of process 'OrderLifecycle' have no code, which binding 'Order.state' needs: Draft, Review, Fulfilment, Closed, Extra1 and 2 more; give each a code, or run Sync codes.", finding.Message);
    }

    [Fact]
    public async Task MQ9210_sits_on_the_subjects_entry_in_the_subjects_form()
    {
        var model = Load();
        model.BindReference();
        var entry = model.UseSubjects()["id"]!.GetValue<string>();
        model.State(Closed).Remove("code");

        var finding = Assert.Single(await ValidateAsync(model), d => d.Rule == "MQ9210");

        Assert.Equal((entry, "/subjects/0/boundAttribute"), (finding.ElementId, finding.JsonPointer));
    }

    [Theory]
    [InlineData("plain")]
    [InlineData("reference")]
    public async Task A_lifecycle_with_no_codes_gets_MQ9210_and_not_MQ9208(string binding)
    {
        // lifecycle-design.md section 11: a lifecycle bound to a plain attribute with allowedValues, or to a reference-typed one, that has
        // no codes yet gets one MQ9210 per binding, and not MQ9208 listing every value, which MQ9210 already covers.
        var model = Load();
        if (binding == "plain")
            model.BindPlain();
        else
            model.BindReference();
        foreach (var state in new[] { Draft, Review, Fulfilment, Closed })
            model.State(state).Remove("code");

        var diagnostics = await ValidateAsync(model);

        Assert.Contains(diagnostics, d => d.Rule == "MQ9210");
        Assert.DoesNotContain(diagnostics, d => d.Rule == "MQ9208");
    }

    [Fact]
    public async Task Binding_findings_sit_on_the_binding_and_code_findings_on_the_code()
    {
        var model = Load();
        model.BindReference();
        model.UseSubjects();
        model.State(Closed)["code"] = "XXX";
        model.StateAttribute["default"] = "REV";
        var entry = model.Process["subjects"]![0]!["id"]!.GetValue<string>();

        var diagnostics = await ValidateAsync(model);

        var wrong = Assert.Single(diagnostics, d => d.Rule == "MQ9207");
        Assert.Equal((Closed, "/states/4/code"), (wrong.ElementId, wrong.JsonPointer));
        Assert.Equal("State 'Closed' of process 'OrderLifecycle' has code 'XXX', which is not a code of reference type 'OrderState', which attribute 'state' of entity 'Order' binds; write one of its codes, or add a row with it.", wrong.Message);
        Assert.Equal([(entry, "/subjects/0/boundAttribute")], diagnostics.Where(d => d.Rule is "MQ9205" or "MQ9208").Select(d => (d.ElementId!, d.JsonPointer!)).Distinct());
        Assert.Contains("'CLS'", diagnostics.Single(d => d.Rule == "MQ9208").Message, StringComparison.Ordinal);
        Assert.Contains("whose code is 'DRF'; set the default to 'DRF'", diagnostics.Single(d => d.Rule == "MQ9205").Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Two_lifecycles_binding_one_attribute_are_judged_on_the_entities_they_list()
    {
        // Decision 11: an entity both list by name, never a derived one; two lifecycles in the one-subject form stay with MQ9201 alone.
        var shared = Load();
        Failing["MQ9202 two lifecycles bind one attribute for an entity both list"](shared);
        var both = (await ValidateAsync(shared)).Where(d => d.Rule == "MQ9202").ToList();
        Assert.Equal([".maquettiste/model/processes/order-lifecycle.json", ".maquettiste/model/processes/q.json"], both.Select(d => d.FilePath!).Order(StringComparer.Ordinal));

        var oneSubject = Load();
        oneSubject.AddQ();
        oneSubject.Files["model/processes/q.json"]["use"] = "lifecycle";
        oneSubject.Files["model/processes/q.json"]["subject"] = Order;
        oneSubject.Files["model/processes/q.json"]["boundAttribute"] = Status;
        var findings = await ValidateAsync(oneSubject);
        Assert.DoesNotContain(findings, d => d.Rule == "MQ9202");
        Assert.Contains(findings, d => d.Rule == "MQ9201");
    }

    [Fact]
    public async Task Use_subjects_gives_the_same_findings_but_the_deprecation_and_the_back_reference()
    {
        // Each failing lifecycle case in the one-subject form, converted: the findings stay but MQ9209 and the entity-names-it-back MQ9201.
        foreach (var (name, mutate) in Failing.Where(c => c.Key[..6] is "MQ9202" or "MQ9203" or "MQ9204" or "MQ9205" && !c.Key.Contains("subjects entry", StringComparison.Ordinal)
                     && !c.Key.Contains("orchestration", StringComparison.Ordinal)))
        {
            var before = Load();
            mutate(before);
            var after = Load();
            mutate(after);
            after.UseSubjects();

            static string Rules(IEnumerable<Diagnostic> found) => string.Join(",", found.Select(d => d.Rule).Where(r => r != "MQ9209").Order(StringComparer.Ordinal));
            var expected = Rules(await ValidateAsync(before));
            Assert.Equal(name + ": " + expected, name + ": " + Rules(await ValidateAsync(after)));
        }

        var converted = Load();
        converted.UseSubjects();
        Assert.Empty(await ValidateAsync(converted));
    }

    [Theory]
    [MemberData(nameof(FailingCases))]
    public async Task A_failing_case_raises_its_rule(string name)
    {
        var model = Load();
        Failing[name](model);
        var rule = name[..6];

        var diagnostics = await ValidateAsync(model);

        var hits = diagnostics.Where(d => d.Rule == rule).ToList();
        Assert.NotEmpty(hits);
        Assert.All(hits, hit =>
        {
            Assert.Equal(RuleCatalog.Get(rule).DefaultSeverity, hit.Severity);
            Assert.StartsWith(".maquettiste/model/", hit.FilePath, StringComparison.Ordinal);
            Assert.NotNull(hit.JsonPointer);
            Assert.Matches("[;.] \\S", hit.Message); // the finding, then what to do
        });
    }

    [Theory]
    [MemberData(nameof(PassingCases))]
    public async Task A_passing_case_does_not_raise_its_rule(string name)
    {
        var model = Load();
        Passing[name](model);

        var diagnostics = await ValidateAsync(model);

        Assert.DoesNotContain(diagnostics, d => d.Rule == name[..6]);
    }

    [Fact]
    public void Every_process_rule_has_a_catalog_entry_a_failing_case_and_a_passing_case()
    {
        // MQ9019 is a batch operation's refusal, not a validation finding: ProcessOperationTests covers it. MQ9024 to MQ9026 link processes
        // to operations (processes-operations-design.md section 8): OperationLinkRuleTests covers them with the operation-side rules.
        string[] elsewhere = ["MQ9019", "MQ9024", "MQ9025", "MQ9026"];
        var rules = RuleCatalog.All.Select(r => r.Id).Where(id => (id.StartsWith("MQ90", StringComparison.Ordinal)
            || id.StartsWith("MQ91", StringComparison.Ordinal) || id.StartsWith("MQ92", StringComparison.Ordinal)) && !elsewhere.Contains(id)).ToList();

        Assert.Equal(38, rules.Count);
        Assert.Equal(rules, Failing.Keys.Select(k => k[..6]).Distinct().Order(StringComparer.Ordinal));
        Assert.Equal(rules, Passing.Keys.Select(k => k[..6]).Distinct().Order(StringComparer.Ordinal));
        Assert.All(rules, id => Assert.Contains(";", RuleCatalog.Get(id).Description, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Findings_land_in_the_file_where_the_fix_belongs()
    {
        var model = Load();
        model.Diagram["members"]!.AsArray().Add(new JsonObject { ["element"] = Order });
        model.Files["model/actors/auditor.json"] = new JsonObject { ["kind"] = "actor", ["id"] = "01JACT00000000000000000009", ["name"] = "Auditor", ["type"] = "role" };
        Failing["MQ9201 entity names an orchestration"](model);

        var diagnostics = await ValidateAsync(model);

        Assert.Equal(".maquettiste/model/diagrams/order-lifecycle.json", Assert.Single(diagnostics, d => d.Rule == "MQ9016").FilePath);
        Assert.Equal(".maquettiste/model/actors/auditor.json", Assert.Single(diagnostics, d => d.Rule == "MQ9106").FilePath);
        var entitySide = Assert.Single(diagnostics, d => d.Rule == "MQ9201");
        Assert.Equal((".maquettiste/model/entities/order.json", "/lifecycle"), (entitySide.FilePath, entitySide.JsonPointer));
    }

    [Fact]
    public async Task Messages_name_the_elements_and_the_fix()
    {
        var model = Load();
        Failing["MQ9203 members out of order"](model);
        Failing["MQ9003 unreachable nested state"](model);
        Failing["MQ9008 zero duration"](model);
        Failing["MQ9204 enum shared with another attribute"](model);

        var diagnostics = await ValidateAsync(model);

        Assert.Equal(
            "The members of enum 'OrderStatus' differ from the states of process 'OrderLifecycle' (Draft, Review, Fulfilment, Closed): out of order. Sync the enum from the process.",
            Assert.Single(diagnostics, d => d.Rule == "MQ9203").Message);
        var unreachable = Assert.Single(diagnostics, d => d.Rule == "MQ9003");
        Assert.Equal(("01JSTA00000000000000000099", "/states/2/states/3"), (unreachable.ElementId, unreachable.JsonPointer));
        Assert.Contains("'Review.Parked'", unreachable.Message, StringComparison.Ordinal);
        Assert.Equal("/transitions/3/after", Assert.Single(diagnostics, d => d.Rule == "MQ9008").JsonPointer);
        Assert.Contains("is also used by 'Order.previousStatus'", Assert.Single(diagnostics, d => d.Rule == "MQ9204").Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unreachable_compound_is_reported_once_for_its_subtree()
    {
        var model = Load();
        model.State(Review)["states"]!.AsArray().Add(new JsonObject
        {
            ["id"] = "01JSTA00000000000000000097", ["name"] = "Hold", ["type"] = "compound",
            ["states"] = new JsonArray(new JsonObject { ["id"] = NewId(), ["name"] = "A" }, new JsonObject { ["id"] = NewId(), ["name"] = "B" }),
        });

        var hit = Assert.Single(await ValidateAsync(model), d => d.Rule == "MQ9003");

        Assert.Equal("01JSTA00000000000000000097", hit.ElementId);
        Assert.Contains("nor its 2 descendant state(s)", hit.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Scoped_validation_of_the_bound_enum_rechecks_its_lifecycle()
    {
        var model = Load();
        Failing["MQ9203 members out of order"](model);

        var scoped = await ValidateAsync(model, new ValidationScope(["01JENM00000000000000000001"]));

        Assert.Equal(".maquettiste/model/processes/order-lifecycle.json", Assert.Single(scoped, d => d.Rule == "MQ9203").FilePath);
    }

    [Fact]
    public async Task Scoped_validation_of_a_process_rechecks_actors_it_stopped_referencing()
    {
        var model = Load();
        model.Event(Submit).Remove("actors");

        var scoped = await ValidateAsync(model, new ValidationScope([P]));

        Assert.Contains(scoped, d => d.Rule == "MQ9106" && d.ElementId == Clerk);
    }

    // MQ3001, MQ3018, MQ2003, MQ2004 and MQ5001 on processes, their nodes, actors and scenarios (section 2.6).
    private static readonly Dictionary<string, (string Rule, Action<Mutable> Change)> NameAndMarkFailing = new(StringComparer.Ordinal)
    {
        ["root sibling states"] = ("MQ3001", m => m.State(Closed)["name"] = "Draft"),
        ["nested sibling states"] = ("MQ3001", m => m.State(Escalated)["name"] = "Pending"),
        ["two events"] = ("MQ3001", m => m.Event(Cancel)["name"] = "submit"),
        ["two guards"] = ("MQ3001", m => m.Process["guards"]!.AsArray().Add(new JsonObject { ["id"] = NewId(), ["name"] = "isLarge" })),
        ["two actions"] = ("MQ3001", m => m.Process["actions"]![1]!["name"] = "recordSubmission"),
        ["two invokes"] = ("MQ3001", m => m.State(Pending)["invoke"] = new JsonArray(new JsonObject { ["id"] = NewId(), ["name"] = "managerReview", ["type"] = "human-task" })),
        ["two gate meanings"] = ("MQ3001", m => m.Gate["meanings"]![1]!["name"] = "reviewedAsManager"),
        ["two processes in a package"] = ("MQ3001", m =>
        {
            m.AddQ();
            m.Files["model/processes/q.json"]["name"] = "OrderLifecycle";
            m.Files["model/processes/q.json"]["package"] = (string?)m.Process["package"];
        }),
        ["two actors"] = ("MQ3001", m => m.Actor(Manager)["name"] = (string?)m.Actor(Clerk)["name"]),
        ["two scenarios of a process"] = ("MQ3001", m =>
        {
            var copy = m.Files["model/scenarios/order-lifecycle/happy-path.json"].DeepClone().AsObject();
            copy["id"] = "01JSCN00000000000000000099";
            m.Files["model/scenarios/order-lifecycle/happy-path-2.json"] = copy;
        }),
        ["state name not an identifier"] = ("MQ3018", m => m.State(Draft)["name"] = "Draft state"),
        ["event name not an identifier"] = ("MQ3018", m => m.Event(Submit)["name"] = "1submit"),
        ["process name not an identifier"] = ("MQ3018", m => m.Process["name"] = "Order lifecycle"),
        ["unknown stereotype on a state"] = ("MQ2003", m => m.State(Draft)["stereotypes"] = new JsonArray("nosuchmark")),
        ["unknown stereotype on a transition"] = ("MQ2003", m => m.Transition(0)["stereotypes"] = new JsonArray("nosuchmark")),
        ["unknown stereotype on an event"] = ("MQ2003", m => m.Event(Submit)["stereotypes"] = new JsonArray("nosuchmark")),
        ["actor stereotype on a state"] = ("MQ2004", m => m.State(Draft)["stereotypes"] = new JsonArray("persona")),
        ["state property fails its extension"] = ("MQ5001", m =>
        {
            m.Extensions.Add(StateExtension());
            m.State(Draft)["properties"] = new JsonObject { ["weight"] = "heavy" };
        }),
        ["transition property fails its extension"] = ("MQ5001", m =>
        {
            m.Extensions.Add(StateExtension("transition"));
            m.Transition(0)["properties"] = new JsonObject { ["weight"] = "heavy" };
        }),
        ["event property fails its extension"] = ("MQ5001", m =>
        {
            m.Extensions.Add(StateExtension("event"));
            m.Event(Submit)["properties"] = new JsonObject { ["weight"] = "heavy" };
        }),
    };

    private static readonly Dictionary<string, (string Rule, Action<Mutable> Change)> NameAndMarkPassing = new(StringComparer.Ordinal)
    {
        ["same state name under different parents"] = ("MQ3001", m => m.State(Packing)["name"] = "Pending"),
        ["an event and a guard share a name"] = ("MQ3001", m => m.Event(Cancel)["name"] = "isLarge"),
        ["processes of different packages share a name"] = ("MQ3001", m =>
        {
            m.AddQ();
            m.Files["model/processes/q.json"]["name"] = "OrderLifecycle";
        }),
        ["scenarios of different processes share a name"] = ("MQ3001", m =>
        {
            m.AddQ();
            var copy = m.Files["model/scenarios/order-lifecycle/happy-path.json"].DeepClone().AsObject();
            copy["id"] = "01JSCN00000000000000000099";
            copy["process"] = Q;
            copy["steps"] = new JsonArray(new JsonObject { ["id"] = NewId(), ["event"] = QGo, ["expect"] = new JsonObject { ["states"] = new JsonArray(QEnd) } });
            m.Files["model/scenarios/q/happy-path.json"] = copy;
        }),
        ["identifier state name"] = ("MQ3018", m => m.State(Draft)["name"] = "Draft_2"),
        ["actor name with a space is a label"] = ("MQ3018", m => m.Actor(Clerk)["name"] = "Order clerk"),
        ["stereotype applicable to states"] = ("MQ2004", m =>
        {
            var stereotype = m.Files["model/vocabularies/stereotypes/persona.json"].DeepClone().AsObject();
            stereotype["id"] = "01JSTR00000000000000000099";
            stereotype["key"] = "milestone";
            stereotype["name"] = "Milestone";
            stereotype["appliesTo"] = new JsonArray("state", "transition", "event");
            m.Files["model/vocabularies/stereotypes/milestone.json"] = stereotype;
            m.State(Draft)["stereotypes"] = new JsonArray("milestone");
            m.Transition(0)["stereotypes"] = new JsonArray("milestone");
            m.Event(Submit)["stereotypes"] = new JsonArray("milestone");
        }),
        ["state property fits its extension"] = ("MQ5001", m =>
        {
            m.Extensions.Add(StateExtension());
            m.State(Draft)["properties"] = new JsonObject { ["weight"] = 3 };
        }),
    };

    private static JsonObject StateExtension(string kind = "state") => new()
    {
        ["name"] = "weights-" + kind,
        ["appliesTo"] = new JsonObject { ["kinds"] = new JsonArray(kind) },
        ["properties"] = new JsonObject { ["weight"] = new JsonObject { ["type"] = "integer" } },
    };

    public static TheoryData<string> NameAndMarkFailingCases => [.. NameAndMarkFailing.Keys];

    public static TheoryData<string> NameAndMarkPassingCases => [.. NameAndMarkPassing.Keys];

    private static async Task<IReadOnlyList<Diagnostic>> ValidateAllAsync(Mutable model) =>
        (await ValidationFixture.Validator().ValidateAsync(model.Build(), ValidationScope.All, null, Ct)).Diagnostics;

    [Theory]
    [MemberData(nameof(NameAndMarkFailingCases))]
    public async Task Names_and_marks_of_process_nodes_are_checked(string name)
    {
        var (rule, change) = NameAndMarkFailing[name];
        var model = Load();
        Assert.DoesNotContain(await ValidateAllAsync(model), d => d.Rule == rule);
        change(model);

        var diagnostic = Assert.Single(await ValidateAllAsync(model), d => d.Rule == rule);

        Assert.NotNull(diagnostic.JsonPointer); // on the node, not the file
    }

    [Theory]
    [MemberData(nameof(NameAndMarkPassingCases))]
    public async Task Names_and_marks_of_process_nodes_pass_nearby_changes(string name)
    {
        var (rule, change) = NameAndMarkPassing[name];
        var model = Load();
        change(model);

        Assert.DoesNotContain(await ValidateAllAsync(model), d => d.Rule == rule);
    }

    [Fact]
    public async Task Allowed_values_of_an_enum_attribute_name_members()
    {
        var model = Load();
        model.Entity["attributes"]![1]!["validation"] = new JsonObject { ["allowedValues"] = new JsonArray("Draft", "Review") };
        Assert.DoesNotContain(await ValidateAllAsync(model), d => d.Rule == "MQ3013");

        model.Entity["attributes"]![1]!["validation"] = new JsonObject { ["allowedValues"] = new JsonArray("Draft", "Archived") };
        var diagnostic = Assert.Single(await ValidateAllAsync(model), d => d.Rule == "MQ3013");

        Assert.Equal("/attributes/1/validation/allowedValues/1", diagnostic.JsonPointer);
        Assert.Contains("name a member", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rules_are_deterministic_across_parallelism()
    {
        var model = Load();
        foreach (var name in new[] { "MQ9003 unreachable nested state", "MQ9006 unguarded before another", "MQ9013 unused event", "MQ9203 extra member" })
            Failing[name](model);
        var snapshot = model.Build();

        var one = await ValidationFixture.Validator(parallelism: 1).ValidateAsync(snapshot, ValidationScope.All, null, Ct);
        var many = await ValidationFixture.Validator(parallelism: 8).ValidateAsync(snapshot, ValidationScope.All, null, Ct);

        Assert.Equal(ValidationFixture.ToGolden(one.Diagnostics), ValidationFixture.ToGolden(many.Diagnostics));
    }

    [Theory]
    [InlineData("P2D", true)]
    [InlineData("PT1H30M", true)]
    [InlineData("P1Y2M3W4DT5H6M7.5S", true)]
    [InlineData("PT0.5S", true)]
    [InlineData("P0D", false)]
    [InlineData("PT0S", false)]
    [InlineData("P", false)]
    [InlineData("PT", false)]
    [InlineData("P1DT", false)]
    [InlineData("-P1D", false)]
    [InlineData("2 days", false)]
    public void Durations_are_positive_ISO_8601(string value, bool positive) => Assert.Equal(positive, ProcessRules.IsPositiveDuration(value));

    [Fact]
    public void Analysis_reaches_states_through_initials_regions_history_defaults_and_done()
    {
        var process = Load().Build().Get<Process>(P)!;

        var analysis = new ProcessAnalysis(process);

        Assert.Equal(14, analysis.States.Length);
        Assert.Equal(14, analysis.Reachable.Count); // Resume through Decide, Paid through the Billing region, Closed through done
        Assert.Equal("Fulfilment.Shipping.Packing", analysis.Find(Packing)!.Path);
        Assert.Equal("/states/3/states/0/states/0", analysis.Find(Packing)!.Pointer);
        Assert.Equal(Pending, analysis.InitialOf(analysis.Find(Review))!.Id);
        Assert.Equal(Draft, analysis.InitialOf(null)!.Id);
        Assert.True(analysis.Completes(analysis.Find(Fulfilment)));
        Assert.True(analysis.Completes(null));
        Assert.Equal([Shipped, "01JSTA00000000000000000013"], analysis.FinalDescendants(analysis.Find(Fulfilment)).Select(s => s.Id));
        Assert.Equal([Review, Pending, Resume], analysis.EntryClosure(analysis.Find(Resume)!).Select(s => s.Id).Order(StringComparer.Ordinal));
        Assert.Equal([Fulfilment, Shipping, Packing, "01JSTA00000000000000000011", "01JSTA00000000000000000012"],
            analysis.EntryClosure(analysis.Find(Packing)!).Select(s => s.Id).Order(StringComparer.Ordinal));
        Assert.Empty(analysis.EventlessCycles());
        var decide = Assert.Single(analysis.Groups, g => g.Source == Decide);
        Assert.Equal((TransitionTrigger.Always, ""), (decide.Trigger, decide.Key));
        Assert.Equal([1, 2], decide.Transitions);
        Assert.Equal(9, analysis.Groups.Length);
    }

    [Fact]
    public void Analysis_ignores_guards_but_not_structure()
    {
        var model = Load();
        model.State(Shipped).Remove("type");
        var analysis = new ProcessAnalysis(model.Build().Get<Process>(P)!);

        Assert.False(analysis.Completes(analysis.Find(Fulfilment)));
        Assert.Contains(Closed, (IReadOnlySet<string>)analysis.Reachable); // still through cancel
        Assert.True(analysis.Completes(null));
    }

    [Fact]
    public void Analysis_finds_an_eventless_cycle_with_the_transition_that_closes_it()
    {
        var model = Load();
        Failing["MQ9009 eventless cycle"](model);
        var analysis = new ProcessAnalysis(model.Build().Get<Process>(P)!);

        var (states, transition) = Assert.Single(analysis.EventlessCycles());

        Assert.Equal(2, transition);
        Assert.Contains(states, s => s.Id == Decide);
        Assert.Contains(states, s => s.Id == Pending);
    }
}
