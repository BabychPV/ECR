import { afterEach, describe, expect, it } from 'vitest';
import { cleanup, render, screen, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import type { DocumentTableDto } from '@/api/types';
import { TableNavigator } from '../TableNavigator';
import { buildTableTree } from '../tableTreeModel';

/**
 * Позначка «рядки без правила методології» в дереві таблиць (AN-69): число на рядку таблиці, у назві рядка дерева
 * - текст з лічильником; таблиця без проблем - без позначки.
 *
 * Мутаційний доказ (виконано, відновлено): умова `noRule > 0` -> `true` малює «0» і валить «нуль - без позначки»;
 * прибрати `noRuleText` з `aria-label` валить «доступна назва»; шукати лічильник не за `tableCode` валить «число».
 */
function table(ordinal: number, name: string): DocumentTableDto {
  return {
    allowsDynamicRows: false,
    copyOrdinal: 1,
    isCopyable: false,
    maxDynamicRows: null,
    sheetCode: 'S1',
    sheetDefId: 1,
    sheetNameL10n: { values: { en: 'Sheet' } },
    sheetOrdinal: 1,
    tableCode: `T${String(ordinal)}`,
    tableDefId: ordinal,
    tableInstanceId: 100 + ordinal,
    tableNameL10n: { values: { en: name } },
    tableOrdinal: ordinal,
  };
}

const items = buildTableTree([table(1, 'Mass emissions'), table(2, 'Fuel use'), table(3, 'Hours')], undefined);

afterEach(cleanup);

function renderTree(counts: ReadonlyMap<string, number>) {
  render(
    <MantineProvider>
      <TableNavigator id="nav" items={items} selectedTableInstanceId={101} onSelect={() => undefined} issueCounts={counts} />
    </MantineProvider>,
  );
}

const rows = (): HTMLElement[] => within(screen.getByRole('tree')).getAllByRole('treeitem');

describe('TableNavigator - рядки без правила', () => {
  it('число на рядку таблиці з проблемами', () => {
    renderTree(new Map([['T2', 3]]));

    const badges = screen.getAllByTestId('table-no-rule-badge');
    expect(badges).toHaveLength(1);
    expect(badges[0]!.textContent).toBe('3');
    expect(within(rows()[1]!).getByTestId('table-no-rule-badge')).toBe(badges[0]);
  });

  it('доступна назва рядка дерева несе лічильник', () => {
    renderTree(new Map([['T2', 3]]));

    expect(rows()[1]!.getAttribute('aria-label')).toContain('⟦calculationIssues.badgeLabel (count=3)⟧');
    expect(rows()[0]!.getAttribute('aria-label')).not.toContain('calculationIssues');
  });

  it('нуль або порожня мапа - без позначки', () => {
    renderTree(new Map([['T1', 0]]));

    expect(screen.queryByTestId('table-no-rule-badge')).toBeNull();
  });
});
