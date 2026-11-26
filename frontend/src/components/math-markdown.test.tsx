import { cleanup, render } from "@testing-library/react";
import { afterEach, describe, expect, it } from "vitest";

import { MathMarkdown } from "./math-markdown";

afterEach(cleanup);

describe("MathMarkdown links", () => {
  it("renders markdown links as anchors by default", () => {
    const { container } = render(<MathMarkdown>{"[x](https://example.com)"}</MathMarkdown>);
    expect(container.querySelectorAll("a")).toHaveLength(1);
  });

  it("renders no anchor with links={false} but keeps the text", () => {
    const { container } = render(<MathMarkdown links={false}>{"[x](https://example.com)"}</MathMarkdown>);
    expect(container.querySelector("a")).toBeNull();
    expect(container.textContent).toContain("x");
    expect(container.textContent).not.toContain("example.com");
  });
});

describe("MathMarkdown layout", () => {
  it("lets long words wrap and wide display formulas scroll inside the content, not the page", () => {
    const { container } = render(<MathMarkdown>{"$$x^2$$\n\nMộtchuỗirấtdàikhôngcókhoảngtrắng"}</MathMarkdown>);
    const root = container.firstElementChild as HTMLElement;
    expect(root.className).toContain("wrap-anywhere");
    expect(root.className).toContain("min-w-0");
    expect(root.className).toContain("[&_.katex-display]:overflow-x-auto");
    expect(root.textContent).toContain("Mộtchuỗirấtdàikhôngcókhoảngtrắng");
  });
});
