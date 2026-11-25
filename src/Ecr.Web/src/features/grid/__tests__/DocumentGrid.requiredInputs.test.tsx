import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { TableSliceDto } from '@/api/types';
import { cancelAutosave } from '../autosave';
import { resetPending } from '../pendingStore';
import { DocumentGrid } from '../DocumentGrid';

/**
 * ✎ 25.11, живий прохід (документ 100 LAND_UI, таблиця 3.00): попередження «обов'язкові колонки методології»
 * (`ECR-CALC-0437`, Warn) у СІТЦІ.
 *
 * До фіксу: розгорнутий `Alert` над сіткою (штовхав її вниз), рядок на кожну (рядок, колонку, методологію) — 8 рядків
 * на 2 колонки, повтор того самого рядка після кожного збереження (ключі `List.Item` повторювались, і React лишав старі
 * елементи: заголовок «4», перелік — 131), українське речення сервера з кодом колонки.
 *
 * Після: згорнута кнопка в рядку інструментів (нічого не розгорнуто), перелік на вимогу — рядок на колонку з її
 * назвою, лічильник = перелік, повторне збереження нічого не додає. Заглушка RevoGrid — як у
 * `DocumentGrid.patchNotices.test.tsx`; тексти — ключі (`⟦key (params)⟧`), каталогу в цьому тесті немає.
 */

vi.mock('@revolist/react-datagrid', () => ({
  RevoGrid: (props: { onAfteredit?: (event: { detail: unknown }) => void }) => (
    <div data-testid="revogrid-stub">
      <button
        type="button"
        onClick={() => props.onAfteredit?.({ detail: { prop: 'NAME', model: { __rowKey: 'r1' }, val: `x${String(Math.random())}` } })}
      >
        simulate-edit
      </button>
    </div>
  ),
}));

function column(code: string, header: string, ordinal: number): TableSliceDto['columns'][number] {
  return {
    code,
    dataType: 'String',
    defaultValue: null,
    displayFormat: null,
    header,
    id: ordinal + 1,
    isReadOnly: false,
    isRequired: false,
    isRequiredByMethodology: code !== 'NAME',
    lookupRegistryDefId: null,
    ordinal,
    unitId: null,
    unitSymbol: null,
  };
}

function sliceFixture(): TableSliceDto {
  return {
    cellConfirmations: {},
    cellPermissions: {},
    periodKey: 202609,
    tableInstanceId: 1,
    columns: [column('NAME', 'Name', 0), column('Land_TypeEquipment', 'Type of equipment', 1), column('Land_TypeFuel', 'Type of fuel', 2)],
    rows: [{ cells: { NAME: 'a' }, isOrphaned: false, label: null, ordinal: 0, rowKey: 'r1', rowKind: 'Dynamic', rowVersion: 'v1' }],
  };
}

/** Відповідь сервера ДО фіксу: по повідомленню на кожну методологію, українською, з кодом колонки. */
const Legacy = ['ECW_C05_01', 'ECW_C05_02', 'ECW_C05_03', 'ECW_C05_04'].flatMap((methodology) =>
  ['Land_TypeEquipment', 'Land_TypeFuel'].map((columnCode) => ({
    ruleCode: 'ECR-CALC-0437',
    severity: 'Warning',
    message: `Колонка «${columnCode}» обов'язкова для методології «${methodology}».`,
    rowKey: 'r1',
    columnCode,
  })),
);

function mockServer(validation: unknown[]): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (_path: string, init?: RequestInit) =>
      init?.method === 'PATCH'
        ? new Response(JSON.stringify({ appliedCells: 1, rowVersions: { r1: 'v2' }, validation }), {
            status: 200,
            headers: { 'Content-Type': 'application/json' },
          })
        : new Response(JSON.stringify(sliceFixture()), { status: 200, headers: { 'Content-Type': 'application/json' } }),
    ),
  );
}

function show(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <MantineProvider>
      <QueryClientProvider client={client}>
        <DocumentGrid
          documentId={1}
          tableInstanceId={1}
          tableDefId={1}
          periodKey={202609}
          readOnly={false}
          allowsDynamicRows
          maxDynamicRows={null}
        />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  cancelAutosave();
  resetPending();
  vi.unstubAllGlobals();
});

async function patches(n: number): Promise<void> {
  await waitFor(() => expect(vi.mocked(fetch).mock.calls.filter(([, init]) => init?.method === 'PATCH')).toHaveLength(n), {
    timeout: 8_000,
  });
}

describe('DocumentGrid: попередження обов’язкових колонок під час введення', () => {
  it('згорнуте, рядок на колонку, назви колонок, лічильник = перелік; повторне збереження не додає рядків', async () => {
    mockServer(Legacy);
    show();
    await screen.findByTestId('revogrid-stub');
    const user = userEvent.setup();

    await user.click(screen.getByRole('button', { name: 'simulate-edit' }));
    await patches(1);
    const summary = await waitFor(() => {
      const node = document.querySelector<HTMLElement>('[data-required-inputs="warning"]');
      expect(node).not.toBeNull();
      return node as HTMLElement;
    });

    // Згорнуто: ні переліку, ні українського тексту, ні alert-банера над сіткою.
    expect(summary.getAttribute('aria-expanded')).toBe('false');
    expect(summary.textContent).toContain('grid.requiredInput.summary (count=2)');
    expect(screen.queryByRole('list')).toBeNull();
    expect(document.body.textContent).not.toMatch(/обов'язкова|Колонка/);
    expect(document.querySelector('.mantine-Alert-root')).toBeNull();

    // Друге збереження того самого рядка — та сама відповідь: нічого не накопичується.
    await user.click(screen.getByRole('button', { name: 'simulate-edit' }));
    await patches(2);

    const toggle = document.querySelector<HTMLElement>('[data-required-inputs="warning"]') as HTMLElement;
    expect(toggle.textContent).toContain('(count=2)');
    await user.click(toggle);
    const list = await screen.findByRole('list', {}, { timeout: 5_000 });
    const items = within(list).getAllByRole('listitem');
    expect(items).toHaveLength(2);
    expect(items[0]?.textContent).toContain('column=Type of equipment');
    expect(items[0]?.textContent).toContain('rows=1');
    expect(items[1]?.textContent).toContain('column=Type of fuel');
    expect(list.textContent).not.toMatch(/Land_|[Ѐ-ӿ]/);
  });
});
