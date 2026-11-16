import { describe, expect, it } from 'vitest';
import { render, screen } from '@testing-library/react';
import type { RowLockDto } from '@/features/documents/rowLocks/api';
import { Shell, Themes } from '@/test/__tests__/a11yFixtures';
import { describe as describeViolations, findViolations } from '@/test/a11y';
import RowLockDialog from '../RowLockDialog';
import RowLocksPanel from '../RowLocksPanel';

/**
 * ✎ B139 (B75): діалог «Заблокувати рядки…» і перелік блокувань в обох темах — без блокуючих порушень axe
 * (підписи полів дат і примітки, таблиця з заголовками колонок, кнопки «Зняти» з назвою рядка, контраст бейджа
 * «ще не почалось»).
 */
function lock(over: Partial<RowLockDto> = {}): RowLockDto {
  return {
    canUnlock: true,
    id: 5,
    isActive: true,
    lockFrom: '2026-11-01',
    lockUntil: '2026-11-30',
    note: 'Annual audit',
    ownerDisplayName: 'A. Serikbayev',
    ownerUserId: 3,
    rowKey: 'R2',
    sheetCode: 'S1',
    sheetCopyId: null,
    tableCode: 'T1',
    ...over,
  };
}

describe('Блокування рядків — доступність', { timeout: 120_000 }, () => {
  for (const colorScheme of Themes) {
    it(`${colorScheme}: діалог «Заблокувати рядки…» без порушень`, async () => {
      render(
        <Shell colorScheme={colorScheme}>
          <RowLockDialog
            documentId={96}
            periodKey={202611}
            table={{ sheetCode: 'S1', tableCode: 'T1', sheetCopyId: null }}
            rows={[
              { rowKey: 'R1', label: 'Boiler 1' },
              { rowKey: 'R2', label: 'Boiler 2' },
            ]}
            onClose={() => {}}
          />
        </Shell>,
      );
      await screen.findByRole('dialog');

      const violations = await findViolations(document.body);
      expect(violations, describeViolations(violations)).toHaveLength(0);
    });

    it(`${colorScheme}: перелік блокувань зі «Зняти» і ще не початим блокуванням без порушень`, async () => {
      render(
        <Shell colorScheme={colorScheme}>
          <RowLocksPanel
            documentId={96}
            periodKey={202611}
            locks={[lock(), lock({ id: 6, rowKey: 'R1', isActive: false, canUnlock: false, note: null, ownerDisplayName: null })]}
            rowName={(rowKey) => (rowKey === 'R1' ? 'Boiler 1' : 'Boiler 2')}
            onClose={() => {}}
          />
        </Shell>,
      );
      await screen.findByRole('dialog');

      const violations = await findViolations(document.body);
      expect(violations, describeViolations(violations)).toHaveLength(0);
    });
  }
});
