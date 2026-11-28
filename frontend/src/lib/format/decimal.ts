/** Format a JS number as a plain decimal string (never scientific notation like "1e-7"). */
export function formatDecimalForInput(value: number): string {
  const str = String(value);
  if (!/[eE]/.test(str)) return str;
  const sign = value < 0 ? "-" : "";
  const [mantissa, expPart] = str.replace(/^-/, "").toLowerCase().split("e");
  const exp = Number(expPart);
  const [intPart, fracPart = ""] = mantissa.split(".");
  const digits = intPart + fracPart;
  const decimalPos = intPart.length + exp;
  if (decimalPos <= 0) {
    return `${sign}0.${"0".repeat(-decimalPos)}${digits}`;
  }
  if (decimalPos >= digits.length) {
    return `${sign}${digits}${"0".repeat(decimalPos - digits.length)}`;
  }
  return `${sign}${digits.slice(0, decimalPos)}.${digits.slice(decimalPos)}`;
}

function normalizeDecimal(s: string): string {
  let t = s.replace(/^\+/, "");
  const neg = t.startsWith("-");
  t = t.replace(/^-/, "");
  const [rawInt, rawFrac = ""] = t.split(".");
  const intPart = rawInt.replace(/^0+(?=\d)/, "") || "0";
  const fracPart = rawFrac.replace(/0+$/, "");
  const out = fracPart ? `${intPart}.${fracPart}` : intPart;
  return neg && out !== "0" ? `-${out}` : out;
}

/**
 * Parse a finite decimal string (accepting comma or dot) and reject values that IEEE-754 Number
 * would silently round, while preserving every valid JS double (including 16-17 significant digits
 * loaded from the server).
 */
export function parseExactDecimal(raw: string): number | null {
  const trimmed = raw.trim().replace(",", ".");
  if (!/^[+-]?(\d+(\.\d+)?|\.\d+)$/.test(trimmed)) return null;
  const num = Number(trimmed);
  if (!Number.isFinite(num)) return null;
  if (formatDecimalForInput(num) !== normalizeDecimal(trimmed)) return null;
  return num;
}
