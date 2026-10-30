import { useEffect, type JSX } from 'react';
import { FileInput, Select, Skeleton, Text, TextInput } from '@mantine/core';
import { language, t } from '@/shared/i18n';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import type { ImportProfileSummary } from '../airTypes';
import { useImportProfiles } from '../useAirLimits';
import { AreaHints, RegionHints, type ImportDraft } from './importModel';

export interface FileStepProps {
  readonly draft: ImportDraft;
  readonly onChange: (patch: Partial<ImportDraft>) => void;
  /** Текст помилки поля (перша невдала спроба «Next»). */
  readonly problem: 'file' | 'region' | 'extension' | null;
}

function profileName(profile: ImportProfileSummary): string {
  const names = profile.nameL10n;

  return names[language()] ?? names['en'] ?? Object.values(names)[0] ?? profile.code;
}

/** Крок 1: файл, регіон/ділянка, профіль. Профіль за замовчуванням — перший (`ERA_TABLE_33`). */
export function FileStep({ draft, onChange, problem }: FileStepProps): JSX.Element {
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

  return (
    <div className="pi-grid">
      <FileInput
        label={t('permits.air.import.file.label')}
        description={t('permits.air.import.file.hint')}
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
      <Select
        label={t('permits.air.import.profile.label')}
        description={t('permits.air.import.profile.hint')}
        data={options}
        value={profileCode}
        allowDeselect={false}
        onChange={(value) => onChange({ profileCode: value ?? '', spec: null })}
      />
      <TextInput
        label={t('permits.air.import.region.label')}
        description={t('permits.air.import.region.hint')}
        required
        list="pi-region-hints"
        value={draft.region}
        error={problem === 'region' ? t('permits.air.import.region.required') : null}
        onChange={(event) => onChange({ region: event.currentTarget.value })}
      />
      <datalist id="pi-region-hints">
        {RegionHints.map((code) => (
          <option key={code} value={code} />
        ))}
      </datalist>
      <TextInput
        label={t('permits.air.import.area.label')}
        description={t('permits.air.import.area.hint')}
        list="pi-area-hints"
        value={draft.area}
        onChange={(event) => onChange({ area: event.currentTarget.value })}
      />
      <datalist id="pi-area-hints">
        {AreaHints.map((code) => (
          <option key={code} value={code} />
        ))}
      </datalist>
      <Text size="sm" c="dimmed" style={{ gridColumn: '1 / -1' }}>
        {t('permits.air.import.scopeNote')}
      </Text>
    </div>
  );
}
