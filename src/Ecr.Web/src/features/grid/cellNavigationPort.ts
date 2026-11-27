import type { CellNavigationTarget } from './cellNavigation';

/**
 * ✎ b18 бюджет D-132: «перейти до комірки» без статичного імпорту `cellNavigation` (+ `selection`) у чанк маршруту
 * документа. `cellNavigation` реєструє себе тут, щойно завантажений (сітка тягне його статично); до того — лінивий імпорт.
 */
let navigator: ((target: CellNavigationTarget) => void) | null = null;

/** Викликає лише `cellNavigation.ts` під час завантаження. */
export function registerCellNavigator(navigate: (target: CellNavigationTarget) => void): void {
  navigator = navigate;
}

/** Запит переходу до комірки: синхронно, якщо навігація вже завантажена, інакше — після лінивого імпорту. */
export function navigateToCell(target: CellNavigationTarget): void {
  if (navigator !== null) {
    navigator(target);
    return;
  }

  void import('./cellNavigation').then((module) => { module.requestCellNavigation(target); });
}
