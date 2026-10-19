import type { JSX } from 'react';
import { t } from '@/shared/i18n';
import { PermitsEmptySection } from '@/features/permits/shell/PermitsEmptySection';

/** Привʼязки аркушів до пермітів (`/admin/permits/bindings`). Каркас без API. */
export function PermitBindingsPage(): JSX.Element {
  return (
    <PermitsEmptySection
      current="bindings"
      title={t('permits.bindings.title')}
      emptyTitle={t('permits.bindings.empty.title')}
      emptyHint={t('permits.bindings.empty.hint')}
      actions={[{ label: t('permits.bindings.new'), primary: true }]}
    />
  );
}
