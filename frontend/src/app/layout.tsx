import type { Metadata } from "next";
import Link from "next/link";
import type { ReactNode } from "react";

import { AuthGate } from "@/components/auth-gate";
import { RoleNav } from "@/components/role-nav";
import { UserMenu } from "@/components/user-menu";
import { vi } from "@/messages/vi";

import "./globals.css";
import { Providers } from "./providers";

export const metadata: Metadata = {
  title: vi.app.name,
  description: vi.app.tagline,
};

export default function RootLayout({ children }: { children: ReactNode }) {
  return (
    <html lang="vi">
      <body>
        <Providers>
          <header className="border-b border-slate-200 print:hidden">
            <nav className="mx-auto flex max-w-5xl items-center gap-6 px-4 py-3">
              <Link href="/" className="font-semibold">
                {vi.app.name}
              </Link>
              <Link href="/" className="text-sm text-slate-600 hover:text-slate-900">
                {vi.nav.home}
              </Link>
              <RoleNav />
              <UserMenu />
            </nav>
          </header>
          <main className="mx-auto max-w-5xl px-4 py-8 print:max-w-none print:p-0">
            <AuthGate>{children}</AuthGate>
          </main>
        </Providers>
      </body>
    </html>
  );
}
