import { fetchJson, safeCount } from '../../shared/fetch.js';

export type Procedure = { goal: string; revision: number; applicability: string; preconditions: string; steps: string; verification: string };
export type Source = { class: string; preview: string; sourceRevision: number; warmExcluded: boolean;
  windows: Array<{ days: number; presented: number; applied: number; ratingSampleSize: number; selfReportedTasks: number; utility: number | null }> };
export type Knowledge = {
  status: string; classifiedCount: number; procedureCount: number; occurrenceCount: number; presentationCount: number;
  activeWarmCount: number; workingSetActive: boolean; procedures: Procedure[];
  feedback: Record<string, number>; applications: Record<string, number>; reviewReasons: Record<string, number>;
  sources: Source[];
};
export const unavailable = (): Knowledge => ({ status: 'unavailable', classifiedCount: 0, procedureCount: 0,
  occurrenceCount: 0, presentationCount: 0, activeWarmCount: 0, workingSetActive: false, procedures: [], feedback: {}, applications: {}, reviewReasons: {}, sources: [] });
const feedbackKinds = ['Helpful', 'Harmful', 'Irrelevant', 'Outdated', 'Incorrect', 'Applied'];
const applicationKinds = ['ReportedSuccess', 'VerifiedSuccess', 'Partial', 'Failed', 'NotApplicable', 'Unknown'];
const reviewKinds = ['reported_content_issue', 'negative_agent_feedback_review_only'];

/** Copies only documented counters; unexpected keys and nested payloads cannot reach markup. */
function counts(value: unknown, allowed: string[]): Record<string, number> {
  const result: Record<string, number> = {};
  if (!value || typeof value !== 'object') return result;
  const source = value as Record<string, unknown>;
  for (const key of allowed) if (Object.hasOwn(source, key)) result[key] = safeCount(source[key]);
  return result;
}

/** Bounds untrusted server content and discards all fields outside the public projection. */
export function safeResponse(payload: unknown): Knowledge {
  if (!payload || typeof payload !== 'object') return unavailable();
  const body = payload as Record<string, unknown>;
  if (body.status !== 'available' || !Array.isArray(body.procedures)) return unavailable();
  const procedures: Procedure[] = [];
  for (const raw of body.procedures.slice(0, 50)) {
    if (!raw || typeof raw !== 'object') continue;
    const item = raw as Record<string, unknown>;
    const text = (key: string, max: number) => typeof item[key] === 'string' ? item[key].slice(0, max) : '';
    procedures.push({ goal: text('goal', 1000), revision: safeCount(item.revision), applicability: text('applicability', 1000),
      preconditions: text('preconditions', 4000), steps: text('steps', 8000), verification: text('verification', 4000) });
  }
  return { status: 'available', classifiedCount: safeCount(body.classifiedCount), procedureCount: safeCount(body.procedureCount),
    occurrenceCount: safeCount(body.occurrenceCount), presentationCount: safeCount(body.presentationCount), activeWarmCount: safeCount(body.activeWarmCount),
    workingSetActive: body.workingSetActive === true, procedures, feedback: counts(body.feedback, feedbackKinds),
    applications: counts(body.applications, applicationKinds), reviewReasons: counts(body.reviewReasons, reviewKinds), sources: safeSources(body.sources) };
}

/** Accepts bounded current-source cards and the three documented observation windows. */
function safeSources(payload: unknown): Source[] {
  const sources: Source[] = [];
  if (!Array.isArray(payload)) return sources;
  for (const raw of payload.slice(0, 50)) {
    if (!raw || typeof raw !== 'object' || !['L11', 'L12', 'L13', 'Unclassified'].includes(raw.class) || !Array.isArray(raw.windows)) continue;
    const windows: Source['windows'] = [];
    for (const item of raw.windows.slice(0, 3)) {
      if (!item || ![1, 7, 30].includes(item.days)) continue;
      windows.push({ days: item.days, presented: safeCount(item.presented), applied: safeCount(item.applied), ratingSampleSize: safeCount(item.ratingSampleSize),
        selfReportedTasks: safeCount(item.selfReportedTasks), utility: typeof item.utility === 'number' && Number.isFinite(item.utility) && item.utility >= 0 && item.utility <= 1 ? item.utility : null });
    }
    sources.push({ class: raw.class, preview: typeof raw.preview === 'string' ? raw.preview.slice(0, 400) : '', sourceRevision: safeCount(raw.sourceRevision), warmExcluded: raw.warmExcluded === true, windows });
  }
  return sources;
}

/** Loads a read-only local projection. Failure has a safe unavailable representation. */
export async function load(): Promise<Knowledge> {
  return safeResponse(await fetchJson<unknown>('/api/knowledge'));
}
