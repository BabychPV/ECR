import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { App } from './app/App';

import '@mantine/core/styles.css';
import '@mantine/dates/styles.css';
import '@mantine/notifications/styles.css';

/*
 * ⚠ Точка входу. Файла немає ні в дереві `05-skeleton.md` §1, ні в `05i`
 * (Q-015), але без нього `index.html` нема що завантажувати і `npm run build`
 * неможливий.
 */
const container = document.getElementById('root');
if (container === null) {
  throw new Error('Не знайдено #root: index.html не відповідає точці входу.');
}

createRoot(container).render(
  <StrictMode>
    <App />
  </StrictMode>,
);
