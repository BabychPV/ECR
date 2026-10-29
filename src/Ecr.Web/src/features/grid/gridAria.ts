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
 *    Прокручувана область `rgCol` із `tabindex` (CL-17) без ролі була недозволеною
 *    дитиною `grid` — їй ставиться `role=rowgroup` (P2-1).
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
  private headerRows = 1;

  constructor(revogrid: HTMLRevoGridElement, providers: PluginProviders) {
    super(revogrid, providers);
    revogrid.setAttribute('role', 'grid');

    this.addEventListener('beforesourceset', ({ detail }) => {
      const type = detail.type as keyof GridAriaPlugin['counts'];
      if (type in this.counts) this.counts[type] = detail.source.length;
      revogrid.setAttribute('aria-rowcount', String(this.headerRows + this.counts.rowPinStart + this.counts.rgRow + this.counts.rowPinEnd));
    });

    this.addEventListener('beforecolumnsset', ({ detail }) => {
      // Рядки шапки: рівні групових заголовків + рядок колонок.
      const grouping = detail.columnGrouping as Partial<typeof detail.columnGrouping> | undefined;
      const levels = [...(grouping?.colPinStart ?? []), ...(grouping?.rgCol ?? []), ...(grouping?.colPinEnd ?? [])].map(
        (group) => group.level + 1,
      );
      this.headerRows = 1 + Math.max(0, ...levels);
      revogrid.setAttribute('aria-rowcount', String(this.headerRows + this.counts.rowPinStart + this.counts.rgRow + this.counts.rowPinEnd));
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
    const first = this.headerRows + 1;
    if (type === 'rowPinStart') return first + index;
    if (type === 'rowPinEnd') return first + this.counts.rowPinStart + this.counts.rgRow + index;
    return first + this.counts.rowPinStart + index;
  }

  private patchStatic(): void {
    // Рядки шапки нумеруються зверху вниз у межах кожної секції (групові рівні, потім рядок колонок).
    for (const header of this.revogrid.querySelectorAll('revogr-header')) {
      let position = 0;
      for (const row of header.querySelectorAll('.header-rgRow')) {
        position += 1;
        if (row.getAttribute('role') !== 'row') row.setAttribute('role', 'row');
        if (row.getAttribute('aria-rowindex') !== String(position)) row.setAttribute('aria-rowindex', String(position));
      }
    }
    // CL-17 (B-2): прокручувана область даних (`rgCol`; закріплені `colPinStart`/`colPinEnd` самі не прокручуються)
    // мусить приймати фокус, інакше клавіатурний користувач не прокрутить її стрілками (WCAG 2.1.1, axe
    // `scrollable-region-focusable`). Одна зупинка Tab; стрілки на самій області гортають нативно, а на комірці їх,
    // як і раніше, обробляє сітка.
    const scroller = this.revogrid.querySelector('revogr-viewport-scroll.rgCol');
    if (scroller !== null && scroller.getAttribute('tabindex') !== '0') scroller.setAttribute('tabindex', '0');
    // P2-1: фокусований елемент без ролі axe рахує недозволеною дитиною `role=grid` (`aria-required-children`,
    // critical). `rowgroup` дозволена дитина `grid`, а всередині — ті самі `row` (шапка й рядки даних), тож
    // структура дерева доступності лишається коректною, розкладка й клавіатурна навігація сітки не змінюються.
    if (scroller !== null && scroller.getAttribute('role') !== 'rowgroup') scroller.setAttribute('role', 'rowgroup');
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
