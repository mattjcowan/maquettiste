// The search worker (explorer-redesign.md 3.1; SPEC 13: client-side search indexing runs in workers). One index,
// shared by the tree filter, quick open and the palette; `search/client.ts` talks to it.
import { createSearchHandler, type ToWorker } from "./engine";

const handle = createSearchHandler();
const scope = self as unknown as DedicatedWorkerGlobalScope;
scope.onmessage = (e: MessageEvent<ToWorker>) => {
  const answer = handle(e.data);
  if (answer) scope.postMessage(answer);
};
