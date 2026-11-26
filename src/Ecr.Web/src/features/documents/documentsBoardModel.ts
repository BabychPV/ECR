import type { DocumentSummary } from '@/api/types';
import { documentYearState } from './documentSheets';

/** Стовпець подання «Board» (`UI-40`). */
export type BoardColumnId = 'Draft' | 'PartlySubmitted' | 'Submitted' | 'Rework' | 'Approved' | 'NoApproval' | 'NoState';

export interface BoardColumn<T> {
  readonly id: BoardColumnId;
  /** Стани документа за рік (`documentYearState`), що падають у стовпець. */
  readonly states: readonly string[];
  /** Стовпець «чекає уваги»: лічильник кольором лише тут і лише коли не нуль (KIT §1 п.3). */
  readonly attention: boolean;
  readonly items: readonly T[];
}

/**
 * Стовпці дошки — ті самі стани, що й лічильники над нею (`DocumentListSummaryStrip`, за рік):
 * Draft · Partly submitted · Waiting for approval · Returned or rejected · Approved.
 *
 * ✎ b14 documents-board-yearly (людина 25.11: «4 Draft · 1 Partly submitted» над дошкою, а всі 5 карток — у «No state
 * for this period»): «частково подано» — власний стовпець, як і власне число на смузі; доти воно ховалося в чернетках.
 *
 * ⚠ `Returned` у переліку станів лишається: стан, якого правило не знає, повертається як є, і сервер, що почне його
 * віддавати, потрапить у «Returned or rejected», а не в «без стану».
 */
const Columns: readonly Omit<BoardColumn<never>, 'items'>[] = [
  { id: 'Draft', states: ['Draft'], attention: false },
  { id: 'PartlySubmitted', states: ['PartlySubmitted'], attention: false },
  { id: 'Submitted', states: ['Submitted'], attention: false },
  { id: 'Rework', states: ['Returned', 'Rejected'], attention: true },
  { id: 'Approved', states: ['Approved'], attention: false },
];

type BoardSource = Pick<DocumentSummary, 'sheetStates' | 'workflowMode' | 'periodStates' | 'projectId'>;

/**
 * Розкладає документи сторінки по стовпцях дошки, зберігаючи порядок переліку.
 *
 * ⛔ Стан — ЛИШЕ з відповіді сервера: `documentYearState` зводить стани періодів (`periodStates`, видимі аркуші, P1)
 * тим самим правилом, яким сервер рахує лічильники за рік — тож стовпець і число над ним кажуть одне.
 *
 * @param startedOf розпочаті періоди проєкту (календар); `undefined` — календар ще в дорозі.
 *
 * ⚠ Стовпець «без стану» лишається лише для стану, якого правило не знає (або рядка без станів зовсім): сховати такі
 * документи означало б сказати «їх немає»; тому він з'являється, лише коли в ньому щось є.
 */
export function boardColumns<T extends BoardSource>(
  documents: readonly T[],
  startedOf: (projectId: number) => ReadonlySet<number> | undefined = () => undefined,
): BoardColumn<T>[] {
  const stateOf = new Map(documents.map((document) => [document, documentYearState(document, startedOf(document.projectId))]));
  const columns: BoardColumn<T>[] = Columns.map((column) => ({
    ...column,
    items: documents.filter((document) => {
      const state = stateOf.get(document) ?? null;

      return state !== null && column.states.includes(state);
    }),
  }));

  // ✎ B147 (B75): «без погодження» — не чернетка й не «без стану»: власний стовпець, лише коли в ньому щось є.
  const noApproval = documents.filter((document) => stateOf.get(document) === 'NoApproval');
  if (noApproval.length > 0) columns.push({ id: 'NoApproval', states: ['NoApproval'], attention: false, items: noApproval });

  const placed = new Set(columns.flatMap((column) => column.items));
  const rest = documents.filter((document) => !placed.has(document));
  if (rest.length > 0) columns.push({ id: 'NoState', states: [], attention: false, items: rest });

  return columns;
}

/** Подання переліку документів в адресі (`?view=board`); усе інше — таблиця. */
export type DocumentsView = 'table' | 'board';

export function parseDocumentsView(value: string | null): DocumentsView {
  return value === 'board' ? 'board' : 'table';
}
