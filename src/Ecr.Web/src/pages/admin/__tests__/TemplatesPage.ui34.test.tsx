import { describe, it, expect, vi, afterEach } from 'vitest';
import type { JSX } from 'react';
import { render, screen, fireEvent, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter, Route, Routes, useLocation, useParams } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { TemplatesPage } from '@/pages/admin/TemplatesPage';
import { testTheme } from '@/test/render';

/**
 * `UI-34`: перелік шаблонів за макетом (`screens-templates.js`, «1.
 * /admin/templates — перелік»): смуга показників, фільтр, рядок веде на
 * картку, і ЖОДНИХ колонок без даних.
 */

const templates = {
  items: [
    {
      id: 1,
      code: 'AIR',
      versionCount: 3,
      nameL10n: { values: { en: 'Air emissions' } },
      documentCount: 12,
      isArchived: false,
      updatedAt: '2026-10-01T08:00:00Z',
      draftAuthorDisplayName: 'G. Tulegenova',
      draftCreatedAt: '2026-10-02T08:00:00Z',
    },
    { id: 2, code: 'WATER', versionCount: 1, documentCount: 0, isArchived: false },
    { id: 3, code: 'WASTE', versionCount: 1, documentCount: 3, isArchived: false },
    { id: 4, code: 'FLARE', versionCount: 1, documentCount: 0, isArchived: true },
  ],
  nextCursor: null,
  totalCount: 4,
};

const v = (id: number, version: string, status: string) => ({
  id,
  version,
  status,
  presentationRevision: 0,
  publishedAt: null,
  clonedFromVersionId: null,
});

const versions: Record<string, unknown[]> = {
  '1': [v(11, '1.0.0', 'Published'), v(12, '1.1.0', 'Published'), v(13, '1.2.0', 'Draft')],
  '2': [v(21, '0.1.0', 'Draft')],
  '3': [v(31, '1.0.0', 'Deprecated')],
  '4': [v(41, '2.0.0', 'Published')],
};

function mockApi(list: unknown = templates): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      const json = (body: unknown): Response =>
        new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });

      if (url.includes('/api/v1/me')) {
        return json({
          denies: [],
          grants: {},
          isSimulation: false,
          language: 'en',
          mustChangePassword: false,
          permissions: ['Template.View', 'Template.Edit'],
          simulatedForUserId: null,
          userId: 1,
          userName: 'tester',
        });
      }

      if (/\/api\/v1\/templates\/versions(\?|$)/.test(url)) {
        const ids = [...url.matchAll(/[?&]ids=(\d+)/g)].map((m) => m[1] ?? '');

        return json(ids.map((id) => ({ templateId: Number(id), versions: versions[id] ?? [] })));
      }

      if (url.includes('/api/v1/templates')) return json(list);

      return json(null);
    }),
  );
}

function Card(): JSX.Element {
  const { id } = useParams();

  return <p>card:{id}</p>;
}

function Version(): JSX.Element {
  const location = useLocation();

  return <p>version:{location.pathname}</p>;
}

function show(entry = '/admin/templates', list: unknown = templates): void {
  mockApi(list);

  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={testTheme}>
      <MemoryRouter initialEntries={[entry]}>
        <QueryClientProvider client={client}>
          <Routes>
            <Route path="/admin/templates" element={<TemplatesPage />} />
            <Route path="/admin/templates/:id" element={<Card />} />
            <Route path="/admin/templates/:id/versions/:versionId" element={<Version />} />
          </Routes>
        </QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );
}

function codes(table: HTMLElement): string[] {
  return within(table)
    .getAllByRole('row')
    .slice(1)
    .map((row) => (row.querySelector('td')?.textContent ?? '').trim());
}

async function loadedTable(): Promise<HTMLElement> {
  const table = await screen.findByRole('table');
  await within(table).findByText('1.1.0');

  return table;
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('TemplatesPage (UI-34): колонки макета — лише з даних, що є', () => {
  it('поточна версія — остання опублікована, чернетка — окремо', async () => {
    show();
    const table = await loadedTable();

    const air = within(table).getAllByRole('row').find((row) => row.textContent?.includes('AIR'));
    expect(air).toBeDefined();

    const links = within(air as HTMLElement).getAllByRole('link').map((link) => link.textContent);
    // ⛔ 1.0.0 (стара опублікована) у рядку НЕ показується — лише чинна і чернетка.
    expect(links).toEqual(['Air emissions', '1.1.0', '1.2.0']);
    // Назва — посиланням, код — другим рядком; автор чернетки — під нею.
    expect(air?.textContent).toContain('AIR');
    expect(air?.textContent).toContain('templates.draftBy');
  });

  it('шість колонок макета: Template · Current · Draft · Documents · Updated · State', async () => {
    show();
    const table = await loadedTable();

    const headers = within(table)
      .getAllByRole('columnheader')
      .map((cell) => (cell.textContent ?? '').replace(/[⟦⟧]/g, ''));

    expect(headers).toEqual([
      'templates.card',
      'templates.currentVersion',
      'templates.draft',
      'templates.documents',
      'templates.updated',
      'templates.state',
    ]);
  });

  it('архівований шаблон має стан Archived, хоч версія й опублікована', async () => {
    show();
    const table = await loadedTable();

    const flare = within(table).getAllByRole('row').find((row) => row.textContent?.includes('FLARE'));

    expect(flare?.querySelector('[data-status-state]')?.getAttribute('data-status-state')).toBe('Archived');
  });

  it('шаблон без поточної версії називає останню застарілу і каже, що поточної немає', async () => {
    show();
    const table = await loadedTable();

    const waste = within(table).getAllByRole('row').find((row) => row.textContent?.includes('WASTE'));

    expect(waste?.textContent).toContain('templates.noCurrentVersion');
  });
});

describe('TemplatesPage (UI-34): смуга показників і фільтри в адресі', () => {
  it('смуга рахує шаблони, опубліковані версії й чернетки', async () => {
    show();
    await loadedTable();

    const strip = screen.getByRole('group', { name: /templates\.stats⟧/ });

    expect(strip.textContent).toMatch(/4\s*⟦templates\.stat\.all⟧/);
    expect(strip.textContent).toMatch(/3\s*⟦templates\.stat\.published⟧/);
    expect(strip.textContent).toMatch(/2\s*⟦templates\.stat\.drafts⟧/);
    expect(strip.textContent).toMatch(/15\s*⟦templates\.stat\.documents⟧/);
  });

  it('клац по «drafts» лишає шаблони з чернеткою', async () => {
    show();
    const table = await loadedTable();

    fireEvent.click(screen.getByRole('button', { name: /templates\.stat\.drafts⟧/ }));

    expect(codes(table)).toEqual(['Air emissionsAIR', 'WATER']);
  });

  it('?state= з адреси звужує перелік одразу', async () => {
    show('/admin/templates?state=Deprecated');
    const table = await screen.findByRole('table');
    await within(table).findByText('WASTE');

    expect(codes(table)).toEqual(['WASTE']);
  });

  it('пошук без збігів — окремий стан «нічого не знайдено», не «шаблонів немає»', async () => {
    show('/admin/templates?q=zzz');

    expect(await screen.findByText(/templates\.noMatch⟧/)).toBeDefined();
    expect(screen.queryByText(/templates\.empty⟧/)).toBeNull();
  });
});

describe('TemplatesPage (UI-34): рядок веде на картку', () => {
  it('клац по рядку відкриває картку шаблону', async () => {
    show();
    const table = await loadedTable();

    const water = within(table).getAllByRole('row').find((row) => row.textContent?.includes('WATER'));
    fireEvent.click(water?.querySelector('td:last-child') as HTMLElement);

    expect(await screen.findByText('card:2')).toBeDefined();
  });

  it('клац по версії відкриває ВЕРСІЮ, а не картку', async () => {
    show();
    const table = await loadedTable();

    fireEvent.click(within(table).getByRole('link', { name: '1.2.0' }));

    expect(await screen.findByText('version:/admin/templates/1/versions/13')).toBeDefined();
    expect(screen.queryByText(/^card:/)).toBeNull();
  });

  it('«New template» — єдина головна дія шапки', async () => {
    show();
    await loadedTable();

    expect(screen.getByRole('button', { name: /templates\.create⟧/ })).toBeDefined();
    expect(screen.queryByRole('button', { name: /templates\.newVersion⟧/ })).toBeNull();
  });
});

describe('TemplatesPage (UI-34b): сервер без нових полів', () => {
  // ⚠ Старий сервер: лише {id, code, versionCount}. Колонки й показника без
  // даних немає (D15-06), а не «0».
  const old = {
    items: [
      { id: 1, code: 'AIR', versionCount: 3 },
      { id: 2, code: 'WATER', versionCount: 1 },
    ],
    nextCursor: null,
    totalCount: 2,
  };

  it('немає колонок Documents і Updated, немає показника документів', async () => {
    show('/admin/templates', old);
    const table = await loadedTable();

    const headers = within(table)
      .getAllByRole('columnheader')
      .map((cell) => (cell.textContent ?? '').replace(/[⟦⟧]/g, ''));

    expect(headers).toEqual(['templates.card', 'templates.currentVersion', 'templates.draft', 'templates.state']);
    expect(screen.getByRole('group', { name: /templates\.stats⟧/ }).textContent).not.toMatch(/templates\.stat\.documents⟧/);
    expect(codes(table)).toEqual(['AIR', 'WATER']);
  });
});

describe('TemplatesPage: одне пояснення під заголовком (batch-2-a, дефект 3)', () => {
  it('пояснення — у рядку пояснення сторінки, без другого `meta` і без `<p>` у `<p>`', async () => {
    show();
    await loadedTable();

    const lines = screen.getAllByTestId('page-description');
    expect(lines).toHaveLength(1);
    expect(lines[0]?.textContent).toMatch(/templates\.subtitle/);
    expect(document.querySelectorAll('p p')).toHaveLength(0);
  });
});
