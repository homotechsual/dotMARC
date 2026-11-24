export interface RoadmapIssue {
  number: number; title: string; url: string; status: string;
  milestone: number | null; completed: boolean; awaitingRelease: boolean; statusWarning: boolean;
  changeType: ChangeType | null; areas: ProductArea[]; classificationWarning: boolean;
}
export interface RoadmapMilestone {
  number: number; title: string; description: string; url: string;
  dueOn: string | null; released: boolean; releaseUrl: string | null;
}
export interface Roadmap {
  updatedAt: string; stale: boolean; submissionsEnabled: boolean;
  issues: RoadmapIssue[]; milestones: RoadmapMilestone[];
}

function record(value: unknown): Record<string, unknown> {
  if (!value || typeof value !== 'object' || Array.isArray(value)) throw new Error('Invalid roadmap response.');
  return Object.fromEntries(Object.entries(value));
}
function text(value: unknown): string {
  if (typeof value !== 'string') throw new Error('Invalid roadmap text.');
  return value;
}
function number(value: unknown): number {
  if (typeof value !== 'number' || !Number.isSafeInteger(value) || value < 1) throw new Error('Invalid roadmap identifier.');
  return value;
}
function boolean(value: unknown): boolean {
  if (typeof value !== 'boolean') throw new Error('Invalid roadmap state.');
  return value;
}
function link(value: unknown): string {
  const result = text(value);
  if (!result.startsWith('https://github.com/homotechsual/dotMARC/')) throw new Error('Invalid roadmap link.');
  return result;
}
function date(value: unknown): string {
  const result = text(value);
  if (!Number.isFinite(Date.parse(result))) throw new Error('Invalid roadmap date.');
  return result;
}
export function readRoadmap(value: unknown): Roadmap {
  const data = record(value);
  if (!Array.isArray(data.issues) || !Array.isArray(data.milestones)) throw new Error('Invalid roadmap lists.');
  return {
    updatedAt: date(data.updatedAt), stale: boolean(data.stale), submissionsEnabled: boolean(data.submissionsEnabled),
    issues: data.issues.map((value: unknown) => {
      const issue = record(value);
      const status = text(issue.status);
      if (!['triage', 'considering', 'planned', 'in-progress', 'shipped'].includes(status)) throw new Error('Invalid roadmap status.');
      // Older cached Worker snapshots have no taxonomy; show them as unclassified, not new features.
      const changeType = issue.changeType === undefined ? null : issue.changeType;
      const areas = issue.areas === undefined ? [] : issue.areas;
      if (changeType !== null && !isChangeType(changeType)) throw new Error('Invalid roadmap change type.');
      if (!Array.isArray(areas) || !areas.every(isProductArea) || new Set(areas).size !== areas.length) throw new Error('Invalid roadmap areas.');
      return {
        number: number(issue.number), title: text(issue.title), url: link(issue.url), status,
        milestone: issue.milestone === null ? null : number(issue.milestone),
        completed: boolean(issue.completed), awaitingRelease: boolean(issue.awaitingRelease), statusWarning: boolean(issue.statusWarning),
        changeType, areas, classificationWarning: issue.classificationWarning === undefined
          ? true : boolean(issue.classificationWarning),
      };
    }),
    milestones: data.milestones.map((value: unknown) => {
      const plan = record(value);
      return {
        number: number(plan.number), title: text(plan.title), description: text(plan.description), url: link(plan.url),
        dueOn: plan.dueOn === null ? null : date(plan.dueOn), released: boolean(plan.released),
        releaseUrl: plan.releaseUrl === null ? null : link(plan.releaseUrl),
      };
    }),
  };
}
import {isChangeType, isProductArea, type ChangeType, type ProductArea} from './taxonomy.ts';
