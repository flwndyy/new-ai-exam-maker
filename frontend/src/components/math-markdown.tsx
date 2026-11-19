"use client";

import ReactMarkdown from "react-markdown";
import rehypeKatex from "rehype-katex";
import rehypeSanitize from "rehype-sanitize";
import remarkMath from "remark-math";

// AI-generated content is untrusted (docs/context.md §10): raw HTML is never rendered,
// and the tree is sanitized BEFORE KaTeX runs so KaTeX's own markup is not stripped.
export function MathMarkdown({ children }: { children: string }) {
  return (
    <ReactMarkdown remarkPlugins={[remarkMath]} rehypePlugins={[rehypeSanitize, rehypeKatex]}>
      {children}
    </ReactMarkdown>
  );
}
