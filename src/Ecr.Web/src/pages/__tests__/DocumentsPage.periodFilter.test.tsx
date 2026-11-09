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

      // ✎ Projects/Documents 08.11: варіанти «Year» — лише роки з календарів проєктів (тут 2025 і 2026).
      if (/\/api\/v1\/projects\/\d+\/periods/.test(url)) {
        return new Response(
          JSON.stringify({
            periods: [
              { periodKey: 202512, year: 2025, sequence: 12, state: 'Closed' },
              { periodKey: 202601, year: 2026, sequence: 1, state: 'Open' },
            ],
          }),
          { status: 200, headers: { 'Content-Type': 'application/json' } },
        );
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

function show(entry: string): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider>
      <MemoryRouter initialEntries={[entry]}>
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

// ✎ Projects/Documents 08.11: поля «Month» більше немає (перелік лише за рік) — та сама гарантія «одним переходом»
// перевіряється на полі «Year»: рік, periodKey зі старої адреси й курсор міняються разом, і запит іде з новим роком.
describe('DocumentsPage: вибір року справді фільтрує (lane3; ✎ Projects/Documents 08.11 — лише рік)', () => {
  it(
    'стара адреса з periodKey дає рік; зміна року → одним переходом year без periodKey/cursor, запит з year',
    async () => {
      mockFetch();
      show('/?periodKey=202601&cursor=abc');

      const year = await screen.findByLabelText('⟦documents.year⟧', {}, { timeout: SlowEnvTimeout });
      // Рік — з місяця старої адреси; поля місяця немає.
      expect((year as HTMLSelectElement).value).toBe('2026');
      expect(screen.queryByLabelText('⟦documents.month⟧')).toBeNull();
      // Варіанти — лише роки з календарів проєктів.
      await waitFor(
        () =>
          expect([...(year as HTMLSelectElement).options].map((option) => option.value)).toEqual(['2026', '2025']),
        { timeout: SlowEnvTimeout },
      );

      fireEvent.change(year, { target: { value: '2025' } });

      // ⛔ Мутаційний доказ: окремі сеттери адреси гублять зміну — `seenSearch` лишається зі старим periodKey.
      await waitFor(() => expect(seenSearch).toBe('?year=2025'), { timeout: SlowEnvTimeout });

      const fetchMock = vi.mocked(fetch);
      const lists = (): URL[] =>
        fetchMock.mock.calls
          .map(([requestInput]) => String(requestInput))
          .filter((url) => /\/api\/v1\/documents\?/.test(url))
          .map((url) => new URL(url, 'http://x'));
      await waitFor(
        () => expect(lists().some((u) => u.searchParams.get('year') === '2025')).toBe(true),
        { timeout: SlowEnvTimeout },
      );
      // Перелік ніколи не просить місяць: periodKey зі старої адреси дає лише рік.
      expect(lists().every((u) => !u.searchParams.has('periodKey'))).toBe(true);
      expect(lists().some((u) => u.searchParams.get('year') === '2026')).toBe(true);
    },
    SlowEnvTimeout,
  );
});
