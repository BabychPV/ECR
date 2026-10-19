import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom';
import { testTheme } from '@/test/render';
import type { TemplateSummary, TemplateVersionSummary } from '@/api/types';
import type { PermitBindings } from '../../api/bindingsTypes';
import { PermitBindingsPage } from '@/pages/admin/PermitBindingsPage';
import { ScanConcurrency, ScanPageSize } from '../scanModel';
import { binding, envelope, json, template, version } from './bindingsFixture';

/**
 * Перелік прив'язок аркушів (читання): порційне читання з обмеженою паралельністю, помилка окремої версії, права,
 * порожній стан, «Check more», пошук і режим з адреси, перехід на екран прив'язки.
 *
 * Мутаційний доказ (вручну, відновлено): `Math.min(ScanConcurrency, ...)` замість 4 → `todo.length` валить «не більше
 * 4 запитів»; `useBindingsScan(true, …)` замість `mayView` робить запити без права (падає «без права»); `throw error`
 * замість результату-помилки валить «помилка однієї версії»; зворотний порядок кодів валить «порція». ⚠ Прибрана
 * перевірка `claimed.current.has` тест НЕ ловить: повторне читання все одно віддає кеш `fetchQuery`, тож у мережі
 * різниці немає - це захист від зайвої роботи, а не від видимого дефекту.
 */
vi.mock('@/shared/i18n', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/i18n')>()),
  t: (key: string, params?: Record<string, string | number>) =>
    params === undefined ? key : `${key} ${JSON.stringify(params)}`,
  language: () => 'en',
}));

const session = vi.hoisted(() => ({ permissions: ['Template.View'] as string[] }));
vi.mock('@/shared/session/useSession', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/session/useSession')>()),
  useSession: () => ({ data: { permissions: session.permissions, isSimulation: false } }),
}));

interface Stand {
  templates: TemplateSummary[];
  versions: Map<number, TemplateVersionSummary[]>;
  /** Прив'язки за версією; `'fail'` - сервер відповідає 500. */
  bindings: Map<number, PermitBindings | 'fail'>;
  hold: boolean;
  nextCursor: string | null;
}

interface Held {
  readonly versionId: number;
  readonly release: () => void;
}

const run = {
  requests: [] as string[],
  inflight: 0,
  max: 0,
  held: [] as Held[],
};

let stand: Stand;

function install(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      run.requests.push(url);

      if (url.includes('/api/v1/templates?limit=100')) {
        return json({ items: stand.templates, nextCursor: stand.nextCursor, totalCount: stand.templates.length });
      }
      if (url.includes('/api/v1/templates/versions?')) {
        const ids = [...new URL(url, 'http://x').searchParams.getAll('ids')].map(Number);

        return json(ids.map((templateId) => ({ templateId, versions: stand.versions.get(templateId) ?? [] })));
      }
      const match = /template-versions\/(\d+)\/permit-bindings$/.exec(url);
      if (match !== null) {
        const versionId = Number(match[1]);
        run.inflight += 1;
        run.max = Math.max(run.max, run.inflight);
        if (stand.hold) {
          await new Promise<void>((resolve) => run.held.push({ versionId, release: resolve }));
        }
        run.inflight -= 1;
        const reply = stand.bindings.get(versionId) ?? envelope();

        return reply === 'fail' ? json({ title: 'Server error', status: 500 }, 500) : json(reply);
      }

      return json({ title: 'Not found', status: 404 }, 404);
    }),
  );
}

function bindingRequests(): string[] {
  return run.requests.filter((url) => /permit-bindings$/.test(url));
}

function Where(): null {
  const location = useLocation();
  (globalThis as { __where?: string }).__where = location.pathname + location.search;

  return null;
}

function show(entry = '/admin/permits/bindings'): void {
  render(
    <MantineProvider theme={testTheme}>
      <MemoryRouter initialEntries={[entry]}>
        <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
          <Where />
          <Routes>
            <Route path="/admin/permits/bindings" element={<PermitBindingsPage />} />
            <Route path="/admin/permits/bindings/:id" element={<p>detail</p>} />
          </Routes>
        </QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );
}

/** N шаблонів із кодами T01.., одна опублікована версія кожного (id версії = 100 + id шаблону). */
function manyTemplates(count: number): void {
  stand.templates = Array.from({ length: count }, (_, index) => template(index + 1, `T${String(index + 1).padStart(2, '0')}`));
  stand.versions = new Map(stand.templates.map((tpl) => [tpl.id, [version(100 + tpl.id, 'Published')]]));
}

beforeEach(() => {
  session.permissions = ['Template.View'];
  stand = { templates: [], versions: new Map(), bindings: new Map(), hold: false, nextCursor: null };
  run.requests = [];
  run.inflight = 0;
  run.max = 0;
  run.held = [];
  install();
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe('перелік прив’язок: читання версій', () => {
  it('одночасно читається не більше ScanConcurrency версій, решта - у міру звільнення місць', async () => {
    manyTemplates(10);
    stand.hold = true;
    show();

    await waitFor(() => expect(run.held).toHaveLength(ScanConcurrency));
    expect(bindingRequests()).toHaveLength(ScanConcurrency);

    // Звільнили одне місце - стартувала рівно одна наступна версія, а не всі решта.
    await act(async () => {
      run.held.shift()?.release();
    });
    await waitFor(() => expect(bindingRequests()).toHaveLength(ScanConcurrency + 1));

    while (bindingRequests().length < 10 || run.held.length > 0) {
      const next = run.held.shift();
      if (next === undefined) {
        await act(async () => {
          await new Promise((resolve) => setTimeout(resolve, 5));
        });
        continue;
      }
      await act(async () => {
        next.release();
      });
    }

    expect(run.max).toBe(ScanConcurrency);
    expect(new Set(bindingRequests()).size).toBe(10);
  });

  it('рядки з’являються за мірою відповідей, з назвою таблиці, правилом і шаблоном', async () => {
    manyTemplates(2);
    stand.bindings.set(101, envelope(binding('B1')));
    stand.bindings.set(102, envelope(binding('B2', { mode: 'Formulas', isActive: false })));
    show();

    expect(await screen.findByRole('link', { name: 'TBL_B1' })).toBeTruthy();
    expect(await screen.findByRole('link', { name: 'TBL_B2' })).toBeTruthy();
    const row = screen.getByRole('link', { name: 'TBL_B2' }).closest('tr');
    expect(row?.textContent).toContain('permits.bindingsList.mode.Formulas');
    expect(row?.textContent).toContain('permits.bindingsList.inactive');
    expect(row?.textContent).toContain('T02 · 1.0.0.0');
    expect(screen.getByText(/permits\.bindingsList\.scan\.done/).textContent).toContain('"checked":2,"total":2,"versions":2');
  });

  it('помилка однієї версії не валить перелік: решта на місці, версія названа, повтор читає лише її', async () => {
    manyTemplates(3);
    stand.bindings.set(101, envelope(binding('B1')));
    stand.bindings.set(102, 'fail');
    stand.bindings.set(103, envelope(binding('B3')));
    show();

    const banner = await screen.findByTestId('bindings-failed');
    expect(banner.textContent).toContain('T02 1.0.0.0');
    expect(banner.textContent).toContain('"count":1');
    expect(screen.getByRole('link', { name: 'TBL_B1' })).toBeTruthy();
    expect(screen.getByRole('link', { name: 'TBL_B3' })).toBeTruthy();

    stand.bindings.set(102, envelope(binding('B2')));
    const before = bindingRequests().length;
    fireEvent.click(within(banner).getByRole('button', { name: 'permits.bindingsList.failed.retry' }));

    expect(await screen.findByRole('link', { name: 'TBL_B2' })).toBeTruthy();
    expect(screen.queryByTestId('bindings-failed')).toBeNull();
    expect(bindingRequests().slice(before)).toHaveLength(1);
    expect(bindingRequests().slice(before)[0]).toContain('/template-versions/102/');
  });

  it('читає і опубліковану версію, і чернетку, а застарілу - ні', async () => {
    stand.templates = [template(1, 'T01')];
    stand.versions = new Map([[1, [version(11, 'Deprecated', '0.9.0.0'), version(12, 'Published'), version(13, 'Draft', '1.1.0.0')]]]);
    stand.bindings.set(12, envelope(binding('PUB')));
    stand.bindings.set(13, envelope(binding('DRF')));
    show();

    expect(await screen.findByRole('link', { name: 'TBL_DRF' })).toBeTruthy();
    expect(screen.getByRole('link', { name: 'TBL_PUB' })).toBeTruthy();
    expect(bindingRequests().some((url) => url.includes('/template-versions/11/'))).toBe(false);
  });
});

describe('перелік прив’язок: порції', () => {
  it('перша порція - ScanPageSize шаблонів, «Check more» додає наступні', async () => {
    manyTemplates(ScanPageSize + 5);
    show();

    await waitFor(() => expect(screen.getByText(/permits\.bindingsList\.scan\.done/)).toBeTruthy());
    expect(bindingRequests()).toHaveLength(ScanPageSize);
    expect(screen.getByText(/permits\.bindingsList\.scan\.done/).textContent).toContain(
      `"checked":${String(ScanPageSize)},"total":${String(ScanPageSize + 5)}`,
    );

    const more = screen.getByRole('button', { name: /permits\.bindingsList\.scan\.more/ });
    expect(more.textContent).toContain('"count":5');
    fireEvent.click(more);

    await waitFor(() => expect(bindingRequests()).toHaveLength(ScanPageSize + 5));
    // Раніше прочитані версії не перечитуються.
    expect(new Set(bindingRequests()).size).toBe(ScanPageSize + 5);
    await waitFor(() => expect(screen.queryByRole('button', { name: /permits\.bindingsList\.scan\.more/ })).toBeNull());
  });

  it('сервер віддав не всі шаблони - про це сказано в примітці', async () => {
    manyTemplates(1);
    stand.nextCursor = 'next';
    show();

    expect(await screen.findByText(/permits\.bindingsList\.scan\.truncated/)).toBeTruthy();
  });
});

describe('перелік прив’язок: стани й права', () => {
  it('без права Template.View - ні запитів, ні таблиці, написано чому', () => {
    session.permissions = ['Registry.View'];
    manyTemplates(2);
    show();

    expect(screen.getByText('permits.bindingsList.noAccess.title')).toBeTruthy();
    expect(screen.queryByRole('table')).toBeNull();
    expect(vi.mocked(fetch)).not.toHaveBeenCalled();
  });

  it('жодної прив’язки - порожній стан з поясненням, де їх створюють', async () => {
    manyTemplates(2);
    show();

    expect(await screen.findByText('permits.bindingsList.empty.title')).toBeTruthy();
    expect(screen.getByText('permits.bindingsList.empty.hint')).toBeTruthy();
    expect(screen.getByText('permits.bindingsList.createHint')).toBeTruthy();
  });

  it('немає жодного шаблону - порожній стан, а не вічне завантаження', async () => {
    show();

    expect(await screen.findByText('permits.bindingsList.empty.title')).toBeTruthy();
    expect(bindingRequests()).toHaveLength(0);
  });

  it('помилка переліку шаблонів - екран помилки, а не «порожньо»', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => json({ title: 'Server error', status: 500 }, 500)),
    );
    show();

    expect(await screen.findByRole('alert')).toBeTruthy();
    expect(screen.queryByText('permits.bindingsList.empty.title')).toBeNull();
  });
});

describe('перелік прив’язок: пошук, режим, перехід', () => {
  function twoBindings(): void {
    manyTemplates(2);
    stand.bindings.set(101, envelope(binding('B1')));
    stand.bindings.set(102, envelope(binding('B2', { mode: 'Formulas', permitTypeCode: 'WastePermit' })));
  }

  it('пошук з адреси лишає лише збіги, режим теж; «нічого не знайдено» - окремий стан', async () => {
    twoBindings();
    show('/admin/permits/bindings?q=wastepermit');

    expect(await screen.findByRole('link', { name: 'TBL_B2' })).toBeTruthy();
    expect(screen.queryByRole('link', { name: 'TBL_B1' })).toBeNull();
    cleanup();

    show('/admin/permits/bindings?mode=Rows');
    expect(await screen.findByRole('link', { name: 'TBL_B1' })).toBeTruthy();
    expect(screen.queryByRole('link', { name: 'TBL_B2' })).toBeNull();
    cleanup();

    show('/admin/permits/bindings?q=немає-такого');
    expect(await screen.findByText('permits.bindingsList.noMatches')).toBeTruthy();
  });

  it('клік по рядку відкриває екран прив’язки з версією й шаблоном в адресі', async () => {
    twoBindings();
    show();

    const link = await screen.findByRole('link', { name: 'TBL_B1' });
    expect(link.getAttribute('href')).toBe('/admin/permits/bindings/B1?template=1&version=101');

    const cell = link.closest('tr')?.querySelectorAll('td')[1];
    if (cell === undefined) throw new Error('рядок без комірок');
    fireEvent.click(cell);

    await screen.findByText('detail');
    expect((globalThis as { __where?: string }).__where).toBe('/admin/permits/bindings/B1?template=1&version=101');
  });
});
