import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { testTheme } from '@/test/render';
import { PermitBindingPage } from '@/pages/admin/PermitBindingPage';
import { binding, json, version } from './bindingsFixture';

/**
 * Екран прив'язки (читання): стрілка, режим і правило, зіставлення колонок за позицією з ролями, режим «лише формули»,
 * неповне посилання, відсутня прив'язка, право, посилання на вкладку Permit версії шаблону.
 *
 * Мутаційний доказ (вручну, відновлено): без `.sort((a, b) => a.ordinal - b.ordinal)` колонки йдуть у порядку відповіді
 * (падає «за позицією»); `binding.mode === 'Formulas'` прибрано - формульна прив'язка малює порожню таблицю (падає
 * «лише формули»); `enabled` без `mayView` - запити без права (падає «без права»).
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

let bindingReply: () => Response;

function install(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (/permit-bindings\/[^/?]+$/.test(url)) return bindingReply();
      if (url.includes('/api/v1/templates/versions?')) {
        return json([{ templateId: 7, versions: [version(70, 'Draft', '1.1.0.0')] }]);
      }
      if (/\/api\/v1\/templates\/7$/.test(url)) {
        return json({ id: 7, code: 'GEN07', nameL10n: { values: { en: 'General template' } }, isActive: true });
      }

      return json({ title: 'Not found', status: 404 }, 404);
    }),
  );
}

function show(entry = '/admin/permits/bindings/B1?template=7&version=70'): void {
  render(
    <MantineProvider theme={testTheme}>
      <MemoryRouter initialEntries={[entry]}>
        <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
          <Routes>
            <Route path="/admin/permits/bindings/:id" element={<PermitBindingPage />} />
          </Routes>
        </QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );
}

beforeEach(() => {
  session.permissions = ['Template.View'];
  bindingReply = () => json(binding('B1'));
  install();
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe('екран прив’язки', () => {
  it('показує таблицю аркуша, що читає таблицю дозволу, режим, правило і шаблон з версією', async () => {
    show();

    const detail = await screen.findByText('permits.bindingView.readOnly');
    expect(detail).toBeTruthy();
    expect(screen.getByRole('heading', { level: 1 }).textContent).toBe('TBL_B1');
    const arrow = document.querySelector('[data-binding-arrow]');
    expect(arrow?.textContent).toContain('TBL_B1');
    expect(arrow?.textContent).toContain('AirPermit › AirEmissions');
    expect(await screen.findByText('General template')).toBeTruthy();
    expect(screen.getByText('1.1.0.0')).toBeTruthy();
    expect(screen.getByText('permits.bindingsList.mode.Rows')).toBeTruthy();
    expect(screen.getByText('permits.bindingsList.rule.ByFacilityPeriodEnd')).toBeTruthy();
    expect(screen.getByText('permits.bindingView.active.true')).toBeTruthy();
  });

  it('зіставлення колонок - за позицією, з ролями й поясненням, що це означає на аркуші', async () => {
    show();

    const table = await screen.findByRole('table');
    const rows = within(table).getAllByRole('row').slice(1);
    expect(rows.map((row) => row.getAttribute('data-binding-column'))).toEqual(['SUBSTANCE', 'LIMIT']);
    expect(rows[0]?.textContent).toContain('Substance');
    expect(rows[0]?.textContent).toContain('permits.bindingView.role.RowKey');
    expect(rows[0]?.textContent).toContain('permits.bindingView.roleNote.RowKey');
    expect(rows[1]?.textContent).toContain('Emission_t_year');
    expect(rows[1]?.textContent).toContain('permits.bindingView.role.Limit');
    expect(within(table).getAllByRole('columnheader')).toHaveLength(4);
  });

  it('«лише формули» - без таблиці колонок, з поясненням', async () => {
    bindingReply = () =>
      json(binding('B1', { mode: 'Formulas', selectionRule: 'FromHeaderLookup', headerFieldCode: 'PERMIT', columns: [] }));
    show();

    expect(await screen.findByText('permits.bindingView.formulas.note')).toBeTruthy();
    expect(screen.queryByRole('table')).toBeNull();
    expect(screen.getByText('PERMIT')).toBeTruthy();
    expect(screen.getByText('permits.bindingsList.rule.FromHeaderLookup')).toBeTruthy();
  });

  it('вимкнена прив’язка підписана, а не виглядає чинною', async () => {
    bindingReply = () => json(binding('B1', { isActive: false }));
    show();

    expect(await screen.findByText('permits.bindingView.active.false')).toBeTruthy();
    expect(screen.queryByText('permits.bindingView.active.true')).toBeNull();
  });

  it('посилання на шаблон веде на вкладку Permit версії, назад - на перелік', async () => {
    show();
    await screen.findByText('permits.bindingView.readOnly');

    expect(screen.getByRole('link', { name: 'permits.bindingView.openTemplate' }).getAttribute('href')).toBe(
      '/admin/templates/7/versions/70?tab=permit',
    );
    expect(screen.getByRole('link', { name: /permits\.back\.bindings/ }).getAttribute('href')).toBe('/admin/permits/bindings');
  });

  it('без версії чи шаблону в адресі - пояснення, а не вгадана версія; запитів немає', () => {
    show('/admin/permits/bindings/B1');

    expect(screen.getByText('permits.bindingView.incomplete.title')).toBeTruthy();
    expect(vi.mocked(fetch)).not.toHaveBeenCalled();
  });

  it('прив’язки немає у версії (404) - помилка, а не порожня сторінка', async () => {
    bindingReply = () => json({ title: 'Not found', status: 404 }, 404);
    show();

    expect(await screen.findByRole('alert')).toBeTruthy();
    expect(screen.queryByText('permits.bindingView.readOnly')).toBeNull();
    expect(screen.queryByRole('table')).toBeNull();
  });

  it('без права Template.View - ні запитів, ні даних', () => {
    session.permissions = ['Registry.View'];
    show();

    expect(screen.getByText('permits.bindingsList.noAccess.title')).toBeTruthy();
    expect(vi.mocked(fetch)).not.toHaveBeenCalled();
  });
});
