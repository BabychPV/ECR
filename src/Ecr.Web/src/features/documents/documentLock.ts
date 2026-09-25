import type { components } from '@/api/schema';

type PeriodState = components['schemas']['PeriodState'];
type ProjectStatus = components['schemas']['ProjectStatus'];

/**
 * Чому документ за цей період не редагується — одна причина, найважливіша
 * (`F-18`).
 *
 * ⛔ Живцем на стенді: у закритому періоді й в архівному проєкті екран не
 * казав нічого — банера не було, «Submit», «Import» і «Recalculate» стояли
 * активні, а сервер на кожну відмовляв. Комірки сіріли без пояснення, а
 * банера поданого аркуша (посібник, розділ 5.3) не з'являлося зовсім.
 *
 * ⚠ Порядок — від ширшого до вужчого: архівний проєкт закриває все, закритий
 * (чи ще не відкритий) період — усі аркуші періоду, стан аркуша — лише його.
 * Показується одна причина: друга під першою нічого б не додала — редагувати
 * однаково не можна.
 */
export type DocumentLock = 'projectArchived' | 'periodClosed' | 'periodNotOpen' | 'sheetSubmitted' | 'sheetApproved';

/** Що відомо про документ для рішення. `undefined` — ще не прочитано. */
export interface DocumentLockFacts {
  readonly projectStatus: ProjectStatus | undefined;
  readonly periodState: PeriodState | undefined;
  readonly sheetState: string;
}

export function documentLockOf(facts: DocumentLockFacts): DocumentLock | null {
  if (facts.projectStatus === 'Archived') return 'projectArchived';
  if (facts.periodState === 'Closed') return 'periodClosed';
  if (facts.periodState === 'Scheduled') return 'periodNotOpen';
  if (facts.sheetState === 'Submitted') return 'sheetSubmitted';
  if (facts.sheetState === 'Approved') return 'sheetApproved';

  return null;
}

/**
 * Чи причина закриває ДІЇ зі зміни даних — подання, імпорт, перерахунок.
 *
 * ⚠ Стан аркуша сюди не входить: його дії вже вирішує таблиця переходів
 * (`transitions.ts`) — поданий аркуш і так не показує «Submit», а показує
 * «Approve»/«Return for edits», які закривати не можна.
 */
export function locksDataActions(lock: DocumentLock | null): boolean {
  return lock === 'projectArchived' || lock === 'periodClosed' || lock === 'periodNotOpen';
}
