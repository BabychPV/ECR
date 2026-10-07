import type { JSX, ReactNode } from 'react';
import { NativeSelect, Switch, Text, TextInput } from '@mantine/core';
import { FilterInline, FilterRow, readerOnlyDescription } from '@/shared/ui/FilterBar';
import { t } from '@/shared/i18n';
import { statusKey } from '@/shared/ui/StatusBadge';
import { useFieldDraft } from '@/shared/ui/useFieldDraft';
import type { DocumentStateFilter } from './api';
import { DocumentStateFilters, parseStateFilter, type DocumentListFilters } from './documentListFilters';

interface DocumentListFilterBarProps {
  /** Обраний період; `null` — фільтр стану недоступний. */
  readonly periodKey: number | null;

  readonly filters: DocumentListFilters;

  /** Проєкти для фільтра (`UI-18`); порожньо — фільтр не малюється (проєктів немає або перелік ще в дорозі). */
  readonly projects?: readonly { readonly id: number; readonly code: string }[] | undefined;

  /**
   * Що поставити в КІНЕЦЬ того самого ряду — смуга лічильників етапів
   * (`DocumentListSummaryStrip`). Рішення людини 2026-10-06: «тумблери і
   * кількість — в один рядок», а не число над підписом окремою смугою.
   */
  readonly children?: ReactNode;
}

/**
 * Рядок фільтрів над переліком документів (`BE-09b`).
 *
 * ⚠ Не `shared/ui/FilterBar`: його перелік не вміє бути недоступним і не має
 * перемикача, а тут потрібні обидва. Будова ряду при цьому — спільна
 * (`FilterRow`/`FilterInline`): перемикачі стоять по центру поля «State», а не
 * на окремому відступі `mt="lg"`, що розходився з полем за іншого шрифту.
 *
 * ⛔ Без періоду фільтр стану НЕДОСТУПНИЙ, і причина написана ПІД рядом та
 * прив'язана до поля (`description`, прихований для ока, — див. `FilterHints`:
 * видимий опис під полем опустив би нижню межу лише цього поля й розсунув ряд). Тиха `422` на вибір стану — це
 * порожній екран без пояснення; сховане поле — функція, про яку не дізнаються.
 *
 * ⚠ `NativeSelect`, а не `Select`: п'ять сталих варіантів, пошук не потрібен,
 * а рідний список однаково працює з клавіатурою й читалкою.
 *
 * ⚠ Підписи станів — ті самі рядки `status.sheet.*`, що в бейджах таблиці й у
 * смузі лічильників: один стан — одне слово на всьому екрані.
 */
export function DocumentListFilterBar({
  periodKey,
  filters,
  projects = [],
  children,
}: DocumentListFilterBarProps): JSX.Element {
  const noPeriod = periodKey === null;
  // ⛔ Поле показує ВЛАСНЕ значення, а не адресу (`useFieldDraft`), як і `FilterBar`: кероване адресою
  // воно губило літери, бо адреса оновлюється переходом і запізнюється.
  const search = useFieldDraft(filters.q);

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
    <>
      <FilterRow gap="lg" mb={noPeriod ? 'xs' : 'md'} data-document-filters="true">
        {/* ✎ `UI-18` (макет: `FilterBar` — пошук · All projects · All states · Everyone's documents):
            пошук іде в `q` сервера — по ключу й назві документа, а не лише по завантаженій сторінці. */}
        <TextInput
          size="xs"
          miw={220}
          type="search"
          label={t('documents.search')}
          placeholder={t('documents.searchPlaceholder')}
          value={search.value}
          onFocus={search.onFocus}
          onBlur={search.onBlur}
          onChange={(event) => {
            search.setValue(event.currentTarget.value);
            filters.setQ(event.currentTarget.value);
          }}
          data-documents-search=""
        />

        {projects.length > 0 && (
          <NativeSelect
            size="xs"
            miw={160}
            label={t('documents.project')}
            data={[
              { value: '', label: t('documents.projectAll') },
              ...projects.map((project) => ({ value: String(project.id), label: project.code })),
            ]}
            value={filters.projectId === null ? '' : String(filters.projectId)}
            onChange={(event) => {
              const value = event.currentTarget.value;
              filters.setProjectId(value === '' ? null : Number(value));
            }}
          />
        )}

        <NativeSelect
          size="xs"
          miw={180}
          label={t('documents.state')}
          data={options}
          value={filters.state ?? ''}
          disabled={noPeriod}
          description={noPeriod ? t('documents.stateNeedsPeriod') : undefined}
          styles={readerOnlyDescription}
          onChange={(event) => {
            filters.setState(parseStateFilter(event.currentTarget.value));
          }}
        />

        {/* ⚠ Підпис не обіцяє більше, ніж робить сервер: «лише мої» — це «створені
            або подані мною» (`documents.filterMine`). Макет: «Everyone's documents» / «Only mine». */}
        <NativeSelect
          size="xs"
          miw={200}
          label={t('documents.owners')}
          data={[
            { value: '', label: t('documents.ownersAll') },
            { value: 'mine', label: t('documents.filterMine') },
          ]}
          value={filters.mine ? 'mine' : ''}
          onChange={(event) => {
            filters.setMine(event.currentTarget.value === 'mine');
          }}
        />

        {/*
         * ⚠ На відміну від фільтра стану — НЕ вимкнений без періоду
         * (`noPeriod` тут не читається): сервер приймає `hasLateEdits` за
         * будь-який період, так само, як позначку в самому рядку (`BE-09b`).
         */}
        <FilterInline>
          <Switch
            size="sm"
            label={t('documents.filterLateEdits')}
            checked={filters.hasLateEdits}
            onChange={(event) => {
              filters.setHasLateEdits(event.currentTarget.checked);
            }}
          />
        </FilterInline>

        {children}
      </FilterRow>

      {/* Видима копія пояснення; читалка вже отримує його через поле. */}
      {noPeriod && (
        <Text size="xs" c="dimmed" mb="md" aria-hidden="true" data-document-filters-hint="">
          {t('documents.stateNeedsPeriod')}
        </Text>
      )}
    </>
  );
}

