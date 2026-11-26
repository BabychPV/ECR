import { useEffect, useSyncExternalStore, type DependencyList, type JSX } from 'react';

/**
 * ✎ DOCUX L7 (план `docs/build/UX-REVIEW-document-screen.md` §3.3): повідомлення екрана документа — ОДНЕ сховище.
 *
 * Людина 26.11: банери «не зникають, займають більшу частину екрана, незрозуміло, що робити». Тепер над сіткою —
 * щонайбільше ОДИН рядок (32 px, `NoticeLine`): найважливіше повідомлення + «+N more»; решта — у вкладці Status
 * інспектора, кожне зі своєю дією. Публікує той, хто знає факт (сторінка, сітка, дії аркуша), — сховище модульне, бо
 * сітки живуть у лінивому чанку (`D-132`).
 *
 * Пріоритет (менше — важливіше): 1 відновлення правок · 2 збій збереження · 3 конфлікт · 4 блок подання / незбережене
 * через обов'язкові · 5 поділ рядків, передача місяця · 6 проблеми розрахунку · 7 чужа зміна.
 */
export interface Notice {
  readonly id: string;
  readonly priority: number;
  /** Розмітка ОДНОГО рядка (заввишки 32 px) — і в рядку над сіткою, і в переліку Status. */
  readonly render: () => JSX.Element;
}

const notices = new Map<string, Notice & { readonly seq: number }>();
const listeners = new Set<() => void>();
let seq = 0;
let snapshot: readonly Notice[] = [];

function rebuild(): void {
  snapshot = [...notices.values()].sort((a, b) => a.priority - b.priority || a.seq - b.seq);
  for (const listener of listeners) listener();
}

export function publishNotice(notice: Notice): void {
  const known = notices.get(notice.id);
  seq += 1;
  notices.set(notice.id, { ...notice, seq: known?.seq ?? seq });
  rebuild();
}

export function withdrawNotice(id: string): void {
  if (notices.delete(id)) rebuild();
}

/** Для тестів: забути все. */
export function resetNotices(): void {
  notices.clear();
  rebuild();
}

function subscribe(listener: () => void): () => void {
  listeners.add(listener);
  return () => {
    listeners.delete(listener);
  };
}

function current(): readonly Notice[] {
  return snapshot;
}

/** Повідомлення за пріоритетом (найважливіше першим), з підпискою. */
export function useNotices(): readonly Notice[] {
  return useSyncExternalStore(subscribe, current, current);
}

/**
 * Опублікувати повідомлення, поки `notice` не `null`; знімається, щойно стало `null` або компонент розмонтовано.
 * `deps` — усе, від чого залежить `render` (як у `useEffect`).
 */
export function usePublishNotice(id: string, notice: Omit<Notice, 'id'> | null, deps: DependencyList): void {
  useEffect(() => {
    if (notice === null) withdrawNotice(id);
    else publishNotice({ id, ...notice });
  }, [id, notice === null, ...deps]);
  useEffect(() => () => withdrawNotice(id), [id]);
}
