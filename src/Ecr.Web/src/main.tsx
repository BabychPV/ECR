import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { App } from './app/App';
import { ErrorBoundary } from './app/ErrorBoundary';

import '@mantine/core/styles.css';
import '@mantine/dates/styles.css';
import '@mantine/notifications/styles.css';
import './shared/theme/tokens.css';


/*
 * ⚠ Точка входу. Файла немає ні в дереві `05-skeleton.md` §1, ні в `05i`
 * (Q-015), але без нього `index.html` нема що завантажувати і `npm run build`
 * неможливий.
 */
const container = document.getElementById('root');
if (container === null) {
  throw new Error('Не знайдено #root: index.html не відповідає точці входу.');
}

/*
 * ⚠ `ErrorBoundary` — ОСТАННІЙ рубіж (`D14-11`), а не основний: помилку
 * всередині маршруту ловить `errorElement` у `router.tsx`, лишаючи меню й
 * шапку живими. Сюди доходить лише те, що впало ВИЩЕ за дерево маршрутів
 * (`App.tsx`: тема, кеш запитів, сам `RouterProvider`) — і без цієї межі таке
 * падіння лишало б порожній `#root`, тобто білий екран без жодного пояснення.
 *
 * ⚠ ВСЕРЕДИНІ `StrictMode`, а не навколо: `StrictMode` не рендерить розмітки
 * і сам упасти не може, натомість він навмисно викликає рендер двічі — і межа
 * має стояти там, де вона побачить обидва виклики.
 */
createRoot(container).render(
  <StrictMode>
    <ErrorBoundary>
      <App />
    </ErrorBoundary>
  </StrictMode>,
);
