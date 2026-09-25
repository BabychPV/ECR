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
 * `U-16`: головна кнопка панелі більше не суперечить моделі роботи.
 *
 * ⛔ Що було. Модель збереження в сітці — АВТОЗБЕРЕЖЕННЯ (`autosave.ts`):
 * правильна правка після Enter доїжджає сама, поруч блимає «Saved». А над
 * сіткою при цьому стояла кнопка «Save (0)», завжди видима й вимкнена;
 * лічильник у неї з'являвся РІВНО тоді, коли сервер правку відхилив —
 * «Save (1)» поруч із «NOT SAVED». Тобто підпис обіцяв «стільки змін чекає
 * збереження», а показував «стільки змін збереження не пройшли».
 *
 * ⛔ Обрана модель (одна, і вона тут і перевіряється): автозбереження
 * лишається єдиним способом зберегти, а кнопка з'являється ЛИШЕ тоді, коли є
 * що робити руками — після відмови — і називає саме це: «Retry save (N)».
 *
 * ⚠ Тому обидва твердження йдуть через СПРАВЖНЄ автозбереження (500-мс
 * дебаунс, реальні таймери), а не через клік по кнопці: клік по кнопці не
 * довів би нічого про модель — саме він і був єдиним шляхом у старому тесті.
 *
 * ⚠ RevoGrid підмінено інтерактивною заглушкою — той самий прийом, що в
 * `DocumentGrid.saveError.test.tsx`: кнопка кличе `onAfteredit` так само, як
 * реальна сітка після підтвердженої правки клітинки.
 */

vi.mock('@revolist/react-datagrid', () => ({
  RevoGrid: (props: { onAfteredit?: (event: { detail: unknown }) => void }) => (
    <div data-testid="revogrid-stub">
      <button
        type="button"
        onClick={() =>
          props.onAfteredit?.({
            detail: { prop: 'C1', model: { __rowKey: 'r1' }, val: '42' },
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

/** Скільки разів сервер отримав `PATCH` — ним і міряється «повтор». */
let patches = 0;

/** Сервер, який приймає правку (звичайний, успішний шлях автозбереження). */
function mockAcceptingServer(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (_path: string, init?: RequestInit) => {
      if (init?.method === 'PATCH') {
        patches += 1;

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
}

/** Сервер, який правку відхиляє — рівно той випадок із `cell-text-into-number.png`. */
function mockRejectingServer(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (_path: string, init?: RequestInit) => {
      if (init?.method === 'PATCH') {
        patches += 1;

        return new Response(
          JSON.stringify({
            title: 'Помилка валідації',
            status: 422,
            detail: 'Колонка «C1» очікує число.',
            errorCode: 'ECR-CELL-0422',
            correlationId: 'c1',
            columnCode: 'C1',
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

/** Будь-яка кнопка збереження в панелі — і стара, і нова. */
function saveLikeButton(): HTMLElement | null {
  return screen.queryByRole('button', { name: /grid\.(save|retrySave)/ });
}

/** Автозбереження — реальний дебаунс 500 мс, тож чекати доводиться довше за типове. */
const AutosaveTimeoutMs = 5_000;

afterEach(() => {
  // ⛔ `D14-12`: сховище правок і план дебаунсу модульні — переживають тест.
  cancelAutosave();
  resetPending();
  vi.unstubAllGlobals();
  patches = 0;
});

describe('U-16 · кнопка збереження і модель автозбереження', () => {
  it('правка, яку сервер прийняв: зберігається сама, кнопки збереження немає взагалі', async () => {
    mockAcceptingServer();
    show();

    await screen.findByTestId('revogrid-stub');

    // ⛔ Кнопки немає вже до правки: «Save (0)», завжди видима й вимкнена, —
    // це і був той підпис, який обіцяв неправду.
    expect(saveLikeButton()).toBeNull();

    const user = userEvent.setup();
    await user.click(screen.getByRole('button', { name: 'simulate-edit' }));

    // ⛔ Ніхто не тиснув «зберегти»: `PATCH` надсилає автозбереження.
    await waitFor(() => expect(patches).toBe(1), { timeout: AutosaveTimeoutMs });

    // ⛔ І після успіху кнопки теж немає: пропонувати «зберегти» те, що вже
    // збережено, — рівно та суперечність, заради якої цей набір написаний.
    await waitFor(() => expect(saveLikeButton()).toBeNull(), { timeout: AutosaveTimeoutMs });
  });

  it('правку відхилено: з\'являється кнопка ПОВТОРУ з лічильником, і вона повторює', async () => {
    mockRejectingServer();
    show();

    await screen.findByTestId('revogrid-stub');

    const user = userEvent.setup();
    await user.click(screen.getByRole('button', { name: 'simulate-edit' }));

    await waitFor(() => expect(patches).toBe(1), { timeout: AutosaveTimeoutMs });

    /*
     * ⛔ Саме `grid.retrySave`, а не `grid.save`. Каталог у компонентних
     * тестах порожній, тож `t()` віддає `⟦ключ (count=1)⟧` — тобто в імені
     * кнопки видно і ключ, і лічильник. Ключ перевіряється разом із
     * лічильником навмисно: «1» під підписом «Save» і «1» під підписом
     * «Retry save» — це два РІЗНІ твердження, і вада була саме в тому, яке з
     * них показували.
     */
    const retry = await screen.findByRole(
      'button',
      { name: /grid\.retrySave/ },
      { timeout: AutosaveTimeoutMs },
    );

    expect(retry.textContent).toContain('count=1');
    expect(screen.queryByRole('button', { name: /grid\.save\b/ })).toBeNull();

    // ⛔ Кнопка не декоративна: вона справді шле правку ще раз.
    await user.click(retry);
    await waitFor(() => expect(patches).toBe(2), { timeout: AutosaveTimeoutMs });
  });
});
