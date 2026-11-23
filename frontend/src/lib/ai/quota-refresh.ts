/** Upper bound between quota reloads, so a sleeping tab or clock drift cannot keep a stale quota for long. */
export const QUOTA_REFRESH_MAX_MS = 60 * 60 * 1000;

const MIN_DELAY_MS = 1000;

/**
 * Delay before reloading GET /ai/quota: one second after `resetsAt` (the UTC day boundary, 07:00 in Vietnam),
 * capped at an hour; false when there is nothing to schedule.
 */
export function quotaRefreshDelay(resetsAt: string | undefined, now: number): number | false {
  if (!resetsAt) return false;
  const reset = Date.parse(resetsAt);
  if (Number.isNaN(reset)) return false;
  return Math.min(Math.max(reset - now + MIN_DELAY_MS, MIN_DELAY_MS), QUOTA_REFRESH_MAX_MS);
}
