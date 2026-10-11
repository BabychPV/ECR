import { EcrApiError, SheetBeingSubmittedMessageKey } from '@/api/client';

/**
 * Відступи автоповтору синхронного застосування імпорту, мс (Z1-02).
 *
 * ⚠ Об'єкт, а не голий масив, лише щоб тест міг скоротити очікування; у продукті не змінюється.
 */
export const importBusyRetry: { delaysMs: number[] } = { delaysMs: [2_000, 4_000, 8_000] };

/**
 * Виконує застосування імпорту й повторює його, якщо сервер відмовив `409 sheetBeingSubmitted`.
 *
 * ⛔ Z1-02: сервер бере блокування аркушів книги в одній транзакції; перший аркуш чекає до 30 с, решта —
 * без черги (`EnterEditNoWaitAsync`, R7-Y2-02) і за зайнятості відмовляють `409 ECR-DOC-4091
 * sheetBeingSubmitted`. Це минуща відмова: транзакцію відкочено, нічого не записано, токен перегляду
 * лишився чинним (`ExcelImporter` прибирає його лише після коміту) — повтор тим самим запитом безпечний, а
 * «спробуйте за мить» без повтору змушував людину повторювати руками те, що клієнт зробить сам. Межа —
 * `importBusyRetry.delaysMs.length` повторів, далі людині показується та сама відмова.
 *
 * ⚠ Лише `sheetBeingSubmitted`: інші 409 (`sheetBeingEdited`, конфлікт версій) повтором не лікуються.
 */
export async function withSheetBusyRetry<T>(run: () => Promise<T>): Promise<T> {
  for (let attempt = 0; ; attempt += 1) {
    try {
      return await run();
    } catch (error) {
      const delay = importBusyRetry.delaysMs[attempt];
      if (delay === undefined || !isSheetBeingSubmitted(error)) throw error;

      const floor = (error.problem.retryAfterSeconds ?? 0) * 1000;
      await new Promise<void>((resolve) => setTimeout(resolve, Math.max(delay, floor)));
    }
  }
}

function isSheetBeingSubmitted(error: unknown): error is EcrApiError {
  return (
    error instanceof EcrApiError &&
    error.problem.errorCode === 'ECR-DOC-4091' &&
    error.problem.extensions2?.['messageKey'] === SheetBeingSubmittedMessageKey
  );
}
