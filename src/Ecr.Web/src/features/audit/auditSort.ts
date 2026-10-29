import { DefaultAuditSort, type AuditSort } from '@/features/audit/api';
import { useUrlState } from '@/shared/ui/useUrlState';

/** Колонки, за якими журнали аудиту вміють сортувати (серверний порядок, `AuditSort`). */
export type AuditSortColumn = 'time' | 'user';

const Known: readonly AuditSort[] = ['ChangedAtDesc', 'ChangedAtAsc', 'UserAsc', 'UserDesc'];

/** Порядок з адреси; невідоме значення (адресу правлять руками) — типовий, а не помилка. */
export function parseAuditSort(raw: string | null): AuditSort {
  return Known.find((k) => k === raw) ?? DefaultAuditSort;
}

/** Порядок за яким сортується ця колонка зараз: `asc`/`desc`; `null` — не за нею. */
export function directionOf(sort: AuditSort, column: AuditSortColumn): 'asc' | 'desc' | null {
  if (column === 'time') {
    if (sort === 'ChangedAtAsc') return 'asc';
    if (sort === 'ChangedAtDesc') return 'desc';

    return null;
  }

  if (sort === 'UserAsc') return 'asc';
  if (sort === 'UserDesc') return 'desc';

  return null;
}

/**
 * Наступний порядок по кліку на заголовок колонки: активна колонка міняє напрямок; неактивна стає активною
 * у природному напрямку — час спершу найновіші (як типовий), користувач спершу А→Я.
 */
export function nextAuditSort(sort: AuditSort, column: AuditSortColumn): AuditSort {
  const direction = directionOf(sort, column);

  if (column === 'time') return direction === 'desc' ? 'ChangedAtAsc' : 'ChangedAtDesc';

  return direction === 'asc' ? 'UserDesc' : 'UserAsc';
}

/**
 * Порядок журналу в адресі (`?sort=`): ділиться між вкладками — людина, що дивиться «за користувачем», не
 * втрачає вибір, перемкнувши вкладку, а посилання передає саме той порядок, який вона бачить.
 * Типовий порядок в адресу не пишеться.
 */
export function useAuditSort(): [AuditSort, (sort: AuditSort) => void] {
  const [raw, setRaw] = useUrlState('sort');
  const sort = parseAuditSort(raw);

  return [sort, (next) => setRaw(next === DefaultAuditSort ? null : next)];
}
