import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter, useLocation } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { CampaignOverviewPage } from '@/pages/admin/CampaignOverviewPage';

/**
 * `CampaignOverviewPage`: голий `NumberInput` (підпис `documents.period`,
 * число `202609` без пояснення формату) замінено на `PeriodPicker` —
 * той самий антипатерн, що вже виправлено в `DocumentsPage`/`DocumentPage`
 * (UI-06, `docs/build/DIRECTIVE-15-FRONTEND.md:129`).
 *
 * ⛔ Мутаційний доказ (R-A6, той самий, що вже доведений для `DocumentsPage`):
 * клік «вперед» на грудні мусить дати СІЧЕНЬ НАСТУПНОГО РОКУ, а не
 * `periodKey + 1` (невалідний `…13`). Якби сторінка копіювала `shiftPeriod`
 * замість того, щоб узяти перевірений `PeriodPicker`, саме цей рядок
 * почервонів би на найменшій розбіжності копії з оригіналом.
 *
 * ⚠ `null`-семантика тут — «очищення повертає ефективний період до
 * дефолту»: `setPeriodKey(null)` прибирає `periodKey` з адреси, і
 * `urlPeriod ?? currentPeriodKey()` у самому компоненті підставляє поточний
 * місяць — так само, як робив замінений `NumberInput`
 * (`typeof value === 'number' ? value : null`).
 */

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
            permissions: ['Report.ViewCampaign'],
            simulatedForUserId: null,
            userId: 1,
            userName: 'tester',
          }),
          { status: 200, headers: { 'Content-Type': 'application/json' } },
        );
      }

      if (url.includes('/api/v1/campaign/summary')) {
        return new Response(JSON.stringify({ totalProjects: 0, projects: [], totals: {} }), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        });
      }

      return new Response(JSON.stringify(null), { status: 200 });
    }),
  );
}

let seenSearch = '';
function LocationSpy(): null {
  seenSearch = useLocation().search;
  return null;
}

function show(initialPath: string): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider>
      <MemoryRouter initialEntries={[initialPath]}>
        <LocationSpy />
        <QueryClientProvider client={client}>
          <CampaignOverviewPage />
        </QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
  seenSearch = '';
});

describe('CampaignOverviewPage: PeriodPicker замість NumberInput', () => {
  it(
    'грудень → «вперед» → січень НАСТУПНОГО року (не 202613), календарем, не periodKey+1',
    async () => {
      mockFetch();
      show('/admin/campaign?periodKey=202612');

      const next = await screen.findByRole('button', { name: '⟦period.next⟧' });
      fireEvent.click(next);

      await waitFor(() => expect(seenSearch).toBe('?periodKey=202701'));
    },
  );

  it('очищення поля прибирає periodKey з адреси (та сама поведінка, що й у старого NumberInput)', async () => {
    mockFetch();
    show('/admin/campaign?periodKey=202512');

    const input = await screen.findByLabelText('⟦documents.period⟧');
    fireEvent.change(input, { target: { value: '' } });

    await waitFor(() => expect(seenSearch).not.toContain('periodKey'));
  });
});
