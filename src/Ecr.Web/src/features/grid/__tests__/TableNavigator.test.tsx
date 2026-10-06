import { describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import type { DocumentTableDto } from '@/api/types';
import { TableNavigator } from '../TableNavigator';
import { buildTableTree } from '../tableTreeModel';

/**
 * Дерево таблиць (`UI-22`): клавіатура шаблону WAI-ARIA «tree view», фільтр, стан словами.
 *
 * ⛔ Головне, що тут закрито: дерево з 91 рядка — ОДНА зупинка Tab. Інакше між шапкою
 * документа і сіткою стало б 91 натискання (`e2e/keyboardPath.spec.ts`).
 */
function table(ordinal: number, name: string): DocumentTableDto {
  return {
    allowsDynamicRows: false,
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

const tables = [table(1, 'Mass emissions'), table(2, 'Fuel use'), table(3, 'Operating hours')];
const items = buildTableTree(tables, [
  { tableDefId: 1, sheetCode: 'S1', filledCells: 5, inputCells: 5, errorCount: 0, warningCount: 0 },
  { tableDefId: 2, sheetCode: 'S1', filledCells: 2, inputCells: 5, errorCount: 3, warningCount: 0 },
  { tableDefId: 3, sheetCode: 'S1', filledCells: 0, inputCells: 5, errorCount: 0, warningCount: 2 },
]);

function renderNavigator(selected = 101, onSelect = vi.fn()) {
  render(
    <MantineProvider>
      <TableNavigator id="nav" items={items} selectedTableInstanceId={selected} onSelect={onSelect} />
    </MantineProvider>,
  );

  return onSelect;
}

const rows = (): HTMLElement[] => within(screen.getByRole('tree')).getAllByRole('treeitem');

describe('TableNavigator', () => {
  it('усі таблиці аркуша зі станом, вибрана позначена, одна зупинка Tab', () => {
    renderNavigator(102);

    expect(rows()).toHaveLength(3);
    expect(rows().map((row) => row.getAttribute('data-state'))).toEqual(['filled', 'error', 'empty']);
    expect(rows()[1]!.getAttribute('aria-selected')).toBe('true');
    expect(rows().filter((row) => row.tabIndex === 0)).toEqual([rows()[1]]);

    // Лічильник помилок і стан словами (не лише колір).
    expect(rows()[1]!.textContent).toContain('3');
    expect(rows()[1]!.getAttribute('aria-label')).toContain('grid.tree.state.error');
    expect(rows()[2]!.getAttribute('aria-label')).toContain('grid.tree.warnings');
  });

  it('стрілки й Home/End ходять рядками, Enter і пробіл вибирають, клік вибирає', () => {
    const onSelect = renderNavigator(101);

    rows()[0]!.focus();
    fireEvent.keyDown(rows()[0]!, { key: 'ArrowDown' });
    expect(document.activeElement).toBe(rows()[1]);
    expect(rows().filter((row) => row.tabIndex === 0)).toEqual([rows()[1]]);

    fireEvent.keyDown(rows()[1]!, { key: 'End' });
    expect(document.activeElement).toBe(rows()[2]);
    fireEvent.keyDown(rows()[2]!, { key: 'ArrowDown' });
    expect(document.activeElement).toBe(rows()[2]);
    fireEvent.keyDown(rows()[2]!, { key: 'Home' });
    expect(document.activeElement).toBe(rows()[0]);
    fireEvent.keyDown(rows()[0]!, { key: 'ArrowUp' });
    expect(document.activeElement).toBe(rows()[0]);

    // Хода стрілками нічого не вибирає — вибір лише Enter/пробілом/кліком.
    expect(onSelect).not.toHaveBeenCalled();

    fireEvent.keyDown(rows()[0]!, { key: 'ArrowDown' });
    fireEvent.keyDown(rows()[1]!, { key: 'Enter' });
    expect(onSelect).toHaveBeenLastCalledWith(102);

    fireEvent.keyDown(rows()[1]!, { key: ' ' });
    expect(onSelect).toHaveBeenCalledTimes(2);

    fireEvent.click(rows()[2]!);
    expect(onSelect).toHaveBeenLastCalledWith(103);
  });

  it('фільтр за назвою і «лише з помилками»; нічого не знайдено — скидання фільтра', () => {
    renderNavigator();

    fireEvent.change(screen.getByRole('searchbox', { name: '⟦grid.tree.filter⟧' }), { target: { value: 'fuel' } });
    expect(rows().map((row) => row.textContent)).toEqual([expect.stringContaining('Fuel use')]);
    // Вибрана таблиця відсічена — зупинку Tab тримає показаний рядок.
    expect(rows()[0]).toHaveProperty('tabIndex', 0);

    fireEvent.change(screen.getByRole('searchbox', { name: '⟦grid.tree.filter⟧' }), { target: { value: '' } });
    const errorsOnly = screen.getByRole('button', { name: '⟦grid.tree.errorsOnly⟧' });
    fireEvent.click(errorsOnly);
    expect(errorsOnly.getAttribute('aria-pressed')).toBe('true');
    expect(rows()).toHaveLength(1);

    fireEvent.change(screen.getByRole('searchbox', { name: '⟦grid.tree.filter⟧' }), { target: { value: 'hours' } });
    expect(screen.queryByRole('tree')).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: '⟦grid.tree.clearFilter⟧' }));
    expect(rows()).toHaveLength(3);
    expect(errorsOnly.getAttribute('aria-pressed')).toBe('false');
  });

  it('статусу ще немає — ні смуги заповненості, ні дробу (не «0 із N»)', () => {
    render(
      <MantineProvider>
        <TableNavigator id="nav" items={buildTableTree(tables, undefined)} selectedTableInstanceId={101} onSelect={vi.fn()} />
      </MantineProvider>,
    );

    expect(screen.queryByTestId('table-navigator-progress')).toBeNull();
    expect(rows().every((row) => row.getAttribute('data-state') === 'unknown')).toBe(true);
  });

  it('роль зі scope на один аркуш: чужий аркуш не рахується, лічильники null — «—», не 0', () => {
    // Відповіді API після фіксу «прихований аркуш»: таблиці — лише аркуш S1; лічильники для
    // вузької ролі — null. Статус аркуша HIDDEN, навіть якби прийшов, у дерево не потрапляє.
    const scoped = buildTableTree(tables, [
      { tableDefId: 1, sheetCode: 'S1', filledCells: 5, inputCells: 5, errorCount: null, warningCount: null },
      { tableDefId: 2, sheetCode: 'S1', filledCells: 1, inputCells: 5, errorCount: null, warningCount: null },
      { tableDefId: 3, sheetCode: 'HIDDEN', filledCells: 9, inputCells: 9, errorCount: 7, warningCount: 4 },
    ]);

    render(
      <MantineProvider>
        <TableNavigator id="nav" items={scoped} selectedTableInstanceId={101} onSelect={vi.fn()} />
      </MantineProvider>,
    );

    expect(rows()).toHaveLength(3);
    // Таблиця 3 існує на S1, але її статус прийшов лише з чужого аркуша — стан невідомий.
    expect(rows()[2]!.getAttribute('data-state')).toBe('unknown');
    expect(rows().some((row) => row.textContent?.includes('7') === true)).toBe(false);

    const progress = screen.getByTestId('table-navigator-progress').textContent ?? '';
    expect(progress).not.toContain('grid.tree.issues');
    // Невідома кількість помилок — «—» у тексті стану, а не «0».
    expect(rows()[0]!.getAttribute('aria-label')).toContain('—');
    expect(rows()[0]!.getAttribute('aria-label')).not.toMatch(/\b0\b/);
  });

  it('рядок заповненості аркуша і число зауважень', () => {
    renderNavigator();

    expect(screen.getByTestId('table-navigator-progress').textContent).toContain('grid.tree.tablesFilled');
    expect(screen.getByTestId('table-navigator-progress').textContent).toContain('grid.tree.issues');
  });
});
