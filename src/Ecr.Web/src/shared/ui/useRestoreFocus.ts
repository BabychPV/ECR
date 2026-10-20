import { useEffect, useLayoutEffect, useRef } from 'react';

/**
 * Кнопка меню, якому належить пункт `element` (Mantine: випадний список має `role="menu"` і `aria-labelledby` = id кнопки).
 * `null` - елемент не в меню.
 */
function menuTriggerOf(element: HTMLElement): HTMLElement | null {
  const labelledBy = element.closest('[role="menu"]')?.getAttribute('aria-labelledby');
  const trigger = labelledBy === null || labelledBy === undefined ? null : document.getElementById(labelledBy);

  return trigger instanceof HTMLElement ? trigger : null;
}

/** Останній відступ: заголовок сторінки (`h1`), щоб фокус не падав на `BODY`, коли нема ні елемента, ні кнопки меню. */
function focusHeading(): void {
  const heading = document.querySelector('h1');
  if (!(heading instanceof HTMLElement)) return;
  if (!heading.hasAttribute('tabindex')) heading.setAttribute('tabindex', '-1');
  heading.focus({ preventScroll: true });
}

/**
 * Повертає фокус на елемент, що відкрив модалку/шторку, коли вона закрилась (Esc, хрестик, Cancel) або
 * зникла з дерева (PA-U-03).
 *
 * ⛔ Власний `returnFocus` Mantine запам'ятовує елемент лише при ЗМІНІ `opened` з `false` на `true`. Діалоги
 * Permits монтуються вже відкритими (`?dialog=` в адресі, лінивий чанк після кліку), тож зміни не було, нічого
 * не запам'ятовано, і після Esc фокус падав на `BODY`. Тут елемент береться у `useLayoutEffect` на першому
 * відкритому коміті — до того, як пастка фокуса модалки перенесе фокус усередину.
 */
export function useRestoreFocus(opened: boolean, returnTo?: () => HTMLElement | null): void {
  const origin = useRef<HTMLElement | null>(null);
  const fallback = useRef<HTMLElement | null>(null);
  const wasOpened = useRef(false);
  const armed = useRef(false);
  const provider = useRef(returnTo);
  provider.current = returnTo;

  // ⛔ P3-F2: діалог відкрито ПУНКТОМ МЕНЮ («More» → «End this permit early…»): пункт зникає разом із випадним списком
  // (у тому ж коміті чи за перехід), і запам'ятований елемент уже не в дереві - фокус падав на `BODY`. Порядок відступу:
  // елемент → кнопка меню, якому належав пункт → `returnTo` виклику (кнопка, яку хук не бачив) → заголовок сторінки.
  const restore = (): void => {
    // Діалог закрито після відкриття (а не просто перемальовано закритим): лише тоді щось повертаємо.
    if (!armed.current) return;
    armed.current = false;
    const element = origin.current;
    const trigger = fallback.current;
    origin.current = null;
    fallback.current = null;
    // ⛔ Кнопка меню - ПЕРШОЮ: пункт меню ще в дереві, поки випадний список згасає (перехід `exit`), тож Esc одразу після
    // відкриття діалогу фокусував би пункт, який за мить розмонтується, і фокус падав на `BODY` (trace e2e permitPage:
    // `focus() on BUTTON[menuitem]` ... `removed menu`; CI-запуск lane/integ-y31).
    if (trigger?.isConnected === true) {
      trigger.focus({ preventScroll: true });
      return;
    }

    if (element?.isConnected === true) {
      element.focus({ preventScroll: true });
      return;
    }

    const target = provider.current?.();
    if (target?.isConnected === true) {
      target.focus({ preventScroll: true });
    } else if (element !== null || trigger !== null || provider.current !== undefined) {
      focusHeading();
    }
  };

  useLayoutEffect(() => {
    const justOpened = opened && !wasOpened.current;
    wasOpened.current = opened;
    if (justOpened) {
      armed.current = true;
      const active = document.activeElement;
      origin.current = active instanceof HTMLElement && active !== document.body ? active : null;
      fallback.current = origin.current === null ? null : menuTriggerOf(origin.current);
    } else if (!opened) {
      restore();
    }
  });

  // Зникнення з дерева (діалог рендериться лише за `?dialog=`): закриття без `opened === false`.
  useEffect(
    () => () => {
      restore();
      // StrictMode (dev, стенд e2e) монтує ефекти двічі: повторний запуск має запам'ятати елемент знову.
      wasOpened.current = false;
    },
    // `restore` читає лише ref, тож залежностей немає.
    [],
  );
}

