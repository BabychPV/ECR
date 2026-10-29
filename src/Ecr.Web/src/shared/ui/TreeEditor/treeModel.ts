/**
 * Модель загального редактора дерева (NAE plan N1/N2, макет «NAE structure»).
 *
 * ⚠ Чисті функції без React і без мережі: компонент (`TreeEditor`) лише малює те, що тут пораховано, а правила
 * (видимі рядки, пошук, куди можна перенести, які рівні дозволені дитині) перевіряються без DOM.
 *
 * ⛔ Тип вузла НЕ залежить від серверного DTO: з'єднання з контрактом - тонкий адаптер
 * (`features/registries/tree`), тому редактор можна використати для будь-якого ієрархічного довідника.
 */

export type TreeNodeId = number | string;

/** Вузол дерева в плаский список; дерево збирається за `parentId`. */
export interface TreeNode {
  readonly id: TreeNodeId;
  /** `null` - корінь. Батько, якого немає у списку (відфільтрований), вважається відсутнім: вузол стає коренем. */
  readonly parentId: TreeNodeId | null;
  /** Назва мовою читача. */
  readonly label: string;
  /** Код вузла (моноширинно поруч із назвою). */
  readonly code?: string | undefined;
  /** Код рівня; `null`/відсутній - рівня ще немає (тоді правила рівнів не перевіряються). */
  readonly levelCode?: string | null | undefined;
  /** Позиція серед сусідів. */
  readonly ordinal: number;
  /** Вузол виведено з ужитку (м'яко): показується приглушеним і з текстовою позначкою. */
  readonly retired?: boolean | undefined;
  /** Скільки прямих живих дітей у джерелі (може бути більше, ніж у списку, коли йде пошук). */
  readonly childCount?: number | undefined;
  /** Сам вузол збігається з пошуком (предки збігів лишаються без позначки). */
  readonly isMatch?: boolean | undefined;
}

/** Рівень ієрархії. */
export interface TreeLevel {
  readonly code: string;
  /** 1 - найвищий. */
  readonly ordinal: number;
  readonly name: string;
  /** Вузол може пропустити цей рівень. */
  readonly isOptional: boolean;
}

/** Рядок видимого (розгорнутого) дерева. */
export interface TreeRow {
  readonly node: TreeNode;
  /** 1-based глибина (aria-level). */
  readonly level: number;
  /** 1-based позиція серед сусідів (aria-posinset). */
  readonly posInSet: number;
  readonly setSize: number;
  readonly hasChildren: boolean;
  readonly expanded: boolean;
}

export interface TreeIndex {
  readonly byId: ReadonlyMap<TreeNodeId, TreeNode>;
  /** Діти за батьком (`null` - корені), у порядку `ordinal`, далі за порядком входу. */
  readonly children: ReadonlyMap<TreeNodeId | null, readonly TreeNode[]>;
}

/** Будує індекс; O(n log n) через сортування сусідів. */
export function buildTreeIndex(nodes: readonly TreeNode[]): TreeIndex {
  const byId = new Map<TreeNodeId, TreeNode>();
  for (const node of nodes) byId.set(node.id, node);

  const children = new Map<TreeNodeId | null, TreeNode[]>();
  for (const node of nodes) {
    // ⚠ Батько поза списком (або вузол сам собі батько) - корінь: пошук повертає предків, але перестраховка дешева.
    const parent = node.parentId !== null && node.parentId !== node.id && byId.has(node.parentId) ? node.parentId : null;
    const siblings = children.get(parent);
    if (siblings === undefined) children.set(parent, [node]);
    else siblings.push(node);
  }

  // Array.prototype.sort стабільний: однакові `ordinal` лишаються в порядку джерела (сервер вже дає (parent, ordinal, code)).
  for (const siblings of children.values()) siblings.sort((a, b) => a.ordinal - b.ordinal);

  return { byId, children };
}

/** Корені дерева. */
export function rootsOf(index: TreeIndex): readonly TreeNode[] {
  return index.children.get(null) ?? [];
}

/** Рядки видимого дерева в порядку обходу вглиб. `expandAll` - пошук: усе розгорнуто. */
export function visibleRows(index: TreeIndex, expanded: ReadonlySet<TreeNodeId>, expandAll: boolean): TreeRow[] {
  const rows: TreeRow[] = [];
  // Ітеративний обхід: дерево може бути глибоким, а рекурсія - не потрібна.
  const stack: { siblings: readonly TreeNode[]; at: number; level: number }[] = [
    { siblings: rootsOf(index), at: 0, level: 1 },
  ];

  while (stack.length > 0) {
    const frame = stack[stack.length - 1];
    if (frame === undefined) break;
    const node = frame.siblings[frame.at];
    if (node === undefined) {
      stack.pop();
      continue;
    }
    frame.at += 1;

    const kids = index.children.get(node.id) ?? [];
    const hasChildren = kids.length > 0;
    const open = hasChildren && (expandAll || expanded.has(node.id));
    rows.push({
      node,
      level: frame.level,
      posInSet: frame.at,
      setSize: frame.siblings.length,
      hasChildren,
      expanded: open,
    });
    if (open) stack.push({ siblings: kids, at: 0, level: frame.level + 1 });
  }

  return rows;
}

/** Предки вузла від кореня до батька (без самого вузла). */
export function ancestorsOf(index: TreeIndex, id: TreeNodeId): TreeNode[] {
  const chain: TreeNode[] = [];
  const seen = new Set<TreeNodeId>([id]);
  let current = index.byId.get(id);
  while (current !== undefined && current.parentId !== null && !seen.has(current.parentId)) {
    const parent = index.byId.get(current.parentId);
    if (parent === undefined) break;
    seen.add(parent.id);
    chain.unshift(parent);
    current = parent;
  }

  return chain;
}

/** Усі нащадки вузла (без нього). */
export function descendantIds(index: TreeIndex, id: TreeNodeId): Set<TreeNodeId> {
  const out = new Set<TreeNodeId>();
  const stack: TreeNodeId[] = [id];
  while (stack.length > 0) {
    const next = stack.pop();
    if (next === undefined) break;
    for (const kid of index.children.get(next) ?? []) {
      if (!out.has(kid.id)) {
        out.add(kid.id);
        stack.push(kid.id);
      }
    }
  }

  return out;
}

const PathSeparator = ' › ';

/** Шлях «Регіон › Зона › Майданчик» разом із самим вузлом. */
export function pathLabel(index: TreeIndex, id: TreeNodeId): string {
  const node = index.byId.get(id);
  if (node === undefined) return '';

  return [...ancestorsOf(index, id), node].map((item) => item.label).join(PathSeparator);
}

/** Розбиття тексту за збігом (без урахування регістру) для підсвічування. */
export function splitMatch(text: string, query: string): readonly { readonly text: string; readonly match: boolean }[] {
  const needle = query.trim().toLocaleLowerCase();
  if (needle === '' || text === '') return [{ text, match: false }];

  const haystack = text.toLocaleLowerCase();
  // ⚠ toLocaleLowerCase може змінити довжину (напр. 'İ'): тоді позиції не збігаються - підсвітку пропускаємо, текст цілий.
  if (haystack.length !== text.length) return [{ text, match: false }];

  const parts: { text: string; match: boolean }[] = [];
  let from = 0;
  for (;;) {
    const at = haystack.indexOf(needle, from);
    if (at < 0) break;
    if (at > from) parts.push({ text: text.slice(from, at), match: false });
    parts.push({ text: text.slice(at, at + needle.length), match: true });
    from = at + needle.length;
  }
  if (from < text.length) parts.push({ text: text.slice(from), match: false });

  return parts.length === 0 ? [{ text, match: false }] : parts;
}

/* ───────────────────────────── правила рівнів ───────────────────────────── */

function ordinalOf(levels: readonly TreeLevel[], code: string | null | undefined): number | null {
  if (code === null || code === undefined) return null;

  return levels.find((level) => level.code === code)?.ordinal ?? null;
}

/**
 * Чи можна вузлу рівня `nodeLevel` стояти під батьком рівня `parentLevel` (`null` - корінь).
 * Вузол стоїть лише глибше за батька; пропущені рівні мають бути необов'язковими.
 * Невідомий рівень (немає в переліку) не блокує: остаточне слово за сервером (422).
 */
export function canPlace(
  levels: readonly TreeLevel[],
  parentLevel: string | null | undefined,
  nodeLevel: string | null | undefined,
): boolean {
  const node = ordinalOf(levels, nodeLevel);
  if (node === null) return true;

  const parent = parentLevel === null || parentLevel === undefined ? 0 : ordinalOf(levels, parentLevel);
  if (parent === null) return true;
  if (parent >= node) return false;

  return levels.filter((level) => level.ordinal > parent && level.ordinal < node).every((level) => level.isOptional);
}

/** Рівні, на яких можна додати дитину до батька (`null` - корінь): найближчі, до першого обов'язкового включно. */
export function childLevelsFor(levels: readonly TreeLevel[], parentLevel: string | null | undefined): TreeLevel[] {
  const parent = parentLevel === null || parentLevel === undefined ? 0 : ordinalOf(levels, parentLevel);
  if (parent === null) return [];

  const sorted = [...levels].sort((a, b) => a.ordinal - b.ordinal).filter((level) => level.ordinal > parent);
  const out: TreeLevel[] = [];
  for (const level of sorted) {
    out.push(level);
    if (!level.isOptional) break;
  }

  return out;
}

export interface MoveTarget {
  readonly node: TreeNode;
  readonly path: string;
}

export interface MoveTargets {
  /** Чи можна зробити вузол коренем. */
  readonly rootAllowed: boolean;
  readonly items: readonly MoveTarget[];
  /** Скільки місць підійшло загалом (items обрізано до `limit`). */
  readonly total: number;
}

/**
 * Куди можна перенести вузол: не він сам, не його нащадки, не вже віднятий (виведений) батько,
 * і рівень дозволяє (`canPlace`). Пошук - за кодом і назвою вузла-батька.
 */
export function moveTargets(
  index: TreeIndex,
  levels: readonly TreeLevel[],
  node: TreeNode,
  query: string,
  limit: number,
): MoveTargets {
  const banned = descendantIds(index, node.id);
  banned.add(node.id);
  const needle = query.trim().toLocaleLowerCase();

  const matches: MoveTarget[] = [];
  for (const candidate of index.byId.values()) {
    if (banned.has(candidate.id) || candidate.retired === true) continue;
    if (!canPlace(levels, candidate.levelCode, node.levelCode)) continue;
    if (needle !== '' && !`${candidate.label} ${candidate.code ?? ''}`.toLocaleLowerCase().includes(needle)) continue;
    matches.push({ node: candidate, path: pathLabel(index, candidate.id) });
  }
  matches.sort((a, b) => a.path.localeCompare(b.path, 'en'));

  return {
    rootAllowed: canPlace(levels, null, node.levelCode),
    items: matches.slice(0, limit),
    total: matches.length,
  };
}

/* ───────────────────────────── клавіатура ───────────────────────────── */

export interface TreeKeyInput {
  readonly key: string;
  readonly altKey?: boolean | undefined;
  readonly ctrlKey?: boolean | undefined;
  readonly metaKey?: boolean | undefined;
}

export type TreeKeyAction =
  | { readonly type: 'focus'; readonly id: TreeNodeId }
  | { readonly type: 'expand'; readonly id: TreeNodeId }
  | { readonly type: 'collapse'; readonly id: TreeNodeId }
  | { readonly type: 'select'; readonly id: TreeNodeId }
  | { readonly type: 'retire'; readonly id: TreeNodeId }
  | { readonly type: 'move'; readonly id: TreeNodeId }
  | { readonly type: 'add'; readonly id: TreeNodeId }
  | {
      readonly type: 'reorder';
      readonly id: TreeNodeId;
      readonly parentId: TreeNodeId | null;
      readonly beforeId: TreeNodeId | null;
      readonly position: number;
      readonly total: number;
    }
  | { readonly type: 'none' };

const None: TreeKeyAction = { type: 'none' };

/** Чи це символ для пошуку набором (typeahead). */
export function isTypeaheadKey(input: TreeKeyInput): boolean {
  return input.key.length === 1 && input.key !== ' ' && input.altKey !== true && input.ctrlKey !== true && input.metaKey !== true;
}

/**
 * Перший видимий рядок після `from`, назва якого починається з набраного (циклічно).
 * Один і той самий символ, натиснутий поспіль, перебирає рядки з цією літерою.
 */
export function typeaheadTarget(rows: readonly TreeRow[], from: number, buffer: string): TreeRow | undefined {
  const needle = buffer.toLocaleLowerCase();
  if (needle === '' || rows.length === 0) return undefined;

  const repeated = [...needle].every((char) => char === needle[0]);
  const text = repeated ? (needle[0] ?? '') : needle;
  const start = repeated || needle.length === 1 ? from + 1 : from;
  for (let step = 0; step < rows.length; step += 1) {
    const row = rows[(((start + step) % rows.length) + rows.length) % rows.length];
    if (row !== undefined && row.node.label.toLocaleLowerCase().startsWith(text)) return row;
  }

  return undefined;
}

/**
 * Клавіша → дія (патерн WAI-ARIA tree + керування вузлом).
 *
 * ↑/↓/Home/End - рух; → розгортає або йде до першої дитини; ← згортає або йде до батька;
 * Enter/Space - вибір; Delete - вивести з ужитку; Alt+M - «Перенести до…»; Insert - додати дитину;
 * Alt+↑/Alt+↓ - порядок серед сусідів. `canEdit=false` вимикає лише зміни.
 */
export function resolveTreeKey(
  rows: readonly TreeRow[],
  index: TreeIndex,
  at: number,
  input: TreeKeyInput,
  options: { readonly canEdit: boolean; readonly reorderable: boolean },
): TreeKeyAction {
  const row = rows[at];
  if (row === undefined) return None;
  const focus = (target: TreeRow | undefined): TreeKeyAction =>
    target === undefined ? None : { type: 'focus', id: target.node.id };

  if (input.ctrlKey === true || input.metaKey === true) return None;

  if (input.altKey === true) {
    if (!options.canEdit) return None;
    if (input.key === 'm' || input.key === 'M') return { type: 'move', id: row.node.id };
    if (input.key === 'ArrowUp' || input.key === 'ArrowDown') {
      if (!options.reorderable || row.node.retired === true) return None;
      const parent = row.node.parentId !== null && index.byId.has(row.node.parentId) ? row.node.parentId : null;
      const siblings = index.children.get(parent) ?? [];
      const position = siblings.findIndex((item) => item.id === row.node.id);
      const delta = input.key === 'ArrowUp' ? -1 : 1;
      const target = position + delta;
      if (position < 0 || target < 0 || target >= siblings.length) return None;
      // Перед ким стати: вгору - перед сусідом, що вище; вниз - перед сусідом, що йде за наступним (або в кінець).
      const beforeId = delta < 0 ? (siblings[target]?.id ?? null) : (siblings[target + 1]?.id ?? null);

      return { type: 'reorder', id: row.node.id, parentId: parent, beforeId, position: target + 1, total: siblings.length };
    }

    return None;
  }

  switch (input.key) {
    case 'ArrowDown':
      return focus(rows[at + 1]);
    case 'ArrowUp':
      return focus(rows[at - 1]);
    case 'Home':
      return focus(rows[0]);
    case 'End':
      return focus(rows[rows.length - 1]);
    case 'ArrowRight':
      if (!row.hasChildren) return None;

      return row.expanded ? focus(rows[at + 1]) : { type: 'expand', id: row.node.id };
    case 'ArrowLeft': {
      if (row.hasChildren && row.expanded) return { type: 'collapse', id: row.node.id };
      const parent = rows.slice(0, at).reverse().find((candidate) => candidate.level === row.level - 1);

      return focus(parent);
    }
    case 'Enter':
    case ' ':
      return { type: 'select', id: row.node.id };
    case 'Delete':
      return options.canEdit && row.node.retired !== true ? { type: 'retire', id: row.node.id } : None;
    case 'Insert':
      return options.canEdit ? { type: 'add', id: row.node.id } : None;
    default:
      return None;
  }
}
