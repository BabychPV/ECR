import type { MethodologyDraftVersionDto } from '@/api/types';

/**
 * Рішення «видалити чернетку версії» (`BE-25`), які не є розміткою.
 *
 * ⚠ Заборону тримає сервер (`MethodologyVersion.EnsureDeletable`): кнопка тут
 * лише не веде у відмову, яку видно заздалегідь.
 *
 * ⚠ Розбору відмови `409` тут більше немає: причину (`versionNotDraft` /
 * `versionUsedInCalculations`) сервер віддає в `detail`, зібраному з каталогу
 * за `messageKey`, і її показує стандартний `ErrorAlert`.
 */

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
