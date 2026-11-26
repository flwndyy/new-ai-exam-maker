"use client";

import { keepPreviousData, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import Link from "next/link";
import { useState } from "react";

import { PageHeader } from "@/components/shell/page-header";
import { BoardTabs, boardTabPanelProps } from "@/components/ui/board-tabs";
import { Button } from "@/components/ui/button";
import { DataTable } from "@/components/ui/data-table";
import { EmptyState } from "@/components/ui/empty-state";
import { Field, Input, Select } from "@/components/ui/field";
import { FilterBar, FilterSelect } from "@/components/ui/filter-bar";
import { Notice } from "@/components/ui/notice";
import { Pager } from "@/components/ui/pager";
import { Panel } from "@/components/ui/panel";
import { StatusText } from "@/components/ui/status-text";
import { fetchAllDepartments, fetchAllSubjects } from "@/lib/api/catalog";
import { api } from "@/lib/api/client";
import { getApiErrorMessage, getUiErrorMessage } from "@/lib/api/error";
import type { components } from "@/lib/api/schema";
import { useAuth } from "@/lib/auth/auth-context";
import { userTone } from "@/lib/ui/status";
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
  const [showUserForm, setShowUserForm] = useState(false);
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
    placeholderData: keepPreviousData,
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
    placeholderData: keepPreviousData,
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
    placeholderData: keepPreviousData,
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
    placeholderData: keepPreviousData,
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
      setShowUserForm(false);
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

  const BODY = "px-4 py-5 sm:px-7 sm:py-6";

  if (!isAdmin) {
    return (
      <>
        <PageHeader title={vi.admin.title} />
        <div className={BODY}>
          <Notice tone="error">{vi.common.forbidden}</Notice>
        </div>
      </>
    );
  }

  const tabs = [
    { key: "users", label: vi.admin.usersTab },
    { key: "departments", label: vi.admin.departmentsTab },
    { key: "subjects", label: vi.admin.subjectsTab },
    { key: "topics", label: vi.admin.topicsTab },
  ] as const;

  // Each tab counts its own list; no extra request is made just for the board description.
  const activeTotalItems = {
    users: usersQuery.data?.totalItems,
    departments: departmentsListQuery.data?.totalItems,
    subjects: subjectsListQuery.data?.totalItems,
    topics: topicsQuery.data?.totalItems,
  }[tab];

  const departmentLabel = (id?: string | null) =>
    (id && departmentsQuery.data?.find((d) => d.id === id)?.name) || "—";
  const subjectLabel = (id: string) => subjectsQuery.data?.find((s) => s.id === id)?.name ?? "—";

  const rowActions = (onEdit: () => void, onDelete: () => void, deleting: boolean) => (
    <div className="flex justify-end gap-2">
      <Button variant="secondary" size="sm" onClick={onEdit}>
        {vi.common.edit}
      </Button>
      <Button variant="danger" size="sm" disabled={deleting} onClick={onDelete}>
        {vi.common.delete}
      </Button>
    </div>
  );

  return (
    <>
      <PageHeader
        title={vi.admin.title}
        description={activeTotalItems === undefined ? undefined : vi.admin.totalItems(activeTotalItems)}
        actions={
          tab === "users" ? (
            <>
              <Button
                variant="boardSecondary"
                disabled={reconcileMutation.isPending}
                onClick={() => reconcileMutation.mutate()}
              >
                {vi.admin.reconcile}
              </Button>
              <Button variant="boardPrimary" aria-expanded={showUserForm} onClick={() => setShowUserForm((v) => !v)}>
                {vi.admin.createUser}
              </Button>
            </>
          ) : undefined
        }
        tabs={
          <BoardTabs
            id="admin-tabs"
            className="px-0! sm:px-0!"
            active={tab}
            items={tabs.map((item) => ({ key: item.key, label: item.label }))}
            onSelect={(key) => {
              setTab(key as typeof tab);
              setFeedback(null);
              setErrorMsg(null);
            }}
          />
        }
      />
      <div {...boardTabPanelProps("admin-tabs", tab)} className={`${BODY} space-y-4`}>
        {feedback && <Notice tone="success">{feedback}</Notice>}
        {errorMsg && <Notice tone="error">{errorMsg}</Notice>}

        {tab === "users" && (
          <>
            {showUserForm && (
              <Panel>
                <form
                  onSubmit={(e) => {
                    e.preventDefault();
                    createUserMutation.mutate();
                  }}
                  className="grid grid-cols-1 items-end gap-x-3 sm:grid-cols-2 lg:grid-cols-5"
                >
                  <Field label={vi.admin.email} htmlFor="admin-user-email">
                    <Input
                      id="admin-user-email"
                      required
                      type="email"
                      placeholder={vi.admin.email}
                      value={userEmail}
                      onChange={(e) => setUserEmail(e.target.value)}
                    />
                  </Field>
                  <Field label={vi.admin.fullName} htmlFor="admin-user-name">
                    <Input
                      id="admin-user-name"
                      required
                      placeholder={vi.admin.fullName}
                      value={userFullName}
                      onChange={(e) => setUserFullName(e.target.value)}
                    />
                  </Field>
                  <Field label={vi.admin.role} htmlFor="admin-user-role">
                    <Select id="admin-user-role" value={userRole} onChange={(e) => setUserRole(e.target.value as Role)}>
                      <option value="teacher">{vi.auth.roles.teacher}</option>
                      <option value="head">{vi.auth.roles.head}</option>
                      <option value="admin">{vi.auth.roles.admin}</option>
                    </Select>
                  </Field>
                  <Field label={vi.admin.department} htmlFor="admin-user-dept">
                    <Select
                      id="admin-user-dept"
                      required={userRole !== "admin"}
                      disabled={userRole === "admin"}
                      value={userRole === "admin" ? "" : userDeptId}
                      onChange={(e) => setUserDeptId(e.target.value)}
                    >
                      <option value="">{userRole === "admin" ? vi.admin.noDepartment : `-- ${vi.admin.department} --`}</option>
                      {departmentsQuery.data?.map((d) => (
                        <option key={d.id} value={d.id}>{d.name}</option>
                      ))}
                    </Select>
                  </Field>
                  <div className="mb-3.5">
                    <Button type="submit" disabled={createUserMutation.isPending}>
                      {vi.admin.createUser}
                    </Button>
                  </div>
                </form>
              </Panel>
            )}

            {reconcileReport && (
              <Notice tone={reconcileReport.mismatches.length > 0 ? "warn" : "success"}>
                {vi.admin.reconcileSummary}: {reconcileReport.totalUsers} {vi.admin.reconcileUsersSuffix},{" "}
                {reconcileReport.mismatches.length} {vi.admin.reconcileMismatchesSuffix}
              </Notice>
            )}

            {reconcileReport && reconcileReport.mismatches.length > 0 && (
              <DataTable>
                <table>
                  <thead>
                    <tr>
                      <th>{vi.admin.email}</th>
                      <th>{vi.admin.reconcileIssueLabel}</th>
                      <th>{vi.admin.reconcileDbRoleLabel}</th>
                      <th>{vi.admin.reconcileCognitoGroupsLabel}</th>
                    </tr>
                  </thead>
                  <tbody>
                    {reconcileReport.mismatches.map((m, idx) => (
                      <tr key={`${m.userId ?? m.email}-${m.issue}-${idx}`}>
                        <td className="font-medium">{m.email}</td>
                        <td>{vi.admin.reconcileIssues[m.issue]}</td>
                        <td>{m.dbRole ? vi.auth.roles[m.dbRole] : "—"}</td>
                        <td>{(m.cognitoGroups ?? []).join(", ") || "—"}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </DataTable>
            )}

            {usersQuery.isLoading && <EmptyState busy title={vi.common.loading} />}
            {usersQuery.data && usersQuery.data.items.length === 0 && <EmptyState title={vi.common.empty} />}
            {usersQuery.data && usersQuery.data.items.length > 0 && (
              <DataTable>
                <table>
                  <thead>
                    <tr>
                      <th>{vi.admin.fullName}</th>
                      <th>{vi.admin.email}</th>
                      <th>{vi.admin.role}</th>
                      <th>{vi.admin.department}</th>
                      <th>{vi.common.status}</th>
                      <th>
                        <span className="sr-only">{vi.common.actions}</span>
                      </th>
                    </tr>
                  </thead>
                  <tbody>
                    {usersQuery.data.items.map((item) => (
                      <tr key={item.id} className={item.isActive ? undefined : "text-muted"}>
                        <td className="font-medium">{item.fullName}</td>
                        <td className="break-all">{item.email}</td>
                        <td>{vi.auth.roles[item.role]}</td>
                        <td>{item.role === "admin" ? vi.admin.noDepartment : departmentLabel(item.departmentId)}</td>
                        <td>
                          <StatusText tone={userTone(item.isActive)}>
                            {item.isActive ? vi.admin.active : vi.admin.inactive}
                          </StatusText>
                        </td>
                        <td className="text-right">
                          {item.id !== me?.id && (
                            <Button
                              variant={item.isActive ? "danger" : "secondary"}
                              size="sm"
                              disabled={toggleUserActiveMutation.isPending}
                              onClick={() => {
                                if (item.isActive && !window.confirm(vi.admin.confirmDisableUser)) return;
                                toggleUserActiveMutation.mutate(item);
                              }}
                            >
                              {item.isActive ? vi.admin.disableUser : vi.admin.enableUser}
                            </Button>
                          )}
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </DataTable>
            )}

            {usersQuery.data && ((usersQuery.data.totalPages ?? 1) > 1 || userPage > 1) && (
              <Pager
                page={usersQuery.data.page ?? userPage}
                totalPages={usersQuery.data.totalPages ?? 1}
                onPage={setUserPage}
              />
            )}
          </>
        )}

        {tab === "departments" && (
          <>
            <Panel>
              <form
                onSubmit={(e) => {
                  e.preventDefault();
                  saveDeptMutation.mutate();
                }}
                className="flex flex-wrap items-end gap-x-3"
              >
                <Field label={vi.admin.name} htmlFor="admin-dept-name" className="min-w-[200px] flex-1">
                  <Input
                    id="admin-dept-name"
                    required
                    placeholder={vi.admin.name}
                    value={deptName}
                    onChange={(e) => setDeptName(e.target.value)}
                  />
                </Field>
                <div className="mb-3.5 flex gap-2">
                  <Button type="submit" disabled={saveDeptMutation.isPending}>
                    {editingDeptId ? vi.common.save : vi.common.create}
                  </Button>
                  {editingDeptId && (
                    <Button
                      variant="secondary"
                      onClick={() => {
                        setEditingDeptId(null);
                        setDeptName("");
                      }}
                    >
                      {vi.common.cancel}
                    </Button>
                  )}
                </div>
              </form>
            </Panel>

            {departmentsListQuery.isLoading && <EmptyState busy title={vi.common.loading} />}
            {departmentsListQuery.data && departmentsListQuery.data.items.length === 0 && (
              <EmptyState title={vi.common.empty} />
            )}
            {departmentsListQuery.data && departmentsListQuery.data.items.length > 0 && (
              <DataTable>
                <table>
                  <thead>
                    <tr>
                      <th>{vi.admin.name}</th>
                      <th>
                        <span className="sr-only">{vi.common.actions}</span>
                      </th>
                    </tr>
                  </thead>
                  <tbody>
                    {departmentsListQuery.data.items.map((d) => (
                      <tr key={d.id}>
                        <td className="font-medium">{d.name}</td>
                        <td>
                          {rowActions(
                            () => {
                              setEditingDeptId(d.id);
                              setDeptName(d.name);
                            },
                            () => {
                              if (window.confirm(vi.common.confirmDelete)) deleteDeptMutation.mutate(d.id);
                            },
                            deleteDeptMutation.isPending,
                          )}
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </DataTable>
            )}

            {departmentsListQuery.data && ((departmentsListQuery.data.totalPages ?? 1) > 1 || deptPage > 1) && (
              <Pager
                page={departmentsListQuery.data.page ?? deptPage}
                totalPages={departmentsListQuery.data.totalPages ?? 1}
                onPage={setDeptPage}
              />
            )}
          </>
        )}

        {tab === "subjects" && (
          <>
            <Panel>
              <form
                onSubmit={(e) => {
                  e.preventDefault();
                  saveSubjectMutation.mutate();
                }}
                className="flex flex-wrap items-end gap-x-3"
              >
                <Field label={vi.admin.name} htmlFor="admin-subject-name" className="min-w-[200px] flex-1">
                  <Input
                    id="admin-subject-name"
                    required
                    placeholder={vi.admin.name}
                    value={subjectName}
                    onChange={(e) => setSubjectName(e.target.value)}
                  />
                </Field>
                <Field label={vi.admin.department} htmlFor="admin-subject-dept" className="min-w-[200px]">
                  <Select
                    id="admin-subject-dept"
                    required
                    value={subjectDeptId}
                    onChange={(e) => setSubjectDeptId(e.target.value)}
                  >
                    <option value="">-- {vi.admin.department} --</option>
                    {departmentsQuery.data?.map((d) => (
                      <option key={d.id} value={d.id}>{d.name}</option>
                    ))}
                  </Select>
                </Field>
                <div className="mb-3.5 flex gap-2">
                  <Button type="submit" disabled={saveSubjectMutation.isPending}>
                    {editingSubjectId ? vi.common.save : vi.common.create}
                  </Button>
                  {editingSubjectId && (
                    <Button
                      variant="secondary"
                      onClick={() => {
                        setEditingSubjectId(null);
                        setSubjectName("");
                        setSubjectDeptId("");
                      }}
                    >
                      {vi.common.cancel}
                    </Button>
                  )}
                </div>
              </form>
            </Panel>

            {subjectsListQuery.isLoading && <EmptyState busy title={vi.common.loading} />}
            {subjectsListQuery.data && subjectsListQuery.data.items.length === 0 && (
              <EmptyState title={vi.common.empty} />
            )}
            {subjectsListQuery.data && subjectsListQuery.data.items.length > 0 && (
              <DataTable>
                <table>
                  <thead>
                    <tr>
                      <th>{vi.admin.name}</th>
                      <th>{vi.admin.department}</th>
                      <th>
                        <span className="sr-only">{vi.common.actions}</span>
                      </th>
                    </tr>
                  </thead>
                  <tbody>
                    {subjectsListQuery.data.items.map((s) => (
                      <tr key={s.id}>
                        <td className="font-medium">{s.name}</td>
                        <td>{departmentLabel(s.departmentId)}</td>
                        <td>
                          {rowActions(
                            () => {
                              setEditingSubjectId(s.id);
                              setSubjectName(s.name);
                              setSubjectDeptId(s.departmentId);
                            },
                            () => {
                              if (window.confirm(vi.common.confirmDelete)) deleteSubjectMutation.mutate(s.id);
                            },
                            deleteSubjectMutation.isPending,
                          )}
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </DataTable>
            )}

            {subjectsListQuery.data && ((subjectsListQuery.data.totalPages ?? 1) > 1 || subjectPage > 1) && (
              <Pager
                page={subjectsListQuery.data.page ?? subjectPage}
                totalPages={subjectsListQuery.data.totalPages ?? 1}
                onPage={setSubjectPage}
              />
            )}
          </>
        )}

        {tab === "topics" && (
          <>
            <Panel>
              <form
                onSubmit={(e) => {
                  e.preventDefault();
                  saveTopicMutation.mutate();
                }}
                className="flex flex-wrap items-end gap-x-3"
              >
                <Field label={vi.admin.name} htmlFor="admin-topic-name" className="min-w-[200px] flex-[2]">
                  <Input
                    id="admin-topic-name"
                    required
                    placeholder={vi.admin.name}
                    value={topicName}
                    onChange={(e) => setTopicName(e.target.value)}
                  />
                </Field>
                <Field label={vi.questions.subject} htmlFor="admin-topic-subject" className="min-w-[160px] flex-1">
                  <Select
                    id="admin-topic-subject"
                    required
                    value={topicSubjectId}
                    onChange={(e) => setTopicSubjectId(e.target.value)}
                  >
                    <option value="">-- {vi.questions.subject} --</option>
                    {subjectsQuery.data?.map((s) => (
                      <option key={s.id} value={s.id}>{s.name}</option>
                    ))}
                  </Select>
                </Field>
                <Field label={vi.questions.grade} htmlFor="admin-topic-grade" className="min-w-[110px]">
                  <Select
                    id="admin-topic-grade"
                    value={topicGrade}
                    onChange={(e) => setTopicGrade(Number(e.target.value) as Grade)}
                  >
                    {[10, 11, 12].map((g) => (
                      <option key={g} value={g}>{vi.common.gradePrefix} {g}</option>
                    ))}
                  </Select>
                </Field>
                <Field label={vi.admin.sortOrder} htmlFor="admin-topic-order" className="w-24">
                  <Input
                    id="admin-topic-order"
                    required
                    type="number"
                    min="0"
                    step="1"
                    placeholder={vi.admin.sortOrder}
                    value={topicOrder}
                    onChange={(e) => setTopicOrder(e.target.value)}
                    className="tabular-nums"
                  />
                </Field>
                <div className="mb-3.5 flex gap-2">
                  <Button type="submit" disabled={saveTopicMutation.isPending}>
                    {editingTopicId ? vi.common.save : vi.common.create}
                  </Button>
                  {editingTopicId && (
                    <Button
                      variant="secondary"
                      onClick={() => {
                        setEditingTopicId(null);
                        setTopicName("");
                        setTopicSubjectId("");
                        setTopicGrade(10);
                        setTopicOrder("1");
                      }}
                    >
                      {vi.common.cancel}
                    </Button>
                  )}
                </div>
              </form>
            </Panel>

            <FilterBar className="mb-0">
              <FilterSelect
                label={`${vi.questions.subject}: ${vi.common.all}`}
                value={topicFilterSubjectId}
                onChange={(e) => {
                  setTopicFilterSubjectId(e.target.value);
                  setTopicPage(1);
                }}
              >
                <option value="">{vi.questions.subject}: {vi.common.all}</option>
                {subjectsQuery.data?.map((s) => (
                  <option key={s.id} value={s.id}>{s.name}</option>
                ))}
              </FilterSelect>
            </FilterBar>

            {topicsQuery.isLoading && <EmptyState busy title={vi.common.loading} />}
            {topicsQuery.data && topicsQuery.data.items.length === 0 && <EmptyState title={vi.common.empty} />}
            {topicsQuery.data && topicsQuery.data.items.length > 0 && (
              <DataTable>
                <table>
                  <thead>
                    <tr>
                      <th>{vi.admin.name}</th>
                      <th>{vi.questions.subject}</th>
                      <th>{vi.questions.grade}</th>
                      <th className="num">{vi.admin.sortOrder}</th>
                      <th>
                        <span className="sr-only">{vi.common.actions}</span>
                      </th>
                    </tr>
                  </thead>
                  <tbody>
                    {topicsQuery.data.items.map((t) => (
                      <tr key={t.id}>
                        <td className="font-medium">{t.name}</td>
                        <td>{subjectLabel(t.subjectId)}</td>
                        <td>
                          {vi.common.gradePrefix} {t.grade}
                        </td>
                        <td className="num">{t.sortOrder}</td>
                        <td>
                          {rowActions(
                            () => {
                              setEditingTopicId(t.id);
                              setTopicName(t.name);
                              setTopicSubjectId(t.subjectId);
                              setTopicGrade(t.grade);
                              setTopicOrder(String(t.sortOrder));
                            },
                            () => {
                              if (window.confirm(vi.common.confirmDelete)) deleteTopicMutation.mutate(t.id);
                            },
                            deleteTopicMutation.isPending,
                          )}
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </DataTable>
            )}

            {topicsQuery.data && ((topicsQuery.data.totalPages ?? 1) > 1 || topicPage > 1) && (
              <Pager
                page={topicsQuery.data.page ?? topicPage}
                totalPages={topicsQuery.data.totalPages ?? 1}
                onPage={setTopicPage}
              />
            )}
          </>
        )}

        <p className="text-[13px]">
          <Link href="/admin/ai-usage/" className="text-accent-strong underline underline-offset-2">
            {vi.admin.aiUsageLink}
          </Link>
        </p>
      </div>
    </>
  );
}
