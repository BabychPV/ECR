import type { JSX } from 'react';
import { useParams } from 'react-router-dom';
import { NotFoundPage } from '@/app/NotFoundPage';
import { t } from '@/shared/i18n';
import { can, useSession } from '@/shared/session/useSession';
import { AsyncBoundary } from '@/shared/ui/AsyncBoundary';
import { Banner } from '@/shared/ui/Banner';
import type { Permit } from '@/features/permits/api/types';
import { AirImportWizard } from '@/features/permits/air/import/AirImportWizard';
import { useMayEditPermits } from '@/features/permits/permit/permitDataRights';
import { usePermit } from '@/features/permits/permit/permitQueries';
import { PermitEditorHead } from '@/features/permits/shell/PermitEditorHead';
import { PermitsPaths } from '@/features/permits/shell/permitPaths';

const AirTypeCode = 'AIR_LIMITS';
const EditDefinition = 'Registry.EditDefinition';

function ImportScreen({ permit }: { readonly permit: Permit }): JSX.Element {
  const mayEdit = useMayEditPermits();
  const session = useSession();
  const mayEditProfile = can(session.data, EditDefinition) && session.data?.isSimulation !== true;
  const expired = permit.status === 'Expired';
  const supported = permit.typeCode === AirTypeCode;
  const mayImport = mayEdit && !expired && supported;

  return (
    <>
      <PermitEditorHead
        back={{ label: t('permits.air.import.back', { permit: permit.number }), href: `${PermitsPaths.permits}/${String(permit.id)}?tab=air` }}
        title={t('permits.air.import.title')}
        subtitle={permit.number}
      />
      {!supported && <Banner tone="warning" title={t('permits.air.import.unsupported.title')} text={t('permits.air.import.unsupported.text')} />}
      {supported && expired && <Banner tone="info" title={t('permits.air.import.expired.title')} text={t('permits.air.import.expired.text')} />}
      {supported && !mayEdit && <Banner tone="info" title={t('permits.air.import.readOnly.title')} text={t('permits.air.import.readOnly.text')} />}
      {supported && <AirImportWizard permitId={permit.id} mayImport={mayImport} mayEditProfile={mayEditProfile} />}
    </>
  );
}

/**
 * Майстер імпорту лімітів із Excel для дозволу «Air Limits Import» (`/admin/permits/:id/import`, NAE-план N5).
 * Без права `Registry.EditData` (або для закінченого дозволу) кроки лише читаються: «Next»/«Apply» вимкнено.
 */
export function PermitAirImportPage(): JSX.Element {
  const { id } = useParams();
  const permitId = Number(id);

  return Number.isInteger(permitId) && permitId > 0 ? <ImportById permitId={permitId} /> : <NotFoundPage />;
}

function ImportById({ permitId }: { readonly permitId: number }): JSX.Element {
  const query = usePermit(permitId);

  return (
    <AsyncBoundary<Permit>
      isPending={query.isPending}
      error={query.error}
      data={query.data}
      skeleton="form"
      onRetry={() => void query.refetch()}
    >
      {(permit) => <ImportScreen permit={permit} />}
    </AsyncBoundary>
  );
}
