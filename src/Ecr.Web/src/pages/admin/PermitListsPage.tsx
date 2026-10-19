import type { JSX } from 'react';
import { t } from '@/shared/i18n';
import { PermitsEmptySection } from '@/features/permits/shell/PermitsEmptySection';

/** Списки вибору пермітів (`/admin/permits/lists`). Каркас без API. */
export function PermitListsPage(): JSX.Element {
  return (
    <PermitsEmptySection
      current="lists"
      title={t('permits.lists.title')}
      emptyTitle={t('permits.lists.empty.title')}
      emptyHint={t('permits.lists.empty.hint')}
    />
  );
}
