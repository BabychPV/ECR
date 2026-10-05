import { useCallback, useState, type JSX } from 'react';
import { useSearchParams } from 'react-router-dom';
import { RevoGrid } from '@revolist/react-datagrid';
import type { ColumnRegular } from '@revolist/revogrid';
import { installEnterKeyCompat } from '@/features/grid/keyboardCompat';
import { installKeyCommitGate } from '@/features/grid/keyCommitGate';

/**
 * Стенд черги клавіш (T3-01 → T4-01) для `e2e/keyCommitGateLive.spec.ts`.
 *
 * ⛔ Навіщо стенд, а не модель у jsdom: перше виправлення T3-01 перевірялося
 * МОДЕЛЛЮ часової поведінки RevoGrid (`keyCommitGate.test.ts`), а модель
 * прибирала старий редактор одразу після Enter. Справжній RevoGrid лишає його
 * в DOM до `focuscell` і кілька мс після — тож тест був зелений, а дефект
 * повернувся як T4-01 (інтервал 50–80 мс). Тут — справжній
 * `@revolist/react-datagrid` з тими самими слухачами, що ставить
 * `DocumentGrid` (`installEnterKeyCompat`, `installKeyCommitGate`), і тими
 * самими пропсами, що впливають на редактор (`range`, `applyOnClose`).
 *
 * ⚠ Лише для DEV (`router.tsx`, `devRoutes`), як `/_virtual-grid`: без входу
 * і без бекенда, у виробничій збірці маршруту немає. `?gate=0` — той самий
 * грид без черги (контроль приладу: дефект відтворюється).
 */
const RowCount = 8;

const columns: ColumnRegular[] = [
  { prop: 'code', name: 'CODE', readonly: true, size: 100 },
  { prop: 'qty', name: 'QTY', size: 120 },
];

export function KeyCommitGateStandPage(): JSX.Element {
  const [source] = useState(() =>
    Array.from({ length: RowCount }, (_, index) => ({ code: `R${index + 1}`, qty: '' })),
  );
  const [params] = useSearchParams();
  const withGate = params.get('gate') !== '0';

  const containerRef = useCallback(
    (node: HTMLDivElement | null) => {
      if (node === null) return;

      const cleanups = withGate ? [installEnterKeyCompat(node), installKeyCommitGate(node)] : [];
      node.dataset['ready'] = 'true';

      return () => {
        for (const cleanup of cleanups) cleanup();
      };
    },
    [withGate],
  );

  return (
    <div ref={containerRef} data-stand="key-commit-gate">
      <RevoGrid
        theme="compact"
        range
        applyOnClose
        columns={columns}
        source={source}
        style={{ height: '400px', width: '400px' }}
      />
    </div>
  );
}
