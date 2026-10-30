import { useEffect, type JSX } from 'react';
import { FileInput, Select, Skeleton, Text } from '@mantine/core';
import { language, t } from '@/shared/i18n';
import { Banner } from '@/shared/ui/Banner';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import type { ImportProfileSummary } from '../airTypes';
import { useAirScopeCodes, useImportProfiles } from '../useAirLimits';
import { NoArea, type ImportDraft } from './importModel';

export interface FileStepProps {
  readonly draft: ImportDraft;
  readonly onChange: (patch: Partial<ImportDraft>) => void;
  /** Текст помилки поля (перша невдала спроба «Next»). */
  readonly problem: 'file' | 'region' | 'area' | 'extension' | null;
}

function profileName(profile: ImportProfileSummary): string {
  const names = profile.nameL10n;

  return names[language()] ?? names['en'] ?? Object.values(names)[0] ?? profile.code;
}

/** Крок 1: файл, регіон/ділянка, профіль. Профіль за замовчуванням — перший (`ERA_TABLE_33`). */
export function FileStep({ draft, onChange, problem }: FileStepProps): JSX.Element {
  const profiles = useImportProfiles();
  const scope = useAirScopeCodes();
  const firstCode = profiles.data?.[0]?.code;

  // Показаний профіль і відправлений мають збігатись: вибір за замовчуванням записується в чернетку.
  useEffect(() => {
    if (draft.profileCode === '' && firstCode !== undefined) onChange({ profileCode: firstCode });
  }, [draft.profileCode, firstCode, onChange]);

  if (profiles.isError) return <ErrorAlert error={profiles.error} onRetry={() => void profiles.refetch()} />;
  if (scope.isError) return <ErrorAlert error={scope.error} onRetry={() => void scope.refetch()} />;
  if (profiles.data === undefined || scope.data === undefined) return <Skeleton height={160} role="status" aria-label={t('common.loading')} />;

  const regions = scope.data.regions;
  const region = regions.find((item) => item.code === draft.region);
  const label = (code: string, name: string | null | undefined): string => (name ? `${code} - ${name}` : code);
  const regionOptions = regions.map((item) => ({ value: item.code, label: label(item.code, item.name) }));
  const areaOptions = (region?.areas ?? []).map((item) => ({
    value: item.code,
    label: item.code === NoArea ? t('permits.air.import.area.none') : label(item.code, item.name),
  }));

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
      <Select
        label={t('permits.air.import.region.label')}
        description={t('permits.air.import.region.hint')}
        required
        data={regionOptions}
        value={draft.region === '' ? null : draft.region}
        allowDeselect={false}
        searchable
        nothingFoundMessage={t('permits.air.import.scope.empty')}
        error={problem === 'region' ? t('permits.air.import.region.required') : null}
        onChange={(value) => onChange({ region: value ?? '', area: '', resolutions: {}, confirmedReplace: false })}
      />
      <Select
        label={t('permits.air.import.area.label')}
        description={t('permits.air.import.area.hint')}
        required
        data={areaOptions}
        value={draft.area === '' ? null : draft.area}
        allowDeselect={false}
        disabled={region === undefined}
        error={problem === 'area' ? t('permits.air.import.area.required') : null}
        onChange={(value) => onChange({ area: value ?? '', resolutions: {}, confirmedReplace: false })}
      />
      {regions.length === 0 && (
        <div style={{ gridColumn: '1 / -1' }}>
          <Banner tone="warning" title={t('permits.air.import.scope.empty')} text={t('permits.air.import.scope.emptyHint')} />
        </div>
      )}
      <Text size="sm" c="dimmed" style={{ gridColumn: '1 / -1' }}>
        {t('permits.air.import.scopeNote')}
      </Text>
    </div>
  );
}
