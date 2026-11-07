import { describe, expect, it, vi } from 'vitest';
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

  // PB2-P2-1: без груп колонок `.group-rgRow` порожній — `row` без columnheader дає axe aria-required-children.
  it.each([
    ['без груп: блок не row і прихований', '<div class="rgHeaderCell"></div>', null, 'true'],
    ['з групами: блок — row, група — columnheader', '<div class="rgHeaderCell">Квартал</div>', 'row', null],
  ])('%s', (_name, cells, role, hidden) => {
    const element = grid();
    element.querySelector('revogr-header')?.insertAdjacentHTML('beforeend', `<div class="group-rgRow">${cells}</div>`);
    const plugin = new GridAriaPlugin(element, {} as PluginProviders);

    const block = element.querySelector('.group-rgRow');
    expect(block?.getAttribute('role')).toBe(role);
    expect(block?.getAttribute('aria-hidden')).toBe(hidden);
    if (role === 'row') {
      expect(block?.getAttribute('aria-rowindex')).toBe('1');
      expect(block?.querySelector('.rgHeaderCell')?.getAttribute('role')).toBe('columnheader');
    }

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

  it('закріплені колонки: aria-colindex лишається ГЛОБАЛЬНИМ порядком (закріплені, прокручувані, закріплені справа)', () => {
    const element = grid();
    const plugin = new GridAriaPlugin(element, {} as PluginProviders);
    const column = (prop: string): ColumnRegular => ({ prop, columnProperties: () => ({}), cellProperties: () => ({}) });
    const pinStart = [column('label'), column('type')];
    const main = [column('q1'), column('q2'), column('q3')];
    const pinEnd = [column('total')];

    emit(element, 'beforecolumnsset', { columns: { colPinStart: pinStart, rgCol: main, colPinEnd: pinEnd } });

    const indexOf = (item: ColumnRegular): unknown => (item.columnProperties as () => Record<string, unknown>)()['aria-colindex'];
    const cellIndexOf = (item: ColumnRegular): unknown =>
      (item.cellProperties as (...args: unknown[]) => Record<string, unknown>)({ type: 'rgRow', rowIndex: 0 })['aria-colindex'];

    // Порядок для читалки: label=1, type=2, q1=3 ... q3=5, total=6 - так само, як без закріплення.
    expect([...pinStart, ...main, ...pinEnd].map(indexOf)).toEqual([1, 2, 3, 4, 5, 6]);
    expect([...pinStart, ...main, ...pinEnd].map(cellIndexOf)).toEqual([1, 2, 3, 4, 5, 6]);

    plugin.destroy();
    element.remove();
  });

  // AU-13 N2: прокручуються рядки `.vertical-inner` кожного в'юпорта з колонками (і закріпленого теж).
  const viewport = (cls: string, columns: boolean): string =>
    `<revogr-viewport-scroll class="${cls}"><div class="inner-content-table">` +
    `<div class="header-wrapper">${columns ? '<div class="rgHeaderCell">A</div>' : ''}</div>` +
    '<div class="vertical-inner"><div class="content-wrapper"></div></div></div></revogr-viewport-scroll>';
  const rowsOf = (element: Element, cls: string): Element | null =>
    element.querySelector(`revogr-viewport-scroll.${cls} .vertical-inner`);

  it('CL-17 / AU-13 N2: фокусовані рядки (.vertical-inner) в\'юпортів із колонками, порожній закріплений — ні', () => {
    const element = grid();
    element.insertAdjacentHTML(
      'beforeend',
      viewport('colPinStart', true) + viewport('rgCol', true) + viewport('colPinEnd', false),
    );
    const plugin = new GridAriaPlugin(element, {} as PluginProviders);

    for (const cls of ['colPinStart', 'rgCol']) {
      expect(rowsOf(element, cls)?.getAttribute('tabindex')).toBe('0');
      expect(rowsOf(element, cls)?.getAttribute('role')).toBe('rowgroup');
    }
    expect(rowsOf(element, 'colPinEnd')?.hasAttribute('tabindex')).toBe(false);
    for (const cls of ['colPinStart', 'rgCol', 'colPinEnd']) {
      expect(element.querySelector(`revogr-viewport-scroll.${cls}`)?.hasAttribute('tabindex')).toBe(false);
    }

    plugin.destroy();
    element.remove();
  });

  it('CL-17: область, що з’явилась пізніше (Stencil домальовує), теж отримує tabindex', async () => {
    const element = grid();
    const plugin = new GridAriaPlugin(element, {} as PluginProviders);
    element.insertAdjacentHTML('beforeend', viewport('rgCol', true));

    await vi.waitFor(() => {
      expect(rowsOf(element, 'rgCol')?.getAttribute('tabindex')).toBe('0');
    });

    plugin.destroy();
    element.remove();
  });
});
