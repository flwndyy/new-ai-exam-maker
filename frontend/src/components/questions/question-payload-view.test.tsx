import { cleanup, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it } from "vitest";

import { vi as messages } from "@/messages/vi";
import { QuestionPayloadView } from "./question-payload-view";

afterEach(cleanup);

const WIDE = "$$\n\\frac{(x+1)(x+2)(x+3)(x+4)(x+5)(x+6)}{(x-1)(x-2)}\n$$";

// The text sits in a shrinkable flex item: an inline-block wrapper grows with a wide display
// formula and pushed a 390px page sideways instead of letting the formula scroll inside.
function expectBoundedContent(item: HTMLElement) {
  expect(item.classList.contains("flex")).toBe(true);
  const content = item.querySelector(".math-markdown")!.parentElement as HTMLElement;
  expect(content.classList.contains("min-w-0")).toBe(true);
  expect(content.classList.contains("flex-1")).toBe(true);
  expect(item.querySelector(".inline-block")).toBeNull();
}

describe("QuestionPayloadView", () => {
  it("bounds multiple-choice options and announces the correct one before its text", () => {
    render(
      <QuestionPayloadView
        type="MultipleChoice"
        payload={{
          options: [
            { label: "A", text: "Một" },
            { label: "B", text: WIDE },
            { label: "C", text: "Ba" },
            { label: "D", text: "Bốn" },
          ],
          correctLabel: "A",
        }}
      />,
    );
    const items = screen.getAllByRole("listitem");
    items.forEach(expectBoundedContent);
    expect(items[0].textContent).toBe(`A.${messages.questions.correctOption}: Một`);
    expect(items[1].querySelector(".katex-display")).not.toBeNull();
  });

  it("bounds true/false statements next to their verdict", () => {
    render(
      <QuestionPayloadView
        type="TrueFalse"
        payload={{
          statements: [
            { label: "a", text: WIDE, isTrue: true },
            { label: "b", text: "Hai", isTrue: false },
            { label: "c", text: "Ba", isTrue: true },
            { label: "d", text: "Bốn", isTrue: false },
          ],
        }}
      />,
    );
    for (const item of screen.getAllByRole("listitem")) {
      expectBoundedContent(item.firstElementChild as HTMLElement);
    }
  });
});
