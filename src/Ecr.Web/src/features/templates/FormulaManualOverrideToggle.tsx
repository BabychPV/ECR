import { useState, type JSX } from 'react';
import { Checkbox } from '@mantine/core';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import { queryKeys } from '@/api/queryKeys';
import type { components } from '@/api/schema';
import { t } from '@/shared/i18n';
import { ConfirmModal } from '@/shared/ui/ConfirmModal';
import { showApiError } from '@/shared/ui/notify';

type Result = components['schemas']['FormulaManualOverrideResult'];

/**
 * ✎ D-461: `PUT /template-versions/{id}/formulas/{formulaId}/manual-override` `{allow, dryRun}`.
 *
 * ⚠ Працює й на ОПУБЛІКОВАНІЙ версії (нова версія не створюється) — право `Template.Edit`.
 */
export function setFormulaManualOverride(
  templateVersionId: number,
  formulaId: number,
  allow: boolean,
  dryRun: boolean,
): Promise<Result> {
  return apiFetch<Result>(
    `/api/v1/template-versions/${String(templateVersionId)}/formulas/${String(formulaId)}/manual-override`,
    {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ allow, dryRun }),
    },
  );
}

/**
 * Перемикач «Дозволити ручне значення» формули колонки чи комірки (D-461).
 *
 * ⚠ Вимкнення: спершу сухий прогін (`dryRun`) — якщо в документах версії вже є ручні значення замість цієї
 * формули, людина бачить, скільки їх, і підтверджує: наявні лишаються (видно як ручні, можна повернути формулу),
 * нові — заборонено. Без ручних значень — вимикається одразу.
 *
 * ⚠ Після зміни перечитуються структура версії (стан прапорця) і всі зрізи документів (`table-slice`): у них
 * `formulaOverride.overridableColumns/Cells` — що зараз можна замінити.
 */
export function FormulaManualOverrideToggle({
  templateVersionId,
  formulaId,
  allow,
  disabled,
}: {
  templateVersionId: number;
  formulaId: number;
  allow: boolean;
  disabled: boolean;
}): JSX.Element {
  const queryClient = useQueryClient();
  const [confirm, setConfirm] = useState<{ documents: number; cells: number } | null>(null);

  const apply = useMutation({
    mutationFn: (next: boolean) => setFormulaManualOverride(templateVersionId, formulaId, next, false),
    onSuccess: async () => {
      setConfirm(null);
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: queryKeys.templates.version(templateVersionId) }),
        queryClient.invalidateQueries({ queryKey: queryKeys.slices.all() }),
      ]);
    },
    onError: (error) => showApiError(error),
  });

  const probe = useMutation({
    mutationFn: () => setFormulaManualOverride(templateVersionId, formulaId, false, true),
    onSuccess: (result) => {
      if (result.overriddenCells > 0) {
        setConfirm({ documents: result.overriddenDocuments, cells: result.overriddenCells });
        return;
      }

      apply.mutate(false);
    },
    onError: (error) => showApiError(error),
  });

  const busy = apply.isPending || probe.isPending;

  return (
    <>
      <Checkbox
        size="xs"
        checked={allow}
        disabled={disabled || busy}
        label={t('formulas.allowManualOverride')}
        title={t('formulas.allowManualOverrideHint')}
        onChange={(event) => {
          if (event.currentTarget.checked) apply.mutate(true);
          else probe.mutate();
        }}
        data-formula-manual-override={formulaId}
      />
      <ConfirmModal
        opened={confirm !== null}
        title={t('formulas.disableManualOverrideTitle')}
        text={t('formulas.disableManualOverrideBody', {
          documents: confirm?.documents ?? 0,
          cells: confirm?.cells ?? 0,
        })}
        verb={t('formulas.disableManualOverrideVerb')}
        danger={false}
        isPending={apply.isPending}
        onConfirm={() => apply.mutate(false)}
        onClose={() => setConfirm(null)}
      />
    </>
  );
}
