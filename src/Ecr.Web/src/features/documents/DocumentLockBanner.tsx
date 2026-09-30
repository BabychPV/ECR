import type { JSX } from 'react';
import { Alert } from '@mantine/core';
import { t } from '@/shared/i18n';
import type { DocumentLock } from './documentLock';

/** Властивості банера. */
export interface DocumentLockBannerProps {
  readonly lock: DocumentLock | null;
  /** Період — у тексті, щоб банер казав, ЯКИЙ саме закрито. */
  readonly periodKey: number;
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
export function DocumentLockBanner({ lock, periodKey }: DocumentLockBannerProps): JSX.Element | null {
  if (lock === null) return null;

  return (
    <Alert color="statusWarning" variant="light" role="status" data-document-lock={lock}>
      {textOf(lock, periodKey)}
    </Alert>
  );
}

function textOf(lock: DocumentLock, periodKey: number): string {
  switch (lock) {
    case 'projectArchived':
      return t('document.lock.projectArchived');
    case 'periodClosed':
      return t('document.lock.periodClosed', { period: periodKey });
    case 'periodNotOpen':
      return t('document.lock.periodNotOpen', { period: periodKey });
    case 'sheetSubmitted':
      return t('grid.submittedReadOnlyHint');
    case 'sheetApproved':
      return t('document.lock.sheetApproved');
  }
}
