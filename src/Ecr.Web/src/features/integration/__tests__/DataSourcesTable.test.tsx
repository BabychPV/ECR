import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen, fireEvent, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter, useLocation } from 'react-router-dom';
import type { JSX } from 'react';
import { SourcesPage } from '@/pages/admin/SourcesPage';

/**
 * Перелік З'ЄДНАНЬ на `/admin/sources` (директива №15 §3 `UI-09`).
 *
 * ⛔ Рендериться ВСЯ сторінка, а не сам компонент: доказ у тому, що з'єднання
 * дійшли до маршруту. До цього кроку `listDataSources` не мав жодного
 * споживача, і на `/admin/sources` не було ЖОДНОГО рядка з'єднання — саме це
 * й червоніє на коді до зміни.
 */
const Connections = [
  {
    catalog: 'ProdAF',
    code: 'PI-MAIN',
    collectionSchedules: 3,
    endpoint: 'https://pi.example.invalid/piwebapi',
    hasSecret: false,
    id: 7,
    isActive: true,
    maxParallel: 4,
    nameL10n: { en: 'Main PI server' },
    secondaryEndpoint: null,
    sourceEntities: 1250,
    transport: 'PiWebApi',
  },
  {
    catalog: null,
    code: 'LAB-OLD',
    collectionSchedules: 0,
    endpoint: 'https://lab.example.invalid/api',
    hasSecret: false,
    id: 8,
    isActive: false,
    maxParallel: 1,
    nameL10n: { en: 'Old lab feed' },
    secondaryEndpoint: null,
    sourceEntities: 2,
    transport: 'Rest',
  },
];

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  });
}

/** `/api/v1/data-sources` — за вибором тесту; решта — порожнє, профіль — повний. */
function respond(dataSources: () => Response): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const path = new URL(String(input), 'http://localhost').pathname;

      if (path.endsWith('/api/v1/me')) {
        return json({
          denies: [],
          grants: {},
          isSimulation: false,
          language: 'en',
          mustChangePassword: false,
          permissions: ['Integration.View', 'Integration.Manage'],
          simulatedForUserId: null,
          userId: 1,
          userName: 'tester',
        });
      }

      if (path.endsWith('/api/v1/data-sources')) return dataSources();

      // Сутності збору: порожньо — ця таблиця тут не перевіряється.
      if (path.endsWith('/api/v1/sources')) return json([]);

      return json(null);
    }),
  );
}

/** Показує поточний `?…` адреси — щоб перевіряти стан шухляди, а не вгадувати. */
function Location(): JSX.Element {
  const location = useLocation();

  return <output data-testid="location">{location.search}</output>;
}

function show(entry = '/admin/sources'): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider>
      <QueryClientProvider client={client}>
        <MemoryRouter initialEntries={[entry]}>
          <SourcesPage />
          <Location />
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>,
  );
}

/** Рядок таблиці з'єднань за кодом. */
function connectionRow(code: string): HTMLElement {
  const row = document.querySelector<HTMLElement>(`[data-data-sources] tr[data-row-key="${code}"]`);
  expect(row, `рядок з'єднання ${code}`).not.toBeNull();

  return row as HTMLElement;
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("SourcesPage: перелік з'єднань (UI-09)", () => {
  it("кожне з'єднання — рядок із назвою, кодом, транспортом і лічильниками", async () => {
    respond(() => json(Connections));
    show();

    await screen.findByText('Main PI server');

    const row = connectionRow('PI-MAIN');
    expect(within(row).getByText('PI-MAIN')).toBeTruthy();
    expect(within(row).getByText('PiWebApi')).toBeTruthy();

    // ⛔ Лічильник — число з роздільником розрядів, а не сире `1250`.
    expect(row.textContent).toContain('1,250');
    expect(row.textContent).toContain('3');

    expect(connectionRow('LAB-OLD').textContent).toContain('Old lab feed');
  });

  it("неактивне з'єднання названо для читалки, активне — ні", async () => {
    respond(() => json(Connections));
    show();

    await screen.findByText('Old lab feed');

    expect(connectionRow('LAB-OLD').getAttribute('aria-label') ?? '').toMatch(/inactive/i);
    expect(connectionRow('PI-MAIN').getAttribute('aria-label')).toBeNull();
  });

  it("L10: відмова переліку — не «з'єднань немає»", async () => {
    // ⚠ Без `errorCode`: клієнт сам ставить запасний, а вигаданий код тут
    // зачепив би сторож кодів помилок клієнта (`ClientErrorCodeTests`).
    respond(() => json({ title: 'Server error', status: 500, correlationId: 'c-1' }, 500));
    show();

    const section = await waitFor(() => {
      const node = document.querySelector<HTMLElement>('[data-data-sources] [data-table-state]');
      expect(node).not.toBeNull();

      return node as HTMLElement;
    });

    await waitFor(() => expect(within(section).getByRole('alert')).toBeTruthy());
    expect(section.textContent ?? '').not.toContain('sources.connectionsEmpty');
  });

  it('L2: шухляда закрита за замовчуванням і відкривається назвою з клавіатури', async () => {
    respond(() => json(Connections));
    show();

    await screen.findByText('Main PI server');

    expect(screen.queryByRole('dialog')).toBeNull();
    expect(screen.getByTestId('location').textContent).toBe('');

    // Кнопка назви, а не клац по `<tr>`: рядок таблиці з клавіатури недосяжний.
    fireEvent.click(screen.getByRole('button', { name: 'Main PI server' }));

    await waitFor(() => expect(screen.getByTestId('location').textContent).toBe('?panel=PI-MAIN'));
    expect(await screen.findByRole('dialog')).toBeTruthy();
  });

  it("застарілий ?panel= без такого з'єднання не відкриває порожньої шухляди", async () => {
    respond(() => json(Connections));
    show('/admin/sources?panel=GONE');

    await screen.findByText('Main PI server');

    expect(screen.queryByRole('dialog')).toBeNull();
  });
});
