"use client";

import { keepPreviousData, useQuery } from "@tanstack/react-query";
import { useState } from "react";

import { api } from "@/lib/api/client";
import { getApiErrorMessage, getUiErrorMessage } from "@/lib/api/error";
import type { components } from "@/lib/api/schema";
import { useAuth } from "@/lib/auth/auth-context";
import { vi } from "@/messages/vi";

type AdminUser = components["schemas"]["AdminUser"];
type Filter = { userId: string; from: string; to: string };

const numberFormat = new Intl.NumberFormat("vi-VN");
const costFormat = new Intl.NumberFormat("vi-VN", { minimumFractionDigits: 4, maximumFractionDigits: 4 });
const formatCost = (usd: number) => `$${costFormat.format(usd)}`;
const EMPTY_FILTER: Filter = { userId: "", from: "", to: "" };

async function fetchAllAdminUsers(): Promise<AdminUser[]> {
  const items: AdminUser[] = [];
  for (let page = 1; ; page++) {
    const { data, error } = await api.GET("/admin/users", { params: { query: { page, pageSize: 100 } } });
    if (error || !data) throw new Error(getApiErrorMessage(error));
    items.push(...data.items);
    if (!data.totalPages || page >= data.totalPages) return items;
  }
}

/** Admin AI usage report. Cost is an estimate from the configured price table, never the AWS bill. */
export default function AiUsagePage() {
  const { me } = useAuth();
  const isAdmin = me?.role === "admin";
  const [draft, setDraft] = useState<Filter>(EMPTY_FILTER);
  const [applied, setApplied] = useState<Filter>(EMPTY_FILTER);
  const [page, setPage] = useState(1);

  const usersQuery = useQuery({
    queryKey: ["admin", "users", "all"],
    enabled: isAdmin,
    queryFn: fetchAllAdminUsers,
  });

  const usageQuery = useQuery({
    queryKey: ["admin", "ai-usage", applied, page],
    enabled: isAdmin,
    placeholderData: keepPreviousData,
    queryFn: async () => {
      const { data, error } = await api.GET("/admin/ai-usage", {
        params: {
          query: {
            page,
            pageSize: 20,
            userId: applied.userId || undefined,
            from: applied.from || undefined,
            to: applied.to || undefined,
          },
        },
      });
      if (error || !data) throw new Error(getApiErrorMessage(error));
      return data;
    },
  });

  if (!me) return null;
  if (!isAdmin) return <p className="text-sm text-slate-600">{vi.common.forbidden}</p>;

  const data = usageQuery.data;
  const inputClass = "mt-1 rounded border border-slate-300 px-3 py-2 text-sm";

  return (
    <section className="space-y-4">
      <h1 className="text-2xl font-bold text-slate-900">{vi.aiUsage.title}</h1>
      <p className="rounded border border-amber-300 bg-amber-50 px-3 py-2 text-sm text-amber-900">
        {vi.aiUsage.estimateNotice}
      </p>

      <form
        className="flex flex-wrap items-end gap-3"
        onSubmit={(e) => {
          e.preventDefault();
          setApplied(draft);
          setPage(1);
        }}
      >
        <label className="flex flex-col text-sm text-slate-700">
          {vi.aiUsage.user}
          <select
            value={draft.userId}
            onChange={(e) => setDraft({ ...draft, userId: e.target.value })}
            className={inputClass}
          >
            <option value="">{vi.common.all}</option>
            {(usersQuery.data ?? []).map((u) => (
              <option key={u.id} value={u.id}>
                {u.fullName}
              </option>
            ))}
          </select>
        </label>
        <label className="flex flex-col text-sm text-slate-700">
          {vi.aiUsage.from}
          <input type="date" value={draft.from} onChange={(e) => setDraft({ ...draft, from: e.target.value })} className={inputClass} />
        </label>
        <label className="flex flex-col text-sm text-slate-700">
          {vi.aiUsage.to}
          <input type="date" value={draft.to} onChange={(e) => setDraft({ ...draft, to: e.target.value })} className={inputClass} />
        </label>
        <button type="submit" className="rounded bg-blue-600 px-4 py-2 text-sm font-medium text-white hover:bg-blue-700">
          {vi.aiUsage.filter}
        </button>
      </form>

      {usageQuery.isLoading && <p className="text-sm text-slate-500">{vi.common.loading}</p>}
      {usageQuery.isError && <p className="text-sm text-red-700">{getUiErrorMessage(usageQuery.error)}</p>}

      {data && (
        <>
          {data.items.length === 0 ? (
            <p className="text-sm text-slate-500">{vi.common.empty}</p>
          ) : (
            <div className="overflow-x-auto">
              <table className="min-w-full text-left text-sm">
                <thead className="border-b border-slate-200 text-slate-500">
                  <tr>
                    <th className="px-2 py-2">{vi.aiUsage.date}</th>
                    <th className="px-2 py-2">{vi.aiUsage.user}</th>
                    <th className="px-2 py-2 text-right">{vi.aiUsage.jobs}</th>
                    <th className="px-2 py-2 text-right">{vi.aiUsage.tokens}</th>
                    <th className="px-2 py-2 text-right">{vi.aiUsage.cost}</th>
                  </tr>
                </thead>
                <tbody>
                  {data.items.map((row) => (
                    <tr key={`${row.user.id}-${row.date}`} className="border-b border-slate-100">
                      <td className="px-2 py-2 whitespace-nowrap">{row.date}</td>
                      <td className="px-2 py-2">{row.user.fullName}</td>
                      <td className="px-2 py-2 text-right">{numberFormat.format(row.jobs)}</td>
                      <td className="px-2 py-2 text-right">{numberFormat.format(row.tokens)}</td>
                      <td className="px-2 py-2 text-right">{formatCost(row.estimatedCostUsd)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
          <p className="text-sm font-medium text-slate-700">
            {vi.aiUsage.totals(
              numberFormat.format(data.totals.jobs),
              numberFormat.format(data.totals.tokens),
              formatCost(data.totals.estimatedCostUsd),
            )}
          </p>
          {data.totalPages > 1 && (
            <div className="flex items-center gap-3 text-sm">
              <button type="button" disabled={page <= 1} onClick={() => setPage((p) => p - 1)} className="disabled:opacity-50">
                {vi.common.previousPage}
              </button>
              <span>
                {vi.common.pageLabel} {page}/{data.totalPages}
              </span>
              <button
                type="button"
                disabled={page >= data.totalPages}
                onClick={() => setPage((p) => p + 1)}
                className="disabled:opacity-50"
              >
                {vi.common.nextPage}
              </button>
            </div>
          )}
        </>
      )}
    </section>
  );
}
