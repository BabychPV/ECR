import { Alert, Select, Stack, Text } from '@mantine/core';
import { useQuery } from '@tanstack/react-query';
import { useMemo, useState, type JSX } from 'react';
import { scriptKeys } from './scriptQueryKeys';
import type { MethodologyDraftVersionDto } from '@/api/types';
import { t } from '@/shared/i18n';
import { statusKey } from '@/shared/ui/StatusBadge';
import { AsyncBoundary } from '@/shared/ui/AsyncBoundary';
import { ResizableModal } from '@/shared/ui/ResizableModal';
import { methodologyScript } from './api';
import { ScriptDiffEditor } from './ScriptDiffEditor';
import type { MethodologyScriptDto } from './types';

export interface ScriptCompareModalProps {
  readonly opened: boolean;
  readonly onClose: () => void;
  readonly methodologyId: number;
  /** Версія, що відкрита в редакторі («стало»). */
  readonly currentVersionId: number;
  /** Поточний текст редактора (зі збереженим чи без). */
  readonly currentText: string;
  /** Усі версії методології. */
  readonly versions: readonly MethodologyDraftVersionDto[];
}

/** Варіанти вибору: усі версії, крім відкритої. */
export function compareChoices(
  versions: readonly MethodologyDraftVersionDto[],
  currentVersionId: number,
): { value: string; label: string }[] {
  return versions
    .filter((version) => version.id !== currentVersionId)
    .map((version) => ({
      value: String(version.id),
      label: `${version.versionNumber} · ${t(statusKey('version', version.status))}`,
    }));
}

/**
 * Порівняння тексту поточного скрипта з іншою версією (Monaco diff).
 *
 * ⚠ «Було» — збережений текст обраної версії; «стало» — те, що зараз у
 * редакторі, навіть незбережене: людина порівнює свою роботу, а не стан бази.
 */
export function ScriptCompareModal(props: ScriptCompareModalProps): JSX.Element {
  const { opened, onClose, methodologyId, currentVersionId, currentText, versions } = props;
  const [otherId, setOtherId] = useState<string | null>(null);
  const choices = useMemo(
    () => compareChoices(versions, currentVersionId),
    [versions, currentVersionId],
  );
  const other = otherId === null ? null : Number(otherId);

  const script = useQuery({
    queryKey: scriptKeys.script(other ?? 0),
    queryFn: () => methodologyScript(methodologyId, other ?? 0),
    enabled: opened && other !== null,
  });

  return (
    <ResizableModal
      sizeKey="methodology.scriptCompare"
      opened={opened}
      onClose={onClose}
      title={t('methodologyScript.compare.title')}
      size="xl"
    >
      <Stack gap="sm">
        <Select
          label={t('methodologyScript.compare.pick')}
          placeholder={t('methodologyScript.compare.placeholder')}
          data={choices}
          value={otherId}
          onChange={setOtherId}
          allowDeselect={false}
          disabled={choices.length === 0}
          comboboxProps={{ withinPortal: true }}
        />

        {choices.length === 0 && (
          <Alert color="statusWarning">{t('methodologyScript.compare.noOther')}</Alert>
        )}

        {other !== null && (
          <AsyncBoundary<MethodologyScriptDto>
            isPending={script.isPending}
            error={script.error}
            data={script.data}
            skeleton="table"
            onRetry={() => void script.refetch()}
          >
            {(data) => (
              <Stack gap="xs">
                {data.engine !== 'Script' && (
                  <Text size="sm" c="dimmed">
                    {t('methodologyScript.compare.notScript')}
                  </Text>
                )}
                <ScriptDiffEditor
                  original={data.text}
                  modified={currentText}
                  ariaLabel={t('methodologyScript.compare.title')}
                />
                <Text size="xs" c="dimmed">
                  {t('methodologyScript.compare.legend')}
                </Text>
              </Stack>
            )}
          </AsyncBoundary>
        )}
      </Stack>
    </ResizableModal>
  );
}
