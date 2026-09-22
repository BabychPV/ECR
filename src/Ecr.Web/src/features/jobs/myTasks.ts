import type { JobSummary } from '@/api/types';

/**
 * Стани, у яких ВЛАСНА задача користувача ще чогось чекає.
 *
 * ⛔ Перелік звірений із сервером, а не вигаданий: `CancelJobHandler.Active`
 * (`IntegrationHandlers.cs`) містить рівно `Queued` і `Running`, решта
 * (`Succeeded`, `Failed`, `Cancelled`) — термінальні. Той самий перелік
 * повторений у `pages/admin/JobsPage.tsx` (`CancellableStates`) з ІНШОЮ
 * причиною — «що ще є сенс скасовувати»; звести їх в одне місце означало б
 * ототожнити два різні питання, тож поки вони збігаються свідомо і названо.
 */
export const ActiveJobStates: readonly string[] = ['Queued', 'Running'];

/** Чи задача ще виконується або стоїть у черзі. */
export function isActiveJob(state: string): boolean {
  return ActiveJobStates.includes(state);
}

/**
 * Скільки власних задач ще працює — число на позначці в шапці.
 *
 * ⛔ Рахуються САМЕ активні, не всі рядки переліку. Позначка «7» над задачами,
 * з яких шість учора успішно завершились, повідомляє неправду: індикатор у
 * шапці (`UX-09`) існує, щоб сказати «щось іще йде», а не «сьомий рядок у
 * списку».
 *
 * ⚠ `undefined` (перелік ще не приїхав) — це 0, а не «невідомо»: позначка,
 * яка блимає на кожному завантаженні сторінки, гірша за її відсутність.
 */
export function activeJobCount(jobs: readonly JobSummary[] | undefined): number {
  return (jobs ?? []).filter((job) => isActiveJob(job.state)).length;
}
