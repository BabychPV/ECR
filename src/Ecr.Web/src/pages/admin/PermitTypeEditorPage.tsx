import type { JSX } from 'react';
import { useParams } from 'react-router-dom';
import { routes } from '@/app/routes';
import { t } from '@/shared/i18n';
import { PermitsEmptySection } from '@/features/permits/shell/PermitsEmptySection';

/** Конструктор типу перміта (`/admin/permits/types/:code`). Каркас без API. */
export function PermitTypeEditorPage(): JSX.Element {
  const { code } = useParams();

  return (
    <PermitsEmptySection
      current="types"
      title={t('permits.typeEditor.title')}
      meta={code}
      back={{ label: t('permits.back.types'), href: routes.adminPermitTypes.path }}
      emptyTitle={t('permits.typeEditor.emptyTitle')}
      emptyHint={t('permits.typeEditor.emptyHint')}
    />
  );
}
