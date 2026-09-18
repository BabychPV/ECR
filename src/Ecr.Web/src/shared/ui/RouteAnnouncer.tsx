import { useEffect, useState, type JSX } from 'react';
import { VisuallyHidden } from '@mantine/core';

type Listener = (title: string) => void;

const listeners = new Set<Listener>();
let last = '';

/**
 * Оголошує зміну маршруту (`ФВ-14.19`).
 *
 * ⛔ Викликається зі сторінки, а не з роутера. Роутер знає шлях
 * (`/admin/template-versions/42`), а користувачеві потрібна назва екрана
 * мовою, якою він працює; читалка, що вимовляє URL, гірша за мовчання.
 *
 * ⚠ `last` — пам'ять ПРО ВМІСТ ЖИВОЇ ОБЛАСТІ, а не про історію застосунку:
 * дедуплікація існує лише тому, що запис того самого тексту в уже наявний
 * регіон читалка не озвучує. Тому вона правдива рівно доти, доки та область
 * жива (див. `RouteAnnouncer` нижче).
 */
export function announceRoute(title: string): void {
  if (title === last) return;

  last = title;
  for (const listener of listeners) listener(title);
}

/**
 * Область оголошень для читалки.
 *
 * ⛔ Одна на весь застосунок і **живе постійно**. Область, що з'являється
 * разом із текстом, не озвучується: читалка оголошує зміни всередині вже
 * наявного регіону, а не появу нового вузла.
 *
 * ⚠ `polite`, а не `assertive`: перехід — не аварія, і переривати ним поточне
 * читання не можна.
 */
export function RouteAnnouncer(): JSX.Element {
  const [message, setMessage] = useState('');

  useEffect(() => {
    listeners.add(setMessage);

    return () => {
      listeners.delete(setMessage);

      /*
       * ⛔ Знята область оголошень скидає й пам'ять про свій вміст. Без цього
       * `last` пережив би саму область: нова копія монтується з ПОРОЖНІМ
       * `message`, а `announceRoute` із тим самим заголовком мовчки виходив
       * би по дедуплікації — і екран, на який користувач потрапив, не
       * оголосився б узагалі. Дедуплікувати проти тексту, якого в живій
       * області вже немає, — це не економія, це втрачене оголошення.
       *
       * ⚠ Це не тестова зручність: у застосунку так само вимикається
       * StrictMode-цикл «ефект → прибирання → ефект» і будь-яке
       * перемонтування кореня (зміна мови/теми з новим `key`). Те, що на
       * цьому спіткнулися саме тести (`ChangePasswordPage.pageHeader`,
       * `RouteGuard.a11yFocus`), — наслідок, а не причина.
       */
      if (listeners.size === 0) last = '';
    };
  }, []);

  return (
    <VisuallyHidden role="status" aria-live="polite" aria-atomic="true">
      {message}
    </VisuallyHidden>
  );
}
