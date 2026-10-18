import type { JSX } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, useLocation } from 'react-router-dom';
import type { PermitType } from '@/features/permits/api/types';
import { testTheme } from '@/test/render';
import { TypeEditorDialogs, type TypeEditorDialogsProps } from '../TypeEditorDialogs';
import { permitType } from './fixtures';

/** Хост діалогів (PM-08c): `?dialog=<id>` → діалог, результат → `onChangeType`, адреса очищується. */

const k = (key: string): string => `⟦permits.editor.dialogs.${key}⟧`;

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

const fetchMock = vi.fn();

beforeEach(() => {
  fetchMock.mockReset();
  fetchMock.mockImplementation((input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input);
    const method = init?.method ?? 'GET';

    if (url === '/api/v1/units') {
      return Promise.resolve(
        json([
          { id: 3, code: 'thousand_m3', symbolL10n: { en: 'thous. m3' } },
          { id: 4, code: 't', symbolL10n: { en: 't' } },
        ]),
      );
    }

    if (url.endsWith('/publish') && method === 'POST') {
      return Promise.resolve(json(permitType({ status: 'Published', draftVersion: null, hasUnpublishedChanges: false })));
    }

    if (method === 'DELETE') return Promise.resolve(new Response(null, { status: 204 }));

    return Promise.resolve(json(null));
  });
  vi.stubGlobal('fetch', fetchMock);
});

afterEach(() => {
  vi.unstubAllGlobals();
});

function Probe(): JSX.Element {
  return <output data-testid="search">{useLocation().search}</output>;
}

function mount(entry: string, props: Partial<TypeEditorDialogsProps> = {}) {
  const onChangeType = vi.fn<(next: PermitType) => void>();
  const onTypeReplaced = vi.fn();
  const onDeleted = vi.fn();
  const onSelectTable = vi.fn();
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <MemoryRouter initialEntries={[entry]}>
          <TypeEditorDialogs
            type={permitType()}
            selectedTableCode="Limits"
            onChangeType={onChangeType}
            onTypeReplaced={onTypeReplaced}
            onDeleted={onDeleted}
            onSelectTable={onSelectTable}
            {...props}
          />
          <Probe />
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>,
  );

  return { onChangeType, onTypeReplaced, onDeleted, onSelectTable };
}

describe('TypeEditorDialogs', () => {
  it('без ?dialog= і з невідомим id нічого не показує', () => {
    mount('/types/WaterDischarge');
    expect(screen.queryByRole('dialog')).toBeNull();
  });

  it('невідомий id діалогу ігнорується', () => {
    mount('/types/WaterDischarge?dialog=nope');
    expect(screen.queryByRole('dialog')).toBeNull();
  });

  it('add-column: додає стовпець у вибрану таблицю й очищує ?dialog=', async () => {
    const user = userEvent.setup();
    const { onChangeType } = mount('/types/WaterDischarge?dialog=add-column&t=Limits');

    await user.type(await screen.findByLabelText(k('col.nameEn')), 'Note');
    await user.click(screen.getByRole('button', { name: k('addColumn.apply') }));

    expect(onChangeType).toHaveBeenCalledTimes(1);
    const next = onChangeType.mock.calls[0]?.[0];
    expect(next?.tables[0]?.columns.map((c) => c.code)).toEqual(['Name', 'Annual', 'Note']);
    await waitFor(() => {
      expect(screen.getByTestId('search').textContent).toBe('?t=Limits');
    });
  });

  it('add-column без вибраної таблиці (шапка) — діалогу немає', () => {
    mount('/x?dialog=add-column', { selectedTableCode: null });
    expect(screen.queryByRole('dialog')).toBeNull();
  });

  it('add-series: одиниця тис. м³ береться з довідника одиниць, додається 12 стовпців', async () => {
    const user = userEvent.setup();
    const { onChangeType } = mount('/x?dialog=add-series');

    await user.click(await screen.findByRole('button', { name: k('addSeries.verb') }));

    const next = onChangeType.mock.calls[0]?.[0];
    const columns = next?.tables[0]?.columns ?? [];
    expect(columns).toHaveLength(14);
    expect(columns[2]).toMatchObject({ code: 'January', unitId: 3, order: 3 });
  });

  it('add-rule: перевірка лягає в шапку типу або в таблицю', async () => {
    const user = userEvent.setup();
    const { onChangeType } = mount('/x?dialog=add-rule', { selectedTableCode: null });

    await user.type(await screen.findByLabelText(new RegExp(k('addRule.name'))), 'Months add up');
    await user.type(screen.getByLabelText(new RegExp(k('addRule.expression'))), 'TRUE');
    await user.click(screen.getByRole('button', { name: k('addRule.apply') }));

    const next = onChangeType.mock.calls[0]?.[0];
    expect(next?.rules.map((r) => r.code)).toEqual(['Months_add_up']);
    expect(next?.tables[0]?.rules).toEqual([]);
  });

  it('add-table: таблиця додається, адреса очищується, нову таблицю пропонують вибрати', async () => {
    const user = userEvent.setup();
    const { onChangeType, onSelectTable } = mount('/x?dialog=add-table');

    await user.type(await screen.findByLabelText(new RegExp(k('addTable.nameEn'))), 'Storage');
    await user.click(screen.getByTestId('wizard-next'));
    await user.click(screen.getByTestId('wizard-next'));
    await user.click(screen.getByTestId('wizard-apply'));

    const next = onChangeType.mock.calls[0]?.[0];
    expect(next?.tables.map((t) => [t.code, t.order])).toEqual([
      ['Limits', 1],
      ['Storage', 2],
    ]);
    await waitFor(() => {
      expect(onSelectTable).toHaveBeenCalledWith('Storage');
    });
    expect(screen.getByTestId('search').textContent).toBe('');
  });

  it('publish-type: POST з причиною й токеном, відповідь передано сторінці, діалог закрито', async () => {
    const user = userEvent.setup();
    const { onTypeReplaced } = mount('/x?dialog=publish-type');

    await user.type(await screen.findByLabelText(k('publish.reason')), 'Added the storage table');
    await user.click(screen.getByRole('button', { name: k('publish.verb') }));

    await waitFor(() => {
      expect(onTypeReplaced).toHaveBeenCalledTimes(1);
    });
    const call = fetchMock.mock.calls.find(([url]) => String(url).endsWith('/publish'));
    expect(String(call?.[0])).toBe('/api/v1/permit-types/WaterDischarge/publish');
    expect(JSON.parse(String((call?.[1] as RequestInit).body))).toEqual({
      reason: 'Added the storage table',
      rowVersion: 'AAAAAAAAB9E=',
    });
    await waitFor(() => {
      expect(screen.getByTestId('search').textContent).toBe('');
    });
  });

  it('publish-type без таблиць: відмова, а «Add table…» відкриває майстер', async () => {
    const user = userEvent.setup();
    mount('/x?dialog=publish-type', { type: permitType({ tables: [] }), selectedTableCode: null });

    await user.click(await screen.findByRole('button', { name: k('publish.addTable') }));

    await waitFor(() => {
      expect(screen.getByTestId('search').textContent).toBe('?dialog=add-table');
    });
    expect(await screen.findByTestId('wizard-next')).toBeDefined();
    expect(fetchMock.mock.calls.some(([url]) => String(url).endsWith('/publish'))).toBe(false);
  });

  it('delete-type: DELETE, onDeleted, адреса очищена', async () => {
    const user = userEvent.setup();
    const { onDeleted } = mount('/x?dialog=delete-type');

    await user.click(await screen.findByRole('button', { name: k('deleteType.verb') }));

    await waitFor(() => {
      expect(onDeleted).toHaveBeenCalledTimes(1);
    });
    const call = fetchMock.mock.calls.find(([, init]) => (init as RequestInit | undefined)?.method === 'DELETE');
    expect(String(call?.[0])).toBe('/api/v1/permit-types/WaterDischarge');
  });
});
