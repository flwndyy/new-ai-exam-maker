import type { Metadata } from "next";
import type { ReactNode } from "react";

import { AuthGate } from "@/components/auth-gate";
import { AppShell } from "@/components/shell/app-shell";
import { SidebarProvider } from "@/components/shell/sidebar-state";
import { vi } from "@/messages/vi";

import { sans, serif } from "./fonts";
import "./globals.css";
import { Providers } from "./providers";

export const metadata: Metadata = {
  title: vi.app.name,
  description: vi.app.tagline,
};

export default function RootLayout({ children }: { children: ReactNode }) {
  return (
    <html lang="vi" className={`${sans.variable} ${serif.variable}`}>
      <body>
        <Providers>
          <SidebarProvider>
            <AppShell>
              <AuthGate>{children}</AuthGate>
            </AppShell>
          </SidebarProvider>
        </Providers>
      </body>
    </html>
  );
}
