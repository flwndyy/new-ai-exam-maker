import { Be_Vietnam_Pro, Noto_Serif } from "next/font/google";

// Self-hosted at build time by next/font; CSP keeps `font-src 'self'`.
export const sans = Be_Vietnam_Pro({
  subsets: ["vietnamese", "latin"],
  weight: ["400", "500", "600", "700"],
  display: "swap",
  variable: "--font-be-vietnam",
});

// Serif face for the printed exam sheet.
export const serif = Noto_Serif({
  subsets: ["vietnamese", "latin"],
  weight: ["400", "700"],
  display: "swap",
  preload: false, // only used on the print page
  variable: "--font-noto-serif",
});
