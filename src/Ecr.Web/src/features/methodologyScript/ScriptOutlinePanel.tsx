import { scriptKeys } from './scriptQueryKeys';
import { Stack, Text } from '@mantine/core';
import { useQuery } from '@tanstack/react-query';
import { useMemo, type JSX } from 'react';
import { methodologyCoverage } from '@/features/methodologies/api';
import { t } from '@/shared/i18n';
import { outputsWithoutColumn } from './scriptCoverage';
import { ScriptOutlineView } from './ScriptOutlineView';
import type { ScriptOutlineDto } from './types';

export interface ScriptOutlinePanelProps {
  readonly methodologyId: number;
  readonly versionId: number;
  readonly outline: ScriptOutlineDto | null;
}

/** «Що читає / що пише»: входи, виходи (з позначкою «немає колонки»), таблиці, імпорти. */
export function ScriptOutlinePanel({
  methodologyId,
  versionId,
  outline,
}: ScriptOutlinePanelProps): JSX.Element {
  // ⚠ Ключ покриття версії «виходи → колонки» (`scriptKeys.coverage`): збереження скрипта його інвалідує.
  const coverage = useQuery({
    queryKey: scriptKeys.coverage(methodologyId, versionId),
    queryFn: () => methodologyCoverage(methodologyId, versionId),
  });

  const missing = useMemo(
    () => outputsWithoutColumn(outline, coverage.data),
    [outline, coverage.data],
  );

  if (outline === null) return <Text c="dimmed">{t('common.loading')}</Text>;

  return (
    <Stack gap="sm">
      <ScriptOutlineView outline={outline} outputsWithoutColumn={missing} />
      {missing.size > 0 && (
        <Text size="xs" c="dimmed">
          {t('methodologyScript.outline.noColumnHint')}
        </Text>
      )}
    </Stack>
  );
}
