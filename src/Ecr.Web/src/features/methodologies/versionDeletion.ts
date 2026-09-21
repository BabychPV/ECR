import { EcrApiError } from '@/api/client';
import type { MethodologyDraftVersionDto } from '@/api/types';

/**
 * Рішення «видалити чернетку версії» (`BE-25`), які не є розміткою.
 *
 * ⚠ Заборону тримає сервер (`MethodologyVersion.EnsureDeletable`): кнопка тут
 * лише не веде у відмову, яку видно заздалегідь.
 */

/** Право, під яким сервер приймає `DELETE …/versions/{vid}`. */
export const DeleteVersionPermission = 'Calculation.EditFormula';

/** `messageKey` відмови «версія не чернетка» (`ECR-CALC-0409`). */
export const NotDraftMessageKey = 'err.ECR-CALC-0409.versionNotDraft';

/** `messageKey` відмови «версією вже рахували» (`ECR-CALC-0409`). */
export const UsedInCalculationsMessageKey = 'err.ECR-CALC-0409.versionUsedInCalculations';

/**
 * Чи показувати кнопку видалення в рядку версії.
 *
 * ⛔ Лише `Draft`: опублікована й виведена з обігу вже рахували числа,
 * подані регуляторові. Чи рахували ЧЕРНЕТКОЮ (`UsedInCalculations`), клієнт
 * не знає — це відмова сервера, яку показуємо причиною.
 */
export function mayDeleteVersion(version: MethodologyDraftVersionDto, allowed: boolean): boolean {
  return allowed && version.status === 'Draft';
}

/** Відмова видалення, яку клієнт розпізнав за `messageKey`. */
export interface DeleteRefusal {
  readonly reason: 'UsedInCalculations' | 'NotDraft';

  /** Номер версії з відмови; якщо сервер його не дав — той, що видаляли. */
  readonly version: string;

  /** Для `NotDraft` — стан версії (`Published`, `Deprecated`); інакше `null`. */
  readonly state: string | null;

  readonly errorCode: string;
  readonly correlationId: string;
}

/**
 * Розпізнає `409` видалення версії за `messageKey`.
 *
 * ⚠ За ключем, а не за `reason`: ключ — контракт тексту, який показується
 * (`09-seed.sql`), а `reason` — лише параметр до нього. Невідомий ключ дає
 * `null`, і відмова йде загальним шляхом (`ErrorAlert`) — із текстом сервера,
 * а не з вигаданою тут причиною.
 */
export function deleteRefusalOf(error: unknown, fallbackVersion: string): DeleteRefusal | null {
  if (!(error instanceof EcrApiError) || error.problem.status !== 409) return null;

  const extensions = error.problem.extensions2 ?? {};
  const key = extensions['messageKey'];
  const version = typeof extensions['version'] === 'string' ? extensions['version'] : fallbackVersion;
  const common = {
    version,
    errorCode: error.problem.errorCode,
    correlationId: error.problem.correlationId,
  };

  if (key === UsedInCalculationsMessageKey) {
    return { ...common, reason: 'UsedInCalculations', state: null };
  }

  if (key === NotDraftMessageKey) {
    const state = extensions['reason'];

    return { ...common, reason: 'NotDraft', state: typeof state === 'string' ? state : null };
  }

  return null;
}
