import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ColumnDto, TableSliceDto } from '@/api/types';
import { cancelAutosave } from '../autosave';
import { inFlightRowKeys } from '../inFlightEdits';
import { resetPending } from '../pendingStore';
import { DocumentGrid } from '../DocumentGrid';

/**
 * `G1-02`: людина повертає комірку до збереженого значення, поки її попереднє
 * значення ЛЕТИТЬ на сервер. Збережено C1=0 → введено 5 (PATCH у дорозі) →
 * введено 0. Доти `captureEdit` порівнював 0 із кешем (там ще 0), `V-01` знімав
 * правку зі сховища, і після відповіді на сервері й на екрані лишалось 5.
 *
 * ⚠ Автозбереження приглушене (як у `DocumentGrid.concurrentSave.test.tsx`):
 * порядок запитів задає тест через Ctrl+S, а не годинник машини.
 */
vi.mock('../autosave', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../autosave')>();

  return { ...actual, scheduleAutosave: (): void => undefined };
});

vi.mock('@revolist/react-datagrid', () => ({
  RevoGrid: (props: { onAfteredit?: (event: { detail: unknown }) => void }) => (
    <div data-testid="revogrid-stub">
      <div className="edit-input-wrapper">
        <input data-testid="editor-input" />
      </div>
      {['5', '0'].map((val) => (
        <button
          key={val}
          type="button"
          onClick={() => props.onAfteredit?.({ detail: { prop: 'C1', model: { __rowKey: 'r1' }, val } })}
        >
          {`type-${val}`}
        </button>
      ))}
    </div>
  ),
}));

function column(code: string): ColumnDto {
  return {
    code,
    dataType: 'Decimal',
    defaultValue: null,
    displayFormat: null,
    header: code,
    id: 1,
    isReadOnly: false,
    isRequired: false,
    isRequiredByMethodology: false,
    lookupRegistryDefId: null,
    ordinal: 0,
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
    columns: [column('C1')],
    rows: [
      {
        cells: { C1: '0' },
        isOrphaned: false,
        label: null,
        ordinal: 0,
        rowKey: 'r1',
        rowVersion: 'v1',
        rowKind: 'Item' as const,
      },
    ],
  };
}

interface SentPatch {
  rows: { rowKey: string; baseVersion: string | null; cells: { columnCode: string; value: unknown }[] }[];
  settle: (rowVersion: string) => void;
}

const sent: SentPatch[] = [];

function mockServer(): void {
  sent.length = 0;

  vi.stubGlobal(
    'fetch',
    vi.fn((_path: string, init?: RequestInit) => {
      if (init?.method === 'PATCH') {
        const body = JSON.parse(String(init.body)) as Pick<SentPatch, 'rows'>;

        return new Promise<Response>((resolve) => {
          sent.push({
            rows: body.rows,
            settle: (rowVersion) =>
              resolve(
                new Response(JSON.stringify({ appliedCells: 1, rowVersions: { r1: rowVersion }, validation: [] }), {
                  status: 200,
                  headers: { 'Content-Type': 'application/json' },
                }),
              ),
          });
        });
      }

      return Promise.resolve(
        new Response(JSON.stringify(sliceFixture()), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        }),
      );
    }),
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
          allowsDynamicRows={false}
          maxDynamicRows={null}
        />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

/**
 * Людина набирає значення в редакторі комірки, а потім кнопка-заглушка комітить його.
 *
 * ⚠ Без вводу в редакторі сітка вважає коміт «редактор відкрито й закрито без
 * вводу» (`untouched`, AN-39/L8-15), і `captureOverInFlight` такий коміт свідомо
 * не бере — це захист від луни показаного значення. Тест G1-02 про ЯВНЕ
 * повернення значення, тож і введення має бути явним.
 */
function type(val: string): void {
  fireEvent.input(screen.getByTestId('editor-input'), { target: { value: val } });
  fireEvent.click(screen.getByRole('button', { name: `type-${val}` }));
}

async function pressCtrlS(): Promise<void> {
  await act(async () => {
    fireEvent.keyDown(screen.getByTestId('revogrid-stub'), { key: 's', ctrlKey: true });
    await Promise.resolve();
  });
}

afterEach(() => {
  cancelAutosave();
  resetPending();
  vi.unstubAllGlobals();
});

describe('DocumentGrid: повернення до збереженого, поки PATCH у дорозі (G1-02)', () => {
  it('виправлення не викидається — після відповіді воно їде окремим PATCH з новою версією', async () => {
    mockServer();
    show();

    await screen.findByTestId('revogrid-stub');

    type('5');
    await pressCtrlS();
    await waitFor(() => expect(sent).toHaveLength(1));
    expect(Number(sent[0]?.rows[0]?.cells[0]?.value)).toBe(5);

    // Людина бачить помилку й повертає збережене 0, поки PATCH(5) летить.
    type('0');

    // Рядок у дорозі — другий запит чекає відповіді першого (`splitByInFlight`).
    await pressCtrlS();
    expect(sent).toHaveLength(1);

    await act(async () => {
      sent[0]?.settle('v2');
      await Promise.resolve();
    });

    // ⚠ Відповідь розбирається кілька тактів (`Response.json`), а відкладене
    // автозбереження тут приглушене: Ctrl+S, натиснутий поки рядок ще «в дорозі»,
    // знову відклав би правку — і назавжди. Тож спершу — дочекатися, що запит
    // справді завершився (`endInFlight`), як `settle` + `waitFor` у
    // `DocumentGrid.concurrentSave.test.tsx`.
    await waitFor(() => expect(inFlightRowKeys(1, 202609).size).toBe(0));

    // ⛔ Мутаційний доказ: без `captureOverInFlight` сховище порожнє — другого
    // PATCH немає, на сервері лишається 5.
    await pressCtrlS();
    await waitFor(() => expect(sent).toHaveLength(2));
    expect(sent[1]?.rows[0]?.rowKey).toBe('r1');
    expect(sent[1]?.rows[0]?.baseVersion).toBe('v2');
    expect(Number(sent[1]?.rows[0]?.cells[0]?.value)).toBe(0);
  });

  it('повернення до збереженого ДО надсилання — як і раніше, скасування без запиту (V-01)', async () => {
    mockServer();
    show();

    await screen.findByTestId('revogrid-stub');

    type('5');
    type('0');
    await pressCtrlS();

    expect(sent).toHaveLength(0);
  });
});
