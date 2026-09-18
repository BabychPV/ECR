import type { JSX } from 'react';
import { MantineProvider } from '@mantine/core';
import { Notifications } from '@mantine/notifications';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { RouterProvider } from 'react-router-dom';
import { applySliceCachePolicy } from '@/features/grid/sliceCache';
import { applyDensity, density } from '@/shared/theme/preferences';
import { theme } from '@/shared/theme/theme';
import { router } from './router';
import { NewVersionBanner } from './staleVersion';

import '@/shared/theme/motion.css';
import '@/shared/theme/cell-states.css';

const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      // Дані звітності змінюються рідко, але помилково показати застаріле — дорого
      staleTime: 30_000,
      retry: (failureCount, error) => {
        // Не повторювати 4xx: 403 і 422 повторення не виправить
        const status = (error as { problem?: { status: number } })?.problem?.status;
        if (status !== undefined && status >= 400 && status < 500) return false;
        return failureCount < 2;
      },
    },
  },
});

/**
 * Політика кешу для зрізів таблиць (`CL-02`, `DIRECTIVE-14-ARCH.md` §3.5).
 *
 * ⛔ `refetchOnWindowFocus` за замовчуванням увімкнений, і на цьому екрані він
 * коштує дорожче, ніж будь-де: оператор звіряє числа з Excel, повертається до
 * вкладки через 31 с — і КОЖЕН змонтований зріз (до 91 на аркуші)
 * перезапитується одночасно найважчим запитом системи. За ті 31 с у
 * документі, який він сам і тримає відкритим, не змінилося нічого.
 *
 * ⚠ `staleTime` 5 хв замість глобальних 30 с — про те саме: зріз змінюють
 * рівно три події (власне збереження, імпорт, перерахунок), і кожна з них уже
 * ЯВНО інвалідує кеш (`sliceCache.ts`). Час тут нічого не стереже.
 *
 * ⚠ `setQueryDefaults`, а не опції в `useQuery`: `DocumentGrid.tsx` у цьому
 * пакеті недоторканний (рядок плану `D14-12`), а дефолти за префіксом ключа —
 * штатний механізм TanStack Query і діють на всі зрізи одразу, включно з тими,
 * що з'являться пізніше.
 */
applySliceCachePolicy(queryClient);

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
