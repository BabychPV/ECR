import { useEffect, useLayoutEffect, useRef } from 'react';

/**
 * Повертає фокус на елемент, що відкрив модалку/шторку, коли вона закрилась (Esc, хрестик, Cancel) або
 * зникла з дерева (PA-U-03).
 *
 * ⛔ Власний `returnFocus` Mantine запам'ятовує елемент лише при ЗМІНІ `opened` з `false` на `true`. Діалоги
 * Permits монтуються вже відкритими (`?dialog=` в адресі, лінивий чанк після кліку), тож зміни не було, нічого
 * не запам'ятовано, і після Esc фокус падав на `BODY`. Тут елемент береться у `useLayoutEffect` на першому
 * відкритому коміті — до того, як пастка фокуса модалки перенесе фокус усередину.
 */
export function useRestoreFocus(opened: boolean): void {
  const origin = useRef<HTMLElement | null>(null);
  const wasOpened = useRef(false);

  const restore = (): void => {
    const element = origin.current;
    origin.current = null;
    if (element?.isConnected === true) element.focus({ preventScroll: true });
  };

  useLayoutEffect(() => {
    const justOpened = opened && !wasOpened.current;
    wasOpened.current = opened;
    if (justOpened) {
      const active = document.activeElement;
      origin.current = active instanceof HTMLElement && active !== document.body ? active : null;
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

