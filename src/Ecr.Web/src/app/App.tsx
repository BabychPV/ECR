import type { JSX } from 'react';
import { MantineProvider } from '@mantine/core';
import { Notifications } from '@mantine/notifications';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { RouterProvider } from 'react-router-dom';
import { router } from './router';

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

/** Корінь застосунку. */
export function App(): JSX.Element {
  return (
    <MantineProvider defaultColorScheme="auto">
      <QueryClientProvider client={queryClient}>
        <Notifications position="top-right" />
        <RouterProvider router={router} />
      </QueryClientProvider>
    </MantineProvider>
  );
}
