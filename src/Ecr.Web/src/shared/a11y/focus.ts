import { useCallback, useEffect, useRef, useState, type RefObject } from 'react';

/**
 * Утримання фокуса клавіатури там, де Mantine його губить (WCAG 2.4.3, `ФВ-14.16`).
 *
 * ⚠ Три місця, де фокус тихо падає на `<body>`, і всі три — не вада екрана, а
 * поведінка бібліотеки чи браузера:
 *   — `Button loading` ставить `disabled`: натиснута кнопка втрачає фокус саме
 *     тоді, коли людина чекає на результат, і після відповіді `Tab` починається
 *     з початку сторінки;
 *   — `Modal` повертає фокус лише на зміну `opened`; діалог, змонтований за
 *     умовою (`{editing && <Modal opened …/>}`), розмонтовується без неї;
 *   — прибраний рядок списку забирає з собою кнопку, на якій був фокус.
 *
 * ⛔ Фокус повертається лише тоді, коли він справді загубився (`<body>` або
 * від'єднаний вузол): якщо людина тим часом перейшла деінде, її не смикаємо.
 */
export function focusIsLost(): boolean {
  const active = document.activeElement;
  return active === null || active === document.body || !active.isConnected;
}

/** Фокус на вузол наступним тактом — після того, як діалог відпустить пастку. */
export function focusSoon(target: HTMLElement | null | undefined, onlyIfLost = false): void {
  if (target === null || target === undefined) return;
  window.setTimeout(() => {
    if (!target.isConnected) return;
    if (onlyIfLost && !focusIsLost()) return;
    target.focus();
  }, 0);
}

/**
 * Для діалогу, змонтованого за умовою: запам'ятовує, де був фокус у мить
 * відкриття, і повертає його туди при розмонтуванні.
 */
export function useReturnFocusOnUnmount(): void {
  // ⚠ Ініціалізатор стану, а не ефект: ефект спрацює вже ПІСЛЯ того, як
  // діалог перехопить фокус, і запам'ятав би вузол усередині самого діалогу.
  const [origin] = useState<HTMLElement | null>(() => {
    const active = document.activeElement;
    return active instanceof HTMLElement && active !== document.body ? active : null;
  });

  useEffect(() => () => focusSoon(origin, true), [origin]);
}

/**
 * Кнопка, що на час запиту стає `loading`: `arm()` у `onClick`, і коли `busy`
 * зникне, фокус повернеться на кнопку, якщо за цей час він загубився.
 *
 * ⚠ Для кнопок у рядках таблиці (одна мутація на всі рядки) — `arm(event.currentTarget)`:
 * повертається саме натиснута кнопка, а `ref` не потрібен.
 */
export function useFocusAfterBusy<T extends HTMLElement = HTMLButtonElement>(
  busy: boolean,
): { readonly ref: RefObject<T | null>; readonly arm: (target?: HTMLElement) => void } {
  const ref = useRef<T | null>(null);
  const armed = useRef<HTMLElement | 'ref' | null>(null);

  useEffect(() => {
    if (busy || armed.current === null) return;
    const target = armed.current === 'ref' ? ref.current : armed.current;
    armed.current = null;
    focusSoon(target, true);
  }, [busy]);

  const arm = useCallback((target?: HTMLElement) => {
    armed.current = target ?? 'ref';
  }, []);

  return { ref, arm };
}

/**
 * Список із «Додати» / «Прибрати» (правила, нові поля): після додавання фокус —
 * у перше поле нового (останнього) рядка з атрибутом `data-focus-row`, після
 * прибирання — на «Додати».
 *
 * ⚠ Без цього «Додати» лишає фокус на кнопці, і новий рядок треба шукати
 * `Tab`-ом через увесь список; «Прибрати» забирає разом із рядком і кнопку,
 * на якій стояв фокус, — і він падає на `<body>`.
 */
export function useListFocus(length: number): {
  readonly container: RefObject<HTMLDivElement | null>;
  readonly addButton: RefObject<HTMLButtonElement | null>;
  readonly added: () => void;
  readonly removed: () => void;
} {
  const container = useRef<HTMLDivElement | null>(null);
  const addButton = useRef<HTMLButtonElement | null>(null);
  const pending = useRef<'added' | 'removed' | null>(null);

  useEffect(() => {
    const what = pending.current;
    pending.current = null;

    if (what === 'added') {
      const rows = container.current?.querySelectorAll('[data-focus-row]') ?? [];
      const last = rows[rows.length - 1];
      last
        ?.querySelector<HTMLElement>('input:not([disabled]):not([type="hidden"]), select:not([disabled]), textarea:not([disabled])')
        ?.focus();
    } else if (what === 'removed') {
      addButton.current?.focus();
    }
  }, [length]);

  const added = useCallback(() => {
    pending.current = 'added';
  }, []);
  const removed = useCallback(() => {
    pending.current = 'removed';
  }, []);

  return { container, addButton, added, removed };
}
