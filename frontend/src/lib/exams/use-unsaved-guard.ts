import { useEffect } from "react";

/**
 * Ask the browser to confirm closing/reloading the tab while there are unsaved edits.
 * App Router has no navigation-blocking API, so in-app links outside the page are not covered.
 */
export function useUnsavedGuard(dirty: boolean): void {
  useEffect(() => {
    if (!dirty) return;
    const handler = (event: BeforeUnloadEvent) => {
      event.preventDefault();
      event.returnValue = "";
    };
    window.addEventListener("beforeunload", handler);
    return () => window.removeEventListener("beforeunload", handler);
  }, [dirty]);
}
