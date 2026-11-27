"use client";

import { usePathname } from "next/navigation";
import type { ReactNode } from "react";

import { isPublicPath, useAuth } from "@/lib/auth/auth-context";
import { SideNav } from "./side-nav";

/**
 * Page frame. The side menu only exists for a signed-in user on a non-public path;
 * otherwise just the content area renders (login redirect, access denied, callback).
 * Keeping the same element structure in both cases avoids remounting the content.
 */
export function AppShell({ children }: { children: ReactNode }) {
  const { status, me } = useAuth();
  const pathname = usePathname();
  const showNav = status === "signedIn" && me !== null && !isPublicPath(pathname);

  return (
    <div className="flex min-h-screen print:block">
      {showNav ? <SideNav /> : null}
      <main className="min-w-0 flex-1">{children}</main>
    </div>
  );
}
