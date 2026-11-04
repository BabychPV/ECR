/**
 * Прапорці розділу Permits.
 *
 * `permitSheetBindings` — прив'язки таблиць аркушів до дозволу (PM-09/PM-11: вкладка «Permit binding» налаштувань
 * таблиці шаблону, розділ «Sheet bindings», «Read by» картки й переліку). Людина 30.10 (план PERMIT-USAGE, п. 1):
 * дозвіл пов'язується з даними ЛИШЕ значенням (поле шапки чи колонка Lookup на перелік дозволів), прив'язки не
 * потрібні. Тому прапорець ВИМКНЕНО: входи в інтерфейсі сховані, код, модель і міграції лишаються.
 *
 * Увімкнути на одному браузері (перевірка старого шляху, e2e `permitBindings.spec.ts`):
 * `localStorage.setItem('ecr.feature.permitSheetBindings', 'on')`.
 */
export const PermitSheetBindingsFlag = 'ecr.feature.permitSheetBindings';

/** Чи показувати входи в прив'язки аркушів. Типово — ні; сховище недоступне — теж ні. */
export function permitSheetBindingsEnabled(): boolean {
  try {
    return globalThis.localStorage?.getItem(PermitSheetBindingsFlag) === 'on';
  } catch {
    return false;
  }
}
