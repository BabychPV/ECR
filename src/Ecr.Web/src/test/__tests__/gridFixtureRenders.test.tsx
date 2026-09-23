import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { DocumentGrid } from '@/features/grid/DocumentGrid';
import { emptyBodyFor, registerGridLayout } from './a11yFixtures';

/**
 * Сторож самого гейта доступності (`ФВ-14.16`).
 *
 * ⛔ Навіщо окремий тест на ПРИЛАД, а не на застосунок. Гейт `a11y` існував і
 * мовчав: `emptyBodyFor` віддавав `{ columns: [], rows: [] }`, сітка малювала
 * порожній стан, і `axe` жодного разу не бачив комірки. Дефект «текст у
 * комірці невидимий у темній темі» (1.35 : 1, виміряно в браузері) проїхав
 * повз перевірку, яка формально його область покриває.
 *
 * ⛔ Полагодити прилад і не поставити на нього сторожа означало б відкласти ту
 * саму поразку: наступний, хто спростить `emptyBodyFor` «бо порожнє швидше»,
 * знову вимкне половину гейта — і знову мовчки, бо `a11y` лишиться зеленим.
 * Цей тест червоніє одразу і за секунди, а не за 26 хвилин повного прогону.
 *
 * ⚠ Тест навмисно живе в ШВИДКОМУ наборі (`npm test`), а не серед
 * `*.a11y.test.tsx`: він не проганяє `axe` і не має ділити з ним 400-секундні
 * межі часу.
 */

afterEach(() => {
  vi.unstubAllGlobals();
});

registerGridLayout();

function show(): HTMLElement {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  return render(
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
  ).container;
}

describe('Прилад a11y віддає сітку з КОМІРКАМИ, а не порожній стан', () => {
  it('на відповіді `emptyBodyFor` RevoGrid малює реальні комірки', async () => {
    // ⛔ Береться САМЕ `emptyBodyFor`, а не константа зрізу: регрес, якого тут
    // бояться, — не «хтось видалив фікстуру», а «хтось знову спростив гілку
    // `/tables/...` до порожньої», як це вже було.
    vi.stubGlobal(
      'fetch',
      vi.fn(async () =>
        Promise.resolve(
          new Response(JSON.stringify(emptyBodyFor('/api/documents/1/tables/1?periodKey=202601')), {
            status: 200,
            headers: { 'Content-Type': 'application/json' },
          }),
        ),
      ),
    );

    const container = show();

    // ⚠ Саме `.rgCell`, а не «сітка змонтувалася»: `<revo-grid>` гідрується в
    // jsdom навіть із нульовими розмірами — і саме тому «компонент є» ніколи
    // не був доказом, що є що перевіряти.
    await waitFor(
      () => {
        expect(container.querySelectorAll('.rgCell').length).toBeGreaterThan(0);
      },
      { timeout: 5000 },
    );

    /*
     * ⚠ `UI-08` додав до сітки ЗАКРІПЛЕНИЙ рядок підсумків
     * (`pinnedBottomSource`), і його комірки — теж `.rgCell`. Розділено їх
     * явно, а не підправлено число: сторож тут про те, що прилад віддає
     * КОМІРКИ ЗРІЗУ зі станами, і «6 стало 9» без пояснення наступного разу
     * прочиталося б як «фікстура знову з'їхала».
     *
     * ⚠ Заразом це й перевірка, що закріплений рядок доходить до СПРАВЖНЬОГО
     * RevoGrid, а не лише до заглушки в
     * `features/grid/__tests__/DocumentGrid.formulaTotals.test.tsx`.
     */
    const all = [...container.querySelectorAll('.rgCell')];
    const totals = all.filter((cell) => cell.hasAttribute('data-grid-totals'));
    const cells = all.filter((cell) => !cell.hasAttribute('data-grid-totals'));

    // Три колонки × два рядки зрізу.
    expect(cells).toHaveLength(6);

    // І три комірки підсумків — по одній на колонку.
    expect(totals).toHaveLength(3);

    // Звичайна редагована комірка — та сама, що була виміряна на 1.35 : 1:
    // жодного класу стану, отже жодного нашого фону, отже колір тексту бере
    // виключно з CSS пакета.
    // ⚠ ТОЧНИЙ токен, а не підрядок: після `U-05` числова комірка
    // несе ще й `ecr-cell-numeric` (вирівнювання, жодного фону), і
    // `includes('ecr-cell')` вважав би таку комірку за комірку зі СТАНОМ.
    const plain = cells.filter((cell) => !cell.classList.contains('ecr-cell'));
    expect(plain.map((cell) => cell.textContent)).toContain('182.5');

    // І всі стани, заради яких зріз зроблений саме таким.
    const states = new Set(
      cells
        .map((cell) => cell.getAttribute('data-cell-state'))
        .filter((state): state is string => state !== null),
    );

    expect(states).toEqual(new Set(['calculated', 'readOnly', 'orphaned']));
  });
});
