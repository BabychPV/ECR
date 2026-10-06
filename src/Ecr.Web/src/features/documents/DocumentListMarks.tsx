import type { JSX } from 'react';
import { Badge } from '@mantine/core';
import { formatNumber } from '@/shared/format';
import { t } from '@/shared/i18n';

/*
 * Позначки рядка переліку документів — спільні для таблиці (`DocumentsPage`) і дошки
 * (`DocumentsBoard`, `UI-40`), щоб «пігулка» помилок і «око» були однакові в обох поданнях.
 */
/** Число відкритих помилок — червона «пігулка», як у макеті (`.count.bad`). */
export function IssueCount({ count }: { readonly count: number }): JSX.Element {
  return (
    <Badge
      size="sm"
      variant="transparent"
      radius="xl"
      c="var(--ecr-danger)"
      bg="var(--ecr-danger-soft)"
      ff="monospace"
      title={t('documents.issuesHint', { count })}
      data-issue-count={count}
    >
      {formatNumber(count)}
    </Badge>
  );
}

/** «Око» швидкого перегляду (`kit.js` → `I.eye`). */
export function EyeIcon(): JSX.Element {
  return (
    <svg
      width={16}
      height={16}
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth={1.75}
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden="true"
      focusable="false"
    >
      <path d="M2 12s4-7 10-7 10 7 10 7-4 7-10 7S2 12 2 12zM12 15a3 3 0 1 0 0-6 3 3 0 0 0 0 6z" />
    </svg>
  );
}
