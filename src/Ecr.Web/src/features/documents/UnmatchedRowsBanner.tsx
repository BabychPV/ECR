import type { JSX } from 'react';
import { Alert, Text } from '@mantine/core';
import { t } from '@/shared/i18n';
import type { UnmatchedRows } from './useUnmatchedRows';

interface UnmatchedRowsBannerProps {
  readonly rows: UnmatchedRows;
}

/**
 * Позначка «частину рядків не взято в розрахунок» над сіткою (D14).
 *
 * ⛔ Це ІНФОРМАЦІЯ, а не помилка: у Land рядок зі статусом, відмінним від Running, виключається навмисно, і
 * блокувань тут немає - ні подання, ні перерахунку. Тому тон `info` макета (`E.Banner({ tone: 'info' })`,
 * `docs/design/hybrid/KIT.md`), як у сусіднього `docs-narrow-note`: `Alert color="brand" variant="light"`.
 *
 * ⚠ Рядки - номер у порядку створення й публічний ключ, значень комірок немає; більше десяти сервер не віддає,
 * тому хвіст позначається «…» за `count`.
 */
export function UnmatchedRowsBanner({ rows }: UnmatchedRowsBannerProps): JSX.Element {
  const shown = rows.rows.map((r) => `${String(r.rowNumber)} (${r.rowKey})`).join(', ');
  const list = rows.count > rows.rows.length ? `${shown}, …` : shown;

  return (
    <Alert
      color="brand"
      variant="light"
      role="status"
      title={t('document.unmatchedRows.title')}
      data-testid="document-unmatched-rows"
    >
      <Text size="sm" data-testid="document-unmatched-rows-hint">
        {t('document.unmatchedRows.hint', { count: String(rows.count), rows: list })}
      </Text>
    </Alert>
  );
}