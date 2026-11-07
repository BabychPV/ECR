import { useEffect, useRef } from 'react';

/** Кнопка «More» сторінки документа - справжній відкривач шторки (пункт меню зникає разом із меню). */
const DocumentMoreSelector = '[data-testid="document-more"]';

const Focusable = 'button:not([disabled]), [href], input:not([disabled]):not([type="hidden"]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])';

function panelDialog(panelId: string): HTMLElement | null {
  const root = [...document.querySelectorAll<HTMLElement>('[data-panel]')].find((element) => element.getAttribute('data-panel') === panelId);
  if (root === undefined) return null;

  return root.matches('[role="dialog"]') ? root : root.querySelector<HTMLElement>('[role="dialog"]');
}

function insidePanel(element: Element, panelId: string): boolean {
  return element.closest('[data-panel]')?.getAttribute('data-panel') === panelId;
}

/**
 * PB2-P2-3 (шторка учасників): фокус входить у шторку при відкритті і повертається на «More» після Esc/закриття.
 *
 * ⚠ Шторку відкриває пункт меню «More → Edit access», а меню знімається з дерева разом із пунктом: `DetailDrawer`
 * запам'ятовує відкривачем те, що у фокусі на кадрі відкриття, - тут це BODY, і повертати фокус не було куди. На
 * широкому екрані пастки фокуса немає, тож і всередину шторки фокус сам не входив.
 *
 * @param ready Дані шторки приїхали (кнопки підвалу вже змонтовані) - тоді фокус на `[data-autofocus]`.
 */
export function useDrawerFocus(panelId: string, opened: boolean, ready: boolean): void {
  const wasOpened = useRef(false);

  useEffect(() => {
    if (!opened || !ready) return undefined;
    const timer = window.setTimeout(() => {
      const dialog = panelDialog(panelId);
      const active = document.activeElement;
      if (dialog === null || dialog.contains(active)) return;
      // Над шторкою вже відкрите вкладене вікно - фокус його, не наш.
      if (active instanceof HTMLElement && active !== document.body && active.closest('[role="dialog"]') !== null) return;
      const target = dialog.querySelector<HTMLElement>('[data-autofocus]') ?? dialog.querySelector<HTMLElement>(Focusable) ?? dialog;
      target.focus({ preventScroll: true });
    }, 0);

    return () => window.clearTimeout(timer);
  }, [opened, ready, panelId]);

  useEffect(() => {
    if (opened) {
      wasOpened.current = true;

      return undefined;
    }
    if (!wasOpened.current) return undefined;
    wasOpened.current = false;
    // Після власного повернення `DetailDrawer` (таймер 0): якщо воно вже кудись поставило фокус - не чіпаємо.
    const timer = window.setTimeout(() => {
      const active = document.activeElement;
      const lost = active === null || active === document.body || !active.isConnected || insidePanel(active, panelId);
      if (lost) document.querySelector<HTMLElement>(DocumentMoreSelector)?.focus({ preventScroll: true });
    }, 50);

    return () => window.clearTimeout(timer);
  }, [opened, panelId]);
}
