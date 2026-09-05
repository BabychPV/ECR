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
    };
  }, []);

  return (
    <VisuallyHidden role="status" aria-live="polite" aria-atomic="true">
      {message}
    </VisuallyHidden>
  );
}
