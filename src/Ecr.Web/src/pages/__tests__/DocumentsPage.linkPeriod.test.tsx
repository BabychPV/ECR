import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { DocumentsPage } from '@/pages/DocumentsPage';
import { testTheme } from '@/test/render';

/**
 * UI-walkthrough F3 — «посилання на документ губить обраний період».
 *
 * Посилання було зібране як `/documents/${id}` без `periodKey`, а
 * `DocumentPage.tsx` бере `urlPeriod ?? currentPeriodKey()`. Отже людина, що
 * відфільтрувала перелік періодом і клікнула документ, потрапляла в ПОТОЧНИЙ
 * місяць і бачила чужі числа під тим самим бізнес-ключем.
 *
 * ⛔ Головна складність тесту, а не дрібниця оформлення: період стенда
 * (`202609`) випадково дорівнює поточному місяцю, тож на ньому дефект
 * НЕВИДИМИЙ — тест, що не задає період явно й відмінно від поточного, зелений
 * і без фіксу. Тому тут узятий МИНУЛИЙ період (`202401`), який не збігається з
 * `currentPeriodKey()` за жодного запуску в межах життя системи.
 *
 * Перевіряються ОБИДВІ гілки рендера ключа: документ із людським ім'ям
 * (посилання в `<Stack>`) і документ без нього (посилання на бізнес-ключі).
 */
const PastPeriod = 202401;

const project = { id: 1, code: 'AUDIT_SMOKE_PRJ', status: 'Active' as const };

const named = {
  id: 7,
  businessKey: 'DOC-000007',
  createdAt: '2026-01-01T00:00:00Z',
  nameL10n: { values: { en: 'Named document' } },
  projectId: 1,
  sheetCount: 1,
  sheetStates: { S1: 'Draft' },
};

const unnamed = {
  id: 1,
  businessKey: 'DOC-000001',
  createdAt: '2026-01-01T00:00:00Z',
  projectId: 1,
  sheetCount: 1,
  sheetStates: { S1: 'Draft' },
};

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
        return new Response(
          JSON.stringify({ items: [named, unnamed], nextCursor: null, totalCount: 2 }),
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

function show(entry: string): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={testTheme}>
      <MemoryRouter initialEntries={[entry]}>
        <QueryClientProvider client={client}>
          <DocumentsPage />
        </QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

const SlowEnvTimeout = 400_000;

describe('DocumentsPage: посилання несе обраний період (F3)', () => {
  it(
    'обраний період потрапляє в адресу документа — і для іменованого, і для безіменного',
    async () => {
      mockFetch();
      show(`/?periodKey=${String(PastPeriod)}`);

      // ⛔ Мутаційний доказ: поверни `to={`/documents/${document.id}`}` — і
      // обидва очікування падають на відсутньому `?periodKey=202401`.
      const withName = await screen.findByRole(
        'link',
        { name: 'Named document' },
        { timeout: SlowEnvTimeout },
      );
      expect(withName.getAttribute('href')).toBe(`/documents/7?periodKey=${String(PastPeriod)}`);

      const withoutName = await screen.findByRole(
        'link',
        { name: 'DOC-000001' },
        { timeout: SlowEnvTimeout },
      );
      expect(withoutName.getAttribute('href')).toBe(`/documents/1?periodKey=${String(PastPeriod)}`);

      // ⚠ Страховка від хибнозеленого: період тесту НЕ дорівнює поточному
      // місяцю, тобто збіг із підстановкою `currentPeriodKey()` виключений.
      const now = new Date();
      const currentPeriodKey = now.getUTCFullYear() * 100 + (now.getUTCMonth() + 1);
      expect(currentPeriodKey).not.toBe(PastPeriod);
    },
    SlowEnvTimeout,
  );

  it(
    'без періоду в адресі посилання лишається чистим — зайвого параметра не додає',
    async () => {
      mockFetch();
      show('/');

      const link = await screen.findByRole(
        'link',
        { name: 'DOC-000001' },
        { timeout: SlowEnvTimeout },
      );
      expect(link.getAttribute('href')).toBe('/documents/1');
    },
    SlowEnvTimeout,
  );
});
