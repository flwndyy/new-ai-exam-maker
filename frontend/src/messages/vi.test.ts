import { describe, expect, it } from "vitest";
import { vi as messages } from "./vi";

// Per-key sample arguments for functions whose parameters are not plain numbers.
const SAMPLE_ARGS: Record<string, unknown[]> = {
  "auth.roleLine": ["teacher", "Tổ Toán"],
  "exams.print.departmentLine": ["Tổ Toán"],
  "ai.quota": [7, 10, "64.200", "100.000", "07:00"],
  "aiUsage.totals": ["12", "96.400", "$0,2810"],
  "exams.versionOption": [1, "Nháp"],
};

function collect(value: unknown, path: string, out: { path: string; text: string }[]): void {
  if (typeof value === "string") {
    out.push({ path, text: value });
  } else if (typeof value === "function") {
    const fn = value as (...args: unknown[]) => unknown;
    const args = SAMPLE_ARGS[path] ?? Array.from({ length: fn.length }, () => 1);
    collect(fn(...args), `${path}()`, out);
  } else if (Array.isArray(value)) {
    value.forEach((item, i) => collect(item, `${path}[${i}]`, out));
  } else if (value && typeof value === "object") {
    for (const [key, child] of Object.entries(value)) {
      collect(child, path ? `${path}.${key}` : key, out);
    }
  }
}

describe("vi messages", () => {
  const all: { path: string; text: string }[] = [];
  collect(messages, "", all);

  it("walks a meaningful number of strings", () => {
    expect(all.length).toBeGreaterThan(300);
  });

  it("never joins information with a middle dot", () => {
    expect(all.filter((s) => s.text.includes(" · ")).map((s) => s.path)).toEqual([]);
  });

  it("never uses an arrow", () => {
    expect(all.filter((s) => s.text.includes("→")).map((s) => s.path)).toEqual([]);
  });

  it("keeps sentence case except for printed exam titles", () => {
    const printed = new Set(["exams.print.studentTitle", "exams.print.answerTitle"]);
    const shouted = all.filter((s) => {
      if (printed.has(s.path)) return false;
      const letters = s.text.replace(/[^\p{L}]/gu, "");
      return letters.length >= 5 && letters === letters.toUpperCase();
    });
    expect(shouted.map((s) => s.path)).toEqual([]);
  });

  it("formats the AI quota as what is left", () => {
    expect(messages.ai.quota(7, 10, "64.200", "100.000", "07:00")).toBe(
      "Hôm nay còn 7/10 lượt và 64.200/100.000 token. Làm mới lúc 07:00.",
    );
  });

  it("summarises AI output with and without dropped duplicates", () => {
    expect(messages.ai.summary(5, 0)).toBe("AI tạo 5 câu.");
    expect(messages.ai.summary(5, 2)).toBe("AI tạo 5 câu, bỏ 2 câu trùng với ngân hàng.");
    expect(messages.ai.reviewProgress(3, 5)).toBe("Đã rà soát 3/5");
  });

  it("describes AI usage totals and the cost estimate", () => {
    expect(messages.aiUsage.cost).toBe("Chi phí ước tính");
    expect(messages.aiUsage.estimateNotice).toBe(
      "Chi phí là ước tính theo bảng giá cấu hình, không phải hóa đơn AWS.",
    );
    expect(messages.aiUsage.totals("12", "96.400", "$0,2810")).toBe(
      "Tổng theo bộ lọc: 12 job, 96.400 token, khoảng $0,2810",
    );
  });
});
