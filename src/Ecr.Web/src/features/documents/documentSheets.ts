import type { DocumentSummary } from '@/api/types';
import { localized } from '@/shared/i18n/localized';
import { isCopyStateKey } from './sheetCopies/sheetCopyTypes';

/** Аркуш документа в переліку: код, людська назва, стан. */
export interface SheetLabel {
  readonly code: string;
  readonly label: string;
  readonly state: string;
}

type SheetSource = Pick<DocumentSummary, 'sheetStates' | 'sheets'>;

/**
 * Аркуші документа в ПОРЯДКУ аркушів і з назвою мовою інтерфейсу.
 *
 * ⚠ `sheets` (з назвою й порядком) — першим; без нього — пари словника
 * `sheetStates` з кодом замість назви. Назви немає жодною мовою — підпис падає
 * на код (`S99819007` краще, ніж порожнє місце).
 */
export function sheetLabels(document: SheetSource): SheetLabel[] {
  if (document.sheets !== undefined && document.sheets !== null && document.sheets.length > 0) {
    return document.sheets.map((sheet) => {
      const name = localized(sheet.nameL10n);

      return { code: sheet.code, label: name.length > 0 ? name : sheet.code, state: sheet.state };
    });
  }

  // ⚠ Копії (`КОД#id`) — не окремі аркуші шаблону: їх у переліку немає (вони вкладки аркуша `КОД`).
  return Object.entries(document.sheetStates)
    .filter(([code]) => !isCopyStateKey(code))
    .map(([code, state]) => ({ code, label: code, state }));
}

/** Чи є в документа стани за обраний період (без періоду словник порожній, `D-93`). */
export function hasSheetStates(document: Pick<DocumentSummary, 'sheetStates'>): boolean {
  return Object.keys(document.sheetStates).length > 0;
}

/**
 * Порядок «найгіршого» стану — дзеркало `DocumentListSummaryStore`
 * (`DocumentListSummaryResponse`): `Rejected > Draft > Submitted > Approved`.
 */
const Worst = ['Rejected', 'Draft', 'Submitted', 'Approved'] as const;

/**
 * Стан ДОКУМЕНТА в переліку — найгірший зі станів його аркушів.
 *
 * ⛔ Скалярного стану документа на сервері немає (`D-93`); саме цим правилом
 * сервер рахує смугу лічильників над переліком і фільтр `state`. Колонка мусить
 * рахувати так само, інакше клік «2 Draft» показав би рядки з іншим станом.
 *
 * ⚠ Стан, якого правило не знає (новий стан сервера), повертається як є — і
 * `StatusBadge` позначить його «увагою», а не сховає за відомим словом.
 * Без станів (`null`) — період не обрано або станів за нього немає.
 */
export function documentState(document: Pick<DocumentSummary, 'sheetStates' | 'workflowMode'>): string | null {
  // ✎ B147 (B75): версія шаблону документа за період — без подання й затвердження (`workflowMode = None`): стан
  // «без погодження», а не чернетка; стани аркушів (сервер віддає їх `Draft`) не діють — і за 0 аркушів теж.
  // Те саме правило — фільтр `state=NoApproval` і лічильник смуги `noApproval`.
  if (document.workflowMode === 'None') return 'NoApproval';

  const states = Object.values(document.sheetStates);
  if (states.length === 0) return null;

  const unknown = states.find((state) => !(Worst as readonly string[]).includes(state));
  if (unknown !== undefined) return unknown;

  const worst = Worst.find((state) => states.includes(state)) ?? null;

  // ✎ UAT-recalc-ux: чернетка поруч із поданим/затвердженим — «частково подано» (`02-contracts.md`, стан
  // погодження документа; те саме правило — фільтр `state=PartlySubmitted` і лічильник смуги).
  if (worst === 'Draft' && states.some((state) => state === 'Submitted' || state === 'Approved')) {
    return 'PartlySubmitted';
  }

  return worst;
}

/** Відомі стани клітинки «документ × період» (`DocumentPeriodState.state`). */
const PeriodCellStates = ['Rejected', 'Draft', 'PartlySubmitted', 'Submitted', 'Approved'] as const;

/**
 * ✎ b14 documents-board-yearly: стан ДОКУМЕНТА за РІК — те саме правило, що в сервера для смуги лічильників за рік
 * (`DocumentListSummaryStore.SummarizeYearAsync`) і фільтра `state` за рік (`DocumentStore.WhereYearState`): клітинки —
 * «видимий аркуш × РОЗПОЧАТИЙ період року» без періодів «без погодження». Сервер дає стан кожного періоду
 * (`periodStates`, той самий CASE над аркушами), тож рік зводиться з них без втрат:
 * хоч один період повернуто → `Rejected`; жоден не поданий/затверджений → `Draft`; є і поданий, і неподаний →
 * `PartlySubmitted`; неподаних немає, хоч один поданий → `Submitted`; усі затверджені → `Approved`. Усі розпочаті
 * періоди без погодження → `NoApproval`.
 *
 * ⛔ Корінь дефекту: дошка брала `sheetStates` (стан за ОДИН обраний місяць, D-93), а перелік тепер завжди за рік і
 * місяця не має — словник порожній, і всі документи падали в «без стану», хоча лічильники над дошкою їх рахували.
 *
 * ⚠ `started` — розпочаті періоди проєкту (календар, стан ≠ `Scheduled`): сервер віддає `Scheduled`-місяці як `Draft`,
 * а в річне правило вони не входять. Без календаря (ще в дорозі) — усі періоди: стан може на мить сказати
 * «частково подано» там, де після календаря буде «подано».
 * Без `periodStates` (рядок не за рік) — колишній стан за місяць (`documentState`).
 */
export function documentYearState(
  document: Pick<DocumentSummary, 'sheetStates' | 'workflowMode' | 'periodStates'>,
  started?: ReadonlySet<number>,
): string | null {
  const periods = document.periodStates;
  if (periods === undefined || periods === null) return documentState(document);

  return yearStateOfPeriods(periods, started);
}

/**
 * Річний стан із станів періодів (правило — у `documentYearState`). Те саме зведення для аркуша документа «рік»
 * (`submission-target.monthColumns`: стан аркуша за кожен місяць).
 */
export function yearStateOfPeriods(
  periods: readonly { readonly periodKey: number; readonly state: string }[],
  started?: ReadonlySet<number>,
): string {
  const inYear = started === undefined ? periods : periods.filter((period) => started.has(period.periodKey));
  if (inYear.length > 0 && inYear.every((period) => period.state === 'NoApproval')) return 'NoApproval';

  const cells = inYear.filter((period) => period.state !== 'NoApproval').map((period) => period.state);
  const unknown = cells.find((state) => !(PeriodCellStates as readonly string[]).includes(state));
  if (unknown !== undefined) return unknown;
  if (cells.includes('Rejected')) return 'Rejected';

  const unsent = cells.some((state) => state === 'Draft' || state === 'PartlySubmitted');
  const sent = cells.some((state) => state === 'PartlySubmitted' || state === 'Submitted' || state === 'Approved');
  if (!sent) return 'Draft';
  if (unsent) return 'PartlySubmitted';

  return cells.includes('Submitted') ? 'Submitted' : 'Approved';
}
