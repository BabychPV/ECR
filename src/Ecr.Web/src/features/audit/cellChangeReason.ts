import { t } from '@/shared/i18n';

/**
 * ✎ D-461: причина зміни комірки (`aud.CellChange.Reason`) мовою інтерфейсу; `null` — причини немає.
 *
 * ⚠ Причину вводить людина, коли ставить ручне значення замість формули (`Origin = FormulaOverride`), — вона
 * показується як є. Без людини сервер пише рівно три літерали: «Excel import» (`PatchCellsHandler.ExcelImportOverrideReason`
 * — імпорт книги замінив формулу), «Version migration» (`FormulaOverrideMigration.Reason` — перенос версії повернув
 * формулу); ✎ аудит 31.10 (F2) — «Version migration: no formula» (`FormulaOverrideMigration.UnformulatedReason` — у
 * цільовій версії комірку не рахує жодна формула, значення лишилося введеним). Лише вони перекладаються, точним збігом: схожий текст людини — її слова, а не системний літерал.
 * Ключі — ЛІТЕРАЛАМИ, щоб сторож `EndpointCoverageTests` звіряв кожен із `09-seed.sql`.
 */
export function cellChangeReasonText(reason: string | null | undefined): string | null {
  if (reason === null || reason === undefined || reason.trim().length === 0) return null;

  switch (reason) {
    case 'Excel import':
      return t('audit.reasonExcelImport');
    case 'Version migration':
      return t('audit.reasonVersionMigration');
    case 'Version migration: no formula':
      return t('audit.reasonVersionMigrationNoFormula');
    default:
      return reason;
  }
}
