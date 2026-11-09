// The Storage tab's card for a relation with an end of several types, driven like a person (several-types-design.md 19.10): a pick on an
// existing mapping whose save waits for a fetch, "A type and an id" built column by column, and a link table chosen on a relation with no
// mapping (written only once each end has an entry).
import { useState } from "react";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { QueryClientProvider } from "@tanstack/react-query";
import { describe, expect, it } from "vitest";
import { ServicesProvider } from "@/app/context";
import { SeveralTypesCard } from "@/editors/storage/RelationStorageSeveral";
import type { StorageMapping, TableDocWithKeys } from "@/editors/storage/relationStorage";
import { useMockApi } from "./harness";

type Mapping = (StorageMapping & { id: string }) | null;

const ends = [
  { id: "e1", entity: "Comment", role: "comment", max: "*" as const },
  { id: "e2", role: "attachedTo", max: 1 as const, types: [{ entity: "Part" }, { entity: "Assembly" }] },
];

const col = (id: string, name: string, type = "uuid") => ({ id, name, type });
const tables: Record<string, TableDocWithKeys> = {
  tComments: {
    id: "tComments",
    name: "comments",
    columns: [col("c_id", "id"), col("c_type", "attached_type", "string"), col("c_att", "attached_id"), col("c_p", "part_id"), col("c_a", "assembly_id")],
    primaryKey: { columns: ["c_id"] },
    foreignKeys: [
      { id: "fk_part", name: "fk_part", columns: ["c_p"], referencesTable: "tParts" },
      { id: "fk_asm", name: "fk_asm", columns: ["c_a"], referencesTable: "tAssemblies" },
    ],
  },
  tParts: { id: "tParts", name: "parts", columns: [col("p_id", "id")], primaryKey: { columns: ["p_id"] } },
  tAssemblies: { id: "tAssemblies", name: "assemblies", columns: [col("a_id", "id")], primaryKey: { columns: ["a_id"] } },
  bundles: {
    id: "bundles",
    name: "bundles",
    columns: [col("b_cid", "comment_id"), col("b_att", "attached_id"), col("b_type", "attached_type", "string")],
    foreignKeys: [{ id: "fk_b_comment", name: "fk_b_comment", columns: ["b_cid"], referencesTable: "tComments" }],
  },
};
const boundTables: Record<string, string> = { Comment: "tComments", Part: "tParts", Assembly: "tAssemblies" };

/** Holds the mapping in state; a save lands after a delay, as it does when the editor first fetches the document. */
function Host({ initial, onWrite, bound = boundTables }: { initial: Mapping; onWrite: (m: Mapping) => void; bound?: Record<string, string> }) {
  const [mapping, setMapping] = useState<Mapping>(initial);
  return (
    <SeveralTypesCard
      dom="t"
      databaseName="Store"
      ends={ends}
      roleOf={(id) => ends.find((e) => e.id === id)?.role ?? id}
      mapping={mapping}
      change={(mutate) => {
        void new Promise((r) => setTimeout(r, 15)).then(() => {
          const next = structuredClone(mapping ?? { id: "M" }) as StorageMapping & { id: string };
          mutate(next);
          setMapping(next);
          onWrite(next);
        });
      }}
      loading={false}
      tableOf={(id) => (id ? tables[id] : undefined)}
      boundTable={(entity) => bound[entity] ?? null}
      sharedBound={(entity) => new Set(bound[entity] ? [bound[entity]] : [])}
      entityName={(id) => id}
      linkTables={[{ id: "bundles", name: "bundles" }]}
      attributes={[]}
    />
  );
}

describe("the card for a relation with several types", () => {
  const api = useMockApi();
  const mount = (initial: Mapping, onWrite: (m: Mapping) => void = () => undefined, bound?: Record<string, string>) =>
    render(
      <ServicesProvider services={api.services}>
        <QueryClientProvider client={api.services.queryClient}>
          <Host initial={initial} onWrite={onWrite} bound={bound} />
        </QueryClientProvider>
      </ServicesProvider>,
    );
  const pick = (label: string | RegExp, value: string) => fireEvent.change(screen.getByLabelText(label), { target: { value } });

  it("saves a key picked on an existing mapping whose save waits for a fetch", async () => {
    let last: Mapping = null;
    mount({ id: "M", ends: [{ end: "e2", types: [{ entity: "Part", foreignKey: "fk_part" }] }] }, (m) => (last = m));
    pick("Key for Assembly", "fk_asm");
    await waitFor(() =>
      expect(last?.ends?.[0]?.types).toEqual([
        { entity: "Part", foreignKey: "fk_part" },
        { entity: "Assembly", foreignKey: "fk_asm" },
      ]),
    );
    // The picker shows what was saved.
    expect((screen.getByLabelText("Key for Assembly") as HTMLSelectElement).value).toBe("fk_asm");
  });

  it("sets up A type and an id: the choice survives the first column, and the type column can be picked", async () => {
    let last: Mapping = null;
    mount({ id: "M", ends: [{ end: "e2", foreignKey: "x" }].slice(0, 0) } as Mapping, (m) => (last = m));
    // No mapping content yet: choose Held in, then Linked by.
    pick("Held in, in Store", "other-table");
    await waitFor(() => expect(screen.getByLabelText("Linked by, attachedTo")).toBeTruthy());
    pick("Linked by, attachedTo", "type-and-id");
    pick("Id column", "c_att");
    await waitFor(() => expect(last?.ends).toEqual([{ end: "e2", idColumn: "c_att" }]));
    // Reads as A type and an id, with its Type column picker, though only the id column is written.
    expect((screen.getByLabelText("Linked by, attachedTo") as HTMLSelectElement).value).toBe("type-and-id");
    await waitFor(() => expect((screen.getByLabelText("Type column") as HTMLSelectElement).disabled).toBe(false));
    pick("Type column", "c_type");
    await waitFor(() => expect(last?.ends).toEqual([{ end: "e2", idColumn: "c_att", typeColumn: "c_type" }]));
  });

  it("holds a link table on a relation with no mapping until each end has a way, then writes it whole", async () => {
    const writes: Mapping[] = [];
    mount(null, (m) => writes.push(m));
    pick("Held in, in Store", "link-table");
    pick("Link table in Store", "bundles");
    // Nothing is written yet (the engine refuses a link table with no entry for an end), and the card says so.
    await waitFor(() => expect(screen.getByTestId("relation-storage-held")).toBeTruthy());
    expect((screen.getByLabelText("Link table in Store") as HTMLSelectElement).value).toBe("bundles");
    expect(writes).toEqual([]);
    pick("Linked by, attachedTo", "type-and-id");
    pick("Id column", "b_att");
    await new Promise((r) => setTimeout(r, 40));
    expect(writes).toEqual([]);
    pick("Key", "fk_b_comment");
    await waitFor(() => expect(writes).toHaveLength(1));
    expect(writes[0]).toMatchObject({
      junctionTable: "bundles",
      shape: "junction",
      ends: [
        { end: "e2", idColumn: "b_att" },
        { end: "e1", foreignKey: "fk_b_comment" },
      ],
    });
    expect(screen.queryByTestId("relation-storage-held")).toBeNull();
  });

  it("switches an existing other-end mapping to A link table without keeping its entries", async () => {
    mount({ id: "M", ends: [{ end: "e2", types: [{ entity: "Part", foreignKey: "fk_part" }] }] });
    pick("Held in, in Store", "link-table");
    await waitFor(() => expect(screen.getByLabelText("Link table in Store")).toBeTruthy());
    expect((screen.getByLabelText("Held in, in Store") as HTMLSelectElement).value).toBe("link-table");
  });

  it("gives a type with no table here no Refers to picker", async () => {
    mount({ id: "M", ends: [{ end: "e2", idColumn: "c_att" }] }, undefined, { Comment: "tComments", Part: "tParts" });
    await waitFor(() => expect(screen.getAllByTestId("relation-refers-to")).toHaveLength(1));
    expect(screen.queryByLabelText("Refers to, in Assembly")).toBeNull();
  });
});
