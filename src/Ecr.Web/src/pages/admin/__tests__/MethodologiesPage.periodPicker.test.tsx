import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, fireEvent, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MethodologiesPage } from '@/pages/admin/MethodologiesPage';
import { testTheme } from '@/test/render';

/**
 * `MethodologiesPage`: голий `NumberInput` (`documents.period`) у діалозі
 * «Simulate» замінено на `PeriodPicker` — той самий антипатерн, що вже
 * виправлено в `DocumentsPage`/`DocumentPage` (UI-06,
 * `DIRECTIVE-15-FRONTEND.md:129`).
 *
 * ⛔ Мутаційний доказ (R-A6): клік «вперед» на грудні дає січень НАСТУПНОГО
 * року, не `periodKey + 1` (`…13`).
 *
 * ⚠ `null`-семантика — «очищення НЕ скидає симуляційний період»:
 * `simulationPeriod` — локальний стан, ніколи не `null`, і `value ??
 * simulationPeriod` лишає попереднє значення так само, як робив замінений
 * `NumberInput` (`typeof value === 'number' ? value : simulationPeriod`).
 *
 * ⚠ Підказка `methodologies.simulatePeriodHint` («нічого не зберігається»)
 * раніше жила в `description` того самого `NumberInput`; `PeriodPicker` свій
 * `description` віддає під підпис періоду мовою інтерфейсу, тож підказку
 * винесено окремим рядком під контролом — тест це теж перевіряє.
 */

const methodology = {
  id: 1,
  code: 'M1',
  nameL10n: { values: { en: 'Test Methodology' } },
  versions: [
    {
      id: 10,
      versionNumber: 1,
      level: 'Standard',
      status: 'Draft',
      effectiveFrom: null,
      numericMode: 'Actual',
      traceLevel: 'None',
    },
  ],
};

function mockFetch(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);

      // ⚠ `endsWith`, НЕ `includes`: `/api/v1/methodologies` МІСТИТЬ підрядок
      // `/api/v1/me` («me» — префікс «methodologies»), і `includes` тут ловив
      // би запит списку методологій раніше за власну гілку нижче — сесія
      // приходила б замість масиву, і `all.map` падав на об'єкті.
      if (url.endsWith('/api/v1/me')) {
        return new Response(
          JSON.stringify({
            denies: [],
            grants: {},
            isSimulation: false,
            language: 'en',
            mustChangePassword: false,
            permissions: ['Calculation.View'],
            simulatedForUserId: null,
            userId: 1,
            userName: 'tester',
          }),
          { status: 200, headers: { 'Content-Type': 'application/json' } },
        );
      }

      if (url.includes('/api/v1/methodologies')) {
        return new Response(JSON.stringify([methodology]), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        });
      }

      return new Response(JSON.stringify(null), { status: 200 });
    }),
  );
}

function show(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={testTheme}>
      <MemoryRouter>
        <QueryClientProvider client={client}>
          <MethodologiesPage />
        </QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

async function openSimulateDialog(): Promise<HTMLElement> {
  const simulateButton = await screen.findByRole('button', { name: '⟦methodologies.simulate⟧' });
  fireEvent.click(simulateButton);

  return screen.findByRole('dialog');
}

describe('MethodologiesPage: період симуляції — PeriodPicker замість NumberInput', () => {
  it('грудень → «вперед» → січень НАСТУПНОГО року (не 202613)', async () => {
    mockFetch();
    show();

    const dialog = await openSimulateDialog();
    const input = within(dialog).getByLabelText('⟦documents.period⟧');

    fireEvent.change(input, { target: { value: '202612' } });
    expect(input).toHaveProperty('value', '202612');

    fireEvent.click(within(dialog).getByRole('button', { name: '⟦period.next⟧' }));

    expect(input).toHaveProperty('value', '202701');
  });

  it('очищення поля НЕ скидає період симуляції — лишається попереднє значення', async () => {
    mockFetch();
    show();

    const dialog = await openSimulateDialog();
    const input = within(dialog).getByLabelText('⟦documents.period⟧');

    fireEvent.change(input, { target: { value: '202603' } });
    expect(input).toHaveProperty('value', '202603');

    fireEvent.change(input, { target: { value: '' } });

    /*
     * ⚠ Як і в `SnapshotsPage` (той самий приймальний вираз `value ??
     * simulationPeriod`): DOM після очищення лишається порожнім НА ВИГЛЯД —
     * `setSimulationPeriod(null ?? simulationPeriod)` при незмінному стані
     * не викликає перерендер (`Object.is`), тож ніхто не переписує те, що
     * щойно стер тест. Доказ бере СПРАВЖНІЙ стан через дію, яка форсує
     * перерендер: клік «вперед» рахує сусідній період від пропу `value`
     * (`simulationPeriod`), а не від того, що видно в полі.
     *
     * ⛔ Мутаційний доказ: якби очищення СПРАВДІ скидало `simulationPeriod`
     * (мутація «завжди приймає, що прийшло» замість `value ??
     * simulationPeriod`), крок пішов би не від 202603, і результат не був
     * би рівно 202604.
     */
    fireEvent.click(within(dialog).getByRole('button', { name: '⟦period.next⟧' }));

    expect(input).toHaveProperty('value', '202604');
  });

  it('підказка «нічого не зберігається» лишається видимою під контролом', async () => {
    mockFetch();
    show();

    const dialog = await openSimulateDialog();

    await waitFor(() =>
      expect(within(dialog).getByText('⟦methodologies.simulatePeriodHint⟧')).toBeTruthy(),
    );
  });
});
