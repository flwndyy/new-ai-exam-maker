import { api } from "@/lib/api/client";
import { getApiErrorMessage } from "@/lib/api/error";
import type { components } from "@/lib/api/schema";

type Department = components["schemas"]["Department"];
type Subject = components["schemas"]["Subject"];
type Topic = components["schemas"]["Topic"];
type Grade = components["schemas"]["Grade"];

/** Fetch all pages of departments so dropdowns are never capped at the first 100 items. */
export async function fetchAllDepartments(): Promise<Department[]> {
  const items: Department[] = [];
  let page = 1;
  while (true) {
    const { data, error } = await api.GET("/departments", {
      params: { query: { page, pageSize: 100 } },
    });
    if (error) throw new Error(getApiErrorMessage(error));
    if (!data?.items?.length) break;
    items.push(...data.items);
    if (!data.totalPages || page >= data.totalPages) break;
    page++;
  }
  return items;
}

/** Fetch all pages of subjects so dropdowns are never capped at the first 100 items. */
export async function fetchAllSubjects(departmentId?: string): Promise<Subject[]> {
  const items: Subject[] = [];
  let page = 1;
  while (true) {
    const { data, error } = await api.GET("/subjects", {
      params: { query: { page, pageSize: 100, departmentId: departmentId || undefined } },
    });
    if (error) throw new Error(getApiErrorMessage(error));
    if (!data?.items?.length) break;
    items.push(...data.items);
    if (!data.totalPages || page >= data.totalPages) break;
    page++;
  }
  return items;
}

/** Fetch all pages of topics for a subject (and optional grade) so dropdowns are never capped at 100 items. */
export async function fetchAllTopics(subjectId: string, grade?: Grade): Promise<Topic[]> {
  const items: Topic[] = [];
  let page = 1;
  while (true) {
    const { data, error } = await api.GET("/topics", {
      params: {
        query: {
          page,
          pageSize: 100,
          subjectId,
          grade,
        },
      },
    });
    if (error) throw new Error(getApiErrorMessage(error));
    if (!data?.items?.length) break;
    items.push(...data.items);
    if (!data.totalPages || page >= data.totalPages) break;
    page++;
  }
  return items;
}
