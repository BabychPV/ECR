import { useEffect, useSyncExternalStore, type JSX } from 'react';

/**
 * ✎ DOCUX L7 (план `docs/build/UX-REVIEW-document-screen.md` §3.3): повідомлення екрана документа — ОДНЕ сховище.
 *
 * Людина 26.11: банери «не зникають, займають більшу частину екрана, незрозуміло, що робити». Тепер над сіткою — рядок
 * повідомлень (`NoticeLine`): найважливіше повідомлення + «+N more»; повний вміст кожного — у вкладці Status інспектора.
 * Публікує той, хто знає факт (сторінка, сітка, дії аркуша), — сховище модульне, бо сітки в лінивому чанку (`D-132`).
 *
 * Пріоритет (менше — важливіше): 1 відновлення правок · 2 збій збереження · 3 конфлікт · 4 блок подання / незбережене
 * через обов'язкові, чужий перехід стану аркуша · 5 поділ рядків, передача місяця, чужа зміна даних · 6 проблеми розрахунку ·
 * 8 довідка (округлено, зауваження збереження).
 *
 * ⛔ `blocking` — повідомлення, що зупиняє наступну дію людини (збій збереження, конфлікт): у рядку воно ЗАВЖДИ видиме
 * власним рядком, а не за «+N more».
 *
 * ⚠ Рядок є лише на сторінці документа. Компонент, змонтований деінде (інший екран, одиничний тест), рядка не має —
 * `useNoticeHost` тоді каже «не прийнято», і компонент показує свій вміст на місці, як і раніше.
 */
export interface Notice {
  readonly id: string;
  readonly priority: number;
  /** Зупиняє наступну дію — у рядку завжди видиме. */
  readonly blocking?: boolean;
  /** ОДИН рядок (32 px) — у рядку над сіткою. */
  readonly render: () => JSX.Element;
  /** Повний вміст (перелік, кнопки) — у вкладці Status; без нього — той самий рядок. */
  readonly renderFull?: () => JSX.Element;
}

const notices = new Map<string, Notice & { readonly seq: number }>();
const listeners = new Set<() => void>();
let seq = 0;
let snapshot: readonly Notice[] = [];
let lines = 0;

function notify(): void {
  for (const listener of listeners) listener();
}

function rebuild(): void {
  snapshot = [...notices.values()].sort((a, b) => a.priority - b.priority || a.seq - b.seq);
  notify();
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
  // ⚠ Лічильник рядків не скидається: його веде сам `NoticeLine` (монтування/розмонтування).
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

function hosted(): boolean {
  return lines > 0;
}

/** Повідомлення за пріоритетом (найважливіше першим), з підпискою. */
export function useNotices(): readonly Notice[] {
  return useSyncExternalStore(subscribe, current, current);
}

/** Рядок повідомлень змонтовано (сторінка документа) — він показує повідомлення; викликає `NoticeLine`. */
export function useRegisterNoticeLine(): void {
  useEffect(() => {
    lines += 1;
    notify();
    return () => {
      lines -= 1;
      notify();
    };
  }, []);
}

/** Чи є рядок повідомлень, що прийме повідомлення, з підпискою. */
export function useNoticeLineHosted(): boolean {
  return useSyncExternalStore(subscribe, hosted, hosted);
}

/**
 * Опублікувати повідомлення, поки `notice` не `null`, і зняти, щойно стало `null` чи компонент розмонтовано.
 * Публікується після КОЖНОГО рендеру того, хто публікує: `render` бачить його свіжий стан (без списку залежностей).
 */
export function usePublishNotice(id: string, notice: Omit<Notice, 'id'> | null): void {
  useEffect(() => {
    if (notice === null) withdrawNotice(id);
    else publishNotice({ id, ...notice });
  });
  useEffect(() => () => withdrawNotice(id), [id]);
}

/**
 * Те саме, але лише коли рядок повідомлень є: повертає `true` — повідомлення прийнято рядком (компонент свого блоку
 * не малює); `false` — рядка немає, компонент показує вміст на місці, як і раніше.
 */
export function useNoticeHost(id: string, notice: Omit<Notice, 'id'> | null): boolean {
  const host = useNoticeLineHosted();
  usePublishNotice(id, host ? notice : null);

  return host && notice !== null;
}
