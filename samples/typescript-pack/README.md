# typescript: a custom template pack for a Node service

A small pack that turns a Maquettiste model into TypeScript types and zod schemas. It is an example of custom
generation templates: copy the folder into a repository and it generates next to the built-in packs.

```sh
mkdir -p .maquettiste/templates
cp -R <maquettiste>/samples/typescript-pack .maquettiste/templates/typescript
```

Then, in `.maquettiste/maquettiste.json`, allow an output folder and register the pack:

```json
"outputs": { "allow": [ { "path": "db", "commit": true }, { "path": "src/generated", "commit": true } ] },
"packs": { "sql-ddl": { "output": "db" }, "typescript": { "output": "src/generated" } }
```

`maquettiste generate` then writes `src/generated/`. With `"commit": true` the files are meant to be committed and
`maquettiste generate --check` guards them in CI; drop it to treat the folder as build output (gitignored).

## Files

| File | What it does |
| --- | --- |
| `pack.json` | The pack: three units and their parameters. `entity` renders once per entity to `<kebab-name>.ts`; `model` renders once for the model; `schema` renders once per non-abstract entity. |
| `helpers.js` | JavaScript helpers: `ts_type` and `zod_type` map the model's built-in types (`string`, `uuid`, `decimal`, `datetimeoffset`, ...) to TypeScript and zod; `ts_string` writes a single-quoted literal; `ts_module` writes a relative import. Case conversion uses the engine's own `pascal`, `camel` and `kebab`. |
| `entity.scriban` | One interface per entity. Required attributes are plain, others get `?`. Enum attributes use the enum's type; reference attributes use the reference type's union of codes. Each to-one relation end adds its id (`customerId`) and every navigation adds an optional property (`customer?: Customer`, `lines?: OrderLine[]`). |
| `model.scriban` | File blocks: one module per enum (`order-status.ts`) and per reference type (`product-status.ts`), then the barrel `index.ts` that re-exports every generated module, sorted. |
| `schema.scriban` | File blocks: one zod schema per entity (`order.schema.ts`, `orderSchema` and `OrderInput`), with the ids of to-one relation ends. Enum and reference attributes validate against the exported `...Values` arrays. Written only while the `zod` parameter is true. |

## Parameters

Set them in `maquettiste.json` under `packs.typescript.parameters`; the next `generate` re-renders only what they change.

| Parameter | Default | Effect |
| --- | --- | --- |
| `zod` | `true` | `false` stops the `schema` unit and removes the `*.schema.ts` files it wrote. |
| `enumStyle` | `"union"` | `"union"`: `type OrderStatus = 'pending' \| 'paid'`. `"const"`: a const object `OrderStatus.Paid` plus the same type. Both export `orderStatusValues`. |
| `importExtension` | `".js"` | The extension of relative imports: `.js` for `"moduleResolution": "NodeNext"`, `""` for bundlers. |

## Type choices

`decimal` and `int64` are strings (a JS number loses precision), dates and times are ISO strings, `json` is `unknown`,
`binary` is a base64 string, and value objects are `Record<string, unknown>`. A reference attribute holds a row's code:
a reference type becomes `type ProductStatus = 'DRAFT' | 'ACTIVE' | ...` with `productStatusValues`, in row order (plain
`string` while the type has no rows), so a new row changes only that module. Change `helpers.js` to choose otherwise.
The output uses two-space indentation, single quotes and semicolons, and depends only on the model, the settings and
the pack, so a second run changes nothing. It needs zod 3.23 or later (`z.string().date()`).
