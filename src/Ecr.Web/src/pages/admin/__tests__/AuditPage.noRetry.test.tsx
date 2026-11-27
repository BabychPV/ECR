import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { QueryClientProvider } from '@tanstack/react-query';
import { AuditPage } from '@/pages/admin/AuditPage';
import { createQueryClient } from '@/app/queryClient';
import { testTheme } from '@/test/render';

/**
 * audit-filters-live AC-6: журнал змін комірок, що впав тайм-аутом (`503 ECR-SYS-0503`), не перезапитується сам.
 * ⛔ Клієнт застосунку (`createQueryClient`, а не тестовий з `retry: false`): на базі 7afb19c97 запит повторювався
 * ще двічі (2 с, 4 с) — на live це ~100 с скелета і три сесії SQL по ~30 с. Тепер — одна відмова й «Retry».
 */
const SlowEnvTimeout = 60_000;
const json = (body: unknown, status = 200): Response =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe('AuditPage: відмова журналу без самоповтору (AC-6)', () => {
  it(
    '503 тайм-ауту показується одразу з Retry і не повторюється без кліку',
    async () => {
      let cells = 0;
      vi.stubGlobal(
        'fetch',
        vi.fn(async (input: RequestInfo | URL) => {
          const url = new URL(String(input), 'http://x');
          if (url.pathname === '/api/v1/me') {
            return json({ userId: 1, userName: 'a', language: 'en', permissions: ['Security.ViewAudit'] });
          }
          if (url.pathname === '/api/v1/audit/cells') {
            cells += 1;
            return json({ status: 503, title: 'Service unavailable', errorCode: 'ECR-SYS-0503' }, 503);
          }
          return json(null);
        }),
      );

      render(
        <QueryClientProvider client={createQueryClient()}>
          <MantineProvider theme={testTheme}>
            <MemoryRouter initialEntries={['/admin/audit?from=2026-11-18&to=2026-11-25&documentId=96']}>
              <AuditPage />
            </MemoryRouter>
          </MantineProvider>
        </QueryClientProvider>,
      );

      const retry = await screen.findByRole('button', { name: /retry|повторити/i }, { timeout: 3_000 });
      expect(cells).toBe(1);

      // Повтор бібліотеки на базі приходив через 2 с ±20 %; чекаємо довше — запит лишається один.
      await new Promise((resolve) => setTimeout(resolve, 3_000));
      expect(cells).toBe(1);

      await userEvent.click(retry);
      await vi.waitFor(() => expect(cells).toBe(2));
    },
    SlowEnvTimeout,
  );
});
