import { t } from '@/shared/i18n';

/**
 * Людська назва типу фонової задачі — ЛИШЕ для показу (аудит-пас 8, lane6,
 * п.8).
 *
 * ⛔ Тости (`workflow.recalcQueued`, `snapshots.queued`, `sources.queued`) і
 * перелік `/admin/jobs` показували СИРІ .NET-імена буквально:
 * `IRecalculationJob-a1b2…` (`jobId`, `QuartzJobScheduler.EnqueueAsync` —
 * `$"{typeof(TJob).Name}-{Guid.NewGuid():N}"`) і
 * `Ecr.Application.Ports.IRecalculationJob` (`jobCode`,
 * `QuartzJobScheduler.JobCodeKey` — `typeof(TJob).FullName`). Обидва поля
 * потрібні серверу для внутрішнього зіставлення прогресу з типом задачі —
 * міняти їхній ФОРМАТ у сховищі чи API ризиковано для задач, що вже в черзі
 * (задача, поставлена до фіксу, і задача, поставлена після, мали б різний
 * формат ідентифікатора). Тому фікс — косметичний, лише в рендерингу.
 *
 * ⚠ Перелік типів дослівно повторює інтерфейси `IBackgroundJob` з
 * `IBackgroundJobScheduler.cs`. Невідомий тип (нова задача, яку сюди ще не
 * додали) не ховається — показується проста назва типу, а не вигадана.
 */
const KindKeys: Record<string, string> = {
  IRecalculationJob: 'jobs.kind.recalculation',
  IFormulaRecalculationJob: 'jobs.kind.formulaRecalculation',
  IExcelExportJob: 'jobs.kind.excelExport',
  IExcelImportJob: 'jobs.kind.excelImport',
  IMaterializeCollectedDataJob: 'jobs.kind.materializeCollectedData',
  IReportSnapshotJob: 'jobs.kind.reportSnapshot',
  ICollectionJob: 'jobs.kind.collection',
};

/** Просте ім'я типу з повного (`Ecr.Application.Ports.IRecalculationJob`). */
function simpleTypeName(value: string): string {
  return value.includes('.') ? value.slice(value.lastIndexOf('.') + 1) : value;
}

/**
 * Людська назва типу задачі з `jobCode` (повне ім'я типу — те саме для
 * кожного екземпляра задачі цього виду).
 */
export function jobKindLabel(jobCode: string): string {
  const key = KindKeys[simpleTypeName(jobCode)];

  return key === undefined ? simpleTypeName(jobCode) : t(key);
}

/** Формат `jobId` звичайної (не повторюваної) постановки — `Тип-GUID32`. */
const InstanceIdPattern = /^([A-Za-z]\w*)-([0-9a-fA-F]{32})$/;

/**
 * `jobId` з людським видом типу, але ТИМ САМИМ GUID екземпляра — рядок
 * лишається придатним для впізнання конкретної задачі (скопіювати, вставити
 * у пошук на `/admin/jobs`), а не лише перекладом «якийсь перерахунок».
 *
 * ⚠ Формат, якого функція не впізнає (повторювана задача:
 * `QuartzJobScheduler.ScheduleRecurringAsync` кодує ключ як `Тип:відбиток`,
 * без GUID), повертається БЕЗ ЗМІН — це той самий принцип запасного варіанту,
 * що й `jobKindLabel` вище: не вигадувати вигляд для того, чого не впізнано.
 */
export function humanizeJobId(jobId: string): string {
  const match = InstanceIdPattern.exec(jobId);
  if (match === null) return jobId;

  const [, typeName, guid] = match;
  const key = KindKeys[typeName!];

  return key === undefined ? jobId : `${t(key)}-${guid}`;
}
