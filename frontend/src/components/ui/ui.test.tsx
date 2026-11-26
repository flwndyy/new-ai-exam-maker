import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import { vi as messages } from "@/messages/vi";
import { BoardTabs } from "./board-tabs";
import { Button } from "./button";
import { DataTable } from "./data-table";
import { EmptyState } from "./empty-state";
import { Radio } from "./field";
import { MathMarkdown } from "../math-markdown";
import { ListRow } from "./list-row";
import { Notice } from "./notice";
import { Pager } from "./pager";
import { StatusText } from "./status-text";

afterEach(cleanup);

describe("Button", () => {
  it("explains a disabled state through aria-describedby", () => {
    render(<Button disabled disabledReason="Cần rà soát trước">Gửi duyệt</Button>);
    const button = screen.getByRole("button") as HTMLButtonElement;
    expect(button.disabled).toBe(true);
    const id = button.getAttribute("aria-describedby");
    expect(id).toBeTruthy();
    expect(document.getElementById(id as string)?.textContent).toBe("Cần rà soát trước");
    expect(button).toHaveProperty("title", "Cần rà soát trước");
  });

  it("defaults to type=button", () => {
    render(<Button>Lưu</Button>);
    expect(screen.getByRole("button").getAttribute("type")).toBe("button");
  });
});

describe("ListRow", () => {
  it("links the title and keeps the end block clickable", () => {
    const onAdd = vi.fn();
    const { container } = render(
      <ListRow
        tone="ok"
        href="/questions/detail/?id=1"
        title="Câu hỏi một"
        end={<button onClick={onAdd}>Thêm</button>}
      />,
    );
    const link = screen.getByRole("link", { name: "Câu hỏi một" });
    expect(link.getAttribute("href")).toMatch(/^\/questions\/detail\/?\?id=1$/);
    fireEvent.click(screen.getByRole("button", { name: "Thêm" }));
    expect(onAdd).toHaveBeenCalledTimes(1);
    expect(container.querySelector("[data-tone='ok']")).not.toBeNull();
  });

  it("contains long content horizontally and lets the end block wrap", () => {
    const { container } = render(
      <ListRow
        tone="wait"
        href="/x"
        title={<span className="katex">a very long inline formula</span>}
        end={<button>Thêm</button>}
      />,
    );
    const row = container.querySelector("[data-tone]") as HTMLElement;
    const content = container.querySelector("[data-row-content]") as HTMLElement;
    const end = container.querySelector("[data-row-end]") as HTMLElement;
    for (const cls of ["min-w-0", "max-w-full", "overflow-x-auto", "break-words"]) {
      expect(content.classList.contains(cls)).toBe(true);
    }
    expect(content.className).toContain("[&_.katex-display]:overflow-x-auto");
    expect(row.classList.contains("min-w-0")).toBe(true);
    expect(row.classList.contains("max-w-full")).toBe(true);
    expect(end.classList.contains("flex-wrap")).toBe(true);
    expect(end.classList.contains("z-10")).toBe(true);
    // Overlay stays scoped to the row: the content container is not positioned.
    expect(content.classList.contains("relative")).toBe(false);
    expect(screen.getByRole("link").className).toContain("after:inset-0");
  });

  it("keeps exactly one anchor when the title is a markdown stem with a link", () => {
    const { container } = render(
      <ListRow
        tone="ok"
        href="/q/1"
        title={<MathMarkdown links={false}>{"Xem [tài liệu](https://example.com) này"}</MathMarkdown>}
      />,
    );
    expect(container.querySelectorAll("a")).toHaveLength(1);
    expect(container.querySelector("a")?.getAttribute("href")).toBe("/q/1");
  });

  it("shows the focus ring on the row and suppresses the clipped outline on the title link", () => {
    const { container } = render(<ListRow tone="ok" href="/x" title="Tiêu đề" end={<button>Thêm</button>} />);
    const row = container.querySelector("[data-tone]") as HTMLElement;
    expect(row.className).toContain("has-[a:focus-visible]:ring-2");
    expect(row.className).toContain("has-[a:focus-visible]:ring-accent");
    const link = screen.getByRole("link");
    expect(link.className).toContain("focus-visible:outline-none");
    expect(link.className).toContain("after:inset-0");
    expect((container.querySelector("[data-row-end]") as HTMLElement).classList.contains("z-10")).toBe(true);
  });

  it("renders no link without href", () => {
    render(<ListRow tone="draft" title="Không liên kết" />);
    expect(screen.queryByRole("link")).toBeNull();
    expect(screen.getByText("Không liên kết")).toBeTruthy();
  });
});

describe("Pager", () => {
  it("disables previous on the first page and calls onPage for next", () => {
    const onPage = vi.fn();
    render(<Pager page={1} totalPages={3} onPage={onPage} />);
    expect(
      (screen.getByRole("button", { name: messages.common.previousPage }) as HTMLButtonElement).disabled,
    ).toBe(true);
    fireEvent.click(screen.getByRole("button", { name: messages.common.nextPage }));
    expect(onPage).toHaveBeenCalledWith(2);
    expect(screen.getByText(messages.common.pageOf(1, 3))).toBeTruthy();
  });

  it("disables next on the last page", () => {
    render(<Pager page={3} totalPages={3} onPage={() => {}} />);
    expect(
      (screen.getByRole("button", { name: messages.common.nextPage }) as HTMLButtonElement).disabled,
    ).toBe(true);
  });
});

describe("Notice", () => {
  it("uses role=alert for errors and role=status otherwise", () => {
    render(
      <>
        <Notice tone="error">Lỗi</Notice>
        <Notice tone="info">Thông tin</Notice>
      </>,
    );
    expect(screen.getByRole("alert").textContent).toContain("Lỗi");
    expect(screen.getByRole("status").textContent).toContain("Thông tin");
  });
});

describe("BoardTabs", () => {
  it("marks the active tab and reports selection", () => {
    const onSelect = vi.fn();
    render(
      <BoardTabs
        items={[
          { key: "a", label: "Câu hỏi" },
          { key: "b", label: "Đề thi" },
        ]}
        active="a"
        onSelect={onSelect}
      />,
    );
    expect(screen.getByRole("tab", { name: "Câu hỏi" }).getAttribute("aria-selected")).toBe("true");
    expect(screen.getByRole("tab", { name: "Đề thi" }).getAttribute("aria-selected")).toBe("false");
    fireEvent.click(screen.getByRole("tab", { name: "Đề thi" }));
    expect(onSelect).toHaveBeenCalledWith("b");
  });
});

describe("StatusText", () => {
  it("uses the tone text class", () => {
    render(<StatusText tone="wait">Chờ duyệt</StatusText>);
    expect(screen.getByText("Chờ duyệt").className).toContain("text-wait-text");
  });
});

describe("EmptyState", () => {
  it("renders an h3 by default and the requested heading level otherwise", () => {
    const { rerender } = render(<EmptyState title="Tiêu đề" />);
    expect(screen.getByRole("heading", { level: 3, name: "Tiêu đề" })).toBeTruthy();
    rerender(<EmptyState title="Tiêu đề" headingLevel={1} />);
    expect(screen.getByRole("heading", { level: 1, name: "Tiêu đề" })).toBeTruthy();
  });
});

describe("Radio", () => {
  it("forwards the ref and associates the label with the input", () => {
    const ref = { current: null as HTMLInputElement | null };
    render(<Radio ref={ref} name="x" label="A." />);
    const input = screen.getByRole("radio", { name: "A." }) as HTMLInputElement;
    expect(ref.current).toBe(input);
    expect(input.type).toBe("radio");
  });
});

describe("scroll containers", () => {
  // An absolutely positioned descendant (e.g. a sr-only label) of an unpositioned scroll box is
  // laid out against an outer ancestor and widens the page on a 390px screen.
  it("positions DataTable and BoardTabs so absolute children stay inside the scroll box", () => {
    const { container } = render(
      <>
        <DataTable>
          <table>
            <tbody>
              <tr>
                <td>x</td>
              </tr>
            </tbody>
          </table>
        </DataTable>
        <BoardTabs items={[{ key: "a", label: "A" }]} active="a" onSelect={() => {}} />
      </>,
    );
    const table = container.querySelector("table")!.parentElement!;
    expect(table.classList.contains("relative")).toBe(true);
    expect(table.classList.contains("overflow-x-auto")).toBe(true);
    expect(screen.getByRole("tablist").className).toContain("relative");
  });
});
