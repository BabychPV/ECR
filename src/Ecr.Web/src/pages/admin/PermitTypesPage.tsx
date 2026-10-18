import type { JSX } from 'react';
import { t } from '@/shared/i18n';
import { PermitsEmptySection } from '@/features/permits/shell/PermitsEmptySection';

/** Типи пермітів (`/admin/permits/types`). Каркас без API. */
export function PermitTypesPage(): JSX.Element {
  return (
    <PermitsEmptySection
      current="types"
      title={t('permits.types.title')}
      emptyTitle={t('permits.types.empty.title')}
      emptyHint={t('permits.types.empty.hint')}
      actions={[{ label: t('permits.types.new'), primary: true }]}
    />
  );
}
