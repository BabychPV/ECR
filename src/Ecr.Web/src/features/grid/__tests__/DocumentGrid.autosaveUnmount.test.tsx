import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, render, screen, fireEvent } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { TableSliceDto } from '@/api/types';
import { DocumentGrid } from '../DocumentGrid';

/**
 * Таймер автозбереження не переживає розмонтування гріда.
 *
 * ⛔ Дефект, який цей файл тримає закритим: `autosaveDebouncer` (`autosave.ts`,
 * `createDebouncer`) планував `setTimeout` на 500 мс і НЕ скасовувався при
 * розмонтуванні — лише при переході на інший зріз. Аркуші документа
 * рендеряться списком гридів із `key={tableInstanceId}` (`DocumentPage.tsx`),
 * тож перемикання аркуша розмонтовує гриди попереднього. Правка, введена
 * менш ніж за 500 мс до перемикання, ішла на сервер `PATCH`-ом ВІД ІМЕНІ
 * ГРІДА, ЯКОГО ВЖЕ НЕМА: показати конфлікт `409`, відмову валідації чи банер
 * «не збережено» було нікому — весь цей стан належав розмонтованому дереву.
 *
 * ⚠ Той самий «зомбі-таймер» був і причиною плаваючого падіння
 * `DocumentGrid.concurrentSave.test.tsx` під повним набором: таймер, заведений
 * у ОДНОМУ тесті, спрацьовував уже під час НАСТУПНОГО і додавав чужий `PATCH`
 * у спільний журнал запитів. Знайдено саме за цим слідом — стек того зайвого
 * запиту вів у `Timeout._onTimeout (autosave.ts:40)`, а не в клік користувача.
 *
 * ⚠ Очікування тут справжнє (не фальшиві таймери) і навмисно ДОВШЕ за 500 мс.
 * Помилитися воно може лише в один бік: під навантаженням реального часу мине
 * ще більше, тобто незакритий таймер мав би ще БІЛЬШЕ шансів спрацювати.
 * «Зелено через те, що не встигло» тут неможливе — зелено лише тоді, коли
 * таймер справді скасовано.
 */

vi.mock('@revolist/react-datagrid', () => ({
  RevoGrid: (props: { onAfteredit?: (event: { detail: unknown }) => void }) => (
    <div data-testid="revogrid-stub">
      <button
        type="button"
        onClick={() =>
          props.onAfteredit?.({
            detail: { prop: 'C1', model: { __rowKey: 'r1' }, val: '5' },
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
        header: 'C1',
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
        cells: { C1: 1 },
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

/** Усі `PATCH`, що дійшли до мережі, у порядку надходження. */
const patched: string[] = [];

function mockServer(): void {
  patched.length = 0;

  vi.stubGlobal(
    'fetch',
    vi.fn(async (_path: string, init?: RequestInit) => {
      if (init?.method === 'PATCH') {
        patched.push(String(init.body));

        return new Response(
          JSON.stringify({ appliedCells: 1, rowVersions: {}, validation: [] }),
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

function show(): { unmount: () => void } {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  return render(
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

/** Пропускає повний інтервал дебаунсу (500 мс) із запасом. */
async function passDebounceWindow(): Promise<void> {
  await act(async () => {
    await new Promise((resolve) => setTimeout(resolve, 900));
  });
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('DocumentGrid: автозбереження не переживає розмонтування', () => {
  it('правка перед розмонтуванням НЕ йде на сервер запитом від мертвого компонента', async () => {
    mockServer();
    const view = show();

    await screen.findByTestId('revogrid-stub');
    fireEvent.click(screen.getByRole('button', { name: 'simulate-edit' }));

    // Оператор перемикає аркуш одразу — грід зникає, не дочекавшись дебаунсу.
    view.unmount();
    await passDebounceWindow();

    // ⛔ Мутаційний доказ (RED без скасування в `useEffect`): тут приходив
    // рівно один `PATCH` із рядком `r1`, надісланий уже після розмонтування.
    expect(patched).toHaveLength(0);
  });

  it('доки грід змонтований, дебаунс автозбереження таки спрацьовує', async () => {
    mockServer();
    show();

    await screen.findByTestId('revogrid-stub');
    fireEvent.click(screen.getByRole('button', { name: 'simulate-edit' }));

    // ⚠ Зворотний бік того самого скасування: якби фікс знімав таймер завжди
    // (а не лише на розмонтуванні), автозбереження просто перестало б
    // існувати — і перший тест був би зелений із зовсім іншої причини.
    await passDebounceWindow();

    expect(patched).toHaveLength(1);
    expect(String(patched[0])).toContain('"rowKey":"r1"');
  });
});
