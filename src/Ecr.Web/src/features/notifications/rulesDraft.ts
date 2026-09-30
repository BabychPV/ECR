import type { NotificationRule } from '@/features/notifications/api';

/**
 * Чернетка матриці правил сповіщень (`RulesMatrixPanel`) і її порівняння з
 * точкою відліку (аудит U14a).
 *
 * ⚠ Окремий модуль, а не функції всередині панелі: «що вважається зміною» —
 * рішення, яке панель, тест і реєстрація в `UnsavedGuard` мусять читати
 * однаково, і перевіряти його простіше без рендера.
 */

/** Подія, про яку сповіщають. */
export type EventKind = NotificationRule['eventKind'];

/** Межа серйозності правила. */
export type Severity = NotificationRule['minSeverity'];

/** Клітинка чернетки: чи діє правило і з якою межею. */
export interface Cell {
  readonly isEnabled: boolean;
  readonly minSeverity: Severity;
}

export type Draft = ReadonlyMap<string, Cell>;

/** Адреса клітинки в чернетці. */
export function cellKey(eventKind: EventKind, channelId: number): string {
  return `${eventKind}:${String(channelId)}`;
}

/** Чернетка з відповіді сервера. */
export function draftOf(rules: readonly NotificationRule[]): Draft {
  return new Map(
    rules.map((rule) => [
      cellKey(rule.eventKind, rule.channelId),
      { isEnabled: rule.isEnabled, minSeverity: rule.minSeverity },
    ]),
  );
}

/**
 * Що клітинка РЕАЛЬНО означає для сервера: межа, якщо правило діє, інакше
 * «вимкнено».
 *
 * ⛔ Межа вимкненої клітинки в тіло `PUT` не потрапляє (див.
 * `RulesMatrixPanel.enabledRules`), тож і зміною вона не є: зняти прапорець,
 * змінити межу нікуди й поставити прапорець назад — це повернення до вихідного
 * стану, а не правка, про яку треба питати при виході.
 */
function meaning(cell: Cell | undefined): string {
  return cell?.isEnabled === true ? cell.minSeverity : 'off';
}

/** Скільки клітинок чернетки означають не те саме, що в точці відліку. */
export function changedCells(seed: Draft, draft: Draft): number {
  const keys = new Set([...seed.keys(), ...draft.keys()]);
  let changed = 0;

  for (const key of keys) {
    if (meaning(seed.get(key)) !== meaning(draft.get(key))) changed++;
  }

  return changed;
}
