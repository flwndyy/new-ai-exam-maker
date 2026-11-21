"use client";

import { useAuth } from "@/lib/auth/auth-context";
import { vi } from "@/messages/vi";

export function UserMenu() {
  const { status, me, logout } = useAuth();
  if (status !== "signedIn" || !me) return null;

  const details = [vi.auth.roles[me.role], me.departmentName].filter(Boolean).join(" · ");

  return (
    <div className="ml-auto flex items-center gap-3 text-sm">
      <div className="text-right">
        <div className="font-medium">{me.fullName}</div>
        <div className="text-xs text-slate-500">{details}</div>
      </div>
      <button
        type="button"
        onClick={() => void logout()}
        className="rounded border border-slate-300 px-3 py-1 hover:bg-slate-100"
      >
        {vi.auth.logout}
      </button>
    </div>
  );
}
