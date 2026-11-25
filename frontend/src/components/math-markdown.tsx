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

// AI-generated content is untrusted (docs/context.md §10): raw HTML is never rendered,
// and the tree is sanitized BEFORE KaTeX runs so KaTeX's own markup is not stripped.
export function MathMarkdown({ children, links = true }: { children: string; links?: boolean }) {
  return (
    <ReactMarkdown
      remarkPlugins={[remarkMath]}
      rehypePlugins={[rehypeSanitize, rehypeKatex]}
      components={links ? undefined : PLAIN_LINKS}
    >
      {children}
    </ReactMarkdown>
  );
}
