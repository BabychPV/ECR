import type { ColumnData, ColumnGrouping, ColumnRegular } from '@revolist/revogrid';

/**
 * Групові заголовки колонок (до 3 рівнів) над рядком колонок.
 *
 * Контракт: `TableSliceDto.headerGroups` — плоский перелік груп (`id`, `parentId`, `label`),
 * `ColumnDto.headerGroupId` — найглибша група колонки. Сервер гарантує суміжність: одна група —
 * один безперервний проміжок колонок, тож дерево будується одним проходом.
 *
 * RevoGrid (4.11) не має rowspan: колонки без групи (або з меншою глибиною) отримують ПОРОЖНІ
 * клітинки групових рядків над собою. Їх перекриває сама клітинка колонки: клас `ecr-hg-up-N`
 * піднімає її на N рядків угору (`headerGroups.css`). Це перевірено спайком; `columnProperties`
 * повертає ЛИШЕ `class` — повернутий `style` ламає гідрацію.
 */

export interface HeaderGroupInput {
  readonly id: number;
  readonly parentId?: number | null;
  readonly label: string;
}

export interface HeaderGroupColumnInput {
  readonly code: string;
  readonly headerGroupId?: number | null;
}

/** Межа глибини з контракту (`maxHeaderGroupDepth`). */
export const MaxHeaderGroupDepth = 3;

/** Клас вузла групи (клітинка групового рядка) і мітка всередині неї. */
export const HeaderGroupCellClass = 'ecr-hg-grp';
export const HeaderGroupLabelClass = 'ecr-hg-label';

type Props = Record<string, unknown>;
type PropsFn = (...args: never[]) => unknown;

/** Підпис шляху групи для читалки: «Група / Підгрупа». */
export const PathSeparator = ' / ';

function pathOf(
  groupId: number | null | undefined,
  byId: ReadonlyMap<number, HeaderGroupInput>,
): readonly HeaderGroupInput[] {
  const path: HeaderGroupInput[] = [];
  const seen = new Set<number>();
  let current = groupId ?? null;

  while (current !== null && !seen.has(current) && path.length < MaxHeaderGroupDepth) {
    const group = byId.get(current);

    if (group === undefined) break;
    seen.add(current);
    path.unshift(group);
    current = group.parentId ?? null;
  }

  return path;
}

function mergeClass(base: unknown, extra: string): string {
  if (typeof base === 'string') return `${base} ${extra}`.trim();
  if (base !== null && typeof base === 'object') {
    const names = Object.entries(base as Record<string, unknown>)
      .filter(([, on]) => Boolean(on))
      .map(([name]) => name);

    return [...names, extra].join(' ');
  }

  return extra;
}

function withSpan(column: ColumnRegular, up: number, path: readonly HeaderGroupInput[]): ColumnRegular {
  const original = column.columnProperties as PropsFn | undefined;
  const originalTemplate = column.columnTemplate;
  const pathText = path.map((group) => group.label).join(PathSeparator);

  const columnProperties = ((...args: never[]) => {
    const base = (original?.(...args) ?? {}) as Props;
    const extra = up > 0 ? `ecr-hg-col ecr-hg-span ecr-hg-up-${up}` : 'ecr-hg-col';

    return { ...base, class: mergeClass(base.class, extra) };
  }) as unknown as ColumnRegular['columnProperties'];

  const columnTemplate =
    path.length === 0
      ? originalTemplate
      : ((h: Parameters<NonNullable<ColumnRegular['columnTemplate']>>[0], props: Parameters<NonNullable<ColumnRegular['columnTemplate']>>[1]) =>
          h('span', { class: 'ecr-hg-leaf' }, [
            // Читалка чує шлях групи перед назвою колонки; очі бачать його у груповому рядку.
            h('span', { class: 'ecr-header-sr' }, `${pathText}: `),
            originalTemplate === undefined ? String(column.name ?? '') : originalTemplate(h, props),
          ])) as ColumnRegular['columnTemplate'];

  return {
    ...column,
    columnProperties,
    ...(columnTemplate === undefined ? {} : { columnTemplate }),
  } as ColumnRegular;
}

interface OpenGroup {
  readonly group: HeaderGroupInput;
  readonly node: ColumnGrouping & { children: (ColumnGrouping | ColumnRegular)[] };
  readonly span: { columns: number };
}

/**
 * Перетворює плоский перелік колонок сітки на дерево `ColumnData`. Колонки, які не є колонками
 * даних (`code` відсутній у `columns`, наприклад колонка підпису рядка), лишаються без групи.
 *
 * Без груп (порожній `groups` або жодна колонка не посилається на групу) повертається САМЕ
 * вхідний масив — сітка з одним рядком шапки не змінюється.
 */
export function withHeaderGroups(
  gridColumns: readonly ColumnRegular[],
  groups: readonly HeaderGroupInput[] | null | undefined,
  columns: readonly HeaderGroupColumnInput[],
): ColumnData {
  if (groups === null || groups === undefined || groups.length === 0) return gridColumns as ColumnRegular[];

  const byId = new Map(groups.map((group) => [group.id, group] as const));
  const groupIdByCode = new Map(columns.map((column) => [column.code, column.headerGroupId ?? null] as const));
  const paths = gridColumns.map((column) => pathOf(groupIdByCode.get(String(column.prop)), byId));
  const depth = paths.reduce((max, path) => Math.max(max, path.length), 0);

  if (depth === 0) return gridColumns as ColumnRegular[];

  const result: (ColumnGrouping | ColumnRegular)[] = [];
  const stack: OpenGroup[] = [];
  const target = (): (ColumnGrouping | ColumnRegular)[] => stack[stack.length - 1]?.node.children ?? result;

  gridColumns.forEach((column, index) => {
    const path = paths[index] ?? [];
    let common = 0;

    while (common < stack.length && common < path.length && stack[common]?.group.id === path[common]?.id) common += 1;
    stack.length = common;

    for (const group of path.slice(common)) {
      const span = { columns: 0 };
      const node = {
        name: group.label,
        children: [] as (ColumnGrouping | ColumnRegular)[],
        columnTemplate: (h: (tag: string, props: Props, children: string) => unknown) =>
          h('span', { class: HeaderGroupLabelClass }, group.label),
        // `aria-colspan` читається на момент малювання, коли всі діти вже додані.
        columnProperties: () => ({ class: HeaderGroupCellClass, 'aria-colspan': span.columns }),
      } as unknown as OpenGroup['node'];

      target().push(node);
      stack.push({ group, node, span });
    }

    for (const open of stack) open.span.columns += 1;

    target().push(withSpan(column, depth - path.length, path));
  });

  return result;
}

/** Кількість рядків шапки: рівні груп плюс рядок колонок. */
export function headerRowCountOf(groupLevels: number): number {
  return 1 + Math.max(0, Math.min(MaxHeaderGroupDepth, groupLevels));
}

/** Відступ мітки від країв видимої частини клітинки, px. */
const LabelGap = 6;
/** Мінімальна ширина видимої частини, з якої підпис ще показується, px. */
const MinVisibleLabelWidth = 28;

/**
 * «Липка» мітка групи: центрує підпис над ВИДИМОЮ частиною клітинки групи, а не над усією (група
 * ширша за вікно прокрутки не втрачає підпис, коли її перша колонка пішла за край). CSS `sticky`
 * тут не працює — `.header-content` має `overflow: hidden`. Мітка не залишає клітинки.
 *
 * @returns функція зняття слухачів.
 */
export function attachStickyGroupLabels(grid: HTMLElement): () => void {
  let frame = 0;
  let scroller: Element | null = null;

  const update = (): void => {
    frame = 0;
    const viewport = (scroller ?? grid.querySelector('revogr-viewport-scroll.rgCol'))?.getBoundingClientRect();

    if (viewport === undefined) return;

    for (const label of grid.querySelectorAll<HTMLElement>(`.${HeaderGroupLabelClass}`)) {
      const cell = label.closest('.rgHeaderCell');

      if (cell === null) continue;

      const box = cell.getBoundingClientRect();
      const visibleLeft = Math.max(box.left, viewport.left);
      const visibleRight = Math.min(box.right, viewport.right);
      // Мітка природно стоїть по центру клітинки (`margin: 0 auto`), тож відлік — від центру клітинки.
      // `scrollWidth` — повна ширина тексту незалежно від поточного `max-width`.
      const full = label.scrollWidth;
      const available = Math.max(0, Math.min(visibleRight - visibleLeft, box.width) - 2 * LabelGap);
      // Видима частина вужча за мінімум — підпис ховається (не показуємо обрізок «eb»); інакше ужимається з «…».
      const hidden = available < MinVisibleLabelWidth;
      const width = Math.min(full, available);
      const maxWidth = hidden || width >= full ? '' : `${Math.floor(width)}px`;
      const wanted = (visibleLeft + visibleRight) / 2 - (box.left + box.right) / 2;
      // Мітка не виходить за клітинку групи.
      const room = Math.max(0, (box.width - Math.min(full, width)) / 2);
      const shift = Math.round(Math.max(-room, Math.min(room, wanted)));
      const value = shift === 0 ? '' : `translateX(${shift}px)`;

      if (label.style.transform !== value) label.style.transform = value;
      if (label.style.maxWidth !== maxWidth) label.style.maxWidth = maxWidth;
      const visibility = hidden ? 'hidden' : '';
      if (label.style.visibility !== visibility) label.style.visibility = visibility;
    }
  };

  const schedule = (): void => {
    if (frame === 0) frame = requestAnimationFrame(update);
  };

  const bind = (): void => {
    const next = grid.querySelector('revogr-viewport-scroll.rgCol');

    if (next === scroller) return;
    scroller?.removeEventListener('scroll', schedule);
    scroller = next;
    scroller?.addEventListener('scroll', schedule, { passive: true });
  };

  // RevoGrid зсуває шапку ПІСЛЯ події scroll (трансформація стилем), тож слухаємо ще й `style`;
  // власні правки міток (`transform`, `max-width`, `visibility`) ігноруємо, щоб не зациклитись.
  const observer = new MutationObserver((records) => {
    const foreign = records.some(
      (record) => record.type === 'childList' || !(record.target instanceof HTMLElement && record.target.classList.contains(HeaderGroupLabelClass)),
    );

    if (!foreign) return;
    bind();
    schedule();
  });

  observer.observe(grid, { childList: true, subtree: true, attributes: true, attributeFilter: ['style'] });
  window.addEventListener('resize', schedule);
  bind();
  schedule();

  return () => {
    observer.disconnect();
    window.removeEventListener('resize', schedule);
    scroller?.removeEventListener('scroll', schedule);
    if (frame !== 0) cancelAnimationFrame(frame);
  };
}
