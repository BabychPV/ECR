import { useEffect, useState, type JSX } from 'react';
import { Button, FileInput, Select, Skeleton, Text } from '@mantine/core';
import { usePendingLoading } from '@/features/common/usePendingLoading';
import { language, t } from '@/shared/i18n';
import { Banner } from '@/shared/ui/Banner';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { showDone } from '@/shared/ui/notify';
import { getImportProfile } from '../airApi';
import type { ImportProfileSummary } from '../airTypes';
import { useDeleteImportProfile, useImportProfiles } from '../useAirLimits';
import type { ImportDraft } from './importModel';
import { InputOrder } from './inputOrder';

export interface FileStepProps {
  readonly draft: ImportDraft;
  readonly onChange: (patch: Partial<ImportDraft>) => void;
  /** Текст помилки поля (перша невдала спроба «Next»). */
  readonly problem: 'file' | 'extension' | null;
  /** Right `Registry.EditDefinition`: «Edit profile» / «Delete profile…» (pass 3). */
  readonly mayEditProfile?: boolean;
  /** «Edit profile»: read the file with the chosen profile and open its mapping in the editor. */
  readonly onEditProfile?: () => void;
}

function profileName(profile: ImportProfileSummary): string {
  const names = profile.nameL10n;

  return names[language()] ?? names['en'] ?? Object.values(names)[0] ?? profile.code;
}

/**
 * Pass 3 (UAT 08.11: «existing profiles cannot be edited, re-saved or deleted»): what can be done with the chosen profile. The
 * built-in one says in visible text why it is read-only; another one is edited (next step, editor open, «Save to profile») or
 * deleted after a confirmation, with the token it has now.
 */
function ProfileActions({
  profile,
  mayEditProfile,
  onEditProfile,
  onDeleted,
}: {
  readonly profile: ImportProfileSummary;
  readonly mayEditProfile: boolean;
  readonly onEditProfile: (() => void) | undefined;
  readonly onDeleted: () => void;
}): JSX.Element | null {
  const [confirming, setConfirming] = useState(false);
  const [reading, setReading] = useState(false);
  const [failure, setFailure] = useState<unknown>(null);
  const remove = useDeleteImportProfile();
  const deleting = usePendingLoading(remove.isPending || reading);

  if (profile.builtIn) {
    return (
      <Text size="sm" c="dimmed" data-testid="air-profile-builtin">
        {t('permits.air.import.profile.builtInNote', { code: profile.code })}
      </Text>
    );
  }
  if (!mayEditProfile) return null;

  const confirmDelete = (): void => {
    if (remove.isPending || reading) return;
    setFailure(null);
    setReading(true);
    getImportProfile(profile.code)
      .then((current) =>
        remove.mutateAsync({ code: current.code, rowVersion: current.rowVersion }).then(() => {
          showDone(t('permits.air.import.profile.deleted', { code: current.code }));
          setConfirming(false);
          onDeleted();
        }),
      )
      .catch(setFailure)
      .finally(() => setReading(false));
  };

  return (
    <div className="pi-panel" data-testid="air-profile-actions">
      {failure !== null && <ErrorAlert error={failure} />}
      {confirming ? (
        <>
          <Banner
            tone="danger"
            testId="air-profile-delete-confirm"
            title={t('permits.air.import.profile.deleteTitle', { name: profileName(profile), code: profile.code })}
            text={t('permits.air.import.profile.deleteText')}
          />
          <div className="pi-actions">
            <Button variant="default" onClick={() => setConfirming(false)}>
              {t('common.cancel')}
            </Button>
            <Button loading={deleting} onClick={confirmDelete}>
              {t('permits.air.import.profile.deleteConfirm')}
            </Button>
          </div>
        </>
      ) : (
        <div className="pi-actions">
          {onEditProfile !== undefined && (
            <Button variant="default" onClick={onEditProfile}>
              {t('permits.air.import.profile.edit')}
            </Button>
          )}
          <Button variant="default" onClick={() => setConfirming(true)}>
            {t('permits.air.import.profile.delete')}
          </Button>
        </div>
      )}
    </div>
  );
}

/** Крок «Файл»: книга й профіль імпорту. Профіль за замовчуванням — перший (`ERA_TABLE_33`). */
export function FileStep({ draft, onChange, problem, mayEditProfile = false, onEditProfile }: FileStepProps): JSX.Element {
  const profiles = useImportProfiles();
  const firstCode = profiles.data?.[0]?.code;

  // Показаний профіль і відправлений мають збігатись: вибір за замовчуванням записується в чернетку.
  useEffect(() => {
    if (draft.profileCode === '' && firstCode !== undefined) onChange({ profileCode: firstCode });
  }, [draft.profileCode, firstCode, onChange]);

  if (profiles.isError) return <ErrorAlert error={profiles.error} onRetry={() => void profiles.refetch()} />;
  if (profiles.data === undefined) return <Skeleton height={160} role="status" aria-label={t('common.loading')} />;

  const options = profiles.data.map((profile) => ({ value: profile.code, label: `${profileName(profile)} (${profile.code})` }));
  const profileCode = draft.profileCode === '' ? (profiles.data[0]?.code ?? '') : draft.profileCode;
  const chosen = profiles.data.find((profile) => profile.code === profileCode);

  return (
    <div className="pi-grid">
      <FileInput
        label={t('permits.air.import.file.label')}
        description={t('permits.air.import.file.hint')}
        inputWrapperOrder={InputOrder}
        placeholder={t('permits.air.import.file.placeholder')}
        accept=".xlsx"
        clearable
        required
        value={draft.file}
        onChange={(file) => onChange({ file, spec: null, resolutions: {}, confirmedReplace: false })}
        error={
          problem === 'file'
            ? t('permits.air.import.file.required')
            : problem === 'extension'
              ? t('permits.air.import.file.extension')
              : null
        }
        data-autofocus
      />
      <div>
        <Select
          label={t('permits.air.import.profile.label')}
          description={t('permits.air.import.profile.hint')}
          inputWrapperOrder={InputOrder}
          data={options}
          value={profileCode}
          allowDeselect={false}
          onChange={(value) => onChange({ profileCode: value ?? '', spec: null })}
        />
        {chosen !== undefined && (
          <ProfileActions
            key={chosen.code}
            profile={chosen}
            mayEditProfile={mayEditProfile}
            onEditProfile={onEditProfile}
            onDeleted={() => onChange({ profileCode: '', spec: null })}
          />
        )}
      </div>
    </div>
  );
}
