"use client";

import { useQuery } from "@tanstack/react-query";
import { useSearchParams } from "next/navigation";
import { Suspense, useMemo } from "react";

import { GenerateForm } from "@/components/ai/generate-form";
import { JobList } from "@/components/ai/job-list";
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
    return <p className="text-sm text-slate-600">{vi.ai.forbidden}</p>;
  }
  if (!departmentId) {
    return <p className="text-sm text-slate-600">{vi.ai.noDepartment}</p>;
  }

  // Mount the form once its limits and any prefill are known, so defaults and the count limit are fixed at mount.
  const ready = !quotaQuery.isPending && !(fromJobId && fromJobQuery.isPending);

  return (
    <section className="space-y-6">
      <h1 className="text-2xl font-bold text-slate-900">{vi.ai.title}</h1>
      {ready ? (
        <GenerateForm departmentId={departmentId} quota={quotaQuery.data} initial={fromJobQuery.data?.params} />
      ) : (
        <p className="text-sm text-slate-500">{vi.common.loading}</p>
      )}
      <JobList subjectNames={subjectNames} />
    </section>
  );
}

export default function AiPage() {
  return (
    <Suspense fallback={<p>{vi.common.loading}</p>}>
      <AiPageInner />
    </Suspense>
  );
}
