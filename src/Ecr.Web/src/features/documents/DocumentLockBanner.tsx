import type { JSX } from 'react';
import { Alert, Group } from '@mantine/core';
import { formatPeriodKey } from '@/shared/format/periodKey';
import { t } from '@/shared/i18n';
import type { DocumentLock } from './documentLock';

/** Властивості банера. */
interface DocumentLockBannerProps {
  readonly lock: DocumentLock | null;
  /** Період — у тексті, щоб банер казав, ЯКИЙ саме закрито. */
  readonly periodKey: number;
  /**
   * Приймальна 08.11 (i1): дія закритого періоду — «Reopen … for this document…» (або недоступна кнопка з причиною).
   * Показується лише під причиною `periodClosed`.
   */
  readonly periodAction?: JSX.Element | null;
}

/**
 * Банер «чому тут нічого не змінити» над сітками документа (`F-18`).
 *
 * ⚠ Текст поданого аркуша — рівно той, що обіцяє посібник користувача
 * (розділ 5.3, `grid.submittedReadOnlyHint`): людина, яка шукає його за
 * посібником, має знайти те саме речення.
 *
 * ⚠ `role="status"`, а не `alert`: це стан документа, а не щойно сталася
 * помилка — читалка має сказати його, але не перебивати.
 */
export function DocumentLockBanner({ lock, periodKey, periodAction = null }: DocumentLockBannerProps): JSX.Element | null {
  if (lock === null) return null;

  return (
    <Alert color="statusWarning" variant="light" role="status" data-document-lock={lock}>
      {textOf(lock, periodKey)}
      {lock === 'periodClosed' && periodAction !== null && <Group mt="xs">{periodAction}</Group>}
    </Alert>
  );
}

function textOf(lock: DocumentLock, periodKey: number): string {
  // ✎ UAT 08.11 (F13-p209 P2-1): період — назвою («January 2026»), а не сирим ключем 202601.
  const period = formatPeriodKey(periodKey) || String(periodKey);

  switch (lock) {
    case 'projectArchived':
      return t('document.lock.projectArchived');
    case 'periodClosed':
      return t('document.lock.periodClosed', { period });
    case 'periodNotOpen':
      return t('document.lock.periodNotOpen', { period });
    case 'sheetSubmitted':
      return t('grid.submittedReadOnlyHint');
    case 'sheetApproved':
      return t('document.lock.sheetApproved');
  }
}
