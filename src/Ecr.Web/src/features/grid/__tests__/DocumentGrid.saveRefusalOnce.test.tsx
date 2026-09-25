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
 * `U-17`: одна відмова — одне повідомлення.
 *
 * ⛔ Що було. Та сама відмова стояла на екрані двічі поруч: у рядку кнопок —
 * «NOT SAVED — SEE THE ERROR ABOVE» великими червоними, а безпосередньо ПІД
 * ним — рамка «Not saved — see the error above» і вже сама причина. Обидва
 * написи малював ОДИН ключ (`grid.saveError`), тому вони й не могли не
 * збігатися, а верхній ще й відсилав «вище» до того, що насправді нижче.
 *
 * ⛔ Чому перевіряються РІЗНІ ключі, а не «badge зник». Позначка в рядку
 * кнопок лишається навмисно: банер може бути прокручений, а `role="alert"` на
 * позначці тримає `DocumentGrid.a11y-status.test.tsx`. Вада була не в тому,
 * що місць два, а в тому, що обидва говорили ОДНЕ Й ТЕ САМЕ речення.
 *
 * ⚠ Каталог у компонентних тестах порожній, тож `t()` віддає `⟦ключ⟧` — саме
 * тому тут видно, ЯКИЙ ключ малює кожне з двох місць. Що самі тексти в сіді
 * різні й не посилаються «вище», доводить `saveRefusalTexts.test.ts` поруч.
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

const SERVER_MESSAGE = 'Колонка «C1» очікує число.';

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

function mockRejectingServer(): void {
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

/** Автозбереження — реальний дебаунс 500 мс. */
const AutosaveTimeoutMs = 5_000;

afterEach(() => {
  cancelAutosave();
  resetPending();
  vi.unstubAllGlobals();
});

describe('U-17 · відмова збереження не друкується двічі', () => {
  it('позначка в рядку кнопок і банер причини — РІЗНІ тексти, не один ключ двічі', async () => {
    mockRejectingServer();
    show();

    await screen.findByTestId('revogrid-stub');

    const user = userEvent.setup();
    await user.click(screen.getByRole('button', { name: 'simulate-edit' }));

    // Причина (текст сервера) з'являється — це і є той єдиний банер.
    await waitFor(() => expect(screen.getByText(SERVER_MESSAGE)).toBeTruthy(), {
      timeout: AutosaveTimeoutMs,
    });

    const mark = document.querySelector('[data-save-status="error"]');

    expect(mark, 'позначка відмови в рядку кнопок має лишитися').not.toBeNull();

    const markText = (mark?.textContent ?? '').trim();

    // ⛔ Саме те, що ламалося: позначка малювалася ключем `grid.saveError` —
    // тим самим, що й заголовок банера нижче.
    expect(markText).toContain('grid.saveFailedMark');
    expect(markText).not.toContain('grid.saveError');

    // ⛔ І з іншого боку: заголовок банера свій ключ не втратив, тобто два
    // місця не «злилися» тим, що обидва стали позначкою.
    const banner = screen.getByText(SERVER_MESSAGE).closest('[role="alert"]');

    expect(banner, 'банер із причиною має бути').not.toBeNull();
    expect(banner?.textContent ?? '').toContain('grid.saveError');

    /*
     * ⛔ Головне твердження одним рядком: жоден із двох текстів не є копією
     * іншого. Без нього тест лишився б зеленим, якби ключ перейменували, а
     * значення в каталозі залишили тим самим реченням.
     */
    expect(markText).not.toBe((banner?.textContent ?? '').trim());
  });
});
