import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { TemplateTable } from '../ctorModel';
import { ColumnsTable } from '../ColumnsTable';
import { bulkGroupChanges, groupPath, NoGroup } from '../BulkGroupDialog';

/**
 * PERMITES §3 (макет `?dialog=edit-selected`, KIT §6.5.1): вибір колонок, панель «N selected», «Edit selected…» —
 * ОДИН `PATCH …/presentation?expectedPresentationRevision=N` на всі вибрані (все або нічого, рішення людини 10.11).
 * ⚠ Каталог не вантажиться: `t()` дає `⟦ключ⟧`.
 *
 * Мутації (локально, червоніють): прибрати `?expectedPresentationRevision` з URL — «тіло й ревізія»; слати
 * по PATCH на колонку — «одним запитом»; не перечитувати структуру на 409 — «застаріла вкладка».
 */
function column(id: number, code: string, headerGroupId: number | null = null) {
  return {
    id,
    code,
    dataType: 'Decimal',
    headerL10n: { values: { en: code } },
    isHidden: false,
    isReadOnly: false,
    unitSymbol: null,
    formulaExpression: null,
    formulaDialect: null,
    displayFormat: null,
    headerGroupId,
  };
}

const groups = [
  { id: 5, parentId: null, code: 'POL', ordinal: 1, labelL10n: { values: { en: 'Pollutants' } } },
  { id: 7, parentId: 5, code: 'GAS', ordinal: 1, labelL10n: { values: { en: 'Gases' } } },
];

const table = {
  id: 41,
  code: 'T',
  nameL10n: { values: { en: 'T' } },
  ordinal: 1,
  rowMode: 'Fixed',
  maxDynamicRows: null,
  rows: [],
  headerGroups: groups,
  columns: [column(1, 'SO2'), column(2, 'NOX'), column(3, 'CO'), column(4, 'PM10', 7)],
} as unknown as TemplateTable;

interface Call {
  url: string;
  method: string;
  body: unknown;
}

function stubFetch(respond: (call: Call) => Response): Call[] {
  const calls: Call[] = [];
  vi.stubGlobal(
    'fetch',
    vi.fn((url: string, init?: RequestInit) => {
      const call = { url, method: init?.method ?? 'GET', body: init?.body ? (JSON.parse(String(init.body)) as unknown) : null };
      calls.push(call);
      return Promise.resolve(respond(call));
    }),
  );
  return calls;
}

const json = (status: number, body: unknown, type = 'application/json'): Response =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': type } });

function show(client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })) {
  render(
    <MantineProvider>
      <QueryClientProvider client={client}>
        <ColumnsTable
          table={table}
          templateVersionId={9}
          presentationRevision={4}
          canEditPresentation
          canEditStructure={false}
          reorderPending={false}
          deletingCode={null}
          onReorder={vi.fn()}
          onUsage={vi.fn()}
          onPresentation={vi.fn()}
          onEdit={vi.fn()}
          onDelete={vi.fn()}
          onFormula={vi.fn()}
        />
      </QueryClientProvider>
    </MantineProvider>,
  );
  return client;
}

function rowOf(code: string): HTMLElement {
  const row = screen.getByText(`(${code})`).closest('tr');
  if (row === null) throw new Error(`немає рядка ${code}`);
  return row;
}

const pick = (code: string): void => {
  fireEvent.click(within(rowOf(code)).getByRole('checkbox'));
};
const bar = (): HTMLElement | null => document.querySelector('[data-columns-bulk="bar"]');

async function chooseGroup(name: RegExp): Promise<void> {
  const dialog = await screen.findByRole('dialog');
  fireEvent.click(within(dialog).getByRole('textbox', { name: /colgroups\.field/ }));
  fireEvent.click(await screen.findByRole('option', { name }));
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('ColumnsTable — колонка «Group» і вибір', () => {
  it('колонка «Group» показує шлях групи, без групи — «—»', () => {
    show();
    expect(document.querySelector('[data-column-group="PM10"]')?.textContent).toBe('Pollutants › Gases');
    expect(document.querySelector('[data-column-group="SO2"]')?.textContent).toBe('—');
    expect(groupPath(groups, 999)).toBe('');
  });

  it('панель з’являється на вибір, «вибрати все» вибирає всі, «Clear selection» ховає панель', () => {
    show();
    expect(bar()).toBeNull();

    pick('SO2');
    expect(bar()).not.toBeNull();

    fireEvent.click(screen.getByRole('checkbox', { name: '⟦columns.selectAll⟧' }));
    for (const code of ['SO2', 'NOX', 'CO', 'PM10']) {
      expect((within(rowOf(code)).getByRole('checkbox') as HTMLInputElement).checked).toBe(true);
    }

    fireEvent.click(within(bar() as HTMLElement).getByRole('button', { name: '⟦columns.clearSelection⟧' }));
    expect(bar()).toBeNull();
    expect((within(rowOf('SO2')).getByRole('checkbox') as HTMLInputElement).checked).toBe(false);
  });

  it('без Template.Edit вибору немає', () => {
    render(
      <MantineProvider>
        <QueryClientProvider client={new QueryClient()}>
          <ColumnsTable
            table={table}
            templateVersionId={9}
            presentationRevision={4}
            canEditPresentation={false}
            canEditStructure={false}
            reorderPending={false}
            deletingCode={null}
            onReorder={vi.fn()}
            onUsage={vi.fn()}
            onPresentation={vi.fn()}
            onEdit={vi.fn()}
            onDelete={vi.fn()}
            onFormula={vi.fn()}
          />
        </QueryClientProvider>
      </MantineProvider>,
    );
    expect(screen.queryAllByRole('checkbox')).toHaveLength(0);
  });
});

describe('BulkGroupDialog — «Edit selected…»', () => {
  it('форма тіла: зміна HeaderGroupId на кожну колонку, «none» — null', () => {
    const cols = table.columns.slice(0, 2);
    expect(bulkGroupChanges(cols, '7')).toEqual([
      { entityType: 'ColumnDef', entityId: 1, field: 'HeaderGroupId', value: '7' },
      { entityType: 'ColumnDef', entityId: 2, field: 'HeaderGroupId', value: '7' },
    ]);
    expect(bulkGroupChanges(cols, NoGroup).map((c) => c.value)).toEqual([null, null]);
  });

  it('три вибрані колонки — ОДИН PATCH з ревізією; успіх перечитує структуру й знімає вибір', async () => {
    const calls = stubFetch(() => json(200, { presentationRevision: 5 }));
    const client = show();
    const invalidate = vi.spyOn(client, 'invalidateQueries');

    pick('SO2');
    pick('NOX');
    pick('CO');
    fireEvent.click(screen.getByRole('button', { name: '⟦columns.editSelected⟧' }));
    await chooseGroup(/Gases/);
    fireEvent.click(screen.getByRole('button', { name: '⟦columns.bulkApply⟧' }));

    await vi.waitFor(() => {
      expect(bar()).toBeNull();
    });

    const patches = calls.filter((c) => c.method === 'PATCH');
    expect(patches).toHaveLength(1);
    expect(patches[0]?.url).toContain('/api/v1/template-versions/9/presentation?expectedPresentationRevision=4');
    expect(patches[0]?.body).toEqual(
      [1, 2, 3].map((entityId) => ({ entityType: 'ColumnDef', entityId, field: 'HeaderGroupId', value: '7' })),
    );
    expect(invalidate).toHaveBeenCalledWith({ queryKey: ['templates', 'version', 9] });
  });

  it('та сама група, що вже стоїть, — «Apply» вимкнена (без порожнього аудиту)', async () => {
    stubFetch(() => json(200, { presentationRevision: 5 }));
    show();

    pick('PM10');
    fireEvent.click(screen.getByRole('button', { name: '⟦columns.editSelected⟧' }));
    await screen.findByRole('dialog');
    expect((screen.getByRole('button', { name: '⟦columns.bulkApply⟧' }) as HTMLButtonElement).disabled).toBe(true);
  });

  it('«— none —» знімає групу (value null)', async () => {
    const calls = stubFetch(() => json(200, { presentationRevision: 5 }));
    show();

    pick('PM10');
    fireEvent.click(screen.getByRole('button', { name: '⟦columns.editSelected⟧' }));
    await chooseGroup(/columns\.bulkNoGroup/);
    fireEvent.click(screen.getByRole('button', { name: '⟦columns.bulkApply⟧' }));

    await vi.waitFor(() => {
      expect(calls.filter((c) => c.method === 'PATCH')).toHaveLength(1);
    });
    expect(calls.find((c) => c.method === 'PATCH')?.body).toEqual([
      { entityType: 'ColumnDef', entityId: 4, field: 'HeaderGroupId', value: null },
    ]);
  });

  it('409 застаріла ревізія: зрозуміле повідомлення в діалозі, структуру перечитано, вибір лишається', async () => {
    stubFetch(() =>
      json(
        409,
        {
          title: 'Conflict',
          status: 409,
          errorCode: 'ECR-TMPL-0409',
          correlationId: 'c-1',
          messageKey: 'err.ECR-TMPL-0409.presentationRevisionConflict',
          expectedRevision: '4',
          actualRevision: '6',
        },
        'application/problem+json',
      ),
    );
    const client = show();
    const invalidate = vi.spyOn(client, 'invalidateQueries');

    pick('SO2');
    fireEvent.click(screen.getByRole('button', { name: '⟦columns.editSelected⟧' }));
    await chooseGroup(/Gases/);
    fireEvent.click(screen.getByRole('button', { name: '⟦columns.bulkApply⟧' }));

    const alert = await vi.waitFor(() => {
      const found = document.querySelector('[data-bulk-error="stale"]');
      if (found === null) throw new Error('немає повідомлення');
      return found;
    });
    expect(alert.textContent).toContain('⟦columns.bulkStale⟧');
    expect(invalidate).toHaveBeenCalledWith({ queryKey: ['templates', 'version', 9] });
    expect(screen.queryByRole('button', { name: '⟦columns.bulkApply⟧' })).not.toBeNull();
    expect(bar()).not.toBeNull();
  });

  it('відмова «група не підряд» (ECR-TMPL-4229): текст сервера в діалозі, вибір лишається', async () => {
    stubFetch(() =>
      json(
        422,
        {
          title: 'Invalid header groups',
          status: 422,
          detail: 'Columns of header group GAS must be adjacent.',
          errorCode: 'ECR-TMPL-4229',
          correlationId: 'c-2',
          messageKey: 'err.ECR-TMPL-4229.notContiguous',
        },
        'application/problem+json',
      ),
    );
    show();

    pick('SO2');
    pick('CO');
    fireEvent.click(screen.getByRole('button', { name: '⟦columns.editSelected⟧' }));
    await chooseGroup(/Gases/);
    fireEvent.click(screen.getByRole('button', { name: '⟦columns.bulkApply⟧' }));

    const box = await vi.waitFor(() => {
      const found = document.querySelector('[data-bulk-error="server"]');
      if (found === null) throw new Error('немає відмови');
      return found;
    });
    expect(box.textContent).toContain('ECR-TMPL-4229');
    expect(document.querySelector('[data-bulk-error="stale"]')).toBeNull();
    expect(bar()).not.toBeNull();
  });
});
