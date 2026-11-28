"use client";

import type { ReactNode } from "react";
import ReactMarkdown, { type Components } from "react-markdown";
import rehypeKatex from "rehype-katex";
import rehypeSanitize from "rehype-sanitize";
import remarkMath from "remark-math";

// With `links={false}` anchors are rendered as plain spans: used where the markdown sits inside
// another link (list row titles), since nested anchors are invalid and expose untrusted URLs to Tab.
const PLAIN_LINKS: Components = {
  a: ({ children }: { children?: ReactNode }) => <span>{children}</span>,
};

// Long unbroken words wrap anywhere and a wide display formula scrolls inside itself, so
// question content never makes the page scroll sideways on a narrow screen.
const CONTAINER = "math-markdown min-w-0 max-w-full wrap-anywhere [&_.katex-display]:max-w-full [&_.katex-display]:overflow-x-auto [&_.katex-display]:overflow-y-hidden";

// AI-generated content is untrusted (docs/context.md §10): raw HTML is never rendered,
// and the tree is sanitized BEFORE KaTeX runs so KaTeX's own markup is not stripped.
export function MathMarkdown({ children, links = true }: { children: string; links?: boolean }) {
  return (
    <div className={CONTAINER}>
      <ReactMarkdown
        remarkPlugins={[remarkMath]}
        rehypePlugins={[rehypeSanitize, rehypeKatex]}
        components={links ? undefined : PLAIN_LINKS}
      >
        {children}
      </ReactMarkdown>
    </div>
  );
}
