"use client";

import { useQuery } from "@tanstack/react-query";
import { useSearchParams } from "next/navigation";
import { Suspense, useMemo } from "react";

import { formatQuotaLine, GenerateForm } from "@/components/ai/generate-form";
import { JobList } from "@/components/ai/job-list";
import { PageHeader } from "@/components/shell/page-header";
import { EmptyState } from "@/components/ui/empty-state";
import { Notice } from "@/components/ui/notice";
import { quotaRefreshDelay } from "@/lib/ai/quota-refresh";
import { fetchAllSubjects } from "@/lib/api/catalog";
import { api } from "@/lib/api/client";
import { getApiErrorMessage } from "@/lib/api/error";
import { useAuth } from "@/lib/auth/auth-context";
import { vi } from "@/messages/vi";

function AiPageInner() {
  const { me } = useAuth();
  const fromJobId = useSearchParams().get("from");
  const departmentId = me?.departmentId ?? null;
  const canGenerate = (me?.role === "teacher" || me?.role === "head") && Boolean(departmentId);

  const quotaQuery = useQuery({
    queryKey: ["ai", "quota"],
    enabled: canGenerate,
    queryFn: async () => {
      const { data, error } = await api.GET("/ai/quota");
      if (error || !data) throw new Error(getApiErrorMessage(error));
      return data;
    },
    // The quota resets at the UTC day boundary: reload just after resetsAt so a page left open overnight
    // unlocks again, and on refocus because a hidden tab skips interval refetches.
    refetchInterval: (query) => quotaRefreshDelay(query.state.data?.resetsAt, Date.now()),
    refetchOnWindowFocus: true,
  });

  // "Sinh lại với cùng yêu cầu": reuse the parameters of an earlier job; a missing job just leaves the form empty.
  const fromJobQuery = useQuery({
    queryKey: ["ai", "job", fromJobId],
    enabled: canGenerate && Boolean(fromJobId),
    queryFn: async () => {
      const { data, error } = await api.GET("/ai/jobs/{id}", { params: { path: { id: fromJobId! } } });
      if (error || !data) throw new Error(getApiErrorMessage(error));
      return data;
    },
  });

  const subjectsQuery = useQuery({
    queryKey: ["catalog", "subjects", departmentId],
    enabled: canGenerate,
    queryFn: () => fetchAllSubjects(departmentId!),
  });
  const subjectNames = useMemo(
    () => new Map((subjectsQuery.data ?? []).map((s) => [s.id, s.name] as const)),
    [subjectsQuery.data],
  );

  if (!me) return null;
  if (me.role !== "teacher" && me.role !== "head") {
    return <Blocked message={vi.ai.forbidden} />;
  }
  if (!departmentId) {
    return <Blocked message={vi.ai.noDepartment} />;
  }

  // Mount the form once its limits and any prefill are known, so defaults and the count limit are fixed at mount.
  const ready = !quotaQuery.isPending && !(fromJobId && fromJobQuery.isPending);

  return (
    <>
      <PageHeader title={vi.ai.title} description={quotaQuery.data ? formatQuotaLine(quotaQuery.data) : undefined} />
      <div className="grid items-start gap-6 px-4 py-5 sm:px-7 sm:py-6 lg:grid-cols-2">
        <div className="min-w-0">
          {ready ? (
            <GenerateForm departmentId={departmentId} quota={quotaQuery.data} initial={fromJobQuery.data?.params} />
          ) : (
            <EmptyState busy title={vi.common.loading} />
          )}
        </div>
        <div className="min-w-0">
          <JobList subjectNames={subjectNames} />
        </div>
      </div>
    </>
  );
}

function Blocked({ message }: { message: string }) {
  return (
    <>
      <PageHeader title={vi.ai.title} />
      <div className="px-4 py-5 sm:px-7 sm:py-6">
        <Notice tone="warn">{message}</Notice>
      </div>
    </>
  );
}

export default function AiPage() {
  return (
    <Suspense fallback={<EmptyState busy title={vi.common.loading} />}>
      <AiPageInner />
    </Suspense>
  );
}
