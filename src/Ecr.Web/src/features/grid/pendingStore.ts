import { useSyncExternalStore } from 'react';
import type { PendingEdit } from './useCellPatch';

/**
 * Незбережені правки належать ДОКУМЕНТУ, а не сітці (`D14-12`).
 *
 * ⛔ Що ламалося. `pending` жив у `useState` всередині `DocumentGrid`, а
 * `SheetTables` монтує сітки за прокруткою — тобто на аркуші їх N, у кожної
 * власний стан, і жоден зовнішній код про нього не знає. Наслідки цього три, і
 * кожен коштує даних:
 *   — перемикання аркуша скасовувало дебаунс (`DocumentGrid.tsx`,
 *     cleanup `useEffect`), і правка молодша за 500 мс зникала МОВЧКИ (`W-02`);
 *   — `beforeunload` бачив лише ту сітку, у якій стояв курсор;
 *   — `useBlocker` на виході з документа неможливий у принципі: питати «є
 *     незбережені зміни?» нікому, бо лічильника поза компонентом не існує.
 *
 * ⚠ Сховище модульне, а не React-контекст, і це та сама причина, що в
 * `app/staleVersion.tsx` та `shared/i18n`: стан має пережити розмонтування
 * компонента, який його породив. Контекст цього не вміє за визначенням —
 * він помирає разом із провайдером.
 *
 * ⛔ Сховище прив'язане до ОДНОГО документа. Ключ зрізу
 * (`tableInstanceId:periodKey`) унікальний у межах документа, але не між
 * документами, і без явного `openDocument` правки одного звіту тихо
 * дописалися б до іншого — найдорожчий різновид помилки в цій системі.
 *
 * ⚠ Чого тут НЕМАЄ навмисно: надсилання. Сховище зберігає намір, а хто і коли
 * шле `PATCH` — справа `DocumentPage` (крок 2 `D14-12`). Інакше сховище знало
 * б про мережу, і перевірити його без сервера було б нічим.
 */

/** Ключ зрізу в межах документа. */
type SliceKey = string;

/** Ключ комірки в межах зрізу. */
type CellKey = string;

function sliceKey(tableInstanceId: number, periodKey: number): SliceKey {
  return `${String(tableInstanceId)}:${String(periodKey)}`;
}

/** Ключ комірки — той самий, що в `DocumentGrid` (`rowKey:columnCode`). */
export function cellKey(edit: Pick<PendingEdit, 'rowKey' | 'columnCode'>): CellKey {
  return `${edit.rowKey}:${edit.columnCode}`;
}

/** Документ, чиї правки зараз у сховищі; `null` — жодного. */
let openDocumentId: number | null = null;

/** Правки за зрізами. Порожній зріз у мапі не лишається (див. `replace`). */
const slices = new Map<SliceKey, ReadonlyMap<CellKey, PendingEdit>>();

const listeners = new Set<() => void>();

/*
 * ⚠ Порожня мапа — ОДИН екземпляр на весь модуль. `useSyncExternalStore`
 * порівнює знімки за посиланням і кидає «getSnapshot should be cached», якщо
 * знімок створюється щоразу; `new Map()` у геттері дав би саме це — нескінченне
 * перемальовування на кожному зрізі без правок, тобто на більшості.
 */
const Empty: ReadonlyMap<CellKey, PendingEdit> = new Map();

function notify(): void {
  for (const listener of listeners) listener();
}

/** Підписка на будь-яку зміну сховища; повертає відписку. */
export function subscribePending(listener: () => void): () => void {
  listeners.add(listener);

  return () => {
    listeners.delete(listener);
  };
}

/**
 * Оголошує, чиї правки тепер у сховищі.
 *
 * ⛔ Зміна документа СТИРАЄ накопичене. Це не втрата: сховище доживає рівно до
 * виходу зі сторінки, а вихід із незбереженими правками перехоплює `useBlocker`
 * (крок 3). Лишити чужі правки означало б надіслати їх у наступний документ.
 *
 * ⚠ Повторний виклик із тим самим ідентифікатором не робить НІЧОГО: `DocumentPage`
 * кличе це в `useEffect`, а той у `StrictMode` виконується двічі — стирання
 * «про всяк випадок» знищило б правки на другому проході.
 */
export function openDocument(documentId: number): void {
  if (openDocumentId === documentId) return;

  openDocumentId = documentId;
  slices.clear();
  notify();
}

/** Документ, чиї правки зараз у сховищі. Для перевірок і тестів. */
export function currentDocumentId(): number | null {
  return openDocumentId;
}

/** Правки одного зрізу. Посилання стабільне, доки зріз не змінювався. */
export function pendingSlice(
  tableInstanceId: number,
  periodKey: number,
): ReadonlyMap<CellKey, PendingEdit> {
  return slices.get(sliceKey(tableInstanceId, periodKey)) ?? Empty;
}

/**
 * Замінює правки зрізу цілком.
 *
 * ⚠ Порожній зріз ВИДАЛЯЄТЬСЯ з мапи, а не лишається порожнім: інакше
 * `pendingCount()` довелося б обходити сотні мертвих ключів, а `hasPending()`
 * відповідав би «так» на документ, у якому все збережено.
 */
export function replacePendingSlice(
  tableInstanceId: number,
  periodKey: number,
  edits: ReadonlyMap<CellKey, PendingEdit>,
): void {
  const key = sliceKey(tableInstanceId, periodKey);
  const current = slices.get(key) ?? Empty;

  // Та сама мапа — та сама подія: зайве сповіщення перемальовує всі сітки.
  if (current === edits) return;

  if (edits.size === 0) {
    if (!slices.has(key)) return;
    slices.delete(key);
  } else {
    slices.set(key, edits);
  }

  notify();
}

/** Додає або оновлює одну правку зрізу. */
export function putPendingEdit(
  tableInstanceId: number,
  periodKey: number,
  edit: PendingEdit,
): void {
  const next = new Map(pendingSlice(tableInstanceId, periodKey));
  next.set(cellKey(edit), edit);

  replacePendingSlice(tableInstanceId, periodKey, next);
}

/**
 * Прибирає правки перелічених РЯДКІВ зрізу — форма, якою користується
 * збереження: сервер відповідає по рядках, і підтверджувати треба рядок цілком.
 *
 * ⚠ Саме рядки, а не комірки: доки patch летів, користувач міг правити ту саму
 * комірку далі, і прибрати «все, що було в запиті» означало б стерти правку,
 * якої сервер не бачив.
 */
export function discardPendingRows(
  tableInstanceId: number,
  periodKey: number,
  rowKeys: Iterable<string>,
  keepChangedAfter?: ReadonlyMap<CellKey, PendingEdit>,
): void {
  const current = pendingSlice(tableInstanceId, periodKey);
  if (current.size === 0) return;

  const rows = new Set(rowKeys);
  const next = new Map<CellKey, PendingEdit>();

  for (const [key, edit] of current) {
    const sent = rows.has(edit.rowKey);
    const newer = keepChangedAfter !== undefined && keepChangedAfter.get(key) !== edit;

    if (!sent || newer) next.set(key, edit);
  }

  replacePendingSlice(tableInstanceId, periodKey, next);
}

/** Скільки незбережених комірок у ВСЬОМУ документі. */
export function pendingCount(): number {
  let total = 0;
  for (const slice of slices.values()) total += slice.size;

  return total;
}

/** Чи є в документі бодай одна незбережена правка. */
export function hasPending(): boolean {
  return slices.size > 0;
}

/** Зрізи, у яких є незбережені правки, — для надсилання їх усіх разом. */
export function pendingSlices(): readonly {
  readonly tableInstanceId: number;
  readonly periodKey: number;
  readonly edits: readonly PendingEdit[];
}[] {
  return [...slices].map(([key, edits]) => {
    const [tableInstanceId, periodKey] = key.split(':');

    return {
      tableInstanceId: Number(tableInstanceId),
      periodKey: Number(periodKey),
      edits: [...edits.values()],
    };
  });
}

/**
 * Скидає сховище повністю — вихід із документа і тести.
 *
 * ⚠ Модульний стан переживає розмонтування, тож без цього наступний тест
 * бачив би правки попереднього (той самий урок, що `resetStaleVersion`).
 */
export function resetPending(): void {
  openDocumentId = null;
  slices.clear();
  notify();
}

/** Правки зрізу з підпискою на зміни. */
export function usePendingSlice(
  tableInstanceId: number,
  periodKey: number,
): ReadonlyMap<CellKey, PendingEdit> {
  return useSyncExternalStore(
    subscribePending,
    () => pendingSlice(tableInstanceId, periodKey),
    () => Empty,
  );
}

/**
 * Кількість незбережених правок документа з підпискою.
 *
 * ⚠ Знімок — ЧИСЛО, а не об'єкт: `useSyncExternalStore` порівнює за
 * посиланням, і будь-який складений знімок довелося б кешувати вручну. Той
 * самий прийом і з тієї ж причини, що в `shared/i18n`.
 */
export function usePendingCount(): number {
  return useSyncExternalStore(subscribePending, pendingCount, () => 0);
}
