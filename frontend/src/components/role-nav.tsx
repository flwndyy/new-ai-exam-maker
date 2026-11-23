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
      <Link href="/exams/" className="text-sm text-slate-600 hover:text-slate-900">
        {vi.nav.exams}
      </Link>
      <Link href="/questions/"className="text-sm text-slate-600 hover:text-slate-900">
        {vi.nav.questions}
      </Link>
      {(me.role === "teacher" || me.role === "head") && (
        <Link href="/ai/" className="text-sm text-slate-600 hover:text-slate-900">
          {vi.nav.ai}
        </Link>
      )}
      {(me.role === "head" || me.role === "admin") && (
        <Link href="/review/" className="text-sm text-slate-600 hover:text-slate-900">
          {vi.nav.review}
        </Link>
      )}
      {me.role === "admin" && (
        <>
          <Link href="/admin/ai-usage/" className="text-sm text-slate-600 hover:text-slate-900">
            {vi.nav.aiUsage}
          </Link>
          <Link href="/admin/" className="text-sm text-slate-600 hover:text-slate-900">
            {vi.nav.admin}
          </Link>
        </>
      )}
    </>
  );
}
