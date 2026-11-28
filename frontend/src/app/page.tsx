"use client";

import { Shortcuts } from "@/components/home/shortcuts";
import { TodoSection } from "@/components/home/todo-section";
import { PageHeader } from "@/components/shell/page-header";
import { useAuth } from "@/lib/auth/auth-context";
import { vi } from "@/messages/vi";

const BODY = "px-4 py-5 sm:px-7 sm:py-6";

export default function HomePage() {
  const { me } = useAuth();
  if (!me) return <PageHeader title={vi.nav.home} />;
  return (
    <>
      <PageHeader title={vi.home.greeting(me.fullName)} description={vi.auth.roleLine(me.role, me.departmentName ?? null)} />
      <div className={`${BODY} space-y-7`}>
        <p className="max-w-[62ch] text-[14px] text-ink-soft">{vi.home.description}</p>
        <Shortcuts role={me.role} />
        <TodoSection role={me.role} />
      </div>
    </>
  );
}
