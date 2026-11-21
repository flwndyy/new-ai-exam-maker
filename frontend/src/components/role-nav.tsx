"use client";

import Link from "next/link";

import { useAuth } from "@/lib/auth/auth-context";
import { vi } from "@/messages/vi";

/** Render role-appropriate feature links for DEV-A workflows without touching auth logic. */
export function RoleNav() {
  const { status, me } = useAuth();
  if (status !== "signedIn" || !me) return null;

  return (
    <>
      {(me.role === "teacher" || me.role === "head") && (
        <Link href="/questions/" className="text-sm text-slate-600 hover:text-slate-900">
          {vi.nav.questions}
        </Link>
      )}
      {(me.role === "head" || me.role === "admin") && (
        <Link href="/review/" className="text-sm text-slate-600 hover:text-slate-900">
          {vi.nav.review}
        </Link>
      )}
      {me.role === "admin" && (
        <Link href="/admin/" className="text-sm text-slate-600 hover:text-slate-900">
          {vi.nav.admin}
        </Link>
      )}
    </>
  );
}
