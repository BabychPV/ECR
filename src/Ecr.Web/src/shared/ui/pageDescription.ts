import { createContext } from 'react';

/**
 * Ключ рядка-пояснення ПОТОЧНОГО маршруту (UI-11, `handle.descriptionKey`).
 *
 * ⚠ Контекст, а не проп: пояснення належить екрану, а не розмітці сторінки,
 * тож його задає реєстр маршрутів (`app/routes.ts`), а ставить `RouteGuard`
 * навколо листа. Інакше кожна з 15 сторінок передавала б `description` сама,
 * і нова сторінка без нього мовчки лишалася б без пояснення.
 *
 * ⛔ Поза маршрутом (тести, вкладені картки) значення — `undefined`, і
 * `PageHeader` малює рівно те, що малював до UI-11.
 */
export const PageDescriptionContext = createContext<string | undefined>(undefined);
