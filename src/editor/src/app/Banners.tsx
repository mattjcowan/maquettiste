import { useEffect } from "react";
import { CircleAlert, Info, RefreshCw, X } from "lucide-react";
import { Button } from "@/components/ui/button";
import { useEditor } from "@/state/store";
import { useServices } from "./context";
import { onApiProblem } from "@/api/client";

/** "New version" after site.deployed with unsaved drafts; "signed out" after a 401. */
export function Banners() {
  const { store } = useServices();
  const banner = useEditor(store, (s) => s.banner);
  useEffect(
    () =>
      onApiProblem((problem) => {
        if (problem.status === 401)
          store.getState().setBanner({
            kind: "signed-out",
            text: "Your editor session ended (the token changed or the cookie expired). Sign in again to keep editing.",
          });
      }),
    [store],
  );
  if (!banner) return null;
  const tone = banner.kind === "signed-out" ? "border-danger" : "border-accent";
  return (
    <div role="alert" className={`flex items-center gap-2 border-b ${tone} bg-surface px-2 py-1 text-13`} data-testid={`banner-${banner.kind}`}>
      {banner.kind === "signed-out" ? <CircleAlert className="size-4 text-danger" aria-hidden /> : <Info className="size-4 text-accent" aria-hidden />}
      <span className="flex-1">{banner.text}</span>
      {banner.kind === "deployed" || banner.kind === "signed-out" ? (
        <Button size="sm" variant="primary" onClick={() => window.location.reload()}>
          <RefreshCw />
          {banner.kind === "signed-out" ? "Sign in" : "Reload"}
        </Button>
      ) : null}
      <Button size="icon-sm" variant="ghost" label="Dismiss" onClick={() => store.getState().setBanner(null)}>
        <X />
      </Button>
    </div>
  );
}

export function Notices() {
  const { store } = useServices();
  const notice = useEditor(store, (s) => s.notice);
  useEffect(() => {
    if (!notice) return;
    const timer = setTimeout(() => {
      if (store.getState().notice?.id === notice.id) store.setState({ notice: null });
    }, 5000);
    return () => clearTimeout(timer);
  }, [notice, store]);
  return (
    <div aria-live="polite" className="pointer-events-none fixed bottom-4 right-4 z-50 flex flex-col gap-2">
      {notice ? (
        <div
          role={notice.level === "error" ? "alert" : "status"}
          data-testid="notice"
          className={`pointer-events-auto max-w-sm rounded-panel border ${notice.level === "error" ? "border-danger" : "border-default"} bg-raised px-2 py-1 text-13 shadow-float`}
        >
          {notice.text}
        </div>
      ) : null}
    </div>
  );
}
