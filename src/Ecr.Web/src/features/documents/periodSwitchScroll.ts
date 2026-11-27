/**
 * Позиція прокрутки, зафіксована ДО перемикання періоду документа.
 *
 * ⛔ Що було (PERIODNAV, живий стенд): під час `isPending` нового періоду сторінка заміняє ВЕСЬ документ скелетом
 * (`AsyncBoundary`), `scrollHeight` падає до висоти вікна, браузер притискає прокрутку до 0 — і захоплення
 * `window.scrollY` у рендері нового періоду бачило вже не ту позицію. Тут позиція береться в обробнику, що міняє
 * період, тобто до будь-якої зміни розкладки.
 */
const ExpiryMs = 15_000;

let remembered: { documentId: number; y: number; at: number } | null = null;

/** Запам'ятовує поточну прокрутку; не перезаписує ще не використаний запис (редиректи якоря/найближчого періоду). */
export function rememberScrollBeforePeriodSwitch(documentId: number, y: number = window.scrollY, now: number = performance.now()): void {
  if (remembered !== null && remembered.documentId === documentId && now - remembered.at < ExpiryMs) return;
  remembered = { documentId, y, at: now };
}

/** Запам'ятована позиція документа (без споживання) або `null`. */
export function peekScrollBeforePeriodSwitch(documentId: number, now: number = performance.now()): number | null {
  if (remembered === null || remembered.documentId !== documentId || now - remembered.at >= ExpiryMs) return null;

  return remembered.y;
}

/** Споживає запис: наступне перемикання знову запам'ятає актуальну позицію. */
export function clearScrollBeforePeriodSwitch(): void {
  remembered = null;
}
