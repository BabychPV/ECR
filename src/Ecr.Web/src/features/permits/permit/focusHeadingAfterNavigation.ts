import { RouteHeadingClass } from '@/shared/theme/routeHeading';

export interface HeadingFocusOptions {
  /** Заголовок сторінки ДО переходу: новий `h1` має бути іншим елементом (інша сторінка) або містити `expectText`. */
  readonly previous: Element | null;
  /** Текст, що з'являється в заголовку лише на сторінці-призначенні (номер нової чернетки); той самий екран перемонтовується без нового `h1`. */
  readonly expectText?: string;
}

/** Скільки кадрів чекати на сторінку-призначення (~2 с), перш ніж поставити фокус на те, що є. */
const MaxFrames = 120;

export function pageHeading(): HTMLElement | null {
  return document.querySelector<HTMLElement>(`.${RouteHeadingClass}`);
}

function isSettled(heading: HTMLElement, options: HeadingFocusOptions): boolean {
  if (heading !== options.previous) return true;

  return options.expectText !== undefined && (heading.textContent ?? '').includes(options.expectText);
}

/**
 * Фокус після діалогу, який закривається РАЗОМ із переходом на іншу сторінку (Renew -> нова чернетка, Delete draft ->
 * перелік). Хук `useRestoreFocus` встигає повернути фокус на кнопку «More», але сторінка за мить змінюється - і фокус
 * падав на `BODY`. Тут фокус переноситься на `h1.ecr-route-heading` (`tabIndex=-1`) вже ПЕРЕХОДУ-призначення: чекаємо
 * (rAF), поки заголовок новий, а не той, що був до переходу. Якщо користувач сам перевів фокус деінде - не чіпаємо.
 */
export function focusHeadingAfterNavigation(options: HeadingFocusOptions): void {
  let frames = 0;

  const step = (): void => {
    frames += 1;
    const active = document.activeElement;
    const userMoved =
      active instanceof HTMLElement &&
      active !== document.body &&
      !active.hasAttribute('data-permit-more') &&
      active !== options.previous;
    if (userMoved) return;

    const heading = pageHeading();
    if (heading !== null && (isSettled(heading, options) || frames >= MaxFrames)) {
      heading.focus({ preventScroll: true });
      return;
    }
    if (frames < MaxFrames) requestAnimationFrame(step);
  };

  requestAnimationFrame(step);
}
