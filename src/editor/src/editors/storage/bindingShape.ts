// What a binding's shape beyond one table changes for its field map, free of React so the Storage tab and the mock backend agree: the
// bound base (table per type), what the base's binding stores, what its child tables store, and the key columns the base foreign key joins.
import { boundBaseOf, joinedKeyColumns, type BoundBase, type JoinedKeyColumn } from "./baseTable";
import { storageAttributes, type Binding, type ProblemExtras, type StorageAttribute } from "./fieldMap";
import { plainTable, type TableDoc } from "./tableDoc";
import { hostOf } from "./typeColumn";

type Json = Record<string, unknown>;

export interface BindingShape {
  base: BoundBase | undefined;
  /** The documents of the table the binding keeps its rows in and of the table the base's binding does, when they are table files. */
  host: TableDoc | undefined;
  baseHost: TableDoc | undefined;
  baseAttributes: StorageAttribute[];
  joined: JoinedKeyColumn[];
  /** What the field map and the checks leave out: `baseStored` is empty without a base foreign key. */
  extras: ProblemExtras;
}

/** `tableOf` answers a table's document by id. */
export function bindingShape(entityId: string, binding: Binding, docs: ReadonlyMap<string, Json>, tableOf: (id: string) => TableDoc | undefined): BindingShape {
  const base = boundBaseOf(entityId, binding.database, docs);
  const hostKey = plainTable(hostOf(binding));
  const baseHostKey = base ? plainTable(hostOf(base.binding)) : undefined;
  const host = hostKey ? tableOf(hostKey) : undefined;
  const baseHost = baseHostKey ? tableOf(baseHostKey) : undefined;
  const baseAttributes = base ? storageAttributes(base.entityId, docs) : [];
  const baseStored = new Set(binding.baseForeignKey ? baseAttributes.map((a) => a.ref) : []);
  const childStored = new Set((binding.childTables ?? []).map((c) => c.attribute));
  const fk = host?.foreignKeys?.find((k) => k.id === binding.baseForeignKey);
  const joined = fk ? joinedKeyColumns(fk, host, baseHost, base?.binding, baseAttributes) : [];
  return {
    base,
    host,
    baseHost,
    baseAttributes,
    joined,
    extras: { baseStored, childStored, baseKeyColumns: new Map(joined.map((j) => [j.column, j.attribute ?? j.referenced])) },
  };
}
