import { useSyncExternalStore } from 'react';

/**
 * ✎ D-6: ціль рішень над місяцем річного аркуша — спільна для рядка дій (`useSheetActions`) і сітки (заголовок
 * місячної колонки). Живуть вони в різних гілках дерева (сітка — лінивий чанк), тож зв'язок — маленьке сховище за
 * ключем `документ × аркуш`, а не пропи.
 *
 * - ВХІД (`picks`): місяць, який людина вибрала (▾ чи клік по позначці стану колонки). Рядок дій перевіряє його
 *   проти місяців, що справді чекають дії, — зайвий вибір ігнорується.
 * - ВИХІД (`view`): що опублікував рядок дій — для яких місяців колонку можна зробити ціллю (`eligible`) і яка колонка
 *   є ціллю зараз (`highlight`; підсвічується в заголовку).
 *
 * ⚠ Модуль без залежностей від сітки й від `SheetActions`: його читають обидва, і цикл імпортів тут був би помилкою.
 */

/** Для якого рішення місяць доступний цій людині: `Submitted` — затвердити/відхилити, `Approved` — повернути. */
export type DecisionKind = 'Submitted' | 'Approved';

/** Опубліковане рядком дій. */
export interface DecisionView {
  /** Місяці, які цій людині можна зробити ціллю рішення. */
  readonly eligible: ReadonlyMap<number, DecisionKind>;
  /** Місяць-ціль, що показується в заголовку колонки; `null` — підсвічувати нічого. */
  readonly highlight: number | null;
}

interface Entry {
  picks: { decide: number | null; reopen: number | null };
  view: DecisionView;
}

const Empty: DecisionView = { eligible: new Map(), highlight: null };

const entries = new Map<string, Entry>();
const listeners = new Set<() => void>();
let version = 0;

const keyOf = (documentId: number, sheetDefId: number): string => `${String(documentId)}:${String(sheetDefId)}`;

function entryOf(key: string): Entry {
  let entry = entries.get(key);
  if (entry === undefined) {
    entry = { picks: { decide: null, reopen: null }, view: Empty };
    entries.set(key, entry);
  }

  return entry;
}

function changed(): void {
  version += 1;
  listeners.forEach((listener) => listener());
}

/** Людина вибрала місяць (▾ чи позначка колонки): `Submitted` — ціль Approve/Reject, `Approved` — ціль Return for edits. */
export function pickDecisionMonth(documentId: number, sheetDefId: number, periodKey: number, kind: DecisionKind): void {
  const entry = entryOf(keyOf(documentId, sheetDefId));
  const field = kind === 'Submitted' ? 'decide' : 'reopen';
  if (entry.picks[field] === periodKey) return;

  entry.picks = { ...entry.picks, [field]: periodKey };
  changed();
}

/** Вибір людини для аркуша: `decide` / `reopen`. */
export function decisionPicks(documentId: number, sheetDefId: number): { readonly decide: number | null; readonly reopen: number | null } {
  return entries.get(keyOf(documentId, sheetDefId))?.picks ?? { decide: null, reopen: null };
}

/** Рядок дій публікує, які місяці доступні й який — ціль. Однаковий вигляд — без сповіщення (ефект не зациклюється). */
export function publishDecisionView(documentId: number, sheetDefId: number, view: DecisionView): void {
  const entry = entryOf(keyOf(documentId, sheetDefId));
  const same =
    entry.view.highlight === view.highlight &&
    entry.view.eligible.size === view.eligible.size &&
    [...view.eligible].every(([key, kind]) => entry.view.eligible.get(key) === kind);
  if (same) return;

  entry.view = view;
  changed();
}

/** Рядок дій зник (інший документ чи аркуш): вибір і вигляд забуваються. */
export function clearDecisionTarget(documentId: number, sheetDefId: number): void {
  if (entries.delete(keyOf(documentId, sheetDefId))) changed();
}

function subscribe(listener: () => void): () => void {
  listeners.add(listener);

  return () => listeners.delete(listener);
}

/** Знімок для `useSyncExternalStore`: версія змінюється на кожну зміну сховища. */
function snapshot(): number {
  return version;
}

/** Вибір людини; перерахунок — на зміну сховища. */
export function useDecisionPicks(documentId: number, sheetDefId: number): { readonly decide: number | null; readonly reopen: number | null } {
  useSyncExternalStore(subscribe, snapshot, snapshot);

  return decisionPicks(documentId, sheetDefId);
}

/** Опублікований вигляд цілі для сітки. */
export function useDecisionView(documentId: number, sheetDefId: number | undefined): DecisionView {
  useSyncExternalStore(subscribe, snapshot, snapshot);

  return sheetDefId === undefined ? Empty : (entries.get(keyOf(documentId, sheetDefId))?.view ?? Empty);
}
