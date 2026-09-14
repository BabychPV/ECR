import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter, useLocation } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { DocumentsPage } from '@/pages/DocumentsPage';

/**
 * UI-аудит, lane 3: `lane3-documents-list-period-field-nonfunctional.md` —
 * поле «Period» на сторінці документів виглядало інтерактивним (приймало
 * цифри, показувало введене), але жоден запит `GET /api/v1/documents`
 * ніколи не бачив `periodKey` в адресі.
 *
 * ⛔ Корінь — обробник `onChange` викликав ДВА окремі сеттери `useUrlState`
 * (`setPeriodKey`, одразу за ним `setCursor(null)`) в одному синхронному
 * тіку. `useSearchParams`'s сеттер, викликаний двічі поспіль так, губить
 * ОБИДВІ зміни, не лише другу (мутаційно доведено в `useUrlState.test.tsx`
 * ізольовано, без jsdom-специфіки цього компонента). Фікс —
 * `useUrlParamsSetter`, що оновлює обидва параметри ОДНИМ переходом.
 *
 * Тест доводить наскрізно: після зміни поля Period (а) адреса сторінки
 * містить `periodKey`, і (б) НАСТУПНИЙ запит до `GET /api/v1/documents`
 * справді несе `periodKey` як query-параметр.
 */
const project = { id: 1, code: 'AUDIT_SMOKE_PRJ', status: 'Active' as const };

function documentsResponse() {
  return { items: [], nextCursor: null, totalCount: 0 };
}

function mockFetch(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);

      if (url.includes('/api/v1/me')) {
        return new Response(
          JSON.stringify({
            denies: [],
            grants: {},
            isSimulation: false,
            language: 'en',
            mustChangePassword: false,
            permissions: [],
            simulatedForUserId: null,
            userId: 1,
            userName: 'tester',
          }),
          { status: 200, headers: { 'Content-Type': 'application/json' } },
        );
      }

      if (url.includes('/api/v1/documents')) {
        return new Response(JSON.stringify(documentsResponse()), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        });
      }

      if (url.includes('/api/v1/projects')) {
        return new Response(JSON.stringify({ items: [project], nextCursor: null, totalCount: 1 }), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        });
      }

      return new Response(JSON.stringify(null), { status: 200 });
    }),
  );
}

let seenSearch = '';
function LocationSpy() {
  seenSearch = useLocation().search;
  return null;
}

function show(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider>
      <MemoryRouter initialEntries={['/']}>
        <LocationSpy />
        <QueryClientProvider client={client}>
          <DocumentsPage />
        </QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
  seenSearch = '';
});

const SlowEnvTimeout = 400_000;

describe('DocumentsPage: поле «Period» справді фільтрує (lane3)', () => {
  it(
    'зміна поля Period потрапляє в адресу і в наступний запит до API',
    async () => {
      mockFetch();
      show();

      const input = await screen.findByLabelText('⟦documents.period⟧', {}, { timeout: SlowEnvTimeout });

      fireEvent.change(input, { target: { value: '202601' } });

      // ⛔ Мутаційний доказ: без фіксу `seenSearch` лишається порожнім
      // назавжди — обидва сеттери гублять свою зміну.
      await waitFor(() => expect(seenSearch).toBe('?periodKey=202601'), { timeout: SlowEnvTimeout });

      const fetchMock = vi.mocked(fetch);
      await waitFor(
        () => {
          const calledWithPeriod = fetchMock.mock.calls.some(([requestInput]) =>
            String(requestInput).includes('periodKey=202601'),
          );
          expect(calledWithPeriod).toBe(true);
        },
        { timeout: SlowEnvTimeout },
      );
    },
    SlowEnvTimeout,
  );
});
