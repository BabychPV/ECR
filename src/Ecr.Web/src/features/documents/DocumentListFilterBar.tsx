import type { JSX } from 'react';
import { Group, NativeSelect, Switch } from '@mantine/core';
import { t } from '@/shared/i18n';
import { statusKey } from '@/shared/ui/StatusBadge';
import type { DocumentStateFilter } from './api';
import { DocumentStateFilters, parseStateFilter, type DocumentListFilters } from './documentListFilters';

export interface DocumentListFilterBarProps {
  /** Обраний період; `null` — фільтр стану недоступний. */
  readonly periodKey: number | null;

  readonly filters: DocumentListFilters;
}

/**
 * Рядок фільтрів над переліком документів (`BE-09b`).
 *
 * ⚠ Не `shared/ui/FilterBar`: його перелік не вміє бути недоступним і не має
 * перемикача, а тут потрібні обидва.
 *
 * ⛔ Без періоду фільтр стану НЕДОСТУПНИЙ, і причина написана ПІД полем та
 * прив'язана до нього (`aria-describedby`). Тиха `422` на вибір стану — це
 * порожній екран без пояснення; сховане поле — функція, про яку не дізнаються.
 *
 * ⚠ `NativeSelect`, а не `Select`: п'ять сталих варіантів, пошук не потрібен,
 * а рідний список однаково працює з клавіатурою й читалкою.
 *
 * ⚠ Підписи станів — ті самі рядки `status.sheet.*`, що в бейджах таблиці й у
 * смузі лічильників: один стан — одне слово на всьому екрані.
 */
export function DocumentListFilterBar({ periodKey, filters }: DocumentListFilterBarProps): JSX.Element {
  const noPeriod = periodKey === null;

  /*
   * ⚠ Кожен підпис — окремим викликом із ЛІТЕРАЛАМИ: сторож каталогу
   * (`EndpointCoverageTests`) розбирає `statusKey('sheet', 'Draft')` як ключ,
   * а `statusKey('sheet', state)` у циклі — ні.
   */
  const labels: Readonly<Record<DocumentStateFilter, string>> = {
    Draft: t(statusKey('sheet', 'Draft')),
    Submitted: t(statusKey('sheet', 'Submitted')),
    Approved: t(statusKey('sheet', 'Approved')),
    Rejected: t(statusKey('sheet', 'Rejected')),
  };

  const options = [
    { value: '', label: t('documents.stateAll') },
    ...DocumentStateFilters.map((state) => ({ value: state, label: labels[state] })),
  ];

  return (
    <Group gap="lg" align="flex-start" mb="md" data-document-filters="true">
      <NativeSelect
        size="xs"
        miw={180}
        label={t('documents.state')}
        data={options}
        value={filters.state ?? ''}
        disabled={noPeriod}
        description={noPeriod ? t('documents.stateNeedsPeriod') : undefined}
        inputWrapperOrder={['label', 'input', 'description']}
        onChange={(event) => {
          filters.setState(parseStateFilter(event.currentTarget.value));
        }}
      />

      {/* ⚠ Підпис не обіцяє більше, ніж робить сервер: «мої» — це «створені
          або подані мною», і саме так він і читається. */}
      <Switch
        mt="lg"
        size="sm"
        label={t('documents.filterMine')}
        checked={filters.mine}
        onChange={(event) => {
          filters.setMine(event.currentTarget.checked);
        }}
      />
    </Group>
  );
}
