import { useCallback, useRef } from 'react';

/**
 * Повернення фокуса тому, хто відкрив ЛІНИВИЙ діалог (`D-132`, WCAG 2.4.3).
 *
 * ⛔ Mantine запам'ятовує, хто мав фокус, лише коли `opened` ЗМІНЮЄТЬСЯ на
 * `true` (`useFocusReturn` → `useDidUpdate`). Лінивий діалог монтується вже
 * відкритим, тож при першому відкритті запам'ятовувати нічого — і Escape
 * лишав фокус на `body` (`keyboardPath.spec.ts`, «модальний діалог тримає
 * фокус і Escape повертає його»). Тут відкривач фіксується в мить кліку і
 * отримує фокус назад після закриття — для першого й наступних відкриттів.
 *
 * ⚠ Ті самі 10 мс, що й у Mantine: фокус повертається вже після того, як
 * пастка фокуса діалогу знята.
 */
export function useOpenerFocusReturn(): { remember: () => void; restore: () => void } {
  const opener = useRef<HTMLElement | null>(null);

  const remember = useCallback(() => {
    const active = document.activeElement;
    opener.current = active instanceof HTMLElement && active !== document.body ? active : null;
  }, []);

  const restore = useCallback(() => {
    const target = opener.current;
    if (target === null) return;
    window.setTimeout(() => {
      if (target.isConnected) target.focus({ preventScroll: true });
    }, 10);
  }, []);

  return { remember, restore };
}
