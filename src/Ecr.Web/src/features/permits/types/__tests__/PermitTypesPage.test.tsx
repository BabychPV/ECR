import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom';
import { testTheme } from '@/test/render';
import { PermitTypesPage } from '@/pages/admin/PermitTypesPage';
import { FixtureTypes } from './typesFixture';

/**
 * Перелік типів пермітів (PM-08a): дані, завантаження, порожній стан, помилка, пошук і категорія з адреси,
 * показник-фільтр, перехід у редактор, права на «New type».
 *
 * Мутаційний доказ (вручну, відновлено): без `hasTypes &&` над смугою показників порожній стан малював би
 * нулі (падає «порожній»); `onRowClick` без `navigate` валить «клік по рядку»; `can(...)` без
 * `PermitTypesPermission` у `mayCreate` валить «без права кнопки немає»; `filtered` завжди `false` валить
 * «пошук без збігів».
 */
vi.mock('@/shared/i18n', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/i18n')>()),
  t: (key: string, params?: Record<string, string | number>) =>
    params === undefined ? key : `${key} ${JSON.stringify(params)}`,
  language: () => 'en',
}));

const session = vi.hoisted(() => ({
  permissions: ['Registry.View', 'Permit.EditType'] as string[],
  isSimulation: false,
}));
vi.mock('@/shared/session/useSession', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/session/useSession')>()),
  useSession: () => ({ data: { permissions: session.permissions, isSimulation: session.isSimulation } }),
}));

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

let requests: string[] = [];

function stubList(reply: () => Response): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      requests.push(String(input));

      return reply();
    }),
  );
}

const page = (items = FixtureTypes): Response => json({ items, nextCursor: null, totalCount: items.length });

function Where(): null {
  const location = useLocation();
  (globalThis as { __where?: string }).__where = location.pathname + location.search;

  return null;
}

function show(entry = '/admin/permits/types'): void {
  render(
    <MantineProvider theme={testTheme}>
      <MemoryRouter initialEntries={[entry]}>
        <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
          <Where />
          <Routes>
            <Route path="/admin/permits/types" element={<PermitTypesPage />} />
            <Route path="/admin/permits/types/:code" element={<p>editor</p>} />
          </Routes>
        </QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );
}

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  requests = [];
  session.permissions = ['Registry.View', 'Permit.EditType'];
  session.isSimulation = false;
  delete (globalThis as { __where?: string }).__where;
});

const where = (): string | undefined => (globalThis as { __where?: string }).__where;
const bodyRows = (): HTMLElement[] => within(screen.getByRole('table')).getAllByRole('row').slice(1);

describe('PermitTypesPage: дані', () => {
  it('читає ВСІ типи одним запитом limit=500, малює рядки в стовпцях макета', async () => {
    stubList(() => page());
    show();

    expect(await screen.findByText('Air emission permit')).toBeTruthy();
    expect(requests[0]).toBe('/api/v1/permit-types?limit=500');
    expect(bodyRows()).toHaveLength(3);

    const headers = within(screen.getByRole('table')).getAllByRole('columnheader').map((h) => h.textContent);
    expect(headers).toEqual([
      'permits.types.col.type',
      'permits.types.col.category',
      'permits.types.col.tables',
      'permits.types.col.permits',
      'permits.types.col.updated',
      'permits.types.col.state',
      'permits.list.col.actions',
    ]);

    const air = bodyRows()[0]!;
    expect(air.textContent).toContain('Разрешение на выбросы');
    expect(air.textContent).toContain('permits.category.Air');
    expect(air.textContent).toContain('Emission limits (6) · Sources (4)');
    expect(air.textContent).toContain('permits.types.state.published {"version":3}');
    expect(bodyRows()[1]!.textContent).toContain('permits.types.state.draft');
  });

  it('смуга показників: типи, таблиці, зі змінами', async () => {
    stubList(() => page());
    show();
    await screen.findByText('Air emission permit');

    const strip = screen.getByRole('group', { name: 'permits.types.stats.aria' });
    expect(strip.textContent).toContain('3');
    expect(strip.textContent).toContain('4');
    expect(within(strip).getByRole('button', { name: /permits\.types\.stat\.draft/ }).textContent).toContain('2');
    expect(within(strip).queryByRole('button', { name: /permits\.types\.stat\.all/ })).toBeNull();
  });

  it('клік по показнику «зі змінами» лишає лише такі типи й пише ?stat=draft; повторний клік знімає', async () => {
    stubList(() => page());
    show();
    await screen.findByText('Air emission permit');

    const button = screen.getByRole('button', { name: /permits\.types\.stat\.draft/ });
    fireEvent.click(button);
    await waitFor(() => expect(bodyRows()).toHaveLength(2));
    expect(where()).toContain('stat=draft');
    expect(screen.queryByText('Air emission permit')).toBeNull();

    fireEvent.click(screen.getByRole('button', { name: /permits\.types\.stat\.draft/ }));
    await waitFor(() => expect(bodyRows()).toHaveLength(3));
  });

  it('пошук ?q= за назвою будь-якою мовою й категорія ?category= з адреси', async () => {
    stubList(() => page());
    show('/admin/permits/types?q=%D1%81%D0%B1%D1%80%D0%BE%D1%81');
    expect(await screen.findByText('Water discharge permit')).toBeTruthy();
    expect(bodyRows()).toHaveLength(1);
    cleanup();

    show('/admin/permits/types?category=Waste');
    expect(await screen.findByText('Waste storage permit')).toBeTruthy();
    expect(bodyRows()).toHaveLength(1);
  });

  it('пошук, що нічого не знайшов: повідомлення й «Clear» скидає фільтри в адресі', async () => {
    stubList(() => page());
    show('/admin/permits/types?q=zzz&category=Air');

    expect(await screen.findByText('permits.types.noMatches')).toBeTruthy();
    fireEvent.click(screen.getAllByRole('button', { name: 'filters.clear' })[0]!);
    await waitFor(() => expect(bodyRows()).toHaveLength(3));
    expect(where()).toBe('/admin/permits/types');
  });

  it('введення в поле пошуку звужує перелік', async () => {
    stubList(() => page());
    show();
    await screen.findByText('Air emission permit');

    fireEvent.change(screen.getByRole('textbox', { name: 'permits.types.search.label' }), {
      target: { value: 'storage' },
    });
    await waitFor(() => expect(bodyRows()).toHaveLength(1));
    expect(screen.getByText('Waste storage permit')).toBeTruthy();
  });

  it('фільтр категорій пропонує Air/Water/Waste, Other — лише коли такі типи є', async () => {
    stubList(() => page());
    show();
    await screen.findByText('Air emission permit');

    fireEvent.click(screen.getByRole('textbox', { name: 'permits.types.filter.category' }));
    const options = (await screen.findAllByRole('option')).map((o) => o.textContent);
    expect(options).toEqual(['permits.category.Air', 'permits.category.Water', 'permits.category.Waste']);
  });
});

describe('PermitTypesPage: навігація', () => {
  it('клік по рядку веде в редактор типу', async () => {
    stubList(() => page());
    show();
    await screen.findByText('Air emission permit');

    fireEvent.click(within(bodyRows()[1]!).getByText('permits.category.Water'));
    await waitFor(() => expect(where()).toBe('/admin/permits/types/WaterDischarge'));
    expect(screen.getByText('editor')).toBeTruthy();
  });

  it('назва типу — посилання на редактор (клавіатурою), і воно не подвоює перехід', async () => {
    stubList(() => page());
    show();
    const link = await screen.findByRole('link', { name: 'Air emission permit' });
    expect(link.getAttribute('href')).toBe('/admin/permits/types/AirEmission');

    fireEvent.click(link);
    await waitFor(() => expect(where()).toBe('/admin/permits/types/AirEmission'));
  });
});

describe('PermitTypesPage: меню рядка «Delete type…» (PA-U-02)', () => {
  const menuOf = (code: string): HTMLElement => document.querySelector(`[data-type-menu="${code}"]`) as HTMLElement;

  it('чернетка: пункт активний і веде в редактор з відкритим діалогом видалення; рядок не відкривається', async () => {
    stubList(() => page());
    show();
    await screen.findByText('Air emission permit');

    // WasteStorage у фікстурі — status Draft (ніколи не публікувався).
    fireEvent.click(menuOf('WasteStorage'));
    const item = await screen.findByRole('menuitem', { name: 'permits.editor.actions.deleteType' });
    expect(item.hasAttribute('disabled') || item.getAttribute('data-disabled') === 'true').toBe(false);
    fireEvent.click(item);

    await waitFor(() => expect(where()).toBe('/admin/permits/types/WasteStorage?dialog=delete-type'));
  });

  it('опублікований: пункт вимкнений, причина в title, переходу немає', async () => {
    stubList(() => page());
    show();
    await screen.findByText('Air emission permit');

    fireEvent.click(menuOf('AirEmission'));
    const item = await screen.findByRole('menuitem', { name: /permits\.editor\.actions\.deleteType/ });
    expect(item.getAttribute('title')).toBe('permits.editor.dialogs.deleteType.published');
    // PA2-U-04: пункт досяжний з клавіатури (не нативний `disabled`), причину читає читалка через aria-describedby.
    expect(item.hasAttribute('disabled')).toBe(false);
    expect(item.getAttribute('aria-disabled')).toBe('true');
    const reason = document.getElementById(item.getAttribute('aria-describedby') ?? '');
    expect(reason?.textContent).toBe('permits.editor.dialogs.deleteType.published');
    fireEvent.click(item);
    expect(where()).toBe('/admin/permits/types');
  });

  it('без права правити типи колонки дій немає', async () => {
    session.permissions = ['Registry.View'];
    stubList(() => page());
    show();
    await screen.findByText('Air emission permit');

    expect(document.querySelector('[data-type-menu]')).toBeNull();
  });
});

describe('PermitTypesPage: стани', () => {
  it('завантаження: таблиці з даними ще немає, смуги показників немає', () => {
    vi.stubGlobal('fetch', vi.fn(() => new Promise<Response>(() => undefined)));
    show();

    expect(screen.queryByText('Air emission permit')).toBeNull();
    expect(screen.queryByRole('group', { name: 'permits.types.stats.aria' })).toBeNull();
  });

  it('порожній: заголовок і підказка, кнопка «New type» у стані й у шапці, без смуги й фільтрів', async () => {
    stubList(() => page([]));
    show();

    expect(await screen.findByText('permits.types.empty.title')).toBeTruthy();
    expect(screen.getByText('permits.types.empty.hint')).toBeTruthy();
    expect(screen.getAllByRole('button', { name: 'permits.types.new' })).toHaveLength(2);
    expect(screen.queryByRole('group', { name: 'permits.types.stats.aria' })).toBeNull();
    expect(screen.queryByRole('textbox', { name: 'permits.types.search.label' })).toBeNull();
  });

  it('помилка сервера: показано відмову з повтором, перелік не вигаданий', async () => {
    let calls = 0;
    stubList(() => {
      calls += 1;

      return calls === 1
        ? json({ title: 'Unavailable', status: 503, detail: 'down', errorCode: 'ECR-SYS-0503', correlationId: 'c' }, 503)
        : page();
    });
    show();

    expect(await screen.findByText('Unavailable')).toBeTruthy();
    expect(screen.getByText('ECR-SYS-0503')).toBeTruthy();
    expect(screen.queryByText('permits.types.empty.title')).toBeNull();

    fireEvent.click(screen.getByRole('button', { name: /retry/i }));
    expect(await screen.findByText('Air emission permit')).toBeTruthy();
  });
});

describe('PermitTypesPage: «New type»', () => {
  it('з правом: кнопка в шапці відкриває майстер (лінивим чанком)', async () => {
    stubList(() => page());
    show();
    await screen.findByText('Air emission permit');

    fireEvent.click(screen.getByRole('button', { name: 'permits.types.new' }));
    expect(await screen.findByRole('dialog')).toBeTruthy();
    expect(screen.getByText('permits.types.wizard.title')).toBeTruthy();
  });

  it('без права кнопки немає ні в шапці, ні в порожньому стані', async () => {
    session.permissions = ['Registry.View'];
    stubList(() => page([]));
    show();

    await screen.findByText('permits.types.empty.title');
    expect(screen.queryByRole('button', { name: 'permits.types.new' })).toBeNull();
  });

  it('у симуляції кнопки немає навіть з правом', async () => {
    session.isSimulation = true;
    stubList(() => page());
    show();

    await screen.findByText('Air emission permit');
    expect(screen.queryByRole('button', { name: 'permits.types.new' })).toBeNull();
  });
});
