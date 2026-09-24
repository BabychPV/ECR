import { useSyncExternalStore } from 'react';
import { sameCellValue } from './cellValue';
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

/**
 * Правка, яку сервер ВІДХИЛИВ (`V-01`), разом із причиною.
 *
 * ⛔ Що ламалося. Відхилена комірка (`abc` у `Int`, `422`) лишалася в сховищі
 * звичайною незбереженою правкою — і автозбереження везло її з КОЖНИМ
 * наступним пакетом. Сервер часткового застосування не робить, тож відхиляв
 * увесь пакет: нові правки ІНШИХ комірок не зберігалися теж і зникали після
 * перезавантаження. Одна помилка оператора зупиняла збереження всього аркуша.
 *
 * ⚠ Тепер відмова ТРИМАЄ правку: вона лишається незбереженою (позначка,
 * причина, «Retry save»), але автозбереження її не везе. Повторюється вона
 * лише явно — кнопкою/Ctrl+S або новою правкою ЦІЄЇ комірки.
 *
 * ⚠ Тримається саме ЗНАЧЕННЯ, яке відхилили (`edit`), а не комірка взагалі:
 * якщо в комірці вже інше значення, відмова його не стосується (`sendableEdits`).
 */
export interface PendingRejection {
  /** Правка в тому вигляді, в якому її відхилили. */
  readonly edit: PendingEdit;
  /** Причина — текст сервера як є. */
  readonly message: string;
  /**
   * `cell` — відпускає лише нова правка цієї комірки (невірне значення);
   * `row` — будь-яка нова правка рядка (`ECR-CALC-0437`: бракує іншої комірки
   * рядка, і саме її заповнення має поїхати РАЗОМ із відхиленою).
   */
  readonly scope: 'cell' | 'row';
}

/** Відмови за зрізами; існують лише для комірок, що досі в `slices`. */
const rejections = new Map<SliceKey, ReadonlyMap<CellKey, PendingRejection>>();

/*
 * ⚠ Порожня мапа — ОДИН екземпляр на весь модуль. `useSyncExternalStore`
 * порівнює знімки за посиланням і кидає «getSnapshot should be cached», якщо
 * знімок створюється щоразу; `new Map()` у геттері дав би саме це — нескінченне
 * перемальовування на кожному зрізі без правок, тобто на більшості.
 */
const Empty: ReadonlyMap<CellKey, PendingEdit> = new Map();
const EmptyRejections: ReadonlyMap<CellKey, PendingRejection> = new Map();

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
  rejections.clear();
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

  pruneRejections(key, edits);
  notify();
}

/**
 * Прибирає відмови, які більше нічого не тримають: комірку зберегли (її немає
 * в `edits`) або в ній уже ІНШЕ значення.
 *
 * ⚠ Одне місце на всі шляхи зміни зрізу — `put`, підтвердження, заміна: відмова,
 * що пережила свою правку, тримала б наступну, якої сервер ще не бачив.
 */
function pruneRejections(key: SliceKey, edits: ReadonlyMap<CellKey, PendingEdit>): void {
  const current = rejections.get(key);
  if (current === undefined) return;

  const next = new Map<CellKey, PendingRejection>();
  for (const [cell, rejection] of current) {
    const edit = edits.get(cell);
    if (edit !== undefined && wasSent(rejection.edit, edit)) next.set(cell, rejection);
  }

  if (next.size === current.size) return;
  if (next.size === 0) rejections.delete(key);
  else rejections.set(key, next);
}

/** Додає або оновлює одну правку зрізу. */
export function putPendingEdit(
  tableInstanceId: number,
  periodKey: number,
  edit: PendingEdit,
): void {
  const key = sliceKey(tableInstanceId, periodKey);
  const next = new Map(pendingSlice(tableInstanceId, periodKey));
  next.set(cellKey(edit), edit);

  // ⛔ `V-01`: нова правка ЦІЄЇ комірки — явна дія, що відпускає відмову (тож і
  // однакове значення, набране вдруге, поїде знову). Відмову рівня рядка
  // (`ECR-CALC-0437`) відпускає будь-яка правка рядка: саме вона, найімовірніше,
  // і заповнює те, чого бракувало.
  releaseRejections(key, (cell, rejection) =>
    cell === cellKey(edit) || (rejection.scope === 'row' && rejection.edit.rowKey === edit.rowKey),
  );

  replacePendingSlice(tableInstanceId, periodKey, next);
}

function releaseRejections(
  key: SliceKey,
  release: (cell: CellKey, rejection: PendingRejection) => boolean,
): void {
  const current = rejections.get(key);
  if (current === undefined) return;

  const next = new Map([...current].filter(([cell, rejection]) => !release(cell, rejection)));
  if (next.size === current.size) return;

  // ⚠ Без `notify()`: єдиний викликач (`putPendingEdit`) одразу замінює зріз,
  // і сповіщення піде звідти — одне на правку, а не два.
  if (next.size === 0) rejections.delete(key);
  else rejections.set(key, next);
}

/**
 * Знімає незбережену правку однієї комірки — оператор повернув у неї
 * збережене значення (`edits.revertsToSaved`). Відмова, якщо була, зникає
 * разом із правкою (`pruneRejections`).
 */
export function discardPendingEdit(
  tableInstanceId: number,
  periodKey: number,
  cell: Pick<PendingEdit, 'rowKey' | 'columnCode'>,
): void {
  const current = pendingSlice(tableInstanceId, periodKey);
  if (!current.has(cellKey(cell))) return;

  const next = new Map(current);
  next.delete(cellKey(cell));
  replacePendingSlice(tableInstanceId, periodKey, next);
}

/**
 * Позначає правки відхиленими (`V-01`).
 *
 * ⚠ Позначається лише те, що ДОСІ лежить у сховищі з тим самим значенням:
 * доки запит летів, оператор міг виправити комірку, і нове значення сервер ще
 * не бачив — тримати його за чужу відмову не можна.
 *
 * @returns Скільки правок позначено.
 */
export function markPendingRejected(
  tableInstanceId: number,
  periodKey: number,
  marks: readonly PendingRejection[],
): number {
  const key = sliceKey(tableInstanceId, periodKey);
  const edits = pendingSlice(tableInstanceId, periodKey);
  const next = new Map(rejections.get(key) ?? EmptyRejections);
  let marked = 0;

  for (const mark of marks) {
    const cell = cellKey(mark.edit);
    const edit = edits.get(cell);
    if (edit === undefined || !wasSent(mark.edit, edit)) continue;

    next.set(cell, mark);
    marked += 1;
  }

  if (marked === 0) return 0;

  rejections.set(key, next);
  notify();

  return marked;
}

/** Відмови зрізу. Посилання стабільне, доки вони не змінювалися. */
export function pendingRejections(
  tableInstanceId: number,
  periodKey: number,
): ReadonlyMap<CellKey, PendingRejection> {
  return rejections.get(sliceKey(tableInstanceId, periodKey)) ?? EmptyRejections;
}

/** Відмови зрізу з підпискою на зміни. */
export function usePendingRejections(
  tableInstanceId: number,
  periodKey: number,
): ReadonlyMap<CellKey, PendingRejection> {
  return useSyncExternalStore(
    subscribePending,
    () => pendingRejections(tableInstanceId, periodKey),
    () => EmptyRejections,
  );
}

/**
 * Правки зрізу, які автозбереження МАЄ ПРАВО везти: усе, крім відхилених
 * (`V-01`).
 *
 * ⛔ Саме цей фільтр і є виправленням: відхилена правка в пакеті — це пакет,
 * який сервер відхилить цілком, хоч би скільки в ньому було правильних правок.
 */
export function sendableEdits(
  tableInstanceId: number,
  periodKey: number,
  edits: readonly PendingEdit[] = [...pendingSlice(tableInstanceId, periodKey).values()],
): PendingEdit[] {
  const held = pendingRejections(tableInstanceId, periodKey);
  if (held.size === 0) return [...edits];

  return edits.filter((edit) => {
    const rejection = held.get(cellKey(edit));

    return rejection === undefined || !wasSent(rejection.edit, edit);
  });
}

/**
 * Прибирає правки перелічених РЯДКІВ зрізу — форма, якою користується
 * збереження: сервер відповідає по рядках, і підтверджувати треба рядок цілком.
 *
 * ⚠ Саме рядки, а не комірки: доки patch летів, користувач міг правити ту саму
 * комірку далі, і прибрати «все, що було в запиті» означало б стерти правку,
 * якої сервер не бачив.
 *
 * ✎ 2026-09-21. «Правку встигли змінити» звірялося ПОСИЛАННЯМ на об'єкт
 * (`keepChangedAfter.get(key) !== edit`), і це був не той критерій. Патч
 * будують і шляхи, які в сховище не пишуть узагалі — вставка з буфера,
 * undo/redo (`DocumentGrid.applyHistory`), безхазяйний зріз
 * (`autosave.saveOrphanSlice`): у знімку `sent` там лежать НОВІ об'єкти, тож
 * будь-яка правка сховища в тому ж рядку виглядала «новішою» і позначку
 * незбереженої не втрачала, хоч сервер щойно записав те саме значення.
 *
 * ⛔ Тепер звіряється ЗНАЧЕННЯ, і саме тому — рядком. Після `e470777a`
 * `decimal` їде рядком, тож одне й те саме число законно існує як `5`, `'5'` і
 * `'5.0000000000'`; `!==` (як і `String(a) !== String(b)`) відповів би
 * «змінилося» на кожну таку пару, і позначка не знімалася б НІКОЛИ.
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
    const newer = keepChangedAfter !== undefined && !wasSent(keepChangedAfter.get(key), edit);

    if (!sent || newer) next.set(key, edit);
  }

  replacePendingSlice(tableInstanceId, periodKey, next);
}

/**
 * Чи саме ЦЕ значення сервер щойно прийняв.
 *
 * ⛔ Комірки, якої в патчі не було (`sent === undefined`), він не стосується
 * взагалі — вона лишається незбереженою. Це не те саме, що «значення інше»:
 * рядок патчу міг нести дві комірки з трьох.
 *
 * ⚠ `isEmpty` звіряється окремо від значення: «тут свідомо порожньо» і «стерти
 * комірку» — різні наміри при однаковому `value: null` (`R-B4`), і зводити їх
 * до однієї рівності означало б підтвердити не те, що надіслали.
 */
function wasSent(sent: PendingEdit | undefined, edit: PendingEdit): boolean {
  if (sent === undefined) return false;

  return sent.isEmpty === edit.isEmpty && sameCellValue(sent.value, edit.value);
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
export function pendingSlices(options: { sendableOnly?: boolean } = {}): readonly {
  readonly tableInstanceId: number;
  readonly periodKey: number;
  readonly edits: readonly PendingEdit[];
}[] {
  return [...slices]
    .map(([key, edits]) => {
      const [tableInstanceId, periodKey] = key.split(':').map(Number) as [number, number];
      const all = [...edits.values()];

      return {
        tableInstanceId,
        periodKey,
        // ⛔ `V-01`: автозбереження й `beforeunload` беруть лише те, що не
        // тримає відмова, — див. `sendableEdits`.
        edits: options.sendableOnly === true ? sendableEdits(tableInstanceId, periodKey, all) : all,
      };
    })
    .filter((slice) => slice.edits.length > 0);
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
  rejections.clear();
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
