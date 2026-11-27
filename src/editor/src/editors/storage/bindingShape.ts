// What a binding's shape beyond one table changes for its field map, free of React so the Storage tab and the mock backend agree: the
// bound base (table per type), what the base's binding stores, what its child tables store, and the key columns the base foreign key joins.
import { boundBaseOf, joinedKeyColumns, upperLevels, type BoundBase, type JoinedKeyColumn } from "./baseTable";
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
  /** What the field map and the checks leave out. */
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
  // What the tables above store, as the engine reads it: the nearest upper level, which a subtype sharing a table-per-type table has too.
  const above = upperLevels(entityId, binding, docs)[0] ?? (binding.baseForeignKey ? base : undefined);
  const baseStored = new Set(above ? storageAttributes(above.entityId, docs).map((a) => a.ref) : []);
  const childStored = new Set((binding.childTables ?? []).map((c) => c.attribute));
  const fk = host?.foreignKeys?.find((k) => k.id === binding.baseForeignKey);
  const joined = fk ? joinedKeyColumns(fk, host, baseHost, base?.binding, baseAttributes) : [];
  return {
    base,
    host,
    baseHost,
    baseAttributes,
    joined,
    extras: {
      baseStored,
      childStored,
      baseFieldsRefused: !!binding.baseForeignKey,
      baseKeyColumns: new Map(joined.map((j) => [j.column, j.attribute ?? j.referenced])),
    },
  };
}
