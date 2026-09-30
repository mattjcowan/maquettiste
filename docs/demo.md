# Demo: Maquettiste in an existing TypeScript service (20 minutes)

What the audience sees, in three even parts, inside a partner team's existing TypeScript/Node repository:

1. **The editor** (7 min): from an empty model, a Shop domain, an OrderStatus enum, Customer, Product, Order and
   OrderLine, relations with cardinality, a diagram and a database with its live DDL.
2. **The CLI** (7 min): `init`, the model as JSON files, `generate` with the built-in `sql-ddl` pack and with a custom
   TypeScript pack (`samples/typescript-pack`), a parameter change, a template change, `--check` as the CI gate,
   `--watch` while editing in the editor.
3. **Claude Code over MCP** (5 min): `init --mcp --docker <image> --skill`, then two prompts that change and question the model.

Plus one minute of slack. Every command is for **macOS zsh** on the demo Mac (Apple silicon, Podman or Docker Desktop). The
walkthrough was rehearsed end to end on a Linux (WSL2, amd64, Docker Engine) machine with `mattjcowan/maquettiste:0.1.0`
built from this tree: first on 2026-09-28, then again on 2026-09-29 after the editor was rebuilt, from an empty model,
with every CLI and MCP step run from the image. The timings in [Rehearsal record](#rehearsal-record) come from there.
Steps marked **(not verified on a Mac)** ran only on Linux; steps marked **(not verified)** did not run at all.

Throughout, `MQ` is your clone of the Maquettiste repository and `REPO` is the partner team's repository:

```zsh
export MQ=~/src/maquettiste          # adjust
export REPO=~/src/partner-service    # adjust
```

## Before the talk (Mac)

One image, one tag, one compose file. Do this the day before, then only step 4 fifteen minutes before 10:30.
Podman and Docker Desktop both work with the compose file unchanged: the editor looks at who owns `.maquettiste/` and runs
as that owner (Docker Desktop), or stays root when root owns it (rootless Podman, whose root inside the container is you
outside it). No `chmod` and no user variables. With Podman, type `podman` wherever this guide says `docker` (and
`podman compose` for `docker compose`), or install its `docker` command alias.

**1. Pull the image** (published for arm64 and amd64):

```zsh
docker pull mattjcowan/maquettiste:0.3.0   # needs an image whose entrypoint picks the folder's owner (docker/README.md)
```

**2. The `maquettiste` command.** The image carries the CLI, so the Mac needs nothing else. Put this in `~/.zshrc`
(then `source ~/.zshrc`); it runs every command in a throwaway container over the folder you are in:

```zsh
maquettiste() { docker run --rm $([ -t 0 ] && echo -it) --user "$(id -u):$(id -g)" -v "$PWD:/repo" -w /repo mattjcowan/maquettiste:0.3.0 maquettiste "$@"; }
# With Podman, leave --user out (root in the container is you):
# maquettiste() { podman run --rm $([ -t 0 ] && echo -it) -v "$PWD:/repo" -w /repo mattjcowan/maquettiste:0.3.0 maquettiste "$@"; }
maquettiste --version                    # maquettiste 0.3.0 (engine contract 1.0.0, model format 1)
```

**3. Prepare the repository.** In the partner repository, on a branch of its own:

```zsh
cd $REPO
git switch -c maquettiste-demo
maquettiste init                         # writes .maquettiste/ (settings, schemas, the sql-ddl pack) and a .gitignore block
maquettiste validate                     # Validation passed: 0 errors, 0 warnings, 0 infos.
git clone --depth 1 git@github.com:mattjcowan/maquettiste.git /tmp/maquettiste   # only for the TypeScript pack sample (part 2)
export MQ=/tmp/maquettiste
```

`init` names the project from `package.json` `name`, else the git remote, else the folder; `maquettiste init --name Acme`
on a fresh `.maquettiste/` sets it by hand. Then save this as `maquettiste.compose.yaml` in the repository root (it is
the image's compose file with the tag pinned; the model folder and the repository are mounted, nothing else):

```yaml
services:
  maquettiste:
    image: mattjcowan/maquettiste:0.3.0
    user: "0:0"                                                     # starts as root, then runs as the owner of .maquettiste/
    ports: ["127.0.0.1:8080:8080"]
    volumes:
      - maquettiste-host:/data                                      # the editor's own state (users, keys, index cache)
      - ./.maquettiste:/data/sites/maquettiste.localhost/data       # the model
      - ./:/repo                                                    # the repository: generated files land here
    environment:
      MAQUETTISTE_REPO_ROOT: /repo
      MAQUETTISTE_UID: ${MAQUETTISTE_UID:-}                         # optional override; empty = the owner of .maquettiste/
      MAQUETTISTE_GID: ${MAQUETTISTE_GID:-}
      MAQUETTISTE_EDITOR_TOKEN: ${MAQUETTISTE_EDITOR_TOKEN:-}
volumes:
  maquettiste-host:
```

`init` must run before the first `compose up`: compose creates a missing mount folder itself, and then the editor
starts on a project with no settings.

**4. Start the editor** (15 minutes before the talk):

```zsh
cd $REPO
export MAQUETTISTE_EDITOR_TOKEN=$(openssl rand -hex 24)
echo $MAQUETTISTE_EDITOR_TOKEN | pbcopy   # paste it on the sign-in page
docker compose -f maquettiste.compose.yaml up -d
until curl -fsS -H 'Host: maquettiste.localhost:8080' http://127.0.0.1:8080/api/health >/dev/null; do sleep 1; done; echo ready
```

Open **http://maquettiste.localhost:8080**, paste the token into **Editor token**, **Sign in**. You see the project name in
the top bar, the **Domain model** explorer on the left and, in the centre, the first-run panel **Start the model** with New
domain, New entity, New enum, New reference type, New diagram and New database. Leave it there for the talk.

**5. Three checks, once, the day before:**

- `ls -ln .maquettiste/model` after a save in the editor shows your own UID (`id -u`), under Podman and under Docker
  Desktop. `docker compose -f maquettiste.compose.yaml logs | grep "running as\|staying root"` says which user the editor
  picked: your UID under Docker Desktop, root under Podman. If the save fails with "Permission denied", the image is older
  than the owner rule: pull it again and `up -d`.
- In a second terminal, `maquettiste generate --watch`, then save something in the editor: it regenerates within a
  second. If it does not react, Docker Desktop is not forwarding file events; run `maquettiste generate` by hand in
  part 2 instead.
- Claude Code in `$REPO` with the `.mcp.json` and `mcp.sh` from step 3.1 lists 39 tools under `/mcp` (docs/mcp.md lists them; re-check the count on the Mac).

Also before the talk: a second terminal tab in `$REPO`, the repository open in a code editor, Claude Code logged in,
the browser zoomed to 125% for Zoom, and port 8080 free (`lsof -nP -iTCP:8080 -sTCP:LISTEN` prints nothing).

To stop and restart: `docker compose -f maquettiste.compose.yaml down` and `up -d` again (add `down -v` to also drop the
editor's own state). Alternatives that are not needed for the talk (building the image locally, the CLI as a .NET tool
from NuGet, zod and tsconfig notes) are in the [appendix](#appendix-alternatives-not-needed-for-the-talk).

## 1. Model in the editor (7 minutes)

Everything is created in the editor, from the empty model step 3 left. The rail on the left holds, top to bottom,
**Domain model**, **Reference data**, **Databases**, **Diagrams** and **Generate**, with **Settings** (gear) and
**Account** at its foot; the sidebar shows one explorer at a time. Every New dialog starts its **Domain** picker on the
current domain (the row you right-clicked, else the selected element's domain), so everything below lands in Shop.

| Step | Do | Expected |
| --- | --- | --- |
| 1.1 (45 s) | Point at the rail, the top bar (project name, git branch, "Go to element or run a command", Ctrl+K), and the first-run panel **Start the model**. Click **New domain**: Name `Shop`, Parent domain "None (a top-level domain)", **Create**. | The DOMAIN MODEL explorer shows **Shop** "empty"; the domain opens as a centre tab with **General**, **Tags** and **Categories**, the inspector on the right; Problems says "No problems"; toast "Created Shop." |
| 1.2 (45 s) | Right-click **Shop** › **New enum**. Name `OrderStatus` (Domain already Shop), **Create**. On its **Members** tab click **Add member** four times and fill Name and Code: `Pending`/`pending`, `Paid`/`paid`, `Shipped`/`shipped`, `Cancelled`/`cancelled` (Tab goes Name, Value, Code). | The enum tab (Members, Code generation, References), Values 0 to 3 filled in; Shop › Enums › OrderStatus in the explorer; "Saved". The codes are what the TypeScript pack writes in part 2. |
| 1.3 (2.5 min) | Right-click **Shop** › **New entity**, Name `Customer`, **Create**. It opens on its **Attributes** tab with the key `id` (uuid). Click **Add attribute**: the new row is already editing Name. Type `email`, Tab, Enter (opens the Type list), `string`, Enter, Tab, Enter, `200`, Tab (Len), Tab, Tab (past Prec and Scale), Space (ticks Req); with no length, Tab four times from Type to Req. Add `name` (string, Len 120, Req). Then `Order` (right-click the **Entities** folder › **New entity** does the same): `number` (string, 20, Req), `status` (type `OrderStatus`, Req), `placedAt` (`datetimeoffset`, Req). `OrderLine`: `quantity` (`int32`, Req), `unitPrice` (`decimal`, Prec 12, Scale 2, Req). `Product`: `sku` (string, 40, not required), `price` (`decimal`, 12, 2, Req). | Each entity is a centre tab (Attributes, Relationships, Indexes, Mappings, Seed data, References, Code generation) and a file under `.maquettiste/model/entities/`. The grid's hint reads "Enter edits · Tab moves · Ctrl+Enter adds · Ctrl+Delete removes"; the Type list has sections (Built-in, Enums, Reference data, ...) and typing filters it. Shop › Entities shows 4. |
| 1.4 (45 s) | Right-click **Shop** › **New diagram**, Name `Shop overview`, **Create**. The canvas opens and the sidebar switches to the **Diagrams** explorer. Click the **Domain model** rail icon, click **Customer**, **Shift+click** **Product** (selects the four), right-click › **Add to diagram**. Click **Auto-layout**. | Toast "Added 4 elements to Shop overview."; four cards with their attributes (`status` shows `OrderStatus`, `price` `decimal(12,2)`). The picker above the canvas shows **Shop overview** (it also lists "All of Shop"). |
| 1.5 (1.25 min) | Click the Customer card, **Shift+click** Order, **New relation** (canvas toolbar). In "New relation: Customer → Order": Name `places`; Customer end Min 1, Max 1; Order end Role `orders` (Min 0, Max `*` are the defaults); On delete of the Customer: restrict (the default); **Create relation**. Then Order + OrderLine: `contains`, Kind **composition**, Order end 1..1, OrderLine end Role `lines`, On delete: **cascade**. Then Product + OrderLine: `refersTo`, Product end 1..1. **Auto-layout**. | Edges labelled places, contains and refersTo with `1` and `*` at the ends; Shop › Relationships lists the three. (Right-click Shop › **New relationship** also works, with From entity and To entity pickers but no cardinality: set that in its editor.) |
| 1.6 (1 min) | Click the **Databases** rail icon (third): the empty explorer offers **New database**. Name `main`, **Map domains by convention**: **Pick domains**, tick **Shop**, Dialect `postgresql`, **Create** (with None, the default, the database starts empty: say that entities become tables only where you map them). Re-check on the Mac. In the inspector set **Default schema** to `shop`, Tab. Right-click **main** › **Expand all**, then double-click table **orders**. | "main · 4 tables · 4 entities mapped", then main › shop › Tables (customers, order_lines, orders, products). The Database screen: a Tables list, the table diagram, and the **DDL preview** of `shop.orders` with `customer_id uuid NOT NULL` and `CONSTRAINT fk_orders_customer_id FOREIGN KEY (customer_id) REFERENCES shop.customers (id) ON DELETE RESTRICT`. |

If the Database screen says "No databases" right after 1.6's **Create** (an image older than the fix of known issue 2), click **main** in the explorer.
Optional if time allows: select the Order card and open the inspector's **JSON** tab (the file path
`.maquettiste/model/entities/order.json`, ids everywhere, `status` as `{ "ref": ... }`).

### Fallback: the model without the editor

If the editor misbehaves, or time runs short, write the same model as files and through the editor's API. On the empty
model of step 3, first the domain, the diagram, the enum and the database (canonical form, so no MQ1003 warnings):

```zsh
mkdir -p .maquettiste/model/{packages,diagrams,enums,databases/main}
cat > .maquettiste/model/packages/shop.json <<'EOF'
{
  "$schema": "../../.schema/v1/package.json",
  "kind": "package",
  "id": "01K6DEMS000000000000000001",
  "name": "Shop",
  "description": "Customers, the catalog and their orders."
}
EOF
cat > .maquettiste/model/diagrams/shop-overview.json <<'EOF'
{
  "$schema": "../../.schema/v1/diagram.json",
  "kind": "diagram",
  "id": "01K6DEMS000000000000000002",
  "name": "Shop overview",
  "package": "01K6DEMS000000000000000001"
}
EOF
cat > .maquettiste/model/enums/order-status.json <<'EOF'
{
  "$schema": "../../.schema/v1/enum.json",
  "kind": "enum",
  "id": "01K6DEMS000000000000000003",
  "name": "OrderStatus",
  "package": "01K6DEMS000000000000000001",
  "members": [
    {
      "id": "01K6DEMS000000000000000004",
      "name": "Pending",
      "value": 0,
      "code": "pending"
    },
    {
      "id": "01K6DEMS000000000000000005",
      "name": "Paid",
      "value": 1,
      "code": "paid"
    },
    {
      "id": "01K6DEMS000000000000000006",
      "name": "Shipped",
      "value": 2,
      "code": "shipped"
    },
    {
      "id": "01K6DEMS000000000000000007",
      "name": "Cancelled",
      "value": 3,
      "code": "cancelled"
    }
  ]
}
EOF
cat > .maquettiste/model/databases/main/database.json <<'EOF'
{
  "$schema": "../../../.schema/v1/database.json",
  "kind": "database",
  "id": "01K6DEMS000000000000000008",
  "name": "main",
  "dialect": "postgresql",
  "byConvention": "all",
  "version": "16",
  "defaultSchema": "shop",
  "schemas": [
    {
      "id": "01K6DEMS000000000000000009",
      "name": "shop"
    }
  ]
}
EOF
maquettiste validate                     # Validation passed: 0 errors, 0 warnings, 0 infos.
```

Then the four entities and three relations in one batch through the editor's batch endpoint (the editor must be
running), and show them with the diagram picker's **All of Shop**, or **Add related** on the diagram:

```zsh
cat > /tmp/seed-shop.mjs <<'EOF'
// Creates Customer, Product, Order, OrderLine and three relations in the starter's Shop package through the editor's
// batch endpoint. Usage: MAQUETTISTE_EDITOR_TOKEN=... node /tmp/seed-shop.mjs [port]
import http from 'node:http';
const port = process.argv[2] ?? '8080', pkg = '01K6DEMS000000000000000001', status = '01K6DEMS000000000000000003';
let n = 0;
const id = () => '01K6DEMF' + String(++n).padStart(18, '0');
const attr = (name, type, extra = {}) => ({ id: id(), name, type, ...extra });
const entity = (name, attrs) => {
  const key = attr('id', 'uuid', { required: true });
  return { kind: 'entity', id: id(), name, package: pkg, key: { attributes: [key.id], strategy: 'uuid-v7' }, attributes: [key, ...attrs] };
};
const customer = entity('Customer', [attr('email', 'string', { length: 200, required: true }), attr('name', 'string', { length: 120, required: true })]);
const product = entity('Product', [attr('sku', 'string', { length: 40 }), attr('name', 'string', { length: 200, required: true }), attr('price', 'decimal', { precision: 12, scale: 2, required: true })]);
const order = entity('Order', [attr('number', 'string', { length: 20, required: true }), attr('status', { ref: status }, { required: true }), attr('placedAt', 'datetimeoffset', { required: true })]);
const line = entity('OrderLine', [attr('quantity', 'int32', { required: true }), attr('unitPrice', 'decimal', { precision: 12, scale: 2, required: true })]);
const end = (e, role, extra = {}) => ({ id: id(), entity: e.id, role, navigation: role, ...extra });
const rel = (name, a, b, extra = {}) => ({ kind: 'relation', id: id(), name, package: pkg, ...extra, ends: [a, b] });
const docs = [customer, product, order, line,
  rel('places', end(customer, 'customer', { min: 1, max: 1, onDelete: 'restrict' }), end(order, 'orders')),
  rel('contains', end(order, 'order', { min: 1, max: 1, onDelete: 'cascade' }), end(line, 'lines'), { relationKind: 'composition' }),
  rel('refersTo', end(line, 'orderLines'), end(product, 'product', { min: 1, max: 1, onDelete: 'restrict' }))];
const body = Buffer.from(JSON.stringify({ operations: docs.map((element) => ({ op: 'create', element })) }));
const headers = { 'Content-Type': 'application/json', 'Content-Length': body.length, Host: `maquettiste.localhost:${port}` };
if (process.env.MAQUETTISTE_EDITOR_TOKEN) headers.Authorization = `Bearer ${process.env.MAQUETTISTE_EDITOR_TOKEN}`;
http.request({ hostname: '127.0.0.1', port, path: '/api/model/batch', method: 'POST', headers }, (res) => {
  let text = '';
  res.on('data', (c) => (text += c)).on('end', () => console.log(res.statusCode, text.slice(0, 300)));
}).end(body);
EOF
node /tmp/seed-shop.mjs 8080            # 200 {"outcome":"saved",...}
```

Use it only on a model without these entities (after a reset), otherwise the batch is refused for duplicate names and
nothing is written. Without the editor at all, Claude Code over MCP (part 3) can create the same model from the prompt
"Create Customer, Product, Order and OrderLine in Shop as described in docs/demo.md part 1" **(not verified)**.

## 2. The CLI (7 minutes)

| Step | Command | Expected |
| --- | --- | --- |
| 2.1 (45 s) | `maquettiste init` then `git status --short` | Now prints `kept .maquettiste/maquettiste.json`, `kept .maquettiste/.schema/v1/ (26 schemas, current)`, `kept .maquettiste/templates/sql-ddl/ (12 files, 12 kept)`, `kept .gitignore (maquettiste block)`. Explain what the first run wrote: `maquettiste.json` (output roots `db` committed and `src/Generated` built, the `sql-ddl` pack writing to `db`), the JSON schemas for editor completion, the pack's templates as plain files, and a `.gitignore` block; the project's name came from the repository (`--name`, else `package.json`'s `name`, else the git remote, else the folder), which is what the editor's top bar shows (re-check on the Mac). `git status` shows ` M .gitignore` and `?? .maquettiste/`. |
| 2.2 (45 s) | `ls .maquettiste/model/*/` then `cat .maquettiste/model/entities/order.json` | One folder per kind (databases, diagrams, entities, enums, packages, relations, ...), one file per element; the entity with ULID ids, and `status` typed `{ "ref": "<the enum's id>" }` (references are ids, never names). |
| 2.3 (1 min) | `maquettiste validate && maquettiste generate --progress none` then `cat db/main/shop/tables/orders.sql` | `Validation passed`; `A db/main/migrations/0001.sql`, `A db/main/schema.sql`, `A db/main/seed.sql`, `A db/main/shop/tables/<4 tables>.sql`, "Outcome: Succeeded" (validate 0.8 s, generate 1.3 s from the image). The `shop` folder is 1.6's Default schema. The DDL has `customer_id uuid NOT NULL` and `fk_orders_customer_id ... ON DELETE RESTRICT`. |
| 2.4 (1.5 min) | Install the custom pack, then register it (below). `maquettiste generate --progress none`, `cat src/generated/order.ts src/generated/order.schema.ts`, `npx tsc --noEmit` | `A src/generated/customer.ts` ... `A src/generated/index.ts`, `order-status.ts`, four `*.schema.ts` (1.35 s); `K db/main/migrations/0001.sql` means the migration is kept (written once, yours). `order.ts` has `status: OrderStatus`, `customerId: string`, `customer?: Customer`, `lines?: OrderLine[]`. `tsc` prints nothing (0.8 s). |
| 2.5 (45 s) | Set a parameter (below), `maquettiste format`, `maquettiste generate --progress none`, `cat src/generated/order-status.ts` | `formatted .maquettiste/maquettiste.json` (the hand edit back in canonical form; skip it and `generate` warns MQ1003). Then `M src/generated/order-status.ts`: `export const OrderStatus = { Pending: 'pending', ... } as const;`. Mention `"zod": false` removes the four schema files (`D ...schema.ts`). |
| 2.6 (45 s) | Edit one template line (below) and `maquettiste generate --progress none`, `git diff --stat` | `M` for the four entity files only ("0 added, 4 modified, 0 deleted, 12 unchanged"): every attribute is now `readonly`. |
| 2.7 (1 min) | `maquettiste generate --check --progress none; echo $?` then, in the editor, change Customer `name` Len 120 to 150 (Ctrl+P, `Customer`, Enter; double-click the Len cell of `name`, `150`, Enter), and run the check again | First `Outcome: Succeeded`, `0`. Then `Drift: committed output does not match the model; run maquettiste generate and commit the result (exit 2).` with `A db/main/migrations/0002.sql`, `M db/main/schema.sql`, `M db/main/shop/tables/customers.sql`, `M src/generated/customer.schema.ts`, an `error MQ6018` line (the schema snapshot of database 'main' is stale: the next `generate` writes it), and `2`. This is the CI gate. (A hand edit of a generated file gives `MQ6009 Hand edit` and exit 3.) |
| 2.8 (1 min) | Second tab: `maquettiste generate --watch --progress none`. In the editor, on Customer's Attributes tab, **Add attribute** `phone` (string, Len 40). Ctrl+C when done. | `[watch] initial run: ...` with the pending drift applied, then after the edit `M src/generated/customer.ts`, `M src/generated/customer.schema.ts`, `A db/main/migrations/0003.sql`, `M db/...` within half a second of the save (0.36 s measured). |

Commands for 2.4 (copy the pack, then replace `.maquettiste/maquettiste.json`; `name` is the one `init` gave in step 3):

```zsh
cp -R $MQ/samples/typescript-pack .maquettiste/templates/typescript
cat > .maquettiste/maquettiste.json <<'EOF'
{
  "$schema": ".schema/v1/maquettiste.json",
  "formatVersion": 1,
  "name": "partner-service",
  "outputs": {
    "allow": [
      {
        "path": "db",
        "commit": true
      },
      {
        "path": "src/generated",
        "commit": true
      }
    ]
  },
  "packs": {
    "sql-ddl": {
      "output": "db"
    },
    "typescript": {
      "output": "src/generated"
    }
  }
}
EOF
```

Say while it generates: `src/generated` is a committed root so `--check` guards it in CI; a built root would be
gitignored instead. Show `src/generated/index.ts` and, if the service has an entry point, import from it
(`import { orderSchema, type Order } from './generated/index.js';`).

For 2.5, in the code editor, make the `typescript` entry of `.maquettiste/maquettiste.json` read
`"typescript": { "output": "src/generated", "parameters": { "enumStyle": "const" } }` and save, then run
`maquettiste format`, which rewrites the file in canonical form (`maquettiste format --check` exits 2 when a file would
change, for CI). Set it back to `"union"` (or delete `parameters`) before part 3 if you prefer the union form. If the
partner model has reference types, the pack writes each as a union of its codes (`product-status.ts`), whatever
`enumStyle` says.

For 2.6, in the code editor open `.maquettiste/templates/typescript/entity.scriban`, find the line
`{{ end }}  {{ camel a.name }}{{ if !a.required }}?{{ end }}: {{ attr_type(a) }};` and insert `readonly ` before
`{{ camel a.name }}`. Or:

```zsh
sed -i '' 's/^{{ end }}  {{ camel a.name }}/{{ end }}  readonly {{ camel a.name }}/' .maquettiste/templates/typescript/entity.scriban
```

Undo it with `git checkout -- .maquettiste/templates/typescript/entity.scriban` only after committing the pack, or
reverse the `sed` (swap the two patterns), then generate again.

### Or do it in the editor

Every step of part 2 that touches the packs can be shown on the Generate screen instead, which answers "what does
sql-ddl do, what templates does it run, where does it write" on screen. The files are the same ones the CLI reads.

- 2.4: after copying the pack and saving `maquettiste.json`, choose **Generate** in the rail. The explorer lists
  `sql-ddl` and `typescript` with their units, each read aloud (`entity · each entity → entity.scriban →
  <entity>.ts`); click `typescript` to open it: **Units** shows each unit's scope, template, output path and an example
  path, **Outputs** what it wrote. Back on **Plan**: **Plan** prints
  `typescript: <n> units, <n> files to add` (the counts depend on the partner model) and the files grouped by unit, each with its
  element and why it renders; **Apply plan** writes them.
- 2.5: in the `typescript` tab, **Parameters**: set `enumStyle` to `const` and save; **Plan** then shows
  `order-status.ts` to modify, "Parameter enumStyle changed".
- 2.6: **Templates**, open `entity.scriban`, add `readonly ` on the attribute line: the preview on the right follows as you
  type (pick `Order` as the element). Ctrl+S, then **Plan**: four files to modify, grouped under `typescript/entity`,
  each "Template entity.scriban changed". **Unchanged units** lists the rest; **Why not?** on one says its inputs are
  unchanged since the last run.
- A new pack for the partner: **+** in the Generate explorer header, a name, Empty or a copy of `typescript`.

Rehearse this path once on the Mac before showing it: the unit counts and the exact cause sentences come from the
partner's model.

## 3. Claude Code with MCP (5 minutes)

| Step | Do | Expected |
| --- | --- | --- |
| 3.1 (45 s) | `maquettiste init --mcp --docker mattjcowan/maquettiste:0.3.0 --skill`, then `cat .mcp.json` | `created mcp.sh (runs maquettiste mcp in mattjcowan/maquettiste:0.3.0; log in .maquettiste/.cache/mcp.log)`, `created .mcp.json (server maquettiste: ./mcp.sh)`, `created .claude/skills/maquettiste-modeling/SKILL.md`. The file registers `{"type": "stdio", "command": "./mcp.sh", "args": []}`; `mcp.sh` runs `docker run -i --rm --user <you> ... maquettiste mcp` from the editor's own image over the repository, with the same model and write path as the editor, and logs the server's messages to `.maquettiste/.cache/mcp.log`. (With the .NET tool fallback, drop `--docker ...`: `init --mcp` then writes `{"type": "stdio", "command": "maquettiste", "args": ["mcp"]}`.) |
| 3.2 (30 s) | `claude`, approve the project server `maquettiste` when asked, type `/mcp` | maquettiste connected, 39 tools (get_model_index, get_element, create_element, apply_batch, validate, plan, get_plan_diff, apply_plan, reference_type_usage, get_translations, ...). |
| 3.3 (2 min) | Prompt: `Add a Shipment entity related to Order (an order has many shipments) with carrier, an optional trackingNumber, shippedAt and a status enum ShipmentStatus (Preparing, InTransit, Delivered). Then validate and generate.` | About 50 s. Claude sends one `apply_batch` (the enum ShipmentStatus, the entity Shipment, a composition `ships` from Order to many Shipments), then `validate`, `plan`, `apply_plan`. Files: `A src/generated/shipment.ts`, `shipment-status.ts`, `shipment.schema.ts`, `M src/generated/order.ts` (`shipments?: Shipment[]`), `M index.ts`, `A db/main/shop/tables/shipments.sql`, `A db/main/migrations/000N.sql` with `CREATE TABLE shop.shipments ... REFERENCES shop.orders (id) ON DELETE RESTRICT` (the default; add "cascade on delete" to the prompt for CASCADE). The editor shows Shipment in the explorer without a reload; **Add related** on Order puts it on the diagram. |
| 3.4 (1.5 min) | Prompt: `What would change in the generated code and the database scripts if Product.sku became required? Do not change the model; answer in at most 8 lines.` | About 20 s. In rehearsal: `product.ts` drops the `?` on `sku`, `product.schema.ts` drops `.optional()`, `schema.sql` and `products.sql` get `sku varchar(40) NOT NULL`, the existing migrations stay and the next one (`0005.sql`, named correctly) adds `ALTER TABLE ... ALTER COLUMN sku SET NOT NULL`, with a warning about existing NULL rows; the model is unchanged. |
| 3.5 (optional) | `npx tsc --noEmit` | Still compiles with the new Shipment types. |

What was verified for part 3 (2026-09-29, from the image): `init --mcp --skill` keeping the Docker-form entry, the
server's `initialize`, `tools/list` (39 tools) and `validate` over `docker run -i`, and prompts 3.3 and 3.4 word for word
as headless Claude Code runs through this `.mcp.json` (`claude -p '<prompt>' --mcp-config .mcp.json --allowedTools
mcp__maquettiste`): 50 s and 20 s, and `tsc` passed afterwards. The interactive session (approving the server, `/mcp`)
was not run **(not verified)**; it uses the same file.

Commands for 3.1. `--docker` writes the wrapper `mcp.sh` (executable, no absolute path, safe to commit) and registers it;
Claude Code starts it in `$REPO`. An older `.mcp.json` that already names a `maquettiste` server is kept as it is, so remove
that entry (or the file) first when the repository has one from an earlier rehearsal. With Podman, the wrapper's
`--user "$(id -u):$(id -g)"` must go (or become `--userns=keep-id`), and `MAQUETTISTE_DOCKER=podman` names the engine
(docs/mcp.md):

```zsh
maquettiste init --mcp --docker mattjcowan/maquettiste:0.3.0 --skill
cat .mcp.json mcp.sh
echo '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"check","version":"1"}}}' | ./mcp.sh
```

The last line prints the server's `initialize` answer (one line of JSON with `"serverInfo":{"name":"maquettiste"...`) and
exits: the server answers what it has read before it stops. If Claude Code reports the server as failed, read
`.maquettiste/.cache/mcp.log`; "docker not found" there means Claude Code was started without Docker on its `PATH`:
`export MAQUETTISTE_DOCKER=$(which docker)` in the shell that starts `claude`. The wrapper, the `"type": "stdio"` entry and
the answer on a closed stdin are covered by the CLI tests and were checked on Linux with the image built from this tree; the
Mac run of `./mcp.sh` is **(not verified on a Mac)**.

Fallback if Claude Code or the network fails: show the same change through the editor, or read out the verified
answers above.

## Troubleshooting

**Port 8080 is taken.** `lsof -nP -iTCP:8080 -sTCP:LISTEN` names the process. Start on another port and use it in
every URL and `curl`:

```zsh
MAQUETTISTE_PORT=8090 docker compose -f maquettiste.compose.yaml up -d
# open http://maquettiste.localhost:8090 ; curl with -H 'Host: maquettiste.localhost:8090' http://127.0.0.1:8090/...
```

**The Host header and `*.localhost`.** The host routes by name: the site is `maquettiste.localhost`, and local trust
needs a `localhost` or `*.localhost` Host. Browsers resolve `maquettiste.localhost` to loopback; `curl` and scripts
should call `127.0.0.1` with `-H 'Host: maquettiste.localhost:8080'`. Opening `http://127.0.0.1:8080` or
`http://localhost:8080` in the browser reaches the host itself, which redirects to its own `/login` page (measured):
that is not the editor, use the `maquettiste.localhost` URL.

**The token.** The sign-in page appears whenever the container sees the browser as a remote peer (always under Docker
Desktop). The token is whatever `MAQUETTISTE_EDITOR_TOKEN` held at `up`; a new shell loses it. `echo $MAQUETTISTE_EDITOR_TOKEN`,
and if it is empty, set a new one and recreate the container (`... up -d` with the variable set; the model is untouched).
"Your editor session ended" means the token changed: sign in again. Logs: `docker compose -f maquettiste.compose.yaml logs | grep -i "peer\|token"`.

**An element landed in "Not in a domain".** It was created with nothing selected: set **Domain** in its editor, or
right-click it › **Move to domain…**. Right-click **Shop** for New actions and the picker starts on Shop.

**The top bar says `repo`.** `init` found no `package.json` name and no git remote and fell back to the container's
folder `/repo`: set `"name"` in `.maquettiste/maquettiste.json` (then `maquettiste format`) and reload the page.

**Generated files owned by someone else.** Files should belong to you (step 5). If `ls -ln` shows UID 1654, an older
image wrote them: pull the image again, `up -d`, and remove the old files through a container:
`docker run --rm --user 0 -v "$PWD:/w" --entrypoint rm mattjcowan/maquettiste:0.3.0 -rf /w/<path>`.

**`maquettiste: command not found`.** The function of step 2 is not defined in this shell: `source ~/.zshrc`. With the .NET
tool fallback, `export PATH="$PATH:$HOME/.dotnet/tools"`; Claude Code inherits the PATH of the shell that starts it.

**`Access to the path '/repo/...' is denied`.** A `docker run` without `--user` on Linux: the container's UID 1654 cannot
write the repository. Use the function of step 2, which passes `--user "$(id -u):$(id -g)"`.

**`generate` exits 3 (hand edit).** Somebody edited a generated file: `maquettiste generate --hand-edits overwrite`.

**The editor did not notice a change made by the CLI or Claude.** Reload the page; the editor's watcher should pick up
file changes live (it did on Linux; **not verified on a Mac**).

**Never delete or rename the site `maquettiste.localhost`** in the host's management UI: its data folder is your
`.maquettiste/`, and the host deletes it recursively. Recovery is `git checkout -- .maquettiste`.

### Reset the demo repository

```zsh
cd $REPO
docker compose -f maquettiste.compose.yaml down -v   # the editor and its host volume
git checkout -- . && git clean -fd -- .maquettiste .mcp.json .claude db src/generated
git status --short                                                       # clean again
```

Then start again at step 3. To redo only parts 2 and 3 on the same model, commit after part 1
(`git add .maquettiste && git commit -m "Demo model"`) during rehearsal, and reset to that commit instead.

### WSL rehearsal notes

The rehearsal machine is WSL2 with Docker Engine running inside the distribution (`docker version` reports "Docker
Engine - Community", not Docker Desktop), so it behaves like Linux, not like the Mac:

- Port 8090 there is held by another Maquettiste container (`billing-maquettiste-1`, the dev billing fixture) and 8080
  by Windows services, so the re-rehearsal used `MAQUETTISTE_PORT=8094`. The Mac commands above keep 8080.
- The 2026-09-29 rehearsal (image 0.1.0) needed ACLs for UID 1654 on the bind mounts. The current entrypoint needs neither
  ACLs nor variables on Linux: it runs as the owner of `.maquettiste/`, 1000:1000 there, and the
  files it writes are yours (`docker/smoke.sh` checks it).
- The browser is a local peer there (the container's gateway), so the editor opens without the sign-in page even with a token set.
- The rehearsal repository of 2026-09-29 is `shop-api-2` in the session scratchpad
  (`/tmp/claude-1000/-home-mjc-projects-github-com-mattjcowan-maquettiste/4bd3cdf0-05e3-4c41-8e17-843507f7abf8/scratchpad/demo/shop-api-2`),
  a fresh clone of `shop-api` at its baseline commit "shop-api before Maquettiste", on branch `maquettiste-demo`. The
  Playwright scripts that followed part 1 (`r2/lib.cjs`, `r2/a.cjs` to `r2/h.cjs`, run with `node` from `src/editor`)
  and their screenshots are in `scratchpad/demo/r2`; `shop-api-3` is the throwaway copy where the TypeScript pack was
  checked against reference types. Every CLI command ran through the function of B2 (bash, same body).

## Rehearsal record

### Re-rehearsal, 2026-09-29

Linux (WSL2, amd64, Docker Engine), image `mattjcowan/maquettiste:0.1.0` built from the final tree, every CLI command
from the image as the user (`--user 1000:1000`), the editor on port 8094, a fresh repository. Part 1 was driven by a
Playwright script that follows the steps above click for click, so its times are machine times: allow the table's
minutes when you do it by hand.

| Step | Result | Time |
| --- | --- | --- |
| `maquettiste --version` / `init` | `maquettiste 0.3.0 (engine contract 1.0.0, model format 1)`; 4 lines | 0.5 s / 0.65 s |
| `compose up` to `/api/health` 200, first boot | succeeded | 4.9 s |
| 1.1 First-run panel, New domain Shop | created, "No problems" | 0.5 s |
| 1.2 New enum from Shop's menu, four members with codes | saved, Domain preset to Shop | 3.5 s |
| 1.3 Four entities (three from Shop's menu, one from the Entities folder), ten attributes by keyboard | all in Shop, types, lengths and Req as typed | 7 s |
| 1.4 New diagram, Domain model rail icon, Shift+click, Add to diagram, Auto-layout | "Added 4 elements to Shop overview." | 2.5 s |
| 1.5 Three relations on the canvas (places, contains as composition, refersTo) | edges with cardinality | 1.2 s |
| 1.6 New database from the empty Databases explorer (rehearsed before the convention choice existed; re-check on the Mac), Default schema `shop`, Expand all, orders | DDL preview with the foreign key; "No databases" until the row was clicked | 5 s |
| 2.3 `validate` / `generate` sql-ddl (7 files) | 0 errors / Succeeded | 0.8 s / 1.3 s |
| 2.4 `generate` with the TypeScript pack (10 more) / `npx tsc --noEmit` | Succeeded / passed | 1.35 s / 0.8 s |
| 2.5 `enumStyle: const` hand edit (MQ1003 warning), `format`, `generate` | 1 formatted, 1 modified | 0.6 s, 1.3 s |
| 2.6 Template edit (`sed` above) | 4 modified | 1.4 s |
| 2.7 `generate --check` clean / after the editor's Len edit | exit 0 / exit 2 with MQ6018 | 1.3 s each |
| 2.8 `--watch` from the image: editor adds `phone`, to regenerated `customer.ts` | 5 files; `tsc` passed | 0.36 s |
| 3.1 `init --mcp --skill` over the Docker-form `.mcp.json`; probe `initialize`, `tools/list`, `validate` | entry kept, skill created; 24 tools; 0 errors | 0.7 s; 0.6 s; 52 ms |
| 3.3 `claude -p` with the prompt as written | Shipment, ShipmentStatus, `ships`; 8 files; `tsc` passed | 50 s |
| 3.4 `claude -p` with the prompt as written | correct, next migration named right, model unchanged | 20 s |
| `format --check` on a hand-minified file / `format` | exit 2 / exit 0 | 0.6 s |
| TypeScript pack over reference types (throwaway copy: ProductStatus with 5 rows, Unit with none) | `type ProductStatus = 'DRAFT' \| ...`, `z.enum(productStatusValues)`, `string[]` for the empty one; second run unchanged; `tsc` passed | 1.3 s |
| `compose down -v` | removed | 0.7 s |

Not run: the interactive Claude Code session (3.2), the sign-in page (the browser is a local peer here), and anything on
macOS or Docker Desktop.

### First rehearsal, 2026-09-28

Linux (WSL2, amd64, Docker Engine 29.6.1), image `mattjcowan/maquettiste:0.1.0` from the tree of that day, CLI
1.0.0-alpha.1 packed from it. The editor steps then used starter files, since superseded by part 1 above.

| Step | Result | Time |
| --- | --- | --- |
| Image build, `--no-cache`, base images present | succeeded | 70 s |
| CLI `dotnet pack` (host SDK 10.0.109) / in the SDK container with dotnet-install | succeeded | 24 s / 70 s |
| `dotnet tool install` from the local feed | succeeded | 1 s |
| `maquettiste init` | 4 lines, `.maquettiste/` and `.gitignore` | 0.2 s |
| `compose up` to `/api/health` 200: first boot / restart | succeeded | 7 s / 1.2 s |
| Model through `POST /api/model/batch` (11 elements) | `200 saved` | under 1 s |
| Editor walk in Playwright: sign in with the token, open Order; New entity x2; Shift+click, New relation | all passed | 0.4 s / 1.1 s / 1.8 s |
| `validate` | 0 errors | 0.3 s |
| `generate` sql-ddl (7 files) / with the TypeScript pack (10 more) | Succeeded | 0.66 s / 0.74 s |
| `npx tsc --noEmit` over the generated code (zod 3) and a runtime `orderSchema.safeParse` | passed | 0.8 s |
| Parameters `enumStyle: const`, `zod: false`, back to defaults | 2 modified, 4 deleted; then restored; `tsc` passed both ways | under 1 s each |
| `generate --check`: clean / after a model edit / after a hand edit | exit 0 / exit 2 (drift) / exit 3 (MQ6009) | 0.7 s each |
| `--watch`: template edit to regenerated files; Shipment added over MCP to regenerated files | 4 files; 8 files | 0.5 s; about 1 s |
| Editor Generate plan over both packs (API, `force`) | succeeded, no changes (same bytes as the CLI) | 1 s |
| `maquettiste mcp`: `initialize`, `tools/list` | 18 tools | 0.3 s |
| Image rebuilt with the CLI (engine stage cached) | succeeded, 957 MB (was 947 MB) | 26 s |
| From the image, `--user 1000:1000`: `--version` / `init` / `validate` / `generate` / `generate --check` | `1.0.0`; all exit 0, every file owned by 1000 | 0.5 s / 0.8 s / 0.9 s / 1.5 s / 1.5 s |
| From the image: `--check` after a hand edit; `init --mcp --skill` over a Docker-form `.mcp.json` | exit 3; entry kept | 1.5 s; 0.7 s |
| From the image: `generate --watch`, model save to regenerated file; SIGINT | 4 files; exits 0 | 0.5 s; 0.2 s |
| From the image: `docker run -i ... maquettiste mcp`: `initialize`, `tools/list`, `validate` | 18 tools, 0 errors | 0.7 s; `validate` 55 ms |
| `claude -p` over `.mcp.json`: list entities; Product.sku question; Docker-form server | correct answers | 7.7 s; 15 s; 21 s |

Not run: the gate 2 Playwright walk (`samples/reference-app/tools/gate2.sh walk`) works on the reference application's
seeded model, not on this one, and needs the full prepare and seed steps first (well over 5 minutes); a smaller
Playwright walk of the same editor over the demo model ran instead (above). Nothing ran on macOS or Docker Desktop.

### Known issues

Routed around above. Issues 1, 2 and 4 are fixed in the source since the rehearsal, not yet in the rehearsed image.

1. **The CLI from the image named a new project `repo`**, the folder name inside the container. Fixed in the source
   after the rehearsal: `init` now takes `--name`, else `package.json`'s name, else the git remote's repository name
   (B5). Re-check on the Mac with a rebuilt image.
2. **Right after New database, the Database screen said "No databases"** (fixed in the source after the rehearsal: the
   screen lists the index's databases at once; re-check on the Mac) ("Add a database element to the model to design
   its tables.") while the explorer already lists `main`. Workaround: click `main` in the explorer.
3. **A hand edit of `maquettiste.json` or a model file gets MQ1003** ("The file is not in canonical form") in `generate`
   and in Problems. Workaround: `maquettiste format` (2.5).
4. **The domain editor showed Display name, Plural name and Description twice** (above the tabs and again on General).
   Fixed in the source after the rehearsal: General leaves them to the header. Re-check on the Mac.
5. **Packing the CLI needs `bench/`** (`BenchCommand.cs` references `BenchmarkReport`): a partial checkout without it
   fails with CS0246. Only the .NET tool fallback of B2 packs it; a full clone is fine.

Fixed since the first rehearsal and verified on 2026-09-29: the image carries the CLI; docs/user-guide.md covers the
command line; docker/README.md and compose.yaml say to run `init` before `up`; an empty model shows the first-run
panel, and right-click menus and empty explorers create domains, enums, diagrams and databases (so does each explorer
header's **+** button, not clicked in rehearsal) (the starter files are now only a fallback); every New dialog
starts on the current domain (all four entities landed in Shop); `generate` prints each progress stage once, in order,
and `--check` prints no write stage; `maquettiste format` exists; and Claude's answer to 3.4 named the next migration
correctly.

## Appendix: alternatives (not needed for the talk)

**Build the image locally** instead of pulling (native arm64, 5 to 10 minutes on a first build):
`git clone git@github.com:mattjcowan/maquettiste.git && cd maquettiste && docker build -f docker/Dockerfile -t mattjcowan/maquettiste:0.3.0 .` (the tag the compose file and the function name)

**The CLI as a .NET tool** (needs the .NET 10 SDK; NuGet has 0.1.0, which predates `init --mcp --docker`, so prefer the image's version once it is on NuGet): `dotnet tool install -g Maquettiste.Cli --version 0.1.0`,
then `export PATH="$PATH:$HOME/.dotnet/tools"` and `unfunction maquettiste` in a shell that defined the Docker function.
Each Docker command pays a container start of about half a second; the native tool does not.

**zod and tsconfig.** The TypeScript pack's schemas need zod 3.23 or later (`npm ls zod`); zod 4 keeps the used forms as
deprecated ones, and if `tsc` complains set the pack's `zod` parameter to `false`. With `"moduleResolution": "bundler"` set
the pack's `importExtension` to `""`; with `NodeNext` the default `.js` is right. The pack writes `src/generated/`; pick
another folder if that one exists.

**Port 8080 taken.** Change the `ports` line of `maquettiste.compose.yaml` to `"127.0.0.1:8090:8080"` and open
http://maquettiste.localhost:8090.
