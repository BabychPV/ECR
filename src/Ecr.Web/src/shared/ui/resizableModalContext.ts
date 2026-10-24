import { createContext, useContext } from 'react';

/**
 * Чи стоїть компонент усередині `ResizableModal` (P44).
 *
 * ⚠ Окремий крихітний модуль, а не експорт самого вікна: його читає `ExpressionEditor`, і
 * імпорт вікна тягнув би в чанк редактора розмітку, якої той не малює.
 *
 * Усередині такого вікна редактор росте разом із ним (`data-rm-grow`, `ResizableModal.css`);
 * поза ним — лишається заданої висоти, як і досі.
 */
export const ResizableModalContext = createContext(false);

/** `true` — компонент у вікні, яке можна розгорнути чи потягнути за кут. */
export function useInResizableModal(): boolean {
  return useContext(ResizableModalContext);
}
