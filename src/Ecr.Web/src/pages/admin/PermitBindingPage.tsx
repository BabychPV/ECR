import type { JSX } from 'react';
import { useParams } from 'react-router-dom';
import { routes } from '@/app/routes';
import { t } from '@/shared/i18n';
import { PermitsEmptySection } from '@/features/permits/shell/PermitsEmptySection';

/** Привʼязка аркуша до перміта (`/admin/permits/bindings/:id`). Каркас без API. */
export function PermitBindingPage(): JSX.Element {
  const { id } = useParams();

  return (
    <PermitsEmptySection
      current="bindings"
      title={t('permits.binding.title')}
      meta={id}
      back={{ label: t('permits.back.bindings'), href: routes.adminPermitBindings.path }}
      emptyTitle={t('permits.binding.emptyTitle')}
      emptyHint={t('permits.binding.emptyHint')}
    />
  );
}
