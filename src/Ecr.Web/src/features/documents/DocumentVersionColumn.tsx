import type { JSX } from 'react';
import { Badge, Box, Group, Table, Text, UnstyledButton, VisuallyHidden } from '@mantine/core';
import { t } from '@/shared/i18n';
import { formatPeriodKey } from '@/shared/format/periodKey';
import { resolveTemplateVersion, type VersionSort, type VersionSource } from './documentVersion';

/**
 * Колонка «Version» переліку документів: версія ШАБЛОНУ документа за обраний у переліку період
 * (`templateVersionByPeriod[період] ?? templateVersion`, контракт RPT-STABLE).
 *
 * Макет: окремого екрана колонки в `docs/design/hybrid/` немає — стиль найближчої колонки «Issues»
 * (приглушений текст у комірці, «—» для невідомого) + `Badge variant="outline"` з KIT для коду версії.
 * ⛔ Версії немає (`null`) — видиме «—» з поясненням у `title`, а не порожня комірка і не вигадане «v0».
 */
export function DocumentVersionCell({
  source,
  periodKey,
}: {
  readonly source: VersionSource;
  readonly periodKey: number | null;
}): JSX.Element {
  const { version, fromPeriod } = resolveTemplateVersion(source, periodKey);

  if (version === null) {
    return (
      <Text c="dimmed" title={t('documents.versionUnknown')} data-document-version="none">
        —
      </Text>
    );
  }

  // Підказка й текст для читалки - лише коли значення взято з карти періоду: тоді воно ВІДРІЗНЯЄТЬСЯ від типового.
  const period = periodKey === null ? '' : formatPeriodKey(periodKey) || String(periodKey);
  const hint = fromPeriod ? t('documents.versionForPeriod', { period }) : undefined;

  return (
    <Badge
      variant="outline"
      color="gray"
      radius="sm"
      size="sm"
      tt="none"
      title={hint}
      data-document-version={version}
      data-version-source={fromPeriod ? 'period' : 'template'}
    >
      {t('documents.versionValue', { version })}
      {hint !== undefined && <VisuallyHidden> — {hint}</VisuallyHidden>}
    </Badge>
  );
}

/**
 * Заголовок колонки «Version», що сортує. `aria-sort` на колонці; кнопка має підпис і стан словом.
 *
 * ⚠ Сортується ЗАВАНТАЖЕНА сторінка (сервер параметра не має) - це сказано в `title` кнопки, щоб людина не вважала
 * порядок глобальним.
 */
export function DocumentVersionTh({
  sort,
  onSort,
}: {
  readonly sort: VersionSort | null;
  readonly onSort: () => void;
}): JSX.Element {
  return (
    <Table.Th
      data-column="version"
      aria-sort={sort === null ? 'none' : sort === 'asc' ? 'ascending' : 'descending'}
    >
      <UnstyledButton
        onClick={onSort}
        title={t('documents.versionSortBy')}
        fz="inherit"
        fw="inherit"
        c="inherit"
        data-version-sort={sort ?? 'none'}
      >
        <Group gap="xs" wrap="nowrap">
          <span>{t('documents.versionColumn')}</span>
          <Box component="span" display="inline-flex" opacity={sort === null ? 0.35 : 1}>
            <svg width={10} height={10} viewBox="0 0 10 10" aria-hidden="true" focusable="false">
              {sort === 'asc' && <path d="M5 2 9 8H1z" fill="currentColor" />}
              {sort === 'desc' && <path d="M5 8 1 2h8z" fill="currentColor" />}
              {sort === null && <path d="M5 1 8 4H2zM5 9 2 6h6z" fill="currentColor" />}
            </svg>
          </Box>
          {sort !== null && (
            <VisuallyHidden>{sort === 'asc' ? t('documents.versionSortedAsc') : t('documents.versionSortedDesc')}</VisuallyHidden>
          )}
        </Group>
      </UnstyledButton>
    </Table.Th>
  );
}
