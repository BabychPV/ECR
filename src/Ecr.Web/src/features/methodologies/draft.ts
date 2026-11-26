import type { MethodologyDraftVersionDto } from '@/api/types';

/**
 * Рішення конфігуратора методологій, які **не є розміткою** (`ФВ-9.15`).
 *
 * ✎ 24.11 (рішення людини «має бути тільки наш новий редактор»): формульний редактор прибрано з інтерфейсу —
 * чернетка формули (`FormulaDraft`, `formulaBody`) і вибір «версії за замовчуванням» серед формульних версій
 * пішли разом із ним; поточну версію-скрипт обирає `currentScriptVersion` (`newVersionEngine.ts`).
 */

/**
 * Чи можна правити вміст версії з екрана.
 *
 * ⛔ Дві умови, і жодна не замінює другої. `isEditable` каже СЕРВЕР — це стан
 * версії (`ФВ-13.2`: опублікована незмінна); право каже профіль
 * (`Calculation.EditFormula`). Показати дії правки без першої умови означає
 * вести користувача у відмову `ECR-CALC-0409`; без другої — у `403`.
 *
 * ⚠ Заборону тримає не ця функція, а домен на сервері. Тут — лише відсутність
 * кнопок, які не спрацюють.
 */
export function mayEditContent(
  version: MethodologyDraftVersionDto | undefined,
  hasPermission: boolean,
): boolean {
  return version !== undefined && version.isEditable && hasPermission;
}