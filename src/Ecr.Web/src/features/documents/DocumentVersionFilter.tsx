import type { JSX } from 'react';
import { MultiSelect } from '@mantine/core';
import { readerOnlyDescription } from '@/shared/ui/FilterBar';
import { t } from '@/shared/i18n';

/**
 * Мультивибір версій шаблону в панелі фільтрів переліку документів — окремий лінивий чанк (`D-132`): `MultiSelect`
 * тягне `OptionsDropdown` (≈7 КБ gzip разом), а чанк переліку стояв на межі 250 КБ.
 */
export function DocumentVersionFilter({
  versionOptions,
  versions,
  onChange,
}: {
  readonly versionOptions: readonly string[];
  readonly versions: readonly string[];
  readonly onChange: (versions: string[]) => void;
}): JSX.Element {
  return (
      <MultiSelect
        size="xs"
        miw={160}
        maw={260}
        label={t('documents.versionColumn')}
        description={t('documents.versionFilterHint')}
        styles={readerOnlyDescription}
        placeholder={versions.length === 0 ? t('documents.versionAll') : undefined}
        data={[...new Set([...versionOptions, ...versions])].map((version) => ({
          value: version,
          label: t('documents.versionValue', { version }),
        }))}
        value={[...versions]}
        onChange={onChange}
        clearable
        data-version-filter=""
      />
  );
}
