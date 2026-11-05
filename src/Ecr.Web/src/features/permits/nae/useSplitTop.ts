import { useEffect, useLayoutEffect, type RefObject } from 'react';

/** CSS-змінна: відстань від верху документа до блоку «дерево + панель вузла» (px). Читає `naeStructure.css`. */
export const SplitTopVar = '--nae-split-top';

/**
 * Висота блоку «дерево + панель вузла» на сторінці структури NAE дорівнює висоті вікна мінус те, що стоїть над ним
 * (шапка застосунку, заголовок, підменю, кнопки імпорту). Це різне на різних екранах і зміниться, коли розгорнеться
 * панель імпорту, тож відступ ЗМІРЮЄТЬСЯ, а не вгадується константою: тоді сама сторінка не прокручується, а дерево й
 * панель вузла мають кожне свою прокрутку (людина 05.11: «стрибає вікно, коли гортаю до атрибутів вибраного вузла»).
 *
 * Відступ = `top` блоку + прокрутка вікна, тобто не залежить від того, де зараз вікно. Міряється після кожного рендеру
 * (ідемпотентно) і при зміні розміру вікна чи вмісту над блоком.
 */
export function useSplitTop(ref: RefObject<HTMLElement | null>): void {
  const measure = (): void => {
    const element = ref.current;
    if (element === null) return;
    const top = Math.max(0, Math.round(element.getBoundingClientRect().top + window.scrollY));
    element.style.setProperty(SplitTopVar, `${String(top)}px`);
  };

  useLayoutEffect(measure);

  useEffect(() => {
    window.addEventListener('resize', measure);
    const parent = ref.current?.parentElement;
    const observer = typeof ResizeObserver === 'undefined' || !parent ? null : new ResizeObserver(measure);
    if (parent) observer?.observe(parent);

    return () => {
      window.removeEventListener('resize', measure);
      observer?.disconnect();
    };
    // `measure` читає лише ref: вона не змінює поведінки між рендерами.
  }, []);
}
