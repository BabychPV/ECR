import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, fireEvent, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter, useLocation } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { SnapshotsPage } from '@/pages/admin/SnapshotsPage';
import { testTheme } from '@/test/render';

/**
 * `SnapshotsPage` має ДВА місця з голим `NumberInput`
 * (`documents.period`, число без пояснення формату): період фільтра в шапці
 * і період побудови в діалозі «Build snapshot». Обидва замінено на
 * `PeriodPicker` — той самий антипатерн, що вже виправлено в
 * `DocumentsPage`/`DocumentPage` (UI-06, `DIRECTIVE-15-FRONTEND.md:129`).
 *
 * ⛔ Мутаційний доказ (R-A6) для КОЖНОГО з двох місць: клік «вперед» на
 * грудні дає січень НАСТУПНОГО року, не `…13`.
 *
 * ⚠ `null`-семантика РІЗНА для двох місць, і саме тому їх не можна було
 * поєднати бездумно:
 *  - фільтр (`periodKey`) — СПРАВЖНІЙ фільтр, `null` звужує перелік зрізів
 *    до «без періоду» (параметр просто не йде в запит);
 *  - період побудови (`buildPeriod`) — локальний стан діалогу, що НІКОЛИ не
 *    буває `null`: очищення поля лишає попереднє значення (`value ??
 *    buildPeriod`), так само, як робив замінений `NumberInput`.
 */

const project = { id: 1, code: 'AUDIT_SMOKE_PRJ', status: 'Active' as const };

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
            permissions: ['Report.BuildSnapshot', 'Report.EditDefinition'],
            simulatedForUserId: null,
            userId: 1,
            userName: 'tester',
          }),
          { status: 200, headers: { 'Content-Type': 'application/json' } },
        );
      }

      if (url.includes('/api/v1/projects')) {
        return new Response(
          JSON.stringify({ items: [project], nextCursor: null, totalCount: 1 }),
          { status: 200, headers: { 'Content-Type': 'application/json' } },
        );
      }

      if (url.includes('/api/v1/reports/snapshots')) {
        return new Response(JSON.stringify([]), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        });
      }

      if (url.includes('/api/v1/reports')) {
        return new Response(JSON.stringify([]), {
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
    <MantineProvider theme={testTheme}>
      <MemoryRouter initialEntries={[initialPath]}>
        <LocationSpy />
        <QueryClientProvider client={client}>
          <SnapshotsPage />
        </QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
  seenSearch = '';
});

describe('SnapshotsPage: фільтр періоду — PeriodPicker замість NumberInput', () => {
  it('грудень → «вперед» → січень НАСТУПНОГО року (не 202613)', async () => {
    mockFetch();
    show('/admin/snapshots?periodKey=202612');

    const next = await screen.findByRole('button', { name: '⟦period.next⟧' });
    fireEvent.click(next);

    await waitFor(() => expect(seenSearch).toBe('?periodKey=202701'));
  });

  it('очищення поля звужує перелік до «без періоду» — параметр зникає із запиту', async () => {
    mockFetch();
    show('/admin/snapshots?periodKey=202512');

    // ⚠ Дочекатися першого запиту з periodKey, перш ніж очищати поле —
    // інакше "зникнення параметра" не з чим порівняти.
    await waitFor(() => {
      const fetchMock = vi.mocked(fetch);
      const sawPeriod = fetchMock.mock.calls.some(([requestInput]) =>
        String(requestInput).includes('/api/v1/reports/snapshots') &&
        String(requestInput).includes('periodKey=202512'),
      );
      expect(sawPeriod).toBe(true);
    });

    const input = await screen.findByLabelText('⟦documents.period⟧');
    fireEvent.change(input, { target: { value: '' } });

    await waitFor(() => expect(seenSearch).not.toContain('periodKey'));

    await waitFor(() => {
      const fetchMock = vi.mocked(fetch);
      const lastSnapshotsCall = fetchMock.mock.calls
        .map(([requestInput]) => String(requestInput))
        .filter((requested) => requested.includes('/api/v1/reports/snapshots'))
        .at(-1);
      expect(lastSnapshotsCall).toBeDefined();
      expect(lastSnapshotsCall).not.toContain('periodKey');
    });
  });
});

describe('SnapshotsPage: період побудови (діалог «Build snapshot») — PeriodPicker замість NumberInput', () => {
  it('грудень → «вперед» → січень НАСТУПНОГО року (не 202613)', async () => {
    mockFetch();
    // ⚠ Проєкт уже в адресі: кнопка «Build snapshot» вимкнена, доки
    // `projectId === null`, а взаємодія із самим `Select` у jsdom — окреме,
    // повільне питання (`Q-299`), не предмет цього тесту.
    show('/admin/snapshots?projectId=1');

    const buildButton = await screen.findByRole('button', { name: '⟦snapshots.build⟧' });
    fireEvent.click(buildButton);

    const dialog = await screen.findByRole('dialog');
    const input = within(dialog).getByLabelText('⟦documents.period⟧');

    // Ставимо межу року напряму (пряме введення periodKey — те саме поле,
    // що й раніше), а не покладаємось на "поточний місяць" стенда.
    fireEvent.change(input, { target: { value: '202612' } });
    expect(input).toHaveProperty('value', '202612');

    fireEvent.click(within(dialog).getByRole('button', { name: '⟦period.next⟧' }));

    expect(input).toHaveProperty('value', '202701');
  });

  it('очищення поля НЕ скидає період побудови — лишається попереднє значення', async () => {
    mockFetch();
    show('/admin/snapshots?projectId=1');

    const buildButton = await screen.findByRole('button', { name: '⟦snapshots.build⟧' });
    fireEvent.click(buildButton);

    const dialog = await screen.findByRole('dialog');
    const input = within(dialog).getByLabelText('⟦documents.period⟧');

    // ⚠ Значення НАВМИСНО відмінне від поточного місяця стенда: інакше
    // мутація «скидає до `currentPeriodKey()`» лишалася б непоміченою, коли
    // збігається з сьогоднішнім місяцем випадково.
    fireEvent.change(input, { target: { value: '202503' } });
    expect(input).toHaveProperty('value', '202503');

    fireEvent.change(input, { target: { value: '' } });

    /*
     * ⚠ ПІСЛЯ очищення DOM-поле саме собою лишається порожнім НА ВИГЛЯД —
     * `setBuildPeriod(null ?? buildPeriod)` при незмінному `buildPeriod` дає
     * той самий примітив, і React (`Object.is`-порівняння стану) НЕ
     * перерендерює компонент, тож некерований текст у полі, який щойно
     * стер тест, нікому не було чим перезаписати назад. Це не регресія
     * цього PR: та сама властивість була б і в замінюваному `NumberInput`
     * із тим самим виразом `typeof value === 'number' ? value : buildPeriod`.
     *
     * ⛔ Мутаційний доказ бере СПРАВЖНІЙ стан, а не сирий DOM: клік «вперед»
     * рахує сусідній період від пропу `value` (`buildPeriod`), не від того,
     * що видно в полі. Якби очищення СПРАВДІ скинуло `buildPeriod` (мутація
     * «завжди приймає, що прийшло» замість `value ?? buildPeriod`), крок
     * пішов би від `NaN`/`null`, і стрілка була б вимкнена або дала б інше
     * число, а не КАЛЕНДАРНО наступний місяць від 202609.
     */
    fireEvent.click(within(dialog).getByRole('button', { name: '⟦period.next⟧' }));

    expect(input).toHaveProperty('value', '202504');
  });
});
