// Read-only regions: while a snapshot is shown "as of" (docs/engineering/snapshots.md section 4) the shell marks the
// explorer, the screen, the inspector and the bottom panel read-only, and the text controls in them (Input, Textarea,
// the code editors) take `readOnly`. Search and filter fields (`type="search"`) stay usable: they change the view only.
import { createContext, useContext } from "react";

const ReadOnlyContext = createContext(false);

export const ReadOnlyProvider = ReadOnlyContext.Provider;

/** True inside a read-only region. */
export function useReadOnly(): boolean {
  return useContext(ReadOnlyContext);
}
