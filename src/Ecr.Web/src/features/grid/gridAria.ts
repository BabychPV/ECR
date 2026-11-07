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
        // Групові `.header-rgRow.group` у RevoGrid ПОРОЖНІ (лише висота): клітинки всіх рівнів лежать у `.group-rgRow`.
        // Порожній `row` — порушення axe `aria-required-children`, тож розпірки ховаємо від дерева доступності.
        if (row.classList.contains('group')) {
          if (row.getAttribute('aria-hidden') !== 'true') row.setAttribute('aria-hidden', 'true');
          continue;
        }
        if (row.getAttribute('role') !== 'row') row.setAttribute('role', 'row');
        if (row.getAttribute('aria-rowindex') !== String(position)) row.setAttribute('aria-rowindex', String(position));
      }

      // Клітинки груп бібліотека малює без ролі (у колонок вона є), а `aria-colspan` на `div` без ролі недозволений.
      // Блок `.group-rgRow` — один `row` (aria-rowindex 1), група — `columnheader` зі своїм `aria-rowindex` (рівень).
      // Порожні клітинки над колонкою без групи (їх перекриває сама колонка) ховаємо від дерева доступності.
      const block = header.querySelector('.group-rgRow');
      // PB2-P2-1: без груп колонок блок порожній (чи лише порожні клітинки) — `row` без жодного видимого
      // `columnheader` знову `aria-required-children`. Тоді блок не `row`, а прихований від дерева доступності.
      const hasGroups =
        block !== null &&
        [...block.querySelectorAll('.rgHeaderCell')].some((cell) => (cell.textContent ?? '').trim().length > 0);
      if (block !== null && !hasGroups) {
        if (block.hasAttribute('role')) block.removeAttribute('role');
        if (block.hasAttribute('aria-rowindex')) block.removeAttribute('aria-rowindex');
        if (block.getAttribute('aria-hidden') !== 'true') block.setAttribute('aria-hidden', 'true');
      }
      if (block !== null && hasGroups) {
        if (block.hasAttribute('aria-hidden')) block.removeAttribute('aria-hidden');
        if (block.getAttribute('role') !== 'row') block.setAttribute('role', 'row');
        if (block.getAttribute('aria-rowindex') !== '1') block.setAttribute('aria-rowindex', '1');
        for (const cell of block.querySelectorAll('.rgHeaderCell')) {
          if (cell.getAttribute('role') !== 'columnheader') cell.setAttribute('role', 'columnheader');
          const empty = (cell.textContent ?? '').trim().length === 0;
          if (empty && cell.getAttribute('aria-hidden') !== 'true') cell.setAttribute('aria-hidden', 'true');
          if (!empty && cell.hasAttribute('aria-hidden')) cell.removeAttribute('aria-hidden');
        }
      }
    }
    // CL-17 (B-2): прокручувана область даних (`rgCol`; закріплені `colPinStart`/`colPinEnd` самі не прокручуються)
    // мусить приймати фокус, інакше клавіатурний користувач не прокрутить її стрілками (WCAG 2.1.1, axe
    // `scrollable-region-focusable`). Одна зупинка Tab; стрілки на самій області гортають нативно, а на комірці їх,
    // як і раніше, обробляє сітка.
    //
    // AU-13 N2: вертикально прокручується не сам в'юпорт, а його `.vertical-inner` (рядки даних) — і в `rgCol`, і в
    // закріплених `colPinStart`/`colPinEnd`. Тож зупинка Tab — саме `.vertical-inner` кожного в'юпорта з колонками;
    // зовнішній `rgCol` (горизонтальна прокрутка) проходить axe через фокусований вміст і зупинкою більше не є.
    // P2-1: фокусований елемент без ролі axe рахує недозволеною дитиною `role=grid` (`aria-required-children`,
    // critical), тож `.vertical-inner` — `rowgroup` (усередині ті самі `row` рядків даних); шапка — `row` у `grid`.
    for (const viewport of this.revogrid.querySelectorAll('revogr-viewport-scroll')) {
      if (viewport.getAttribute('tabindex') !== null) viewport.removeAttribute('tabindex');
      if (viewport.getAttribute('role') !== null) viewport.removeAttribute('role');
      const rows = viewport.querySelector('.vertical-inner');
      if (rows === null) continue;
      const hasColumns = viewport.querySelector('[role="columnheader"], .rgHeaderCell') !== null;
      if (!hasColumns) {
        if (rows.hasAttribute('tabindex')) rows.removeAttribute('tabindex');
        if (rows.hasAttribute('role')) rows.removeAttribute('role');
        continue;
      }
      if (rows.getAttribute('tabindex') !== '0') rows.setAttribute('tabindex', '0');
      if (rows.getAttribute('role') !== 'rowgroup') rows.setAttribute('role', 'rowgroup');
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
