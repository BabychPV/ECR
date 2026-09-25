import { useMemo, useState, type JSX } from 'react';
import { Select } from '@mantine/core';
import { useDebouncedValue } from '@mantine/hooks';
import { keepPreviousData, useQuery } from '@tanstack/react-query';
import type { ColumnDefSearchResultDto } from '@/api/types';
import { apiFetch } from '@/api/client';
import { localized } from '@/shared/i18n/localized';
import { t } from '@/shared/i18n';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';

/** Затримка між останнім натисканням і запитом пошуку. */
export const ColumnSearchDebounceMs = 300;

/** Скільки колонок просимо в сервера на один запит. */
const ColumnSearchLimit = 50;

/** Підпис колонки у виборі: «Назва (КОД) · АРКУШ/ТАБЛИЦЯ». */
export function columnDefLabel(column: ColumnDefSearchResultDto): string {
  return `${localized(column.headerL10n) || column.code} (${column.code}) · ${column.sheetCode}/${column.tableCode}`;
}

interface ColumnDefPickerProps {
  readonly label: string;
  readonly description: string;
  readonly disabled: boolean;

  /** Обрана колонка; `0` — ще не обрано. */
  readonly value: number;
  readonly onChange: (columnDefId: number) => void;
}

/**
 * Вибір колонки для прив'язки й обов'язкового входу методології — пошуком на
 * СЕРВЕРІ (F-03, четвертий раунд UX).
 *
 * ⛔ Доти перелік вантажився ОДИН раз (`limit=200`) і фільтрувався в браузері:
 * на стенді 5991 колонка, і потрібна (`EMISSION`) у перші 200 просто не
 * потрапляла — прив'язку неможливо було зробити з інтерфейсу. Тепер кожен
 * введений текст (після паузи `ColumnSearchDebounceMs`) іде в
 * `GET /column-defs/search?q=…`, а сервер фільтрує в SQL до обмеження.
 *
 * ⚠ F-28: запит живе в ЦЬОМУ компоненті, а він монтується лише у відкритому
 * діалозі. Доти пошук ішов із самої панелі — щоразу, навіть у публікатора без
 * `Template.View`, і кожне відкриття сторінки лишало в консолі `403`.
 *
 * ⚠ Фільтр Mantine вимкнено (`filter` повертає все): перелік уже відфільтрував
 * сервер, а другий, клієнтський фільтр за іншим правилом (підпис замість коду й
 * перекладів) ховав би частину знайденого.
 */
export function ColumnDefPicker({
  label,
  description,
  disabled,
  value,
  onChange,
}: ColumnDefPickerProps): JSX.Element {
  const [search, setSearch] = useState('');
  const [selected, setSelected] = useState<ColumnDefSearchResultDto | null>(null);

  // ⚠ Після вибору Mantine кладе в поле пошуку ПІДПИС обраної колонки. Шукати
  // за ним на сервері не можна — підпис складений клієнтом і не збігається ні з
  // кодом, ні з заголовком; тоді перелік показує типовий набір.
  const selectedLabel = selected === null ? null : columnDefLabel(selected);
  const typed = search === selectedLabel ? '' : search.trim();
  const [query] = useDebouncedValue(typed, ColumnSearchDebounceMs);

  const columns = useQuery({
    queryKey: ['column-defs-search', query],
    queryFn: () =>
      apiFetch<ColumnDefSearchResultDto[]>(
        `/api/v1/column-defs/search?q=${encodeURIComponent(query)}&limit=${String(ColumnSearchLimit)}`,
      ),
    staleTime: 60 * 1000,
    placeholderData: keepPreviousData,
  });

  const options = useMemo(() => {
    const found = columns.data ?? [];
    const all = selected !== null && !found.some((c) => c.id === selected.id) ? [selected, ...found] : found;

    return all.map((column) => ({ value: String(column.id), label: columnDefLabel(column) }));
  }, [columns.data, selected]);

  if (columns.error !== null && columns.error !== undefined) {
    return <ErrorAlert error={columns.error} onRetry={() => void columns.refetch()} />;
  }

  return (
    <Select
      label={label}
      description={description}
      searchable
      disabled={disabled}
      value={value === 0 ? null : String(value)}
      data={options}
      filter={({ options: all }) => all}
      searchValue={search}
      onSearchChange={setSearch}
      nothingFoundMessage={columns.isFetching ? t('common.loading') : t('methodologies.columnNotFound')}
      onChange={(next) => {
        const column = (columns.data ?? []).find((c) => String(c.id) === next) ?? null;
        setSelected(column ?? (next === null ? null : selected));
        onChange(next === null ? 0 : Number(next));
      }}
    />
  );
}
