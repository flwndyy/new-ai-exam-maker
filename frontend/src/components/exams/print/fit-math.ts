import { useEffect, type RefObject } from "react";

/** CSS custom property read by the print rules in globals.css. */
export const MATH_FIT_VAR = "--math-fit-em";

/**
 * Paper cannot scroll, so in print a formula breaks after top-level relations and operators and
 * any chunk that still does not fit its column is scaled down (globals.css, `@media print`).
 * The CSS needs the width of each formula's widest unbreakable chunk, in em of the formula's own
 * font size: chunks have a fixed intrinsic width, so measuring them on screen is valid on paper.
 */
export function measureMathForPrint(root: ParentNode): void {
  for (const katex of root.querySelectorAll<HTMLElement>(".katex")) {
    const fontSize = parseFloat(getComputedStyle(katex).fontSize);
    let widest = 0;
    for (const base of katex.querySelectorAll<HTMLElement>(":scope > .katex-html > .base")) {
      widest = Math.max(widest, base.getBoundingClientRect().width);
    }
    if (fontSize > 0 && widest > 0) katex.style.setProperty(MATH_FIT_VAR, (widest / fontSize).toFixed(4));
  }
}

/** Measures the sheet's formulas once rendered, again when web fonts finish loading, and right before printing. */
export function useFitMathForPrint(ref: RefObject<HTMLElement | null>, content: unknown): void {
  useEffect(() => {
    const measure = () => {
      if (ref.current) measureMathForPrint(ref.current);
    };
    measure();
    let active = true;
    void document.fonts?.ready.then(() => {
      if (active) measure();
    });
    window.addEventListener("beforeprint", measure);
    return () => {
      active = false;
      window.removeEventListener("beforeprint", measure);
    };
  }, [ref, content]);
}
