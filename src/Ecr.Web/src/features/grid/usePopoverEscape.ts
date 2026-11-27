import { useEffect, type RefObject } from 'react';

/**
 * ✎ DOCUX (живий прохід 27.11): Esc закриває відкриту довідку під таблицею («Cell markers», «Keyboard shortcuts») і повертає
 * фокус на її кнопку.
 *
 * ⚠ Чому не `closeOnEscape` Mantine: він слухає клавіші лише ВСЕРЕДИНІ випадного вікна, а в цих довідках немає жодного
 * елемента, що бере фокус (`trapFocus` вимкнено) — фокус лишається на кнопці чи в сітці, і Esc не доходив. Тут — слухач
 * документа (capture, до RevoGrid), лише поки вікно відкрите; `preventDefault` — щоб той самий Esc не вийшов із повного
 * екрана (`SheetWorkspace.shouldExitOnEscape`) і не скасував правку комірки.
 */
export function usePopoverEscape(opened: boolean, close: () => void, target: RefObject<HTMLElement | null>): void {
  useEffect(() => {
    if (!opened) return undefined;
    const onKey = (event: KeyboardEvent): void => {
      if (event.key !== 'Escape' || event.defaultPrevented) return;
      event.preventDefault();
      event.stopPropagation();
      close();
      target.current?.focus();
    };
    document.addEventListener('keydown', onKey, true);
    return () => document.removeEventListener('keydown', onKey, true);
  }, [opened, close, target]);
}
