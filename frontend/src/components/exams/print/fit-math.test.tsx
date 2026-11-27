import { readFileSync } from "node:fs";
import { join } from "node:path";

import { cleanup, render } from "@testing-library/react";
import { useRef } from "react";
import { afterEach, describe, expect, it, vi } from "vitest";

import { MATH_FIT_VAR, measureMathForPrint, useFitMathForPrint } from "./fit-math";

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

// Mirrors KaTeX output: .katex > .katex-html > .base chunks; a nested .base (inside a fraction) is not a line chunk.
const FORMULA = `
  <span class="katex" style="font-size: 20px">
    <span class="katex-mathml"></span>
    <span class="katex-html">
      <span class="base" data-w="100"><span class="base" data-w="900"></span></span>
      <span class="base" data-w="250"></span>
    </span>
  </span>`;

function mockWidths() {
  vi.spyOn(Element.prototype, "getBoundingClientRect").mockImplementation(function (this: Element) {
    const width = Number(this.getAttribute("data-w") ?? 0);
    return { width, height: 0, top: 0, left: 0, right: width, bottom: 0, x: 0, y: 0, toJSON: () => ({}) } as DOMRect;
  });
}

describe("measureMathForPrint", () => {
  it("stores the widest top-level chunk in em of the formula's font size", () => {
    mockWidths();
    const root = document.body.appendChild(document.createElement("div"));
    root.innerHTML = `<div class="katex-display">${FORMULA}</div>`;
    measureMathForPrint(root);
    root.remove();
    expect((root.querySelector(".katex") as HTMLElement).style.getPropertyValue(MATH_FIT_VAR)).toBe("12.5000");
  });

  it("leaves a formula without measurable chunks untouched", () => {
    const root = document.createElement("div");
    root.innerHTML = FORMULA;
    measureMathForPrint(root); // jsdom has no layout: every width is 0
    expect((root.querySelector(".katex") as HTMLElement).style.getPropertyValue(MATH_FIT_VAR)).toBe("");
  });
});

describe("useFitMathForPrint", () => {
  function Sheet({ html }: { html: string }) {
    const ref = useRef<HTMLDivElement>(null);
    useFitMathForPrint(ref, html);
    return <div ref={ref} dangerouslySetInnerHTML={{ __html: html }} />;
  }

  it("measures after render and again right before printing", () => {
    mockWidths();
    const { container } = render(<Sheet html={FORMULA} />);
    const katex = container.querySelector(".katex") as HTMLElement;
    expect(katex.style.getPropertyValue(MATH_FIT_VAR)).toBe("12.5000");

    katex.querySelector("[data-w='250']")!.setAttribute("data-w", "400");
    window.dispatchEvent(new Event("beforeprint"));
    expect(katex.style.getPropertyValue(MATH_FIT_VAR)).toBe("20.0000");
  });

  it("stops listening for beforeprint after unmount", () => {
    const remove = vi.spyOn(window, "removeEventListener");
    const { unmount } = render(<Sheet html={FORMULA} />);
    unmount();
    expect(remove).toHaveBeenCalledWith("beforeprint", expect.any(Function));
  });
});

describe("print stylesheet", () => {
  it("scales sheet formulas with the variable the hook sets and never clips them", () => {
    const css = readFileSync(join(process.cwd(), "src/app/globals.css"), "utf8");
    const print = css.slice(css.indexOf("@media print"));
    expect(print).toContain(`var(${MATH_FIT_VAR},`);
    expect(print).toMatch(/\.print-sheet \.math-markdown \.katex-display \{\s*overflow: visible;/);
    expect(print).toMatch(/\.print-sheet \.math-markdown \.katex-display > \.katex \{\s*white-space: normal;/);
    expect(print).toMatch(/\.print-sheet \.math-markdown \{\s*container-type: inline-size;/);
  });
});
