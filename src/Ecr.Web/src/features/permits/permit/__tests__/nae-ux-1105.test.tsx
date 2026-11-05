import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { ReactNode } from 'react';
import { cleanup, render, renderHook, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import * as rowsApi from '@/features/registries/rows/api';
import { TestProviders } from '@/test/render';
import type { Permit } from '../../api/types';
import { NaeRegistryCode, NaeSourceLevel } from '../../nae/nodeAttributes';
import { fieldsOf, labelOfId, pickerOptions, type PermitField } from '../model';
import { RowFields } from '../RowFields';
import { usePickerData } from '../usePermitData';
import { column, permit, rowsFor } from './permitFixtures';

/**
 * Дефекти живого стенда 05.11: (1) пікер NAE у формі рядка пропонує лише вузли джерела (`SRC`), а не весь `NAE_TREE`;
 * (2) поля форми рядка мають спільну сітку з однаковим верхом контролів.
 */
vi.mock('@/shared/i18n', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/i18n')>()),
  t: (key: string) => key,
}));
vi.mock('@/api/client', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/api/client')>()),
  apiFetch: vi.fn(),
}));
vi.mock('@/features/registries/rows/api', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/features/registries/rows/api')>()),
  getRegistryRows: vi.fn(),
}));

const getRows = vi.mocked(rowsApi.getRegistryRows);
const fetchApi = vi.mocked(apiFetch);

const entry = (id: number, code: string, display: string, levelCode: string | null) => ({
  id,
  code,
  display,
  levelCode,
  parentEntryId: null,
  validFrom: null,
  validTo: null,
});

const entries = [entry(3, 'T-3', 'Pipe 1085', NaeSourceLevel), entry(4, 'T-4', 'Pipe 1090', NaeSourceLevel)];
const treeNodes = [
  { id: 3, naeCode: '1085', levelCode: NaeSourceLevel },
  { id: 4, naeCode: '1090', levelCode: NaeSourceLevel },
];

let treeFails = false;

beforeEach(() => {
  treeFails = false;
  getRows.mockImplementation((code) => Promise.resolve(rowsFor(code) as never));
  fetchApi.mockImplementation((path: string) => {
    if (path.includes('/entries?')) return Promise.resolve(entries) as never;
    if (path.endsWith('/tree')) return treeFails ? Promise.reject(new Error('tree down')) : (Promise.resolve({ nodes: treeNodes }) as never);
    return Promise.resolve([]) as never;
  });
});
afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

function table(...columns: ReturnType<typeof column>[]) {
  return {
    code: 'Sources',
    nameL10n: { en: 'Sources' },
    order: 1,
    columns: columns.map((c, index) => ({ ...c, order: index + 1 })),
    rules: [],
  };
}

const nae = column('Nae', { dataType: 'Lookup', listScope: 'SharedRegistry', listRegistryCode: NaeRegistryCode, nameL10n: { en: 'NAE' } });
const substance = column('Substance', { dataType: 'Lookup', listScope: 'SharedRegistry', listRegistryCode: 'Substances', nameL10n: { en: 'Substance' } });

function pickerFor(columns: ReturnType<typeof column>[], over: Partial<Permit> = {}) {
  const fields = fieldsOf(table(...columns), new Map());
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const wrapper = ({ children }: { children: ReactNode }) => <QueryClientProvider client={client}>{children}</QueryClientProvider>;
  const hook = renderHook(() => usePickerData(permit(over), fields, true), { wrapper });
  return { fields, hook };
}

describe('пікер NAE: лише вузли джерела', () => {
  it('стовпець на NAE_TREE читає /entries?level=SRC (з asOf), а не /rows; підпис «номер NAE · назва»', async () => {
    const { fields, hook } = pickerFor([nae]);
    await waitFor(() => expect(hook.result.current.pending).toBe(false));

    const urls = fetchApi.mock.calls.map(([path]) => String(path));
    const entriesUrl = urls.find((path) => path.includes('/entries?'));
    expect(entriesUrl).toBeDefined();
    const query = new URLSearchParams(entriesUrl?.split('?')[1]);
    expect(entriesUrl?.startsWith(`/api/v1/registries/${NaeRegistryCode}/entries?`)).toBe(true);
    expect(query.get('level')).toBe(NaeSourceLevel);
    expect(query.get('asOf')).toMatch(/^\d{4}-\d{2}-\d{2}$/);
    expect(getRows).not.toHaveBeenCalled();

    const options = pickerOptions(fields[0] as PermitField, hook.result.current.data);
    expect(options).toEqual([
      { value: '3', label: '1085 · Pipe 1085' },
      { value: '4', label: '1090 · Pipe 1090' },
    ]);
    expect(labelOfId(fields[0], hook.result.current.data, '4')).toBe('1090 · Pipe 1090');
    // Значення, що не є вузлом джерела (регіон), невідоме пікеру: не падає, підпису немає.
    expect(labelOfId(fields[0], hook.result.current.data, '1')).toBeNull();
  });

  it('вузол іншого рівня, що прослизнув у відповідь, відсікається', async () => {
    fetchApi.mockImplementation((path: string) =>
      path.includes('/entries?')
        ? (Promise.resolve([entry(1, 'T-1', 'Atyrau', 'REGION'), entry(2, 'T-2', 'Barge', 'SITE'), ...entries]) as never)
        : (Promise.resolve({ nodes: treeNodes }) as never),
    );
    const { fields, hook } = pickerFor([nae]);
    await waitFor(() => expect(hook.result.current.pending).toBe(false));

    expect(pickerOptions(fields[0] as PermitField, hook.result.current.data).map((option) => option.value)).toEqual(['3', '4']);
  });

  it('дерево недоступне: варіанти лишаються, підпис — із коду запису', async () => {
    treeFails = true;
    const { fields, hook } = pickerFor([nae]);
    await waitFor(() => expect(hook.result.current.pending).toBe(false));

    expect(pickerOptions(fields[0] as PermitField, hook.result.current.data).map((option) => option.label)).toEqual([
      'T-3 · Pipe 1085',
      'T-4 · Pipe 1090',
    ]);
  });

  it('інший спільний реєстр, як і раніше, читається через /rows без level', async () => {
    const { fields, hook } = pickerFor([substance]);
    await waitFor(() => expect(hook.result.current.pending).toBe(false));

    expect(getRows).toHaveBeenCalledTimes(1);
    expect(getRows.mock.calls[0]?.[0]).toBe('Substances');
    expect(fetchApi.mock.calls.some(([path]) => String(path).includes('/entries?'))).toBe(false);
    expect(pickerOptions(fields[0] as PermitField, hook.result.current.data).map((option) => option.label)).toEqual(['Methane', 'Benzene']);
  });

  it('NAE і звичайний реєстр в одній таблиці не ділять кеш: кожен свою адресу', async () => {
    const { hook } = pickerFor([nae, substance]);
    await waitFor(() => expect(hook.result.current.pending).toBe(false));

    expect(getRows.mock.calls.map(([code]) => code)).toEqual(['Substances']);
    expect(fetchApi.mock.calls.filter(([path]) => String(path).includes('/entries?'))).toHaveLength(1);
  });
});

describe('форма рядка: спільна сітка полів', () => {
  const area = column('Area', { dataType: 'Lookup', listScope: 'SharedRegistry', listRegistryCode: 'Areas', nameL10n: { en: 'Area' } });
  const site = column('Site', { nameL10n: { en: 'Site / production' } });
  const issued = column('Issued', { dataType: 'Date', nameL10n: { en: 'Issued' } });
  const active = column('Active', { dataType: 'Bool', nameL10n: { en: 'Active' } });

  function mount(columns: ReturnType<typeof column>[]) {
    const fields = fieldsOf(table(...columns), new Map());
    return render(
      <TestProviders>
        <RowFields
          fields={fields}
          form={{}}
          errors={{}}
          onChange={vi.fn()}
          readOnly={false}
          picker={{ sources: { shared: { Areas: ['North'] }, permitTables: {} }, ids: { Areas: new Map([['North', '1']]) } }}
          pickerPending={false}
          isNew
        />
      </TestProviders>,
    );
  }

  it('кожне поле — клітинка .pp-field сітки; підказка Select у своїй клітинці, у TextInput її немає', async () => {
    const view = mount([site, area]);
    await screen.findByText('Area');

    const grid = view.container.querySelector('.pp-form-grid');
    expect(grid).not.toBeNull();
    const cells = Array.from(grid?.children ?? []);
    expect(cells.map((cell) => cell.className)).toEqual(['pp-field', 'pp-field']);

    const text = view.container.querySelector('[data-pp-field="Site"]');
    const select = view.container.querySelector('[data-pp-field="Area"]');
    expect(text?.querySelector('.mantine-InputWrapper-description')).toBeNull();
    expect(select?.querySelector('.mantine-InputWrapper-description')).not.toBeNull();
    // Підказку Mantine вклав між підписом і полем: CSS (`.pp-field … description {order: 3}`) переставляє її під поле,
    // тож зв'язок `aria-describedby` лишається, а верх контролів збігається.
    const input = select?.querySelector('input');
    const describedBy = input?.getAttribute('aria-describedby') ?? '';
    expect(describedBy).toContain(select?.querySelector('.mantine-InputWrapper-description')?.id ?? 'missing');
  });

  it('перемикач Yes/No має порожній рядок підпису для скринрідера прихованим; дата — своя клітинка', async () => {
    const view = mount([issued, active]);
    await screen.findByText('Active');

    const cell = view.container.querySelector('[data-pp-field="Active"]');
    const spacer = cell?.querySelector('.pp-label-spacer');
    expect(spacer?.getAttribute('aria-hidden')).toBe('true');
    expect(spacer?.textContent).toBe('');
    expect(view.container.querySelector('[data-pp-field="Issued"]')).not.toBeNull();
  });
});
