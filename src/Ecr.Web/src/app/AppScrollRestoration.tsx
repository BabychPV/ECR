import type { JSX } from 'react';
import { ScrollRestoration, type Location } from 'react-router-dom';

/**
 * ✎ debt-1109 x7: ключ відновлення прокрутки — ШЛЯХ, без рядка запиту.
 *
 * ⛔ Типовий ключ — `location.key`, новий на КОЖНУ навігацію, і зміна лише рядка запиту (`?table=`, `?periodKey=`,
 * `?mine=1` — вибір таблиці в дереві, фільтр переліку) для `<ScrollRestoration/>` була новою сторінкою без збереженої
 * позиції: `scrollTo(0, 0)`. Перехід до комірки через це тримав рядок у верхній третині 40 кадрів поспіль
 * (`keepInUpperThird`). Тепер той самий шлях — та сама позиція: зміна запиту прокрутку не скидає.
 */
export function scrollRestorationKey(location: Pick<Location, 'pathname'>): string {
  return location.pathname;
}

/** Єдиний `<ScrollRestoration/>` застосунку (див. коментар у `AppLayout`). */
export function AppScrollRestoration(): JSX.Element {
  return <ScrollRestoration getKey={scrollRestorationKey} />;
}
