import { BasePlugin } from '@revolist/revogrid';
import type { CellProps, ColumnRegular, PluginProviders } from '@revolist/revogrid';

/**
 * Поправки до ARIA-розмітки RevoGrid (вбудований `WCAGPlugin` 4.11), без
 * заміни самого плагіна: він же фокусує комірку після `afterfocus`, і на цьому
 * тримається клавіатурний шлях сітки.
 *
 * Що не так у бібліотеки (axe на сторінці документа, 3 критичні правила):
 * 1. `aria-colindex` / `aria-rowindex` від нуля; ARIA вимагає від одиниці
 *    (`aria-valid-attr-value`).
 * 2. Комірки шапки `role=columnheader` лежать у `div.header-rgRow` без ролі —
 *    у них немає батьківського `row` (`aria-required-parent`).
 * 3. `role=treegrid`, хоча ієрархії рядків немає, а посилання атрибуції
 *    всередині сітки — зайва зупинка Tab і заборонений нащадок
 *    (`aria-required-children`). Атрибуцію не ховаємо (умова ліцензії) —
 *    лише виводимо з дерева доступності й порядку Tab; мишею вона клікабельна.
 * 4. `aria-rowcount` перезаписується кожним джерелом (основне, закріплений
 *    підсумок) — лишалась довжина останнього.
 *
 * Нумерація рядків: шапка — 1, рядки даних — 2…N+1, рядки підсумку — після них.
 *
 * ⚠ Плагіни користувача реєструються ПІСЛЯ системних (`setCorePlugins` →
 * `pluginsChanged`), тож наші обробники тих самих подій ідуть другими й
 * перекривають значення `WCAGPlugin`.
 */
export class GridAriaPlugin extends BasePlugin {
  private readonly counts = { rgRow: 0, rowPinStart: 0, rowPinEnd: 0 };
  private readonly observer: MutationObserver;
  private frame = 0;

  constructor(revogrid: HTMLRevoGridElement, providers: PluginProviders) {
    super(revogrid, providers);
    revogrid.setAttribute('role', 'grid');

    this.addEventListener('beforesourceset', ({ detail }) => {
      const type = detail.type as keyof GridAriaPlugin['counts'];
      if (type in this.counts) this.counts[type] = detail.source.length;
      revogrid.setAttribute('aria-rowcount', String(1 + this.counts.rowPinStart + this.counts.rgRow + this.counts.rowPinEnd));
    });

    this.addEventListener('beforecolumnsset', ({ detail }) => {
      const columns: ColumnRegular[] = [...detail.columns.colPinStart, ...detail.columns.rgCol, ...detail.columns.colPinEnd];
      columns.forEach((column, index) => {
        const header = column.columnProperties;
        const cell = column.cellProperties;
        column.columnProperties = (...args): CellProps => {
          const props: CellProps = { ...(header?.(...args) as CellProps | undefined) };
          props['aria-colindex'] = index + 1;
          return props;
        };
        column.cellProperties = (...args): CellProps => {
          const props: CellProps = { ...(cell?.(...args) as CellProps | undefined) };
          props['aria-colindex'] = index + 1;
          props['aria-rowindex'] = this.rowIndexOf(args[0].type, args[0].rowIndex);
          return props;
        };
      });
    });

    this.addEventListener('beforerowrender', ({ detail }) => {
      detail.node.$attrs$ = {
        ...detail.node.$attrs$,
        'aria-rowindex': this.rowIndexOf(detail.rowType, detail.item.itemIndex),
      };
    });

    // Шапку й атрибуцію Stencil малює сам, подій для них немає — дописуємо
    // атрибути після кожної зміни DOM (не частіше за кадр).
    this.observer = new MutationObserver(() => {
      if (this.frame !== 0) return;
      this.frame = requestAnimationFrame(() => {
        this.frame = 0;
        this.patchStatic();
      });
    });
    this.observer.observe(revogrid, { childList: true, subtree: true });
    this.patchStatic();
  }

  private rowIndexOf(type: string, index: number): number {
    if (type === 'rowPinStart') return 2 + index;
    if (type === 'rowPinEnd') return 2 + this.counts.rowPinStart + this.counts.rgRow + index;
    return 2 + this.counts.rowPinStart + index;
  }

  private patchStatic(): void {
    for (const row of this.revogrid.querySelectorAll('.header-rgRow')) {
      if (row.getAttribute('role') !== 'row') row.setAttribute('role', 'row');
      if (row.getAttribute('aria-rowindex') !== '1') row.setAttribute('aria-rowindex', '1');
    }
    const attribution = this.revogrid.querySelector('revogr-attribution');
    if (attribution === null) return;
    if (attribution.getAttribute('aria-hidden') !== 'true') attribution.setAttribute('aria-hidden', 'true');
    for (const link of attribution.querySelectorAll('a')) {
      if (link.getAttribute('tabindex') !== '-1') link.setAttribute('tabindex', '-1');
    }
  }

  override destroy(): void {
    this.observer.disconnect();
    if (this.frame !== 0) cancelAnimationFrame(this.frame);
    super.destroy();
  }
}
