import { useState, type JSX } from 'react';
import { Group, Select, Text, type ComboboxData } from '@mantine/core';
import { useQuery } from '@tanstack/react-query';
import { t } from '@/shared/i18n';
import { useDebouncedFilter } from '@/shared/ui/useDebouncedFilter';
import { listDocumentOwners } from './api';
import type { OwnerFilterValue } from './documentListFilters';

interface DocumentOwnerFilterProps {
  /** Обрана людина (`owner=` в адресі); `null` — не обрано. */
  readonly owner: number | null;

  /** «Лише мої» (`mine=true`). */
  readonly mine: boolean;

  readonly onChange: (next: OwnerFilterValue) => void;

  /** Ті самі проєкт і період, що в переліку: вибір рахує людей серед тих самих документів. */
  readonly projectId: number | null;
  readonly periodKey: number | null;

  /**
   * Ім'я обраної людини з уже завантаженої сторінки переліку (рядок, де `ownerUserId` = `owner`). Є — підпис поля
   * готовий без запиту; немає (посилання з `owner=` на порожню сторінку) — поле дочитує перелік людей саме.
   */
  readonly ownerName?: string | undefined;
}

/** Значення пунктів, що не є людьми; людина — її `userId` рядком. */
const AllValue = 'all';
const MineValue = 'mine';

/**
 * ✎ D-450 (рішення людини 24.10): фільтр переліку «Власник» — «Усі», «Лише мої», далі люди з кількістю документів.
 *
 * ⚠ `Select` з пошуком, а не `NativeSelect`: людей десятки, і шукати серед них треба за іменем. Перелік людей
 * читається, лише коли список ВІДКРИЛИ (`onDropdownOpen`), а не з відкриттям сторінки: більшість відкриттів переліку
 * фільтром за людиною не користується, а запит — агрегат по всіх видимих документах.
 *
 * ⚠ Сервер віддає до 50 людей (за кількістю документів); набране в пошуку після паузи йде на сервер як `q`, тож
 * людина поза першою півсотнею теж знаходиться. Набране, що дорівнює підпису обраного пункту, — не пошук (Mantine
 * ставить підпис у поле після вибору).
 *
 * ⛔ Люди — лише з відповіді сервера, тією самою межею видимості, що й перелік: клієнт не складає їх із рядків
 * сторінки (там не всі документи) і не вигадує.
 */
export function DocumentOwnerFilter({
  owner,
  mine,
  onChange,
  projectId,
  periodKey,
  ownerName,
}: DocumentOwnerFilterProps): JSX.Element {
  const [requested, setRequested] = useState(false);
  const [search, setSearch] = useState('');

  const value = owner !== null ? String(owner) : mine ? MineValue : AllValue;

  // Підпис обраної людини: зі сторінки переліку, інакше — з відповіді сервера (нижче).
  const people = useQuery({
    queryKey: ['documents', 'owners', projectId, periodKey, ''],
    queryFn: () => listDocumentOwners({ projectId, periodKey, q: '' }),
    enabled: requested || (owner !== null && ownerName === undefined),
    staleTime: 30_000,
  });

  const selectedLabel =
    owner === null
      ? value === MineValue
        ? t('documents.filterMine')
        : t('documents.ownersAll')
      : (ownerName ?? people.data?.find((person) => person.userId === owner)?.displayName ?? t('documents.ownerSelected'));

  const typed = search.trim() === selectedLabel ? '' : search.trim();
  const appliedSearch = useDebouncedFilter(typed);
  const found = useQuery({
    queryKey: ['documents', 'owners', projectId, periodKey, appliedSearch],
    queryFn: () => listDocumentOwners({ projectId, periodKey, q: appliedSearch }),
    enabled: requested && appliedSearch !== '',
    staleTime: 30_000,
  });

  const list = appliedSearch !== '' ? (found.data ?? people.data) : people.data;
  const counts = new Map((list ?? []).map((person) => [String(person.userId), person.documentCount]));
  const items = (list ?? []).map((person) => ({ value: String(person.userId), label: person.displayName }));

  // Обрана людина лишається в переліку, навіть коли її немає серед знайдених: інакше поле втратило б підпис.
  if (owner !== null && !items.some((item) => item.value === String(owner))) {
    items.unshift({ value: String(owner), label: selectedLabel });
  }

  const data: ComboboxData = [
    { value: AllValue, label: t('documents.ownersAll') },
    { value: MineValue, label: t('documents.filterMine') },
    ...(items.length > 0 ? [{ group: t('documents.ownerPeople'), items }] : []),
  ];

  const loading = requested && (people.isPending || (appliedSearch !== '' && found.isPending));
  const failed = people.error !== null || found.error !== null;

  return (
    <Select
      size="xs"
      miw={220}
      label={t('documents.owner')}
      placeholder={t('documents.ownerSearch')}
      data={data}
      value={value}
      searchable
      searchValue={search}
      onSearchChange={setSearch}
      allowDeselect={false}
      // ⚠ Сервер уже звузив перелік за `q`; локальний фільтр Mantine лишається для першої півсотні до паузи в наборі.
      onDropdownOpen={() => {
        setRequested(true);
      }}
      onChange={(next) => {
        if (next === null || next === AllValue) onChange('all');
        else if (next === MineValue) onChange('mine');
        else onChange(Number(next));
      }}
      nothingFoundMessage={
        loading ? t('documents.ownerLoading') : failed ? t('documents.ownerLoadFailed') : t('documents.ownerNothingFound')
      }
      renderOption={({ option }) => {
        const count = counts.get(option.value);
        return (
          <Group gap="xs" justify="space-between" wrap="nowrap" w="100%">
            <Text size="xs" truncate="end">
              {option.label}
            </Text>
            {count !== undefined && (
              <Text size="xs" c="dimmed" style={{ flexShrink: 0 }}>
                {t('documents.ownerCount', { count })}
              </Text>
            )}
          </Group>
        );
      }}
      comboboxProps={{ width: 280, position: 'bottom-start' }}
      data-owner-filter={value}
    />
  );
}
