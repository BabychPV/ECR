import type { JSX } from 'react';
import { MantineProvider } from '@mantine/core';
import { Notifications } from '@mantine/notifications';
import { QueryClientProvider } from '@tanstack/react-query';
import { RouterProvider } from 'react-router-dom';
import { applyDensity, density } from '@/shared/theme/preferences';
import { theme } from '@/shared/theme/theme';
import { createQueryClient } from './queryClient';
import { router } from './router';
import { NewVersionBanner } from './staleVersion';

import '@/shared/theme/motion.css';
import '@/shared/theme/cell-states.css';

/*
 * ⚠ Створення клієнта переїхало у `queryClient.ts` не заради охайності: разом
 * із ним туди переїхала страхувальна сітка `MutationCache.onError` (`UI-00`),
 * а перевірити її можна лише на клієнті, створеному в тесті. Модульна змінна
 * в `App.tsx` такої можливості не давала — тест отримував би той самий
 * екземпляр, що й застосунок, з його кешем і сповіщеннями.
 */
const queryClient = createQueryClient();

/*
 * ⛔ Щільність застосовується ДО першого рендера, а не в `useEffect`.
 * Інакше перший кадр малюється зі стандартною висотою рядка, і таблиця на
 * тисячу рядків смикається на очах у користувача при кожному відкритті.
 */
applyDensity(density());

/** Корінь застосунку. */
export function App(): JSX.Element {
  return (
    /*
     * ⚠ `theme` — та сама тема, що й у `theme.ts`, і єдина: другого виклику
     * `createTheme` в застосунку немає навмисно (`ФВ-14.11`). Обидві теми
     * (світла й темна) виводяться з неї, а `auto` бере системну — користувач,
     * який працює в темній системі, не отримує білого спалаху на весь екран.
     */
    <MantineProvider theme={theme} defaultColorScheme="auto">
      <QueryClientProvider client={queryClient}>
        <Notifications position="top-right" />

        {/*
         * ⚠ Банер «встановлено нову версію» (`DAT-08`) — ПОЗА
         * `RouterProvider`: відмова завантаження чанка стається і без
         * переходу (прогрів за наведенням, `useRoutePrefetch.ts`), тож
         * прив'язувати банер до маршруту означало б не показати його саме в
         * тому випадку, який трапляється першим.
         */}
        <NewVersionBanner />

        <RouterProvider router={router} />
      </QueryClientProvider>
    </MantineProvider>
  );
}
