import { useState, type JSX } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { apiFetch } from '@/api/client';
import { TestProviders } from '@/test/render';
import { Catalog } from '@/test/__tests__/a11yFixtures';
import type { PermitTypeColumn } from '../../api/types';
import { ColumnInspector } from '../ColumnInspector';
import { normalizeColumn } from '../draftModel';
import { column, table } from './fixtures';

/**
 * Air params (AIR-ON-AIR, людина 02.11: вид області налаштовний — «тест чи дикшиорнари чи через крапку з комою»): колонки REGION і
 * AREA таблиці AIR_SOURCES типу AIR налаштовуються ЗВИЧАЙНИМ інспектором стовпця, без окремого UI. Сервер читає вид так само
 * (`AirScopeColumns`): String → Text; Lookup + SharedRegistry → Registry; Lookup + TypeOnly + choices → List.
 */
vi.mock('@/shared/i18n', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/i18n')>()),
  t: (key: string, params?: Record<string, string | number>) =>
    (Catalog[key] ?? `⟦${key}⟧`).replace(/\{(\w+)\}/g, (match, name: string) =>
      params !== undefined && name in params ? String(params[name]) : match,
    ),
}));
vi.mock('@/api/client', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/api/client')>()),
  apiFetch: vi.fn(),
}));

const registries = [
  { id: 1, code: 'LAND_REGION', nameL10n: { values: { en: 'Regions' } } },
  { id: 2, code: 'AIR_AREA', nameL10n: { values: { en: 'Air permit areas' } } },
];

beforeEach(() => {
  vi.mocked(apiFetch).mockImplementation((path: string) => Promise.resolve(path === '/api/v1/registries' ? registries : []) as never);
});
afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

const region = column('REGION', { dataType: 'Lookup', listScope: 'SharedRegistry', listRegistryCode: 'LAND_REGION', required: true, nameL10n: { en: 'Region' } });
const area = column('AREA', { dataType: 'Lookup', listScope: 'SharedRegistry', listRegistryCode: 'AIR_AREA', nameL10n: { en: 'Area' } });
const sources = table('AIR_SOURCES', [column('NAE', { key: true, required: true }), region, area]);
const substances = table('AIR_SOURCE_SUBSTANCES', [column('SUBSTANCE', { key: true, required: true })]);

function Host({ initial, seen }: { initial: PermitTypeColumn; seen: PermitTypeColumn[] }): JSX.Element {
  const [current, setCurrent] = useState(initial);
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  return (
    <TestProviders>
      <QueryClientProvider client={client}>
        <MemoryRouter>
          <ColumnInspector
            table={sources}
            column={current}
            tables={[sources, substances]}
            editable
            problems={[]}
            onChange={(next) => {
              const normalized = normalizeColumn(next);
              seen.push(normalized);
              setCurrent(normalized);
            }}
            onClose={() => undefined}
          />
        </MemoryRouter>
      </QueryClientProvider>
    </TestProviders>
  );
}

describe('Air params: REGION/AREA у редакторі типу - три види', () => {
  it('Registry (Lookup + SharedRegistry): видно довідник LAND_REGION, можна взяти інший', async () => {
    const user = userEvent.setup();
    const seen: PermitTypeColumn[] = [];
    render(<Host initial={region} seen={seen} />);
    expect((screen.getByRole('textbox', { name: 'Type' }) as HTMLInputElement).value).toBe('Pick from a list');
    const registry = await screen.findByRole('textbox', { name: 'Registry' });
    await waitFor(() => expect((registry as HTMLInputElement).value).toBe('Regions (LAND_REGION)'));
    await user.click(registry);
    await user.click(await screen.findByRole('option', { name: 'Air permit areas (AIR_AREA)' }));
    expect(seen.at(-1)).toMatchObject({ code: 'REGION', dataType: 'Lookup', listScope: 'SharedRegistry', listRegistryCode: 'AIR_AREA' });
  });

  it('Text (String): REGION перемикається на текст, блоку джерела немає', async () => {
    const user = userEvent.setup();
    const seen: PermitTypeColumn[] = [];
    render(<Host initial={region} seen={seen} />);
    await user.click(screen.getByRole('textbox', { name: 'Type' }));
    await user.click(await screen.findByRole('option', { name: 'Text' }));
    expect(seen.at(-1)).toMatchObject({ code: 'REGION', dataType: 'String' });
    expect(screen.queryByRole('textbox', { name: 'Choices come from' })).toBeNull();
  });

  it('List (Lookup + TypeOnly + choices): AREA отримує власні значення типу', async () => {
    const user = userEvent.setup();
    const seen: PermitTypeColumn[] = [];
    render(<Host initial={area} seen={seen} />);
    await user.click(await screen.findByRole('textbox', { name: 'Choices come from' }));
    await user.click(await screen.findByRole('option', { name: 'A list only for this permit type' }));
    await user.type(await screen.findByLabelText('Values, one per line'), 'Offshore{Enter}Onshore');
    expect(seen.at(-1)).toMatchObject({ code: 'AREA', dataType: 'Lookup', listScope: 'TypeOnly', listRegistryCode: null, choices: ['Offshore', 'Onshore'] });
  });
});
