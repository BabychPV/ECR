/**
 * ✎ g3 (макет `projects-documents-1108.html`, `.ms` / `CELL`): стан клітинки смуги 12 місяців — ОДНЕ з п'яти
 * значень легенди (Open · Submitted · Approved · Locked · Not open yet) плюс «Returned» для повернутого.
 *
 * ⛔ Корінь дефекту «смуга малює лише порожні рамки»: клітинка брала тільки стан ПОГОДЖЕННЯ документа за місяць
 * (`periodStates`), а в ньому відкритий, заблокований і ще не відкритий місяць — однаково `Draft` (сервер віддає
 * `Scheduled`-місяці як `Draft`), і `Draft` має той самий тон, що й `Approved`. Тож у році, де майже все — чернетки,
 * 12 клітинок рядка виглядали однаково й нічого не казали. Тепер неподаний місяць бере стан ПЕРІОДУ з календаря
 * проєкту (`GET /projects/{id}/periods`): відкритий, заблокований чи ще не відкритий.
 */
export type StripCell = 'open' | 'late' | 'submitted' | 'approved' | 'returned' | 'locked' | 'notOpen' | 'unknown';

/** Стан клітинки з (стан погодження документа за місяць, стан періоду проєкту). */
export function stripCell(documentState: string | null, periodState: string | null): StripCell {
  if (documentState === 'Approved') return 'approved';
  if (documentState === 'Submitted' || documentState === 'PartlySubmitted') return 'submitted';
  if (documentState === 'Rejected') return 'returned';

  switch (periodState) {
    case 'Open':
      return 'open';
    // ✎ F13 g3 (09.11): Grace — окремий стан «Late (flagged)», як на вкладці Periods (перевідкритий місяць — теж
    // Grace); доти смуга казала «Open», а Periods — «Late».
    case 'Grace':
      return 'late';
    case 'Closed':
      return 'locked';
    case 'Scheduled':
      return 'notOpen';
    default:
      return 'unknown';
  }
}

/** Порядок легенди — як у макеті (O, S, A, L, N), «Late» одразу за «Open» і «Returned» наприкінці. */
export const StripLegend: readonly StripCell[] = ['open', 'late', 'submitted', 'approved', 'locked', 'notOpen', 'returned'];
