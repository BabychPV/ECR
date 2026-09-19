import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { App } from './app/App';
import { ErrorBoundary } from './app/ErrorBoundary';

/*
 * ⛔ Шрифт САМОХОСТИНГОМ (`@fontsource`), не з CDN. Рішення замовника
 * дослівне: «IBM Plex дозволити, self-hosted». Посилання на `fonts.googleapis.com`
 * означало б, що кожен вхід оператора в систему повідомляє третій стороні, коли
 * і звідки він увійшов, — і що застосунок у мережі без зовнішнього доступу
 * малюється системним шрифтом, тобто інакше, ніж на скріншотах приймання.
 *
 * ⚠ Ваги рівно ті, що вживає код: `400` (дефолт), `500`, `600`, `700`
 * (`git grep -o "fw={[0-9]*}"` дає 500×4, 600×40, 700×4). Файл `<вага>.css`
 * фонтсорса несе `unicode-range` на кожну підмножину, тому браузер тягне
 * `.woff2` лише тих абеток, які справді трапилися на сторінці: латиниця й
 * кирилиця — так, грецька та в'єтнамська — ні.
 *
 * ⚠ Казахські літери (Ә Ғ Қ Ң Ө Ұ Ү Һ І) покриті двома підмножинами:
 * `cyrillic` (`U+0400-045F` дає І/і, `U+04B0-04B1` — Ұ/ұ) і `cyrillic-ext`
 * (`U+0460-052F` — решта). Обидві входять у `<вага>.css`.
 */
import '@fontsource/ibm-plex-sans/400.css';
import '@fontsource/ibm-plex-sans/500.css';
import '@fontsource/ibm-plex-sans/600.css';
import '@fontsource/ibm-plex-sans/700.css';
import '@fontsource/ibm-plex-mono/400.css';

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
