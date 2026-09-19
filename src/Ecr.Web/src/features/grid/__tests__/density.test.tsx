import { readFileSync } from 'node:fs';
import path from 'node:path';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, render, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { TableSliceDto } from '@/api/types';
import { applyDensity, RowHeightVar, setDensity } from '@/shared/theme/preferences';
import { cancelAutosave } from '../autosave';
import { resetPending } from '../pendingStore';
import { DocumentGrid } from '../DocumentGrid';

/**
 * `UI-03`: сітка документа читає ту саму змінну щільності, що й таблиці.
 *
 * ⛔ Чому це не можна перевірити так само, як таблицю. RevoGrid ВІРТУАЛІЗУЄ
 * подання: він сам рахує, скільки рядків помістилося і на скільки пікселів
 * зсунути полотно, і бере висоту рядка числом (`rowSize`). Висота, задана
 * стилем повз `rowSize`, розсунула б намальоване відносно того, що бібліотека
 * вважає видимим, — комірка під курсором виявилася б не тією, у яку йде
 * введення. Тому сітка читає `--ecr-row-height` через `useRowHeight()`, і
 * доказом є число, що долетіло до самого елемента `<revo-grid>`.
 *
 * ⚠ Обгортка `@revolist/react-datagrid` віддає числовий проп ДВІЧІ: атрибутом
 * `row-size` при рендері і властивістю `rowSize` у `componentDidUpdate`.
 * Перевіряються обидва — атрибут видно в DOM, властивість читає сама
 * бібліотека, і розійтися вони не мають права.
 *
 * ⛔ Очікуване число НЕ зашите в тесті: воно береться з каскаду тим самим
 * читанням, що й у таблиці, і спершу перевіряється на формат. Інакше
 * «змінної немає» тихо прочиталося б як «висота правильна» — та сама пастка,
 * що вже спрацювала в `expectFocusRing`.
 */

const TokensCss = readFileSync(
  path.resolve(process.cwd(), 'src/shared/theme/tokens.css'),
  'utf8',
);

function loadTokens(): void {
  const style = document.createElement('style');
  style.dataset['ecrTestTokens'] = 'true';
  style.textContent = TokensCss;
  document.head.append(style);
}

function rowHeightVarPx(): number {
  const raw = getComputedStyle(document.documentElement).getPropertyValue(RowHeightVar).trim();

  expect(
    raw,
    `${RowHeightVar} не оголошена в каскаді: сітці не було б з чого взяти висоту.`,
  ).toMatch(/^\d+(?:\.\d+)?px$/);

  return Number.parseFloat(raw);
}

function sliceFixture(): TableSliceDto {
  return {
    cellConfirmations: {},
    cellPermissions: {},
    periodKey: 202601,
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

function stubSliceFetch(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async () =>
      Promise.resolve(
        new Response(JSON.stringify(sliceFixture()), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        }),
      ),
    ),
  );
}

async function showGrid(): Promise<Element> {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider>
      <QueryClientProvider client={client}>
        <DocumentGrid
          documentId={1}
          tableInstanceId={1}
          periodKey={202601}
          readOnly={false}
          allowsDynamicRows={false}
          maxDynamicRows={null}
        />
      </QueryClientProvider>
    </MantineProvider>,
  );

  // ⚠ Спершу дочекатися, поки зріз приїде і `AsyncBoundary` віддасть екран:
  // до цього моменту жодного `<revo-grid>` у DOM немає взагалі.
  await waitFor(() => {
    expect(document.querySelector('revo-grid'), 'сітка не змонтувалася').not.toBeNull();
  });

  return document.querySelector('revo-grid') as Element;
}

/**
 * Висота рядка, як її бачить САМА бібліотека: атрибут у розмітці і
 * властивість на елементі.
 */
function gridRowSize(grid: Element): { attribute: string | null; property: unknown } {
  return {
    attribute: grid.getAttribute('row-size'),
    property: (grid as Element & { rowSize?: unknown }).rowSize,
  };
}

/** Перемикає щільність рівно так, як це робить меню профілю. */
function switchDensity(value: 'compact' | 'comfortable'): void {
  act(() => {
    setDensity(value);
    applyDensity(value);
  });
}

afterEach(() => {
  cleanup();
  cancelAutosave();
  resetPending();
  vi.unstubAllGlobals();

  for (const style of document.querySelectorAll('style[data-ecr-test-tokens]')) style.remove();
  delete document.documentElement.dataset['ecrDensity'];

  setDensity('compact');
});

describe('UI-03: перемикання щільності змінює висоту рядка СІТКИ', () => {
  it('rowSize сітки йде за --ecr-row-height, а не за зашитим числом', async () => {
    loadTokens();
    stubSliceFetch();
    switchDensity('compact');

    const grid = await showGrid();

    const compact = rowHeightVarPx();
    expect(gridRowSize(grid)).toEqual({ attribute: String(compact), property: compact });

    switchDensity('comfortable');

    const comfortable = rowHeightVarPx();

    // ⛔ Саме тут падає зашите `rowSize`: число змінної поїхало, а сітка
    // лишилася там, де була.
    expect(gridRowSize(grid)).toEqual({ attribute: String(comfortable), property: comfortable });

    // ⚠ І окремо — що числа справді різні: збіг обох тверджень на одному й
    // тому самому числі не довів би нічого.
    expect(comfortable).toBeGreaterThan(compact);
  });

  it('шкура RevoGrid лишається compact на обох щільностях', async () => {
    loadTokens();
    stubSliceFetch();
    switchDensity('compact');

    const grid = await showGrid();
    expect(grid.getAttribute('theme')).toBe('compact');

    switchDensity('comfortable');

    // ⛔ `theme` RevoGrid — це ШКУРА (шрифт `Nunito`, шапка капслоком, зашиті
    // `#000`/`#f8f9fa` у `theme=default`), а не щільність. Перемикати її разом
    // із висотою рядка означало б міняти шрифт і кольори шапки — і вийти
    // з-під сторожа контрасту, зведеного саме для `[theme=compact]`
    // (`gridCellContrast.test.ts`). Щільність — це `rowSize`.
    expect(grid.getAttribute('theme')).toBe('compact');
  });
});
