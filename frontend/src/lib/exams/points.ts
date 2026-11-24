// Exact decimal arithmetic for exam points. Points are kept as strings in forms and must never
// be added or split with JS floats (0.1 + 0.2 !== 0.3).

const DECIMAL_RE = /^\d+([.,]\d+)?$/;

/** Parse a non-negative decimal string into a scaled BigInt; returns null for invalid input. */
function toScaled(raw: string): { value: bigint; scale: number } | null {
  const trimmed = raw.trim();
  if (!DECIMAL_RE.test(trimmed)) return null;
  const [intPart, fracPart = ""] = trimmed.replace(",", ".").split(".");
  return { value: BigInt(intPart + fracPart), scale: fracPart.length };
}

/** Format a scaled BigInt as a plain decimal string without trailing zeros. */
function fromScaled(value: bigint, scale: number): string {
  if (scale === 0) return value.toString();
  const digits = value.toString().padStart(scale + 1, "0");
  const intPart = digits.slice(0, digits.length - scale);
  const fracPart = digits.slice(digits.length - scale).replace(/0+$/, "");
  return fracPart ? `${intPart}.${fracPart}` : intPart;
}

/** Sum decimal strings exactly (comma or dot separator); blank or invalid entries count as zero. */
export function sumDecimalStrings(values: string[]): string {
  const parsed = values.map(toScaled).filter((p): p is { value: bigint; scale: number } => p !== null);
  const scale = parsed.reduce((max, p) => Math.max(max, p.scale), 0);
  const total = parsed.reduce((sum, p) => sum + p.value * 10n ** BigInt(scale - p.scale), 0n);
  return fromScaled(total, scale);
}

/**
 * Split `total` points across `n` items with 2 decimals, rounding down and putting the cent
 * remainder on the last item so the parts always sum back to `total`.
 */
export function splitPointsEvenly(total: number, n: number): string[] {
  if (n <= 0) return [];
  const totalCents = BigInt(Math.round(total * 100));
  const base = totalCents / BigInt(n);
  const last = totalCents - base * BigInt(n - 1);
  return Array.from({ length: n }, (_, i) => fromScaled(i === n - 1 ? last : base, 2));
}
