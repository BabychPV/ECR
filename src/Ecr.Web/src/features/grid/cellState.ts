import type { ColumnDto, TableSliceDto } from '@/api/types';
import { cellKey, decide } from './permissions';

/**
 * Стан комірки (`ФВ-14.18`, `D-128`).
 *
 * ⛔ Порядок значень у типі — це порядок ПРІОРИТЕТУ, і він не довільний.
 * Комірка буває в кількох станах одночасно (незбережена правка в округленому
 * значенні; обчислена і водночас лише для читання), а показати можна один.
 * Обраний порядок відповідає одному правилу: **показуємо те, що користувач
 * може змінити просто зараз**.
 */
export type CellStateName = 'orphaned' | 'dirty' | 'rounded' | 'calculated' | 'readOnly';

/** Стани в порядку спадання пріоритету. */
const Priority: readonly CellStateName[] = [
  // Осиротілий рядок першим: він блокує подання документа цілком (`ФВ-8.13`),
  // і дізнатися про це з відмови `ECR-SUB-4221` — це дізнатися надто пізно.
  'orphaned',

  // Незбережена правка: єдиний стан, який зникне сам, щойно натиснути
  // «Зберегти». Ховати його за «обчислена» означало б втратити правку мовчки.
  'dirty',

  // Округлення при вставці (`D-116`): показується лише на змінених комірках,
  // інакше лічильник «округлено N значень» втрачає сенс.
  'rounded',

  // Обчислена: не заборонена, але зміниться при наступному перерахунку.
  'calculated',

  // Лише читання — найширший і найтихіший стан, тому останній.
  'readOnly',
];

/** Що клієнт знає про комірку понад те, що прийшло у зрізі. */
export interface LocalCellFlags {
  /** Ключі комірок із незбереженими правками. */
  readonly dirty: ReadonlySet<string>;

  /** Ключі комірок, значення яких округлилося при вставці. */
  readonly rounded: ReadonlySet<string>;
}

/** Порожній набір локальних позначок — для подань лише для читання. */
export const NoLocalFlags: LocalCellFlags = { dirty: new Set(), rounded: new Set() };

/**
 * Визначає стан комірки.
 *
 * ⚠ Чиста функція над зрізом і двома множинами — саме тому її можна перевірити
 * без монтування grid. Стан, обчислений усередині колбека RevoGrid,
 * перевірявся б лише через DOM веб-компонента, тобто на практиці ніколи.
 */
export function cellStateOf(
  slice: TableSliceDto,
  rowKey: string,
  column: ColumnDto,
  flags: LocalCellFlags = NoLocalFlags,
): CellStateName | null {
  const key = cellKey(rowKey, column.code);
  const decision = decide(slice, rowKey, column);
  const row = slice.rows.find((candidate) => candidate.rowKey === rowKey);

  const active: Record<CellStateName, boolean> = {
    orphaned: row?.isOrphaned === true,
    dirty: flags.dirty.has(key),
    rounded: flags.rounded.has(key),

    // ⚠ Обчислена комірка розпізнається за ПРИЧИНОЮ відмови, а не за окремим
    // прапорцем: причину рахує сервер, і другий прапорець довелося б тримати
    // синхронним із нею — тобто рано чи пізно він би розійшовся.
    calculated: decision.reason === 'CalculatedCell',
    readOnly: !decision.editable,
  };

  return Priority.find((state) => active[state]) ?? null;
}

/**
 * Класи CSS для стану.
 *
 * ⚠ Повертається рядок, а не об'єкт: `cellProperties` RevoGrid приймає саме
 * `class` рядком, і складання його на місці виклику розповзлося б по колонках.
 */
export function cellStateClass(state: CellStateName | null): string {
  if (state === null) return '';

  return `ecr-cell ecr-cell--${kebab(state)}`;
}

/** `readOnly` → `read-only`; решта імен уже в потрібному вигляді. */
function kebab(state: CellStateName): string {
  return state.replace(/[A-Z]/g, (letter) => `-${letter.toLowerCase()}`);
}
