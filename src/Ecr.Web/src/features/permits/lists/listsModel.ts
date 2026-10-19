import { t } from '@/shared/i18n';
import { localized } from '@/shared/i18n/localized';
import type { RegistryDefDto } from '@/api/types';
import type { PermitType, PermitTypeColumn } from '../api/types';
import { scopeKeyOf } from '../typeEditor/draftModel';
import { textOf } from '../typeEditor/labels';

/**
 * Модель екрана «Lists» (PM-08d, варіант A): списки вибору зводяться на клієнті з визначень типів
 * (`GET permit-types/{code}`) і наявних довідників. Окремого ендпоінта списків немає — це відомо й чесно
 * сказано на екрані (значення «лише тип» редагуються в конструкторі типу). Нічого з React і мережі.
 */

/** Походження списку; збігається з `ListScopeKey` конструктора. */
export type ListKind = 'shared' | 'type' | 'permit';

export const ListKinds: readonly ListKind[] = ['shared', 'type', 'permit'];

/** Стовпець типу, що бере значення зі списку. */
export interface ListUse {
  readonly typeCode: string;
  readonly typeName: string;
  /** `null` — поле шапки дозволу. */
  readonly tableCode: string | null;
  readonly tableName: string;
  readonly columnCode: string;
  readonly columnName: string;
}

export interface ListRow {
  /** Стабільний ключ рядка й значення `?panel=`. */
  readonly id: string;
  readonly kind: ListKind;
  readonly name: string;
  /** Тип-власник для `type`/`permit`; порожньо для спільного. */
  readonly owner: string;
  /** Код тип-власника (куди веде «Open the permit type»); для спільного — перший тип, що ним користується. */
  readonly typeCode: string;
  /** Код спільного довідника. */
  readonly registryCode: string | null;
  /** Значення «лише тип» (read-only). */
  readonly choices: readonly string[];
  /** Кількість значень; `null` — невідомо (довідник не віддано) або список-таблиця. */
  readonly entries: number | null;
  readonly used: readonly ListUse[];
}

/** Підпис походження словом каталогу (ключі — літерали: сторож каталогу читає їх як текст). */
export function kindLabel(kind: ListKind): string {
  switch (kind) {
    case 'shared':
      return t('permits.lists.kind.shared');
    case 'type':
      return t('permits.lists.kind.type');
    case 'permit':
      return t('permits.lists.kind.permit');
  }
}

const KindOrder: Record<ListKind, number> = { shared: 0, type: 1, permit: 2 };

function useOf(type: PermitType, tableCode: string | null, tableName: string, column: PermitTypeColumn, lang: string | undefined): ListUse {
  return {
    typeCode: type.code,
    typeName: textOf(type.nameL10n, lang) || type.code,
    tableCode,
    tableName,
    columnCode: column.code,
    columnName: textOf(column.nameL10n, lang) || column.code,
  };
}

/** Усі стовпці-списки типу: спершу шапка (`tableCode = null`), далі таблиці. */
function listColumns(type: PermitType): { tableCode: string | null; tableName: string; column: PermitTypeColumn }[] {
  return [
    ...type.headerFields.map((column) => ({ tableCode: null, tableName: '', column })),
    ...type.tables.flatMap((table) =>
      table.columns.map((column) => ({ tableCode: table.code as string | null, tableName: textOf(table.nameL10n), column })),
    ),
  ];
}

/**
 * Рядки екрана: спільний довідник — один рядок на довідник (усі стовпці, що його беруть, у `used`);
 * «лише тип» і «таблиця того ж дозволу» — по рядку на стовпець.
 */
export function buildListRows(
  types: readonly PermitType[],
  registries: readonly RegistryDefDto[],
  lang?: string,
): ListRow[] {
  const shared = new Map<string, { row: ListRow; used: ListUse[] }>();
  const rows: ListRow[] = [];

  for (const type of types) {
    for (const { tableCode, tableName, column } of listColumns(type)) {
      const kind = scopeKeyOf(column);
      if (kind === null) continue;
      const use = useOf(type, tableCode, tableName, column, lang);

      if (kind === 'shared') {
        const code = column.listRegistryCode ?? '';
        if (code === '') continue;
        const known = shared.get(code);
        if (known !== undefined) {
          known.used.push(use);
          continue;
        }
        const registry = registries.find((item) => item.code === code);
        const used: ListUse[] = [use];
        shared.set(code, {
          used,
          row: {
            id: `shared:${code}`,
            kind,
            name: (registry === undefined ? '' : localized(registry.nameL10n)) || code,
            owner: '',
            typeCode: type.code,
            registryCode: code,
            choices: [],
            entries: registry?.entryCount ?? null,
            used,
          },
        });
        continue;
      }

      rows.push({
        id: `${kind}:${type.code}:${tableCode ?? '_header'}:${column.code}`,
        kind,
        name: use.columnName,
        owner: use.typeName,
        typeCode: type.code,
        registryCode: null,
        choices: kind === 'type' ? (column.choices ?? []) : [],
        entries: kind === 'type' ? (column.choices ?? []).length : null,
        used: [use],
      });
    }
  }

  return [...[...shared.values()].map((item) => item.row), ...rows].sort(
    (a, b) => KindOrder[a.kind] - KindOrder[b.kind] || a.name.localeCompare(b.name),
  );
}

export interface ListsStats {
  readonly all: number;
  readonly shared: number;
  readonly type: number;
  readonly permit: number;
}

export function listsStats(rows: readonly ListRow[]): ListsStats {
  return {
    all: rows.length,
    shared: rows.filter((row) => row.kind === 'shared').length,
    type: rows.filter((row) => row.kind === 'type').length,
    permit: rows.filter((row) => row.kind === 'permit').length,
  };
}

export interface ListsFilter {
  readonly needle: string;
  /** Походження з фільтра або показника смуги; порожньо — усі. */
  readonly kind: string;
}

/** Пошук за назвою списку, власником і кодом довідника; походження — рівність. */
export function filterLists(rows: readonly ListRow[], filter: ListsFilter): ListRow[] {
  const needle = filter.needle.trim().toLocaleLowerCase();

  return rows.filter((row) => {
    if (filter.kind !== '' && row.kind !== filter.kind) return false;
    if (needle === '') return true;

    return `${row.name} ${row.owner} ${row.registryCode ?? ''}`.toLocaleLowerCase().includes(needle);
  });
}

/** Значення показника смуги/фільтра як походження або `''`. */
export function kindParam(value: string | null | undefined): string {
  return value !== null && value !== undefined && (ListKinds as readonly string[]).includes(value) ? value : '';
}
