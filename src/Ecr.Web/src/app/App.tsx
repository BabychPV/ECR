import type { JSX } from 'react';
import { MantineProvider } from '@mantine/core';
import { Notifications } from '@mantine/notifications';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { RouterProvider } from 'react-router-dom';
import { applyDensity, density } from '@/shared/theme/preferences';
import { theme } from '@/shared/theme/theme';
import { router } from './router';

import '@/shared/theme/motion.css';

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
        <RouterProvider router={router} />
      </QueryClientProvider>
    </MantineProvider>
  );
}
