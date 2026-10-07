import type { DocumentSummary } from '@/api/types';
import { documentState } from './documentSheets';

/** Стовпець подання «Board» (`UI-40`). */
export type BoardColumnId = 'Draft' | 'Submitted' | 'Rework' | 'Approved' | 'NoState';

export interface BoardColumn<T> {
  readonly id: BoardColumnId;
  /** Стани документа (найгірший стан видимих аркушів), що падають у стовпець. */
  readonly states: readonly string[];
  /** Стовпець «чекає уваги»: лічильник кольором лише тут і лише коли не нуль (KIT §1 п.3). */
  readonly attention: boolean;
  readonly items: readonly T[];
}

/**
 * Стовпці дошки — дзеркало макета (`screens-work.js`, `COLS`): Draft · Waiting for approval ·
 * Returned or rejected · Approved.
 *
 * ⚠ `Returned` у переліку станів лишається, хоч `documentState` його не рахує окремо: стан, якого
 * правило не знає, повертається як є (`documentSheets.ts`), і сервер, що почне його віддавати,
 * потрапить у «Returned or rejected», а не в «без стану».
 */
const Columns: readonly Omit<BoardColumn<never>, 'items'>[] = [
  { id: 'Draft', states: ['Draft'], attention: false },
  { id: 'Submitted', states: ['Submitted'], attention: false },
  { id: 'Rework', states: ['Returned', 'Rejected'], attention: true },
  { id: 'Approved', states: ['Approved'], attention: false },
];

/**
 * Розкладає документи сторінки по стовпцях дошки, зберігаючи порядок переліку.
 *
 * ⛔ Стан — ЛИШЕ з відповіді сервера (`documentState` — найгірший стан ВИДИМИХ аркушів, P1
 * «приховані аркуші»): дошка не вигадує стану й не рахує аркушів понад те, що прийшло.
 *
 * ⚠ П'ятий стовпець «без стану» — не з макета, а з `D-93`: без обраного періоду станів немає
 * зовсім, а невідомий стан нікуди не належить. Сховати такі документи означало б сказати «їх
 * немає»; тому стовпець з'являється, лише коли в ньому щось є.
 */
export function boardColumns<T extends Pick<DocumentSummary, 'sheetStates'>>(
  documents: readonly T[],
): BoardColumn<T>[] {
  const columns: BoardColumn<T>[] = Columns.map((column) => ({
    ...column,
    items: documents.filter((document) => {
      const state = documentState(document);

      return state !== null && column.states.includes(state);
    }),
  }));

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
