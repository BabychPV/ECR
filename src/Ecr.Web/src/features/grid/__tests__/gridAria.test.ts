import { describe, expect, it } from 'vitest';
import type { ColumnRegular, PluginProviders } from '@revolist/revogrid';
import { GridAriaPlugin } from '../gridAria';

/**
 * Поправки ARIA поверх `WCAGPlugin` RevoGrid: індекси від одиниці, шапка — рядок 1,
 * підсумок — після даних, `role=grid`, атрибуція поза деревом доступності й Tab.
 */
function grid(): HTMLRevoGridElement {
  const element = document.createElement('div');
  element.innerHTML =
    '<revogr-attribution><a href="#">RevoGrid</a></revogr-attribution>' +
    '<revogr-header><div class="header-rgRow"><div role="columnheader"></div></div></revogr-header>';
  element.setAttribute('role', 'treegrid');
  document.body.append(element);
  return element as unknown as HTMLRevoGridElement;
}

function emit(target: HTMLElement, name: string, detail: unknown): void {
  target.dispatchEvent(new CustomEvent(name, { detail }));
}

describe('GridAriaPlugin', () => {
  it('ставить role=grid, рядок шапки й ховає атрибуцію з порядку Tab', () => {
    const element = grid();
    const plugin = new GridAriaPlugin(element, {} as PluginProviders);

    expect(element.getAttribute('role')).toBe('grid');
    const header = element.querySelector('.header-rgRow');
    expect(header?.getAttribute('role')).toBe('row');
    expect(header?.getAttribute('aria-rowindex')).toBe('1');
    expect(element.querySelector('revogr-attribution')?.getAttribute('aria-hidden')).toBe('true');
    expect(element.querySelector('a')?.getAttribute('tabindex')).toBe('-1');

    plugin.destroy();
    element.remove();
  });

  it('нумерує колонки й рядки від одиниці, підсумок — після даних', () => {
    const element = grid();
    const plugin = new GridAriaPlugin(element, {} as PluginProviders);

    emit(element, 'beforesourceset', { type: 'rgRow', source: [{}, {}, {}] });
    emit(element, 'beforesourceset', { type: 'rowPinEnd', source: [{}] });
    expect(element.getAttribute('aria-rowcount')).toBe('5');

    // Так їх лишає `WCAGPlugin`: від нуля.
    const zeroBased = (index: number) => () => ({ role: 'gridcell', 'aria-colindex': index, class: 'keep' });
    const columns: ColumnRegular[] = [
      { prop: 'a', columnProperties: zeroBased(0), cellProperties: zeroBased(0) },
      { prop: 'b', columnProperties: zeroBased(1), cellProperties: zeroBased(1) },
    ];
    emit(element, 'beforecolumnsset', { columns: { colPinStart: [], rgCol: columns, colPinEnd: [] } });

    const header = columns[1]?.columnProperties as (...args: unknown[]) => Record<string, unknown>;
    expect(header()['aria-colindex']).toBe(2);

    const cell = columns[0]?.cellProperties as (...args: unknown[]) => Record<string, unknown>;
    const first = cell({ type: 'rgRow', rowIndex: 0 });
    expect(first).toMatchObject({ 'aria-colindex': 1, 'aria-rowindex': 2, role: 'gridcell', class: 'keep' });
    expect(cell({ type: 'rowPinEnd', rowIndex: 0 })['aria-rowindex']).toBe(5);

    const node = { $attrs$: { role: 'row', 'aria-rowindex': 0 } };
    emit(element, 'beforerowrender', { node, rowType: 'rgRow', item: { itemIndex: 2 } });
    expect(node.$attrs$).toEqual({ role: 'row', 'aria-rowindex': 4 });

    plugin.destroy();
    element.remove();
  });
});
