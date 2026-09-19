import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { TableSliceDto } from '@/api/types';
import { cancelAutosave } from '../autosave';
import { resetPending } from '../pendingStore';
import { DocumentGrid } from '../DocumentGrid';

/**
 * Finding 2 (High, Stage 1): "NOT SAVED — SEE THE ERROR ABOVE" вказував у
 * порожнечу — справжня, локалізована причина сервера («Колонка «C1» очікує
 * число.», `ECR-CELL-0422`, `422 Unprocessable Entity`) губилась у
 * необробленому знеструмленні проміса, яке бачить лише консоль розробника.
 *
 * ⛔ Доводить саме ТОЙ шлях, що й у Stage 1: невдала спроба зберегти комірку
 * → банер із РЕАЛЬНИМ текстом сервера з'являється там, де раніше стояла
 * тільки заглушка.
 *
 * ⚠ RevoGrid підмінено, як і в `DocumentGrid.a11y-status.test.tsx` (реальний
 * веб-компонент у jsdom лише заважає), але тут заглушка ІНТЕРАКТИВНА: кнопка
 * викликає `onAfteredit` так само, як зробив би реальний RevoGrid після
 * підтвердженої правки клітинки (Tab чи Enter — обидва йдуть цим самим
 * шляхом, `applyEditedValue`).
 */

vi.mock('@revolist/react-datagrid', () => ({
  RevoGrid: (props: { onAfteredit?: (event: { detail: unknown }) => void }) => (
    <div data-testid="revogrid-stub">
      <button
        type="button"
        onClick={() =>
          props.onAfteredit?.({
            detail: { prop: 'C1', model: { __rowKey: 'r1' }, val: 'abc' },
          })
        }
      >
        simulate-edit
      </button>
    </div>
  ),
}));

function sliceFixture(): TableSliceDto {
  return {
    cellConfirmations: {},
    cellPermissions: {},
    periodKey: 202609,
    tableInstanceId: 1,
    columns: [
      {
        code: 'C1',
        dataType: 'Decimal',
        defaultValue: null,
        displayFormat: null,
        header: 'Колонка 1',
        id: 1,
        isReadOnly: false,
        isRequired: false,
        isRequiredByMethodology: false,
        lookupRegistryDefId: null,
        ordinal: 0,
        unitId: null,
        unitSymbol: null,
      },
    ],
    rows: [
      {
        cells: { C1: 321 },
        isOrphaned: false,
        label: null,
        ordinal: 0,
        rowKey: 'r1',
        rowKind: 'Item',
        rowVersion: 'v1',
      },
    ],
  };
}

/** Той самий текст, що й Stage 1: `422` із конкретним, локалізованим повідомленням. */
const SERVER_MESSAGE = 'Колонка «C1» очікує число.';

function mockServer(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (_path: string, init?: RequestInit) => {
      if (init?.method === 'PATCH') {
        return new Response(
          JSON.stringify({
            title: 'Помилка валідації',
            status: 422,
            detail: SERVER_MESSAGE,
            errorCode: 'ECR-CELL-0422',
            correlationId: 'c1',
            columnCode: 'C1',
            expected: 'число',
            actualKind: 'String',
          }),
          { status: 422, headers: { 'Content-Type': 'application/problem+json' } },
        );
      }

      return new Response(JSON.stringify(sliceFixture()), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      });
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
          periodKey={202609}
          readOnly={false}
          allowsDynamicRows={false}
          maxDynamicRows={null}
        />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  // ⛔ `D14-12`: незбережені правки живуть у МОДУЛЬНОМУ сховищі документа й
  // переживають кінець тесту — як і запланований дебаунсом пакет. Без
  // скидання правка одного сценарію потрапила б у `PATCH` наступного.
  cancelAutosave();
  resetPending();
  vi.unstubAllGlobals();
});

describe('DocumentGrid: справжня причина відмови показується (Q-30x)', () => {
  it('невдале збереження показує РЕАЛЬНИЙ текст сервера, не лише заглушку "NOT SAVED"', async () => {
    mockServer();
    show();

    await screen.findByTestId('revogrid-stub');

    const user = userEvent.setup();
    await user.click(screen.getByRole('button', { name: 'simulate-edit' }));

    // ⚠ Кнопка «Зберегти» — детермінований шлях, без очікування 500-мс
    // дебаунсу автозбереження.
    const saveButton = await screen.findByRole('button', { name: /grid\.save/ });
    await user.click(saveButton);

    // ⛔ Це і є доказ фіксу: раніше під заглушкою "NOT SAVED — SEE THE ERROR
    // ABOVE" не було НІЧОГО — цей текст просто не існував у DOM. Тепер він
    // з'являється, дослівно як відповів сервер.
    await waitFor(() => {
      expect(screen.getByText(SERVER_MESSAGE)).toBeTruthy();
    });
  });

  it('успішне повторне збереження прибирає банер помилки', async () => {
    mockServer();
    show();

    await screen.findByTestId('revogrid-stub');

    const user = userEvent.setup();
    await user.click(screen.getByRole('button', { name: 'simulate-edit' }));
    await user.click(await screen.findByRole('button', { name: /grid\.save/ }));
    await waitFor(() => expect(screen.getByText(SERVER_MESSAGE)).toBeTruthy());

    // Наступна спроба (та сама правка) цього разу приймається сервером.
    vi.stubGlobal(
      'fetch',
      vi.fn(async (_path: string, init?: RequestInit) => {
        if (init?.method === 'PATCH') {
          return new Response(
            JSON.stringify({ appliedCells: 1, rowVersions: { r1: 'v2' }, validation: [] }),
            { status: 200, headers: { 'Content-Type': 'application/json' } },
          );
        }

        return new Response(JSON.stringify(sliceFixture()), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        });
      }),
    );

    await user.click(await screen.findByRole('button', { name: /grid\.save/ }));

    await waitFor(() => {
      expect(screen.queryByText(SERVER_MESSAGE)).toBeNull();
    });
  });
});
