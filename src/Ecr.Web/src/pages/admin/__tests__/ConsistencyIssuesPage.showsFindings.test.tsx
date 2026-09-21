import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { ConsistencyIssuesPage } from '@/pages/admin/ConsistencyIssuesPage';

/**
 * Екран показує САМІ ЗНАХІДКИ, а не їх кількість.
 *
 * ⛔ Саме цієї відповіді й бракувало: `aud.ConsistencyIssue` писалася з
 * першого дня `ConsistencyCheckJob`, а з продукту було видно лише лічильник
 * `ecr.consistency.issues` із міткою `kind` — «є 12 знахідок `BROKEN_FK`» без
 * жодного способу дізнатися, де саме. Тому тест перевіряє не «сторінка
 * зарендерилась», а що на екрані є ТЕКСТ знахідки, її код правила і
 * ідентифікатор зачепленої сутності.
 *
 * ⛔ Мутаційна проба (RED → GREEN, вручну): якщо прибрати комірку з
 * `issue.message` (`<Table.Td>{issue.message}</Table.Td>`) — тобто лишити
 * таблицю, підписи, бейджі й пагінацію на місці, — тест падає на першому ж
 * очікуванні. Дослівний текст падіння наведено в описі PR.
 */
const Finding = {
  id: 7,
  detectedAt: '2026-09-18T03:00:00Z',
  severity: 3,
  ruleCode: 'BROKEN_FK',
  entityType: 'doc.TableRow',
  entityId: 4021,
  message: 'Рядок 4021 посилається на екземпляр таблиці 77 періоду 202601, якого не існує.',
  resolvedAt: null,
  resolvedByUserId: null,
};

/**
 * ⚠ Профіль (`/api/v1/me`) — окрема відповідь: екран питає права для дії
 * «перевірити зараз», і сторінка знахідок на місці профілю — не профіль.
 */
function respondWith(body: unknown): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(
      async (input: RequestInfo | URL) =>
        new Response(
          JSON.stringify(String(input).endsWith('/api/v1/me') ? { permissions: [] } : body),
          {
            status: 200,
            headers: { 'Content-Type': 'application/json' },
          },
        ),
    ),
  );
}

function show(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider>
      <MemoryRouter initialEntries={['/admin/consistency']}>
        <QueryClientProvider client={client}>
          <ConsistencyIssuesPage />
        </QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('ConsistencyIssuesPage: знахідки видно поіменно, а не лише числом', () => {
  it('показує текст знахідки, код правила і зачеплену сутність', async () => {
    respondWith({ items: [Finding], nextCursor: null, totalCount: null });
    show();

    // ⛔ Головне твердження: на екрані сам ТЕКСТ знахідки — те єдине, що
    // відповідає на «де саме», якого лічильник не давав.
    expect(await screen.findByText(Finding.message)).toBeTruthy();

    // Код правила і сутність — без них рядок не з'єднати з базою руками.
    expect(screen.getByText(Finding.ruleCode)).toBeTruthy();
    expect(screen.getByText(`${Finding.entityType} · ${String(Finding.entityId)}`)).toBeTruthy();
  });

  it('порожній журнал показує пояснення, а не порожню таблицю', async () => {
    // ⚠ Зворотний бік: «знахідок немає» мусить читатися як стан системи, а не
    // як зламаний екран (`ФВ-14.23`). Без цього порожня таблиця без жодного
    // напису виглядає однаково з відмовою запиту.
    respondWith({ items: [], nextCursor: null, totalCount: null });
    show();

    expect(await screen.findByText('⟦consistency.empty⟧')).toBeTruthy();
    expect(screen.queryByRole('table')).toBeNull();
  });

  it('фільтр «лише нерозв’язані» увімкнений за замовчуванням і йде в запит', async () => {
    // ⚠ Журнал накопичує знахідки від кожного нічного прогону. Якби типовим
    // був повний перелік, перше, що бачив би адміністратор, — купа вже
    // закритих рядків, серед яких свіжа знахідка непомітна.
    respondWith({ items: [], nextCursor: null, totalCount: null });
    show();

    await screen.findByText('⟦consistency.empty⟧');

    const calls = (globalThis.fetch as unknown as { mock: { calls: unknown[][] } }).mock.calls;
    const urls = calls.map((call) => String(call[0]));

    expect(urls.some((url) => url.includes('openOnly=true'))).toBe(true);
  });
});
