"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";

import { fetchAllDepartments, fetchAllSubjects } from "@/lib/api/catalog";
import { api } from "@/lib/api/client";
import { getApiErrorMessage, getUiErrorMessage } from "@/lib/api/error";
import type { components } from "@/lib/api/schema";
import { useAuth } from "@/lib/auth/auth-context";
import { vi } from "@/messages/vi";

type Role = components["schemas"]["Role"];
type Grade = components["schemas"]["Grade"];
type AdminUser = components["schemas"]["AdminUser"];
type ReconcileReport = components["schemas"]["ReconcileReport"];

export default function AdminPage() {
  const { me } = useAuth();
  const queryClient = useQueryClient();
  const [tab, setTab] = useState<"users" | "departments" | "subjects" | "topics">("users");
  const [feedback, setFeedback] = useState<string | null>(null);
  const [errorMsg, setErrorMsg] = useState<string | null>(null);

  // User form & pagination state
  const [userEmail, setUserEmail] = useState("");
  const [userFullName, setUserFullName] = useState("");
  const [userRole, setUserRole] = useState<Role>("teacher");
  const [userDeptId, setUserDeptId] = useState("");
  const [userPage, setUserPage] = useState(1);
  const [reconcileReport, setReconcileReport] = useState<ReconcileReport | null>(null);

  // Department form & pagination state
  const [deptName, setDeptName] = useState("");
  const [editingDeptId, setEditingDeptId] = useState<string | null>(null);
  const [deptPage, setDeptPage] = useState(1);

  // Subject form & pagination state
  const [subjectName, setSubjectName] = useState("");
  const [subjectDeptId, setSubjectDeptId] = useState("");
  const [editingSubjectId, setEditingSubjectId] = useState<string | null>(null);
  const [subjectPage, setSubjectPage] = useState(1);

  // Topic form & pagination state
  const [topicName, setTopicName] = useState("");
  const [topicSubjectId, setTopicSubjectId] = useState("");
  const [topicGrade, setTopicGrade] = useState<Grade>(10);
  const [topicOrder, setTopicOrder] = useState("1");
  const [editingTopicId, setEditingTopicId] = useState<string | null>(null);
  const [topicFilterSubjectId, setTopicFilterSubjectId] = useState("");
  const [topicPage, setTopicPage] = useState(1);

  const isAdmin = me?.role === "admin";

  const departmentsQuery = useQuery({
    queryKey: ["catalog", "departments"],
    enabled: isAdmin,
    queryFn: () => fetchAllDepartments(),
  });

  const departmentsListQuery = useQuery({
    queryKey: ["catalog", "departments", "admin", deptPage],
    enabled: isAdmin && tab === "departments",
    queryFn: async () => {
      const { data, error } = await api.GET("/departments", {
        params: { query: { page: deptPage, pageSize: 20 } },
      });
      if (error) throw new Error(getApiErrorMessage(error));
      if (deptPage > 1 && data && data.items.length === 0) {
        setDeptPage(Math.max(1, data.totalPages || deptPage - 1));
      }
      return data;
    },
  });

  const subjectsQuery = useQuery({
    queryKey: ["catalog", "subjects"],
    enabled: isAdmin,
    queryFn: () => fetchAllSubjects(),
  });

  const subjectsListQuery = useQuery({
    queryKey: ["catalog", "subjects", "admin", subjectPage],
    enabled: isAdmin && tab === "subjects",
    queryFn: async () => {
      const { data, error } = await api.GET("/subjects", {
        params: { query: { page: subjectPage, pageSize: 20 } },
      });
      if (error) throw new Error(getApiErrorMessage(error));
      if (subjectPage > 1 && data && data.items.length === 0) {
        setSubjectPage(Math.max(1, data.totalPages || subjectPage - 1));
      }
      return data;
    },
  });

  const topicsQuery = useQuery({
    queryKey: ["catalog", "topics", "admin", topicPage, topicFilterSubjectId],
    enabled: isAdmin && tab === "topics",
    queryFn: async () => {
      const { data, error } = await api.GET("/topics", {
        params: {
          query: {
            page: topicPage,
            pageSize: 20,
            subjectId: topicFilterSubjectId || undefined,
          },
        },
      });
      if (error) throw new Error(getApiErrorMessage(error));
      if (topicPage > 1 && data && data.items.length === 0) {
        setTopicPage(Math.max(1, data.totalPages || topicPage - 1));
      }
      return data;
    },
  });

  const usersQuery = useQuery({
    queryKey: ["admin", "users", userPage],
    enabled: isAdmin && tab === "users",
    queryFn: async () => {
      const { data, error } = await api.GET("/admin/users", {
        params: { query: { page: userPage, pageSize: 20 } },
      });
      if (error) throw new Error(getApiErrorMessage(error));
      if (userPage > 1 && data && data.items.length === 0) {
        setUserPage(Math.max(1, data.totalPages || userPage - 1));
      }
      return data;
    },
  });

  const notifySuccess = (message: string, key: readonly string[]) => {
    setErrorMsg(null);
    setFeedback(message);
    void queryClient.invalidateQueries({ queryKey: [...key] });
  };

  const notifyError = (err: unknown) => {
    setFeedback(null);
    setErrorMsg(getUiErrorMessage(err));
  };

  const createUserMutation = useMutation({
    mutationFn: async () => {
      if (userRole !== "admin" && !userDeptId) {
        throw new Error(vi.admin.departmentRequired);
      }
      const { error } = await api.POST("/admin/users", {
        body: {
          email: userEmail.trim(),
          fullName: userFullName.trim(),
          role: userRole,
          departmentId: userRole === "admin" ? null : userDeptId,
        },
      });
      if (error) throw new Error(getApiErrorMessage(error));
    },
    onSuccess: () => {
      setUserEmail("");
      setUserFullName("");
      notifySuccess(vi.admin.saved, ["admin", "users"]);
    },
    onError: notifyError,
  });

  const toggleUserActiveMutation = useMutation({
    mutationFn: async (item: AdminUser) => {
      const targetActive = !item.isActive;
      // If deactivating, use the dedicated soft-delete endpoint so concurrent role/department/email updates are never overwritten.
      if (!targetActive) {
        const { error } = await api.DELETE("/admin/users/{id}", {
          params: { path: { id: item.id } },
        });
        if (error) throw new Error(getApiErrorMessage(error));
        return;
      }
      // If reactivating, fetch the latest profile right before PUT so stale list rows never roll back role/department/email.
      const { data: fresh, error: getError } = await api.GET("/admin/users/{id}", {
        params: { path: { id: item.id } },
      });
      if (getError) throw new Error(getApiErrorMessage(getError));
      const { error } = await api.PUT("/admin/users/{id}", {
        params: { path: { id: item.id } },
        body: {
          email: fresh.email,
          fullName: fresh.fullName,
          role: fresh.role,
          departmentId: fresh.departmentId ?? null,
          isActive: true,
        },
      });
      if (error) throw new Error(getApiErrorMessage(error));
    },
    onSuccess: () => notifySuccess(vi.admin.saved, ["admin", "users"]),
    onError: notifyError,
  });

  const reconcileMutation = useMutation({
    mutationFn: async () => {
      const { data, error } = await api.POST("/admin/users/reconcile");
      if (error) throw new Error(getApiErrorMessage(error));
      return data;
    },
    onSuccess: (report) => {
      setErrorMsg(null);
      setReconcileReport(report);
    },
    onError: notifyError,
  });

  const saveDeptMutation = useMutation({
    mutationFn: async () => {
      const body = { name: deptName.trim() };
      if (editingDeptId) {
        const { error } = await api.PUT("/admin/departments/{id}", { params: { path: { id: editingDeptId } }, body });
        if (error) throw new Error(getApiErrorMessage(error));
        return;
      }
      const { error } = await api.POST("/admin/departments", { body });
      if (error) throw new Error(getApiErrorMessage(error));
    },
    onSuccess: () => {
      setDeptName("");
      setEditingDeptId(null);
      notifySuccess(vi.admin.saved, ["catalog", "departments"]);
    },
    onError: notifyError,
  });

  const deleteDeptMutation = useMutation({
    mutationFn: async (id: string) => {
      const { error } = await api.DELETE("/admin/departments/{id}", { params: { path: { id } } });
      if (error) throw new Error(getApiErrorMessage(error));
      return id;
    },
    onSuccess: (deletedId) => {
      if (editingDeptId === deletedId) {
        setEditingDeptId(null);
        setDeptName("");
      }
      notifySuccess(vi.admin.deleted, ["catalog", "departments"]);
    },
    onError: notifyError,
  });

  const saveSubjectMutation = useMutation({
    mutationFn: async () => {
      const body = { name: subjectName.trim(), departmentId: subjectDeptId };
      if (editingSubjectId) {
        const { error } = await api.PUT("/admin/subjects/{id}", { params: { path: { id: editingSubjectId } }, body });
        if (error) throw new Error(getApiErrorMessage(error));
        return;
      }
      const { error } = await api.POST("/admin/subjects", { body });
      if (error) throw new Error(getApiErrorMessage(error));
    },
    onSuccess: () => {
      setSubjectName("");
      setSubjectDeptId("");
      setEditingSubjectId(null);
      notifySuccess(vi.admin.saved, ["catalog", "subjects"]);
    },
    onError: notifyError,
  });

  const deleteSubjectMutation = useMutation({
    mutationFn: async (id: string) => {
      const { error } = await api.DELETE("/admin/subjects/{id}", { params: { path: { id } } });
      if (error) throw new Error(getApiErrorMessage(error));
      return id;
    },
    onSuccess: (deletedId) => {
      if (editingSubjectId === deletedId) {
        setEditingSubjectId(null);
        setSubjectName("");
        setSubjectDeptId("");
      }
      notifySuccess(vi.admin.deleted, ["catalog", "subjects"]);
    },
    onError: notifyError,
  });

  const saveTopicMutation = useMutation({
    mutationFn: async () => {
      const orderTrimmed = topicOrder.trim();
      const parsedOrder = Number(orderTrimmed);
      if (orderTrimmed === "" || !Number.isInteger(parsedOrder) || parsedOrder < 0) {
        throw new Error(vi.admin.invalidSortOrder);
      }
      const body = {
        name: topicName.trim(),
        subjectId: topicSubjectId,
        grade: topicGrade,
        sortOrder: parsedOrder,
      };
      if (editingTopicId) {
        const { error } = await api.PUT("/admin/topics/{id}", { params: { path: { id: editingTopicId } }, body });
        if (error) throw new Error(getApiErrorMessage(error));
        return;
      }
      const { error } = await api.POST("/admin/topics", { body });
      if (error) throw new Error(getApiErrorMessage(error));
    },
    onSuccess: () => {
      setTopicName("");
      setEditingTopicId(null);
      notifySuccess(vi.admin.saved, ["catalog", "topics"]);
    },
    onError: notifyError,
  });

  const deleteTopicMutation = useMutation({
    mutationFn: async (id: string) => {
      const { error } = await api.DELETE("/admin/topics/{id}", { params: { path: { id } } });
      if (error) throw new Error(getApiErrorMessage(error));
      return id;
    },
    onSuccess: (deletedId) => {
      if (editingTopicId === deletedId) {
        setEditingTopicId(null);
        setTopicName("");
        setTopicSubjectId("");
        setTopicGrade(10);
        setTopicOrder("1");
      }
      notifySuccess(vi.admin.deleted, ["catalog", "topics"]);
    },
    onError: notifyError,
  });

  if (!isAdmin) {
    return <p className="rounded border border-rose-200 bg-rose-50 p-4 text-sm text-rose-800">{vi.common.forbidden}</p>;
  }

  const tabs = [
    { key: "users", label: vi.admin.usersTab },
    { key: "departments", label: vi.admin.departmentsTab },
    { key: "subjects", label: vi.admin.subjectsTab },
    { key: "topics", label: vi.admin.topicsTab },
  ] as const;

  return (
    <section className="space-y-6">
      <h1 className="text-2xl font-bold text-slate-900">{vi.admin.title}</h1>

      <div className="flex flex-wrap gap-2 border-b border-slate-200 pb-2">
        {tabs.map((item) => (
          <button
            key={item.key}
            type="button"
            onClick={() => {
              setTab(item.key);
              setFeedback(null);
              setErrorMsg(null);
            }}
            className={`rounded px-3 py-1.5 text-sm font-medium ${
              tab === item.key ? "bg-slate-900 text-white" : "text-slate-600 hover:bg-slate-100"
            }`}
          >
            {item.label}
          </button>
        ))}
      </div>

      {feedback && <p className="rounded border border-emerald-300 bg-emerald-50 p-3 text-sm text-emerald-800">{feedback}</p>}
      {errorMsg && <p className="rounded border border-rose-300 bg-rose-50 p-3 text-sm text-rose-800">{errorMsg}</p>}

      {tab === "users" && (
        <div className="space-y-5">
          <form
            onSubmit={(e) => {
              e.preventDefault();
              createUserMutation.mutate();
            }}
            className="grid grid-cols-1 gap-3 rounded border border-slate-200 bg-white p-4 sm:grid-cols-5"
          >
            <input
              required
              type="email"
              placeholder={vi.admin.email}
              value={userEmail}
              onChange={(e) => setUserEmail(e.target.value)}
              className="rounded border border-slate-300 px-2.5 py-1.5 text-sm"
            />
            <input
              required
              placeholder={vi.admin.fullName}
              value={userFullName}
              onChange={(e) => setUserFullName(e.target.value)}
              className="rounded border border-slate-300 px-2.5 py-1.5 text-sm"
            />
            <select
              aria-label={vi.admin.role}
              value={userRole}
              onChange={(e) => setUserRole(e.target.value as Role)}
              className="rounded border border-slate-300 px-2.5 py-1.5 text-sm"
            >
              <option value="teacher">{vi.auth.roles.teacher}</option>
              <option value="head">{vi.auth.roles.head}</option>
              <option value="admin">{vi.auth.roles.admin}</option>
            </select>
            <select
              aria-label={vi.admin.department}
              required={userRole !== "admin"}
              disabled={userRole === "admin"}
              value={userRole === "admin" ? "" : userDeptId}
              onChange={(e) => setUserDeptId(e.target.value)}
              className="rounded border border-slate-300 px-2.5 py-1.5 text-sm disabled:bg-slate-100"
            >
              <option value="">{userRole === "admin" ? vi.admin.noDepartment : `-- ${vi.admin.department} --`}</option>
              {departmentsQuery.data?.map((d) => (
                <option key={d.id} value={d.id}>{d.name}</option>
              ))}
            </select>
            <button
              type="submit"
              disabled={createUserMutation.isPending}
              className="rounded bg-slate-900 px-3 py-1.5 text-sm font-medium text-white hover:bg-slate-800"
            >
              {vi.admin.createUser}
            </button>
          </form>

          <div className="space-y-3">
            <div className="flex items-center justify-between">
              <button
                type="button"
                disabled={reconcileMutation.isPending}
                onClick={() => reconcileMutation.mutate()}
                className="rounded border border-slate-300 px-3 py-1.5 text-sm hover:bg-slate-50 disabled:opacity-50"
              >
                {vi.admin.reconcile}
              </button>
              {reconcileReport && (
                <span className="text-xs text-slate-600">
                  {vi.admin.reconcileSummary}: {reconcileReport.totalUsers} {vi.admin.reconcileUsersSuffix}, {reconcileReport.mismatches.length} {vi.admin.reconcileMismatchesSuffix}
                </span>
              )}
            </div>

            {reconcileReport && reconcileReport.mismatches.length > 0 && (
              <div className="overflow-x-auto rounded border border-amber-300 bg-amber-50">
                <table className="w-full text-left text-xs text-amber-950">
                  <thead className="border-b border-amber-200 bg-amber-100/60 uppercase">
                    <tr>
                      <th className="px-3 py-2">{vi.admin.email}</th>
                      <th className="px-3 py-2">{vi.admin.reconcileIssueLabel}</th>
                      <th className="px-3 py-2">{vi.admin.reconcileDbRoleLabel}</th>
                      <th className="px-3 py-2">{vi.admin.reconcileCognitoGroupsLabel}</th>
                    </tr>
                  </thead>
                  <tbody className="divide-y divide-amber-200/60">
                    {reconcileReport.mismatches.map((m, idx) => (
                      <tr key={`${m.userId ?? m.email}-${m.issue}-${idx}`}>
                        <td className="px-3 py-1.5 font-medium">{m.email}</td>
                        <td className="px-3 py-1.5">{vi.admin.reconcileIssues[m.issue]}</td>
                        <td className="px-3 py-1.5">{m.dbRole ? vi.auth.roles[m.dbRole] : "—"}</td>
                        <td className="px-3 py-1.5">{(m.cognitoGroups ?? []).join(", ") || "—"}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
          </div>

          <div className="overflow-x-auto rounded border border-slate-200 bg-white">
            <table className="w-full text-left text-sm">
              <thead className="border-b border-slate-200 bg-slate-50 text-xs uppercase text-slate-600">
                <tr>
                  <th className="px-3 py-2">{vi.admin.fullName}</th>
                  <th className="px-3 py-2">{vi.admin.email}</th>
                  <th className="px-3 py-2">{vi.admin.role}</th>
                  <th className="px-3 py-2">{vi.common.status}</th>
                  <th className="px-3 py-2">{vi.common.actions}</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-slate-100">
                {usersQuery.data?.items.map((item) => (
                  <tr key={item.id}>
                    <td className="px-3 py-2 font-medium">{item.fullName}</td>
                    <td className="px-3 py-2 text-slate-600">{item.email}</td>
                    <td className="px-3 py-2">{vi.auth.roles[item.role]}</td>
                    <td className="px-3 py-2">{item.isActive ? vi.admin.active : vi.admin.inactive}</td>
                    <td className="px-3 py-2">
                      {item.id !== me?.id && (
                        <button
                          type="button"
                          disabled={toggleUserActiveMutation.isPending}
                          onClick={() => {
                            if (item.isActive && !window.confirm(vi.admin.confirmDisableUser)) return;
                            toggleUserActiveMutation.mutate(item);
                          }}
                          className="text-xs font-medium text-slate-800 underline hover:text-slate-600 disabled:opacity-50"
                        >
                          {item.isActive ? vi.admin.disableUser : vi.admin.enableUser}
                        </button>
                      )}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>

          {usersQuery.data && ((usersQuery.data.totalPages ?? 1) > 1 || userPage > 1) && (
            <div className="flex items-center justify-between pt-2 text-sm">
              <button
                type="button"
                disabled={userPage <= 1}
                onClick={() => setUserPage((p) => Math.max(1, p - 1))}
                className="rounded border border-slate-300 px-3 py-1 disabled:opacity-40"
              >
                {vi.common.previousPage}
              </button>
              <span className="text-slate-600">
                {vi.common.pageLabel} {usersQuery.data.page ?? userPage} / {Math.max(1, usersQuery.data.totalPages ?? 1)}
              </span>
              <button
                type="button"
                disabled={userPage >= (usersQuery.data.totalPages ?? 1)}
                onClick={() => setUserPage((p) => p + 1)}
                className="rounded border border-slate-300 px-3 py-1 disabled:opacity-40"
              >
                {vi.common.nextPage}
              </button>
            </div>
          )}
        </div>
      )}

      {tab === "departments" && (
        <div className="space-y-4">
          <form
            onSubmit={(e) => {
              e.preventDefault();
              saveDeptMutation.mutate();
            }}
            className="flex flex-wrap gap-3 rounded border border-slate-200 bg-white p-4"
          >
            <input
              required
              placeholder={vi.admin.name}
              value={deptName}
              onChange={(e) => setDeptName(e.target.value)}
              className="flex-1 rounded border border-slate-300 px-2.5 py-1.5 text-sm"
            />
            <button
              type="submit"
              disabled={saveDeptMutation.isPending}
              className="rounded bg-slate-900 px-4 py-1.5 text-sm font-medium text-white disabled:opacity-50"
            >
              {editingDeptId ? vi.common.save : vi.common.create}
            </button>
            {editingDeptId && (
              <button
                type="button"
                onClick={() => {
                  setEditingDeptId(null);
                  setDeptName("");
                }}
                className="rounded border border-slate-300 px-3 py-1.5 text-sm"
              >
                {vi.common.cancel}
              </button>
            )}
          </form>

          <ul className="divide-y divide-slate-200 rounded border border-slate-200 bg-white">
            {(departmentsListQuery.data?.items ?? departmentsQuery.data)?.map((d) => (
              <li key={d.id} className="flex items-center justify-between px-4 py-2.5 text-sm">
                <span>{d.name}</span>
                <div className="flex gap-3 text-xs">
                  <button
                    type="button"
                    onClick={() => {
                      setEditingDeptId(d.id);
                      setDeptName(d.name);
                    }}
                    className="underline"
                  >
                    {vi.common.edit}
                  </button>
                  <button
                    type="button"
                    disabled={deleteDeptMutation.isPending}
                    onClick={() => {
                      if (window.confirm(vi.common.confirmDelete)) deleteDeptMutation.mutate(d.id);
                    }}
                    className="text-rose-700 underline disabled:opacity-50"
                  >
                    {vi.common.delete}
                  </button>
                </div>
              </li>
            ))}
          </ul>

          {departmentsListQuery.data && ((departmentsListQuery.data.totalPages ?? 1) > 1 || deptPage > 1) && (
            <div className="flex items-center justify-between pt-2 text-sm">
              <button
                type="button"
                disabled={deptPage <= 1}
                onClick={() => setDeptPage((p) => Math.max(1, p - 1))}
                className="rounded border border-slate-300 px-3 py-1 disabled:opacity-40"
              >
                {vi.common.previousPage}
              </button>
              <span className="text-slate-600">
                {vi.common.pageLabel} {departmentsListQuery.data.page ?? deptPage} / {Math.max(1, departmentsListQuery.data.totalPages ?? 1)}
              </span>
              <button
                type="button"
                disabled={deptPage >= (departmentsListQuery.data.totalPages ?? 1)}
                onClick={() => setDeptPage((p) => p + 1)}
                className="rounded border border-slate-300 px-3 py-1 disabled:opacity-40"
              >
                {vi.common.nextPage}
              </button>
            </div>
          )}
        </div>
      )}

      {tab === "subjects" && (
        <div className="space-y-4">
          <form
            onSubmit={(e) => {
              e.preventDefault();
              saveSubjectMutation.mutate();
            }}
            className="flex flex-wrap gap-3 rounded border border-slate-200 bg-white p-4"
          >
            <input
              required
              placeholder={vi.admin.name}
              value={subjectName}
              onChange={(e) => setSubjectName(e.target.value)}
              className="flex-1 rounded border border-slate-300 px-2.5 py-1.5 text-sm"
            />
            <select
              required
              aria-label={vi.admin.department}
              value={subjectDeptId}
              onChange={(e) => setSubjectDeptId(e.target.value)}
              className="rounded border border-slate-300 px-2.5 py-1.5 text-sm"
            >
              <option value="">-- {vi.admin.department} --</option>
              {departmentsQuery.data?.map((d) => (
                <option key={d.id} value={d.id}>{d.name}</option>
              ))}
            </select>
            <button
              type="submit"
              disabled={saveSubjectMutation.isPending}
              className="rounded bg-slate-900 px-4 py-1.5 text-sm font-medium text-white disabled:opacity-50"
            >
              {editingSubjectId ? vi.common.save : vi.common.create}
            </button>
            {editingSubjectId && (
              <button
                type="button"
                onClick={() => {
                  setEditingSubjectId(null);
                  setSubjectName("");
                  setSubjectDeptId("");
                }}
                className="rounded border border-slate-300 px-3 py-1.5 text-sm"
              >
                {vi.common.cancel}
              </button>
            )}
          </form>

          <ul className="divide-y divide-slate-200 rounded border border-slate-200 bg-white">
            {(subjectsListQuery.data?.items ?? subjectsQuery.data)?.map((s) => (
              <li key={s.id} className="flex items-center justify-between px-4 py-2.5 text-sm">
                <span>{s.name}</span>
                <div className="flex gap-3 text-xs">
                  <button
                    type="button"
                    onClick={() => {
                      setEditingSubjectId(s.id);
                      setSubjectName(s.name);
                      setSubjectDeptId(s.departmentId);
                    }}
                    className="underline"
                  >
                    {vi.common.edit}
                  </button>
                  <button
                    type="button"
                    disabled={deleteSubjectMutation.isPending}
                    onClick={() => {
                      if (window.confirm(vi.common.confirmDelete)) deleteSubjectMutation.mutate(s.id);
                    }}
                    className="text-rose-700 underline disabled:opacity-50"
                  >
                    {vi.common.delete}
                  </button>
                </div>
              </li>
            ))}
          </ul>

          {subjectsListQuery.data && ((subjectsListQuery.data.totalPages ?? 1) > 1 || subjectPage > 1) && (
            <div className="flex items-center justify-between pt-2 text-sm">
              <button
                type="button"
                disabled={subjectPage <= 1}
                onClick={() => setSubjectPage((p) => Math.max(1, p - 1))}
                className="rounded border border-slate-300 px-3 py-1 disabled:opacity-40"
              >
                {vi.common.previousPage}
              </button>
              <span className="text-slate-600">
                {vi.common.pageLabel} {subjectsListQuery.data.page ?? subjectPage} / {Math.max(1, subjectsListQuery.data.totalPages ?? 1)}
              </span>
              <button
                type="button"
                disabled={subjectPage >= (subjectsListQuery.data.totalPages ?? 1)}
                onClick={() => setSubjectPage((p) => p + 1)}
                className="rounded border border-slate-300 px-3 py-1 disabled:opacity-40"
              >
                {vi.common.nextPage}
              </button>
            </div>
          )}
        </div>
      )}

      {tab === "topics" && (
        <div className="space-y-4">
          <form
            onSubmit={(e) => {
              e.preventDefault();
              saveTopicMutation.mutate();
            }}
            className="grid grid-cols-1 gap-3 rounded border border-slate-200 bg-white p-4 sm:grid-cols-5"
          >
            <input
              required
              placeholder={vi.admin.name}
              value={topicName}
              onChange={(e) => setTopicName(e.target.value)}
              className="rounded border border-slate-300 px-2.5 py-1.5 text-sm sm:col-span-2"
            />
            <select
              required
              aria-label={vi.questions.subject}
              value={topicSubjectId}
              onChange={(e) => setTopicSubjectId(e.target.value)}
              className="rounded border border-slate-300 px-2.5 py-1.5 text-sm"
            >
              <option value="">-- {vi.questions.subject} --</option>
              {subjectsQuery.data?.map((s) => (
                <option key={s.id} value={s.id}>{s.name}</option>
              ))}
            </select>
            <select
              aria-label={vi.questions.grade}
              value={topicGrade}
              onChange={(e) => setTopicGrade(Number(e.target.value) as Grade)}
              className="rounded border border-slate-300 px-2.5 py-1.5 text-sm"
            >
              {[10, 11, 12].map((g) => (
                <option key={g} value={g}>{vi.common.gradePrefix} {g}</option>
              ))}
            </select>
            <div className="flex gap-2">
              <input
                required
                type="number"
                min="0"
                step="1"
                placeholder={vi.admin.sortOrder}
                value={topicOrder}
                onChange={(e) => setTopicOrder(e.target.value)}
                className="w-20 rounded border border-slate-300 px-2 py-1.5 text-sm"
              />
              <button
                type="submit"
                disabled={saveTopicMutation.isPending}
                className="flex-1 rounded bg-slate-900 px-3 py-1.5 text-sm font-medium text-white disabled:opacity-50"
              >
                {editingTopicId ? vi.common.save : vi.common.create}
              </button>
              {editingTopicId && (
                <button
                  type="button"
                  onClick={() => {
                    setEditingTopicId(null);
                    setTopicName("");
                    setTopicSubjectId("");
                    setTopicGrade(10);
                    setTopicOrder("1");
                  }}
                  className="rounded border border-slate-300 px-3 py-1.5 text-sm"
                >
                  {vi.common.cancel}
                </button>
              )}
            </div>
          </form>

          <div className="flex flex-wrap items-center gap-3">
            <select
              aria-label={`${vi.questions.subject}: ${vi.common.all}`}
              value={topicFilterSubjectId}
              onChange={(e) => {
                setTopicFilterSubjectId(e.target.value);
                setTopicPage(1);
              }}
              className="rounded border border-slate-300 bg-white px-2.5 py-1.5 text-sm"
            >
              <option value="">{vi.questions.subject}: {vi.common.all}</option>
              {subjectsQuery.data?.map((s) => (
                <option key={s.id} value={s.id}>{s.name}</option>
              ))}
            </select>
          </div>

          <ul className="divide-y divide-slate-200 rounded border border-slate-200 bg-white">
            {topicsQuery.data?.items.map((t) => (
              <li key={t.id} className="flex items-center justify-between px-4 py-2.5 text-sm">
                <span>
                  {t.name} ({vi.common.gradePrefix} {t.grade})
                </span>
                <div className="flex gap-3 text-xs">
                  <button
                    type="button"
                    onClick={() => {
                      setEditingTopicId(t.id);
                      setTopicName(t.name);
                      setTopicSubjectId(t.subjectId);
                      setTopicGrade(t.grade);
                      setTopicOrder(String(t.sortOrder));
                    }}
                    className="underline"
                  >
                    {vi.common.edit}
                  </button>
                  <button
                    type="button"
                    disabled={deleteTopicMutation.isPending}
                    onClick={() => {
                      if (window.confirm(vi.common.confirmDelete)) deleteTopicMutation.mutate(t.id);
                    }}
                    className="text-rose-700 underline disabled:opacity-50"
                  >
                    {vi.common.delete}
                  </button>
                </div>
              </li>
            ))}
          </ul>

          {topicsQuery.data && ((topicsQuery.data.totalPages ?? 1) > 1 || topicPage > 1) && (
            <div className="flex items-center justify-between pt-2 text-sm">
              <button
                type="button"
                disabled={topicPage <= 1}
                onClick={() => setTopicPage((p) => Math.max(1, p - 1))}
                className="rounded border border-slate-300 px-3 py-1 disabled:opacity-40"
              >
                {vi.common.previousPage}
              </button>
              <span className="text-slate-600">
                {vi.common.pageLabel} {topicsQuery.data.page ?? topicPage} / {Math.max(1, topicsQuery.data.totalPages ?? 1)}
              </span>
              <button
                type="button"
                disabled={topicPage >= (topicsQuery.data.totalPages ?? 1)}
                onClick={() => setTopicPage((p) => p + 1)}
                className="rounded border border-slate-300 px-3 py-1 disabled:opacity-40"
              >
                {vi.common.nextPage}
              </button>
            </div>
          )}
        </div>
      )}
    </section>
  );
}
