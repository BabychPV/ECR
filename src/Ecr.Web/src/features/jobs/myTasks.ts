import type { JobSummary } from '@/api/types';
import { t } from '@/shared/i18n';

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

/** Повне ім'я маркера каскадного перерахунку формул (`QuartzJobScheduler` пише `FullName`). */
const FormulaRecalculationCode = 'Ecr.Application.Ports.IFormulaRecalculationJob';

/** Повне ім'я маркера експорту документа. */
const ExportCode = 'Ecr.Application.Ports.IExcelExportJob';

/** Ідентифікатор файлу експорту: 32 шістнадцяткові символи (`ExportDocumentHandler`, `Guid("N")`). */
const ExportIdPattern = /^[0-9a-f]{32}$/i;

/**
 * Чи рядок треба показати у «My tasks».
 *
 * ⛔ F-27 (UX-PASS, четвертий раунд): кожне автозбереження ставить у чергу
 * каскадний перерахунок формул, і шухляда заростала записами «Formula
 * recalculation — Recalculated cells: 0». Людина цих задач не замовляла — це
 * наслідок її правки, результат якого вона вже бачить у сітці. Тому успішний
 * перерахунок формул тут не показується; провалений і той, що ще йде, —
 * показуються: там є що сказати.
 *
 * ⚠ Приховується ВЕСЬ успішний, а не лише «0 комірок»: кількість приходить
 * уже перекладеним реченням, і розбирати його назад означало б залежати від
 * тексту каталогу. Екран `#/admin/jobs` показує все як є.
 */
export function isShownInMyTasks(job: JobSummary): boolean {
  return !(job.jobCode === FormulaRecalculationCode && job.state === 'Succeeded');
}

/**
 * Повідомлення рядка «My tasks».
 *
 * ⛔ F-27: задача експорту кладе в повідомлення ІДЕНТИФІКАТОР файлу — його
 * читає клієнт експорту, щоб забрати книгу (`ExportButton`, `resultUrl`), —
 * і шухляда показувала людині 32 шістнадцяткові символи. Тут — людський текст;
 * посилання на файл малює `JobResultLink` поруч.
 */
export function myTaskMessage(job: JobSummary): string | null {
  const message = job.message ?? '';

  if (job.jobCode === ExportCode && ExportIdPattern.test(message)) {
    return t('jobs.exportReady');
  }

  return message === '' ? null : message;
}
