import type { JSX } from 'react';
import { t } from '@/shared/i18n';
import { PermitsEmptySection } from '@/features/permits/shell/PermitsEmptySection';

/** Перелік пермітів (`/admin/permits`). Каркас без API: чесний порожній стан за макетом. */
export function PermitsPage(): JSX.Element {
  return (
    <PermitsEmptySection
      current="permits"
      title={t('nav.permits')}
      emptyTitle={t('permits.empty.title')}
      emptyHint={t('permits.empty.hint')}
      actions={[
        { label: t('permits.empty.new'), primary: true },
        { label: t('permits.empty.import') },
      ]}
    />
  );
}
