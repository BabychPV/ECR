import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom';
import { testTheme } from '@/test/render';
import { PermitsPage } from '@/pages/admin/PermitsPage';
import { FixturePermits } from './permitFixture';

/**
 * Перелік дозволів (PM-07): дані й стовпці макета, показники-фільтри, пошук/тип/стан з адреси, перехід на
 * сторінку дозволу, стани (завантаження, порожньо, помилка), права запису (кнопка «New permit» і меню дій
 * ховаються), меню дій за станом і виклик ендпоінта з рядка.
 *
 * Мутаційний доказ (вручну, відновлено): прибрати `mayWrite ?` у `permitColumns(...)` — валить «без права
 * колонки дій немає»; `onRowClick` без `navigate` валить «клік по рядку»; `hasPermits &&` над смугою
 * показників прибрати — валить «порожній»; `stopPropagation` у `RowMenu` прибрати — валить «меню не відкриває
 * рядок»; `actionsFor` не з тим станом — валить «меню за станом».
 */
vi.mock('@/shared/i18n', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/i18n')>()),
  t: (key: string, params?: Record<string, string | number>) =>
    params === undefined ? key : `${key} ${JSON.stringify(params)}`,
  language: () => 'en',
}));

const session = vi.hoisted(() => ({
  permissions: ['Registry.View', 'Registry.EditData'] as string[],
  isSimulation: false,
}));
vi.mock('@/shared/session/useSession', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/session/useSession')>()),
  useSession: () => ({ data: { permissions: session.permissions, isSimulation: session.isSimulation } }),
}));

const json = (body: unknown, status = 200): Response =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });

interface Call {
  readonly method: string;
  readonly url: string;
}
let calls: Call[] = [];

function stub(items: readonly unknown[] = FixturePermits, listReply?: () => Response): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      calls.push({ method: init?.method ?? 'GET', url });
      if (url.startsWith('/api/v1/permit-types')) return json({ items: [], nextCursor: null, totalCount: 0 });
      if ((init?.method ?? 'GET') === 'GET') {
        return listReply?.() ?? json({ items, nextCursor: null, totalCount: items.length });
      }

      return json({ id: 3 });
    }),
  );
}

function Where(): null {
  const location = useLocation();
  (globalThis as { __where?: string }).__where = location.pathname + location.search;

  return null;
}

function show(entry = '/admin/permits'): void {
  render(
    <MantineProvider theme={testTheme}>
      <MemoryRouter initialEntries={[entry]}>
        <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
          <Where />
          <Routes>
            <Route path="/admin/permits" element={<PermitsPage />} />
            <Route path="/admin/permits/:id" element={<p>permit page</p>} />
          </Routes>
        </QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );
}

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  calls = [];
  session.permissions = ['Registry.View', 'Registry.EditData'];
  session.isSimulation = false;
  delete (globalThis as { __where?: string }).__where;
});

const where = (): string | undefined => (globalThis as { __where?: string }).__where;
const bodyRows = (): HTMLElement[] => within(screen.getByRole('table')).getAllByRole('row').slice(1);
const headers = (): (string | null)[] =>
  within(screen.getByRole('table')).getAllByRole('columnheader').map((h) => h.textContent);

describe('PermitsPage: дані', () => {
  it('читає ВСІ дозволи одним запитом limit=500, малює стовпці макета й рядки', async () => {
    stub();
    show();

    expect(await screen.findByText('KZ06VCZ14825472')).toBeTruthy();
    expect(calls[0]).toEqual({ method: 'GET', url: '/api/v1/permits?limit=500' });
    expect(bodyRows()).toHaveLength(4);
    expect(headers()).toEqual([
      'permits.list.col.permit',
      'permits.list.col.type',
      'permits.list.col.validity',
      'permits.list.col.rows',
      'permits.list.col.readBy',
      'permits.list.col.state',
      'permits.list.col.actions',
    ]);

    const first = bodyRows()[0]!;
    expect(first.textContent).toContain('Central processing facility');
    expect(first.textContent).toContain('Air emission permit');
    expect(first.textContent).toContain('permits.list.rowsMany {"rows":43,"tables":2}');
    expect(first.textContent).toContain('permits.list.readByMany {"count":3}');
    expect(first.textContent).toContain('permits.list.endsIn {"days":40}');
    expect(first.textContent).toContain('permits.state.Active');

    const expired = bodyRows()[3]!;
    expect(expired.textContent).toContain('permits.state.Expired');
    expect(expired.textContent).toContain('permits.list.endedEarly');
    expect(bodyRows()[2]!.textContent).toContain('permits.state.Draft');
  });

  it('смуга показників: усі, чинні, закінчуються, ніким не читаються', async () => {
    stub();
    show();
    await screen.findByText('KZ06VCZ14825472');

    const strip = screen.getByRole('group', { name: 'permits.list.stats.aria' });
    expect(within(strip).queryByRole('button', { name: /permits\.list\.stat\.all/ })).toBeNull();
    expect(within(strip).getByRole('button', { name: /permits\.list\.stat\.active/ }).textContent).toContain('2');
    expect(within(strip).getByRole('button', { name: /permits\.list\.stat\.ending/ }).textContent).toContain('1');
    expect(within(strip).getByRole('button', { name: /permits\.list\.stat\.free/ }).textContent).toContain('2');
  });

  it('показник «ending» лишає один дозвіл і пише ?stat=ending; повторний клік знімає', async () => {
    stub();
    show();
    await screen.findByText('KZ06VCZ14825472');

    fireEvent.click(screen.getByRole('button', { name: /permits\.list\.stat\.ending/ }));
    await waitFor(() => expect(bodyRows()).toHaveLength(1));
    expect(where()).toContain('stat=ending');
    expect(screen.queryByText('OLD-2024')).toBeNull();

    fireEvent.click(screen.getByRole('button', { name: /permits\.list\.stat\.ending/ }));
    await waitFor(() => expect(bodyRows()).toHaveLength(4));
  });

  it('фільтри з адреси: тип, стан, пошук', async () => {
    stub();
    show('/admin/permits?type=WaterDischarge');
    expect(await screen.findByText('KZ14VCZ14523270')).toBeTruthy();
    expect(bodyRows()).toHaveLength(1);
    cleanup();

    show('/admin/permits?state=Draft');
    expect(await screen.findByText('DRAFT-2027')).toBeTruthy();
    expect(bodyRows()).toHaveLength(1);
    cleanup();

    show('/admin/permits?q=offshore');
    expect(await screen.findByText('KZ14VCZ14523270')).toBeTruthy();
    expect(bodyRows()).toHaveLength(1);
  });

  it('фільтри в панелі: стан пропонує Active/Draft/Expired, тип — лише ті, що є в переліку', async () => {
    stub();
    show();
    await screen.findByText('KZ06VCZ14825472');

    fireEvent.click(screen.getByRole('textbox', { name: 'permits.list.filter.state' }));
    expect((await screen.findAllByRole('option')).map((o) => o.textContent)).toEqual([
      'permits.state.Active',
      'permits.state.Draft',
      'permits.state.Expired',
    ]);
    fireEvent.click(screen.getByRole('option', { name: 'permits.state.Expired' }));
    await waitFor(() => expect(bodyRows()).toHaveLength(1));
    expect(where()).toContain('state=Expired');

    fireEvent.click(screen.getByRole('textbox', { name: 'permits.list.filter.type' }));
    expect((await screen.findAllByRole('option')).map((o) => o.textContent)).toEqual([
      'Air emission permit',
      'Water discharge permit',
    ]);
  });

  it('пошук, що нічого не знайшов: повідомлення, «Clear» скидає фільтри в адресі', async () => {
    stub();
    show('/admin/permits?q=zzz&state=Draft&type=AirEmission&stat=free');

    expect(await screen.findByText('permits.list.noMatches')).toBeTruthy();
    // Кнопка САМОЇ таблиці скидає всі чотири параметри, зокрема показник (панель фільтрів показник не знає).
    const tableBox = document.querySelector<HTMLElement>('[data-list-table]')!;
    fireEvent.click(within(tableBox).getByRole('button', { name: 'filters.clear' }));
    await waitFor(() => expect(bodyRows()).toHaveLength(4));
    expect(where()).toBe('/admin/permits');
  });
});

describe('PermitsPage: навігація', () => {
  it('клік по рядку відкриває сторінку дозволу (перехід, не шухляда)', async () => {
    stub();
    show();
    await screen.findByText('KZ06VCZ14825472');

    fireEvent.click(within(bodyRows()[1]!).getByText('Offshore complex'));
    await waitFor(() => expect(where()).toBe('/admin/permits/2'));
    expect(screen.getByText('permit page')).toBeTruthy();
  });

  it('номер — посилання (клавіатурою), без подвійного переходу', async () => {
    stub();
    show();
    const link = await screen.findByRole('link', { name: 'KZ06VCZ14825472' });
    expect(link.getAttribute('href')).toBe('/admin/permits/1');

    fireEvent.click(link);
    await waitFor(() => expect(where()).toBe('/admin/permits/1'));
  });
});

describe('PermitsPage: стани', () => {
  it('завантаження: рядків і смуги показників немає', () => {
    vi.stubGlobal('fetch', vi.fn(() => new Promise<Response>(() => undefined)));
    show();

    expect(screen.queryByText('KZ06VCZ14825472')).toBeNull();
    expect(screen.queryByRole('group', { name: 'permits.list.stats.aria' })).toBeNull();
  });

  it('порожній: заголовок і підказка, «New permit» у шапці й у стані, без смуги й фільтрів', async () => {
    stub([]);
    show();

    expect(await screen.findByText('permits.empty.title')).toBeTruthy();
    expect(screen.getByText('permits.list.empty.hint')).toBeTruthy();
    expect(screen.getAllByRole('button', { name: 'permits.empty.new' })).toHaveLength(2);
    expect(screen.queryByRole('group', { name: 'permits.list.stats.aria' })).toBeNull();
    expect(screen.queryByRole('textbox', { name: 'permits.list.search.label' })).toBeNull();
  });

  it('помилка сервера: відмова з повтором, перелік не вигаданий', async () => {
    let attempts = 0;
    stub(FixturePermits, () => {
      attempts += 1;

      return attempts === 1
        ? json({ title: 'Unavailable', status: 503, errorCode: 'ECR-SYS-0503', correlationId: 'c' }, 503)
        : json({ items: FixturePermits, nextCursor: null, totalCount: 4 });
    });
    show();

    expect(await screen.findByText('Unavailable')).toBeTruthy();
    expect(screen.queryByText('permits.empty.title')).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: /retry/i }));
    expect(await screen.findByText('KZ06VCZ14825472')).toBeTruthy();
  });
});

describe('PermitsPage: права', () => {
  it('з правом запису: «New permit» в шапці відкриває майстер (лінивим чанком)', async () => {
    stub();
    show();
    await screen.findByText('KZ06VCZ14825472');

    fireEvent.click(screen.getByRole('button', { name: 'permits.empty.new' }));
    expect(await screen.findByRole('dialog')).toBeTruthy();
    expect(screen.getByText('permits.list.wizard.title')).toBeTruthy();
  });

  it('тільки читання: немає «New permit», немає колонки дій і меню; перелік видно', async () => {
    session.permissions = ['Registry.View'];
    stub();
    show();
    await screen.findByText('KZ06VCZ14825472');

    expect(screen.queryByRole('button', { name: 'permits.empty.new' })).toBeNull();
    expect(headers()).not.toContain('permits.list.col.actions');
    expect(screen.queryByRole('button', { name: /permits\.list\.rowMenu/ })).toBeNull();
  });

  it('тільки читання і порожній перелік: кнопки створення немає і в порожньому стані', async () => {
    session.permissions = ['Registry.View'];
    stub([]);
    show();

    await screen.findByText('permits.empty.title');
    expect(screen.queryByRole('button', { name: 'permits.empty.new' })).toBeNull();
  });

  it('у симуляції запису немає навіть з правом', async () => {
    session.isSimulation = true;
    stub();
    show();
    await screen.findByText('KZ06VCZ14825472');

    expect(screen.queryByRole('button', { name: 'permits.empty.new' })).toBeNull();
    expect(headers()).not.toContain('permits.list.col.actions');
  });
});

describe('PermitsPage: меню дій рядка', () => {
  async function openMenu(rowIndex: number): Promise<string[]> {
    const row = bodyRows()[rowIndex]!;
    fireEvent.click(within(row).getByRole('button', { name: /permits\.list\.rowMenu/ }));
    const items = await screen.findAllByRole('menuitem');

    return items.map((item) => item.getAttribute('data-permit-action') ?? '');
  }

  it('дії за станом: чернетка, чинний, закінчений', async () => {
    stub();
    show();
    await screen.findByText('KZ06VCZ14825472');

    expect(await openMenu(2)).toEqual(['activate', 'delete']);
    fireEvent.keyDown(document.activeElement ?? document.body, { key: 'Escape' });
    cleanup();

    stub();
    show();
    await screen.findByText('KZ06VCZ14825472');
    expect(await openMenu(0)).toEqual(['renew', 'end']);
    cleanup();

    stub();
    show();
    await screen.findByText('KZ06VCZ14825472');
    expect(await openMenu(3)).toEqual(['renew']);
  });

  it('меню не відкриває рядок', async () => {
    stub();
    show();
    await screen.findByText('KZ06VCZ14825472');

    await openMenu(0);
    expect(where()).toBe('/admin/permits');
  });

  it('«Activate…» з меню чернетки: діалог, підтвердження, POST і повторне читання переліку', async () => {
    stub();
    show();
    await screen.findByText('KZ06VCZ14825472');

    await openMenu(2);
    fireEvent.click(screen.getByRole('menuitem', { name: 'permits.list.action.activate' }));
    expect((await screen.findByRole('dialog')).textContent).toContain('permits.list.activate.title {"number":"DRAFT-2027"}');

    fireEvent.click(screen.getByRole('button', { name: 'permits.list.activate.verb' }));
    await waitFor(() => expect(calls.some((c) => c.method === 'POST' && c.url === '/api/v1/permits/3/activate')).toBe(true));
    await waitFor(() => expect(calls.filter((c) => c.url === '/api/v1/permits?limit=500')).toHaveLength(2));
  });
});
