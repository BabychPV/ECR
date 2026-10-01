import type { JSX } from 'react';
import { useMemo } from 'react';
import { RevoGrid } from '@revolist/react-datagrid';
import type { ColumnRegular } from '@revolist/revogrid';
import { useRowHeight } from '@/shared/theme/preferences';

/**
 * Стенд віртуалізації сітки: 500 рядків × 60 колонок (`ФВ-14.29`, бюджет
 * `tz/08` §8.2 «500×60»).
 *
 * ⛔ Навіщо окрема сторінка. У jsdom розкладки немає, а RevoGrid у юніт-тестах
 * — заглушка, тож довести «малюється лише видиме вікно» там неможливо в
 * принципі. Документ стенда (`e2e-stand.ps1`) таблиці 500×60 не має. Тут —
 * синтетичні дані того самого розміру і ТІ САМІ параметри `<RevoGrid>`, що в
 * `DocumentGrid` (тема, висота рядка, `range`, `resize`, слот 70vh), а міряє
 * `e2e/virtualGrid.spec.ts` у справжньому браузері.
 *
 * ⚠ Лише для DEV (`router.tsx`, `devRoutes`): у виробничій збірці маршруту
 * немає, і в бюджет бандлів (`D-132`) сторінка не входить.
 */
export const StandRows = 500;
export const StandColumns = 60;

export function VirtualGridStandPage(): JSX.Element {
  const rowSize = useRowHeight();

  const columns = useMemo<ColumnRegular[]>(
    () =>
      Array.from({ length: StandColumns }, (_, c) => ({
        prop: `C${c + 1}`,
        name: `C${c + 1}`,
        size: 110,
      })),
    [],
  );

  const rows = useMemo(
    () =>
      Array.from({ length: StandRows }, (_, r) => {
        const row: Record<string, string> = {};
        for (let c = 0; c < StandColumns; c++) row[`C${c + 1}`] = `r${r + 1}c${c + 1}`;
        return row;
      }),
    [],
  );

  return (
    <div data-measure="virtual-grid" data-rows={StandRows} data-columns={StandColumns}>
      <RevoGrid
        theme="compact"
        rowSize={rowSize}
        range
        resize
        columns={columns}
        source={rows}
        style={{ height: '70vh' }}
      />
    </div>
  );
}
