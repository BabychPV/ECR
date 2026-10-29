import { EcrApiError } from '@/api/client';

/** Найближчий допустимий період до `key` (за відстанню ключів; рівновіддалені - раніший); `null` - перелік порожній. */
export function nearestPeriod(periods: readonly number[], key: number): number | null {
  let best: number | null = null;
  for (const candidate of periods) {
    if (best === null) {
      best = candidate;
      continue;
    }

    const distance = Math.abs(candidate - key);
    const bestDistance = Math.abs(best - key);
    if (distance < bestDistance || (distance === bestDistance && candidate < best)) best = candidate;
  }

  return best;
}

/** Відмова сервера `422 ECR-PRD-0422`: період не належить проєкту документа. */
export function isPeriodOutsideProject(error: unknown): boolean {
  return error instanceof EcrApiError && error.problem.errorCode === 'ECR-PRD-0422';
}
