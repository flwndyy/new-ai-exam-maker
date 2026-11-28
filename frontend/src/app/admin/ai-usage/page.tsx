"use client";

import { keepPreviousData, useQuery } from "@tanstack/react-query";
import { useState } from "react";

import { PageHeader } from "@/components/shell/page-header";
import { Button } from "@/components/ui/button";
import { DataTable } from "@/components/ui/data-table";
import { EmptyState } from "@/components/ui/empty-state";
import { Field, Input, Select } from "@/components/ui/field";
import { Notice } from "@/components/ui/notice";
import { Pager } from "@/components/ui/pager";
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

  const BODY = "px-4 py-5 sm:px-7 sm:py-6";

  if (!me) return <PageHeader title={vi.aiUsage.title} />;
  if (!isAdmin) {
    return (
      <>
        <PageHeader title={vi.aiUsage.title} />
        <div className={BODY}>
          <Notice tone="error">{vi.common.forbidden}</Notice>
        </div>
      </>
    );
  }

  const data = usageQuery.data;

  return (
    <>
      <PageHeader title={vi.aiUsage.title} description={vi.aiUsage.description} />
      <div className={`${BODY} space-y-4`}>
        <form
          className="flex flex-wrap items-end gap-x-3"
          onSubmit={(e) => {
            e.preventDefault();
            setApplied(draft);
            setPage(1);
          }}
        >
          <Field label={vi.aiUsage.user} htmlFor="ai-usage-user" className="min-w-[200px]">
            <Select
              id="ai-usage-user"
              value={draft.userId}
              onChange={(e) => setDraft({ ...draft, userId: e.target.value })}
            >
              <option value="">{vi.common.all}</option>
              {(usersQuery.data ?? []).map((u) => (
                <option key={u.id} value={u.id}>
                  {u.fullName}
                </option>
              ))}
            </Select>
          </Field>
          <Field label={vi.aiUsage.from} htmlFor="ai-usage-from">
            <Input
              id="ai-usage-from"
              type="date"
              value={draft.from}
              onChange={(e) => setDraft({ ...draft, from: e.target.value })}
            />
          </Field>
          <Field label={vi.aiUsage.to} htmlFor="ai-usage-to">
            <Input
              id="ai-usage-to"
              type="date"
              value={draft.to}
              onChange={(e) => setDraft({ ...draft, to: e.target.value })}
            />
          </Field>
          <div className="mb-3.5">
            <Button type="submit">{vi.aiUsage.filter}</Button>
          </div>
        </form>

        {usageQuery.isLoading && <EmptyState busy title={vi.common.loading} />}
        {usageQuery.isError && <Notice tone="error">{getUiErrorMessage(usageQuery.error)}</Notice>}

        {data && (
          <>
            <div
              role="group"
              aria-label={vi.aiUsage.totals(
                numberFormat.format(data.totals.jobs),
                numberFormat.format(data.totals.tokens),
                formatCost(data.totals.estimatedCostUsd),
              )}
              className="grid grid-cols-2 gap-3 lg:grid-cols-4"
            >
              {[
                { label: vi.aiUsage.jobs, value: numberFormat.format(data.totals.jobs) },
                { label: vi.aiUsage.tokens, value: numberFormat.format(data.totals.tokens) },
                { label: vi.aiUsage.cost, value: formatCost(data.totals.estimatedCostUsd) },
                { label: vi.aiUsage.rows, value: numberFormat.format(data.totalItems) },
              ].map((stat) => (
                <div key={stat.label} className="rounded-lg border border-line bg-surface px-4 py-3">
                  <div className="text-[12.5px] text-muted">{stat.label}</div>
                  <div className="mt-0.5 text-[22px] leading-tight font-semibold tabular-nums">{stat.value}</div>
                </div>
              ))}
            </div>
            <Notice tone="info">{vi.aiUsage.estimateNotice}</Notice>

            {data.items.length === 0 ? (
              <EmptyState title={vi.common.empty} />
            ) : (
              <DataTable>
                <table>
                  <thead>
                    <tr>
                      <th>{vi.aiUsage.date}</th>
                      <th>{vi.aiUsage.user}</th>
                      <th className="num">{vi.aiUsage.jobs}</th>
                      <th className="num">{vi.aiUsage.tokens}</th>
                      <th className="num">{vi.aiUsage.cost}</th>
                    </tr>
                  </thead>
                  <tbody>
                    {data.items.map((row) => (
                      <tr key={`${row.user.id}-${row.date}`}>
                        <td className="whitespace-nowrap">{row.date}</td>
                        <td>{row.user.fullName}</td>
                        <td className="num">{numberFormat.format(row.jobs)}</td>
                        <td className="num">{numberFormat.format(row.tokens)}</td>
                        <td className="num">{formatCost(row.estimatedCostUsd)}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </DataTable>
            )}
            {data.totalPages > 1 && <Pager page={page} totalPages={data.totalPages} onPage={setPage} />}
          </>
        )}
      </div>
    </>
  );
}
