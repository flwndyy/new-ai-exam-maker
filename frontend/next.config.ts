import type { NextConfig } from "next";

// Static export: no SSR, Server Actions, middleware or ISR (docs/context.md §5.4, §9.2).
const nextConfig: NextConfig = {
  output: "export",
  trailingSlash: true,
  images: { unoptimized: true },
};

export default nextConfig;
