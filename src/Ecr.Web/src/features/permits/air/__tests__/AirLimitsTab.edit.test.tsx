import type { JSX } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { Catalog } from '@/test/__tests__/a11yFixtures';
import { TestProviders } from '@/test/render';
import * as airApi from '@/features/permits/air/airApi';
import * as rowsApi from '@/features/registries/rows/api';
import { AirLimitsTab } from '../AirLimitsTab';
import type { AirLimitsPermit } from '../airTypes';
import { card, source, substance } from './airFixtures';

/**
 * P1 07.11 «користувач має БАЧИТИ ВСІ імпортовані дані»: картка «Sources & parameters» показує колонки, додані до таблиць
 * дозволу («Add as new column»), і править рядок джерела чи речовини загальним пакетним записом із версією рядка.
 * Тексти — справжні, з сіду.
 *
 * Мутаційні докази (виконано вручну одним прогоном, 5 з 6 червоні — кожен на свою мутацію; усе відновлено):
 * - `AirLimitsTab`: `<AddedCells … />` прибрано з рядка джерела — валить «додані колонки джерел і речовин видно»;
 * - `changedValues`: пропуск незмінених полів прибрано (пересилаються всі) — валить «пишеться лише змінене… з версією»;
 * - `AirRowEditor`: `if (result.applied)` замінено на `if (true)` — валить «застаріла версія — текст сервера».
 */
vi.mock('@/shared/i18n', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/i18n')>()),
  t: (key: string, params?: Record<string, string | number>) =>
    (Catalog[key] ?? `⟦${key}⟧`).replace(/\{(\w+)\}/g, (match, name: string) =>
      params !== undefined && name in params ? String(params[name]) : match,
    ),
}));
vi.mock('@/features/permits/air/airApi', () => ({ getAirLimits: vi.fn() }));
vi.mock('@/features/registries/rows/api', () => ({ saveBatch: vi.fn() }));

const getAirLimits = vi.mocked(airApi.getAirLimits);
const saveBatch = vi.mocked(rowsApi.saveBatch);
const text = (key: string, params: Record<string, string> = {}): string =>
  (Catalog[key] ?? key).replace(/\{(\w+)\}/g, (match, name: string) => params[name] ?? match);

function withAdded(): AirLimitsPermit {
  const first = source(1, '1085', {
    added: { FILE_B: 'WS-1085', FILE_FLAG: true },
    version: 'v-src-1',
    substances: [
      { ...substance(11, '0301', 'Nitrogen dioxide'), added: { FILE_AB: '2031.0000000000000000' }, version: 'v-sub-11' },
    ],
  });

  return card({
    sources: [first, source(2, '6001', { sourceName: 'Wellheads', substances: [] })],
    substanceRows: 1,
    sourcesRegistryCode: 'PRM_AIR_AIR_SOURCES',
    substancesRegistryCode: 'PRM_AIR_AIR_SOURCE_SUBSTANCES',
    addedColumns: [
      { table: 'sources', code: 'FILE_B', nameL10n: { en: 'Workshop' }, dataType: 'String' },
      { table: 'sources', code: 'FILE_FLAG', nameL10n: { en: 'Flag' }, dataType: 'Bool' },
      { table: 'substances', code: 'FILE_AB', nameL10n: { en: 'NDV year' }, dataType: 'Int' },
    ],
  });
}

const applied = { applied: true, dryRun: false, added: 0, updated: 1, deleted: 0, unchanged: 0, rows: [], rules: [] };

beforeEach(() => {
  getAirLimits.mockResolvedValue(withAdded());
  saveBatch.mockResolvedValue(applied);
});
afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

function mount(mayImport: boolean): JSX.Element {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  return (
    <TestProviders>
      <QueryClientProvider client={client}>
        <MemoryRouter>
          <AirLimitsTab permitId={5} mayImport={mayImport} />
        </MemoryRouter>
      </QueryClientProvider>
    </TestProviders>
  );
}

const field = (dialog: HTMLElement, code: string): HTMLInputElement => {
  const input = dialog.querySelector<HTMLInputElement>(`[data-field="${code}"]`);
  if (input === null) throw new Error(`no field ${code}`);

  return input;
};

describe('картка «Sources & parameters»: додані колонки й правка рядків', () => {
  it('додані колонки джерел і речовин видно: заголовок — назва колонки, число без хвостових нулів', async () => {
    render(mount(false));
    await screen.findByText('Source 1085');
    expect(screen.getByRole('columnheader', { name: 'Workshop' })).toBeTruthy();
    expect(screen.getByRole('columnheader', { name: 'Flag' })).toBeTruthy();
    const rows = screen.getAllByTestId('air-source-row');
    expect(within(rows[0]!).getByText('WS-1085')).toBeTruthy();
    expect(within(rows[0]!).getByText('true')).toBeTruthy();
    expect(within(rows[1]!).getAllByText('—').length).toBeGreaterThan(0);

    fireEvent.click(screen.getByRole('button', { name: 'Substances of source 1085' }));
    const table = screen.getByRole('table', { name: 'Substances of source 1085' });
    expect(within(table).getByRole('columnheader', { name: 'NDV year' })).toBeTruthy();
    expect(within(table).getByText('2031')).toBeTruthy();
  });

  it('без права запису кнопок «Edit» немає', async () => {
    render(mount(false));
    await screen.findByText('Source 1085');
    expect(screen.queryByRole('button', { name: text('permits.air.edit.source', { nae: '1085' }) })).toBeNull();
  });

  it('правка джерела: усі поля, що правляться (НАЕ й область — ні); пишеться лише змінене пакетом із версією рядка', async () => {
    render(mount(true));
    await screen.findByText('Source 1085');
    fireEvent.click(screen.getByRole('button', { name: text('permits.air.edit.source', { nae: '1085' }) }));
    const dialog = await screen.findByRole('dialog', { name: text('permits.air.edit.source', { nae: '1085' }) });

    expect(dialog.querySelector('[data-field="NAE"]')).toBeNull();
    expect(dialog.querySelector('[data-field="REGION"]')).toBeNull();
    expect(dialog.querySelector('[data-field="FILE_FLAG"]')).toBeNull();
    expect(field(dialog, 'HEIGHT').value).toBe('6.5');
    expect(field(dialog, 'FILE_B').value).toBe('WS-1085');

    fireEvent.change(field(dialog, 'SOURCE_NAME'), { target: { value: 'Boiler house' } });
    fireEvent.change(field(dialog, 'FILE_B'), { target: { value: 'WS-77' } });
    fireEvent.change(field(dialog, 'HEIGHT'), { target: { value: '7.25' } });
    fireEvent.click(within(dialog).getByRole('button', { name: text('common.save') }));

    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
    expect(saveBatch).toHaveBeenCalledWith(
      'PRM_AIR_AIR_SOURCES',
      [
        {
          clientRowId: 'air-1',
          op: 'upsert',
          id: 1,
          code: null,
          baseVersion: 'v-src-1',
          values: { SOURCE_NAME: 'Boiler house', HEIGHT: 7.25, FILE_B: 'WS-77' },
        },
      ],
      false,
    );
    await waitFor(() => expect(getAirLimits).toHaveBeenCalledTimes(2));
  });

  it('правка речовини: t/year і додана колонка, у довідник речовин', async () => {
    render(mount(true));
    await screen.findByText('Source 1085');
    fireEvent.click(screen.getByRole('button', { name: 'Substances of source 1085' }));
    const name = text('permits.air.edit.substance', { code: '0301', nae: '1085' });
    fireEvent.click(screen.getByRole('button', { name }));
    const dialog = await screen.findByRole('dialog', { name });

    expect(dialog.querySelector('[data-field="SUBSTANCE"]')).toBeNull();
    expect(field(dialog, 'FILE_AB').value).toBe('2031');
    fireEvent.change(field(dialog, 'TONS_PER_YEAR'), { target: { value: '2.5' } });
    fireEvent.change(field(dialog, 'FILE_AB'), { target: { value: '2032' } });
    fireEvent.click(within(dialog).getByRole('button', { name: text('common.save') }));

    await waitFor(() => expect(saveBatch).toHaveBeenCalledTimes(1));
    expect(saveBatch.mock.calls[0]![0]).toBe('PRM_AIR_AIR_SOURCE_SUBSTANCES');
    expect(saveBatch.mock.calls[0]![1]).toEqual([
      { clientRowId: 'air-11', op: 'upsert', id: 11, code: null, baseVersion: 'v-sub-11', values: { TONS_PER_YEAR: 2.5, FILE_AB: 2032 } },
    ]);
  });

  it('нічого не змінено — форма закривається без запису', async () => {
    render(mount(true));
    await screen.findByText('Source 1085');
    fireEvent.click(screen.getByRole('button', { name: text('permits.air.edit.source', { nae: '1085' }) }));
    const dialog = await screen.findByRole('dialog');
    fireEvent.click(within(dialog).getByRole('button', { name: text('common.save') }));
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
    expect(saveBatch).not.toHaveBeenCalled();
  });

  function rowError(messageKey: string, errorCode: string): typeof applied {
    return {
      ...applied,
      applied: false,
      updated: 0,
      rows: [{ clientRowId: 'air-1', status: 'error', entryId: 1, version: null, errors: [{ field: null, errorCode, messageKey, params: { entryId: '1', entryCode: 'SRC-1' } }] }],
    } as typeof applied;
  }

  it('застаріла версія — форма закривається (її версія вже стара), картка перечитується', async () => {
    saveBatch.mockResolvedValue(rowError('err.ECR-REG-4093.entryChanged', 'ECR-REG-4093'));
    render(mount(true));
    await screen.findByText('Source 1085');
    fireEvent.click(screen.getByRole('button', { name: text('permits.air.edit.source', { nae: '1085' }) }));
    const dialog = await screen.findByRole('dialog');
    fireEvent.change(field(dialog, 'SOURCE_NAME'), { target: { value: 'Lost update' } });
    fireEvent.click(within(dialog).getByRole('button', { name: text('common.save') }));

    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
    await waitFor(() => expect(getAirLimits).toHaveBeenCalledTimes(2));
  });

  it('інша відмова рядка — текст сервера в формі, форма лишається', async () => {
    saveBatch.mockResolvedValue({
      ...applied,
      applied: false,
      updated: 0,
      rows: [
        {
          clientRowId: 'air-1',
          status: 'error',
          entryId: 1,
          version: null,
          errors: [
            {
              field: null,
              errorCode: 'ECR-REG-0422',
              messageKey: 'err.ECR-REG-0422.required',
              params: { entryId: '1', entryCode: 'SRC-1' },
            },
          ],
        },
      ],
    });
    render(mount(true));
    await screen.findByText('Source 1085');
    fireEvent.click(screen.getByRole('button', { name: text('permits.air.edit.source', { nae: '1085' }) }));
    const dialog = await screen.findByRole('dialog');
    fireEvent.change(field(dialog, 'SOURCE_NAME'), { target: { value: 'Lost update' } });
    fireEvent.click(within(dialog).getByRole('button', { name: text('common.save') }));

    expect(await within(dialog).findByText(text('permits.air.edit.notSaved'))).toBeTruthy();
    expect(screen.getByRole('dialog')).toBeTruthy();
    await waitFor(() => expect(getAirLimits).toHaveBeenCalledTimes(2));
  });
});
