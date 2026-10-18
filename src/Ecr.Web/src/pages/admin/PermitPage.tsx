import type { JSX } from 'react';
import { useParams } from 'react-router-dom';
import { routes } from '@/app/routes';
import { t } from '@/shared/i18n';
import { PermitsEmptySection } from '@/features/permits/shell/PermitsEmptySection';

/** Один перміт (`/admin/permits/:id`). Каркас без API: даних для показу ще немає. */
export function PermitPage(): JSX.Element {
  const { id } = useParams();

  return (
    <PermitsEmptySection
      current="permits"
      title={t('permits.detail.title')}
      meta={id}
      back={{ label: t('permits.back.permits'), href: routes.adminPermits.path }}
      emptyTitle={t('permits.detail.emptyTitle')}
      emptyHint={t('permits.detail.emptyHint')}
    />
  );
}
