import { useState, type JSX } from 'react';
import { Button, Code, Group, Modal, Stack, Text, Textarea } from '@mantine/core';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import type { MethodologyCategoryRuleDto } from '@/api/types';
import { queryKeys } from '@/api/queryKeys';
import { t } from '@/shared/i18n';
import { AsyncBoundary } from '@/shared/ui/AsyncBoundary';
import { problemText } from '@/shared/ui/problemText';
import { Timestamp } from '@/shared/ui/Timestamp';
import { showApiError, showDone } from '@/shared/ui/notify';
import { usePendingLoading } from '@/features/common/usePendingLoading';
import {
  deleteMethodologyCategoryRule,
  methodologyCategoryRule,
  saveMethodologyCategoryRule,
} from './api';

interface Props {
  readonly methodologyId: number;
  readonly versionId: number;
  readonly editable: boolean;
}

/**
 * Правило категорії константи версії (L-2, RC14-F): одне вираження на версію, що дає ключ категорії
 * (`Diesel`, `Loc_BeforeMR_B`) для кожного рядка документа. Без правила константи з кількома
 * категоріями неоднозначні (`constantAmbiguous`) — тому порожній стан каже це прямо.
 *
 * ⚠ Власний `import()`-чанк: сторінка методологій стоїть біля бюджету (`D-132`).
 * ⚠ Порожній/числовий/нерозібраний вираз відхиляє сервер (`422`). Причину показуємо і тостом
 * (`showApiError`), і рядком `role=alert` під полем (Z-4): тост зникає, а правку виразу це не заважає —
 * плюс підказка про лапки (текстові значення — в одинарних лапках).
 */
export function MethodologyCategoryRulePanel({ methodologyId, versionId, editable }: Props): JSX.Element {
  const queryClient = useQueryClient();
  const [draft, setDraft] = useState<string | null>(null);
  const [confirmingDelete, setConfirmingDelete] = useState(false);

  const rule = useQuery({
    queryKey: queryKeys.methodologies.categoryRule(versionId),
    queryFn: () => methodologyCategoryRule(methodologyId, versionId),
  });

  const refresh = (): Promise<void> =>
    queryClient.invalidateQueries({ queryKey: queryKeys.methodologies.categoryRule(versionId) });

  const save = useMutation({
    mutationFn: (expression: string) => saveMethodologyCategoryRule(methodologyId, versionId, { expression }),
    onSuccess: async () => {
      await refresh();
      setDraft(null);
      showDone(t('methodologies.categoryRuleSaved'));
    },
    onError: showApiError,
  });

  const remove = useMutation({
    mutationFn: () => deleteMethodologyCategoryRule(methodologyId, versionId),
    onSuccess: async () => {
      await refresh();
      setConfirmingDelete(false);
      showDone(t('methodologies.categoryRuleDeleted'));
    },
    onError: showApiError,
  });

  const saveLoading = usePendingLoading(save.isPending);
  const removeLoading = usePendingLoading(remove.isPending);

  const current = rule.data?.expression ?? null;

  // N4-02: чернетка живе лише поки правка дозволена. Якщо `editable` згас (інша версія не-чернетка, право
  // знято), відкрита форма не лишається з активною «Save» — показується збережене правило.
  const drafting = editable && draft !== null;

  return (
    <>
      <Group justify="space-between">
        <Text fw={600}>{t('methodologies.categoryRule')}</Text>
        {editable && draft === null && (
          <Group gap="xs">
            {current !== null && (
              <Button size="xs" variant="default" onClick={() => setConfirmingDelete(true)}>
                {t('methodologies.categoryRuleDelete')}
              </Button>
            )}
            <Button size="xs" variant="default" onClick={() => setDraft(current ?? '')}>
              {current === null ? t('methodologies.categoryRuleSet') : t('methodologies.categoryRuleEdit')}
            </Button>
          </Group>
        )}
      </Group>

      <AsyncBoundary<MethodologyCategoryRuleDto>
        isPending={rule.isPending}
        error={rule.error}
        data={rule.data}
        isEmpty={(dto) => dto.expression === null && !drafting}
        emptyTitle={t('methodologies.categoryRuleNone')}
        emptyHint={t('methodologies.categoryRuleNoneHint')}
        skeleton="table"
        onRetry={() => void rule.refetch()}
      >
        {(dto) =>
          drafting ? (
            <Stack gap="xs">
              <Textarea
                label={t('methodologies.categoryRuleExpression')}
                description={t('methodologies.categoryRuleExpressionHint')}
                value={draft}
                autosize
                minRows={3}
                styles={{ input: { fontFamily: 'monospace' } }}
                onChange={(event) => {
                  setDraft(event.currentTarget.value);
                  if (save.isError) save.reset();
                }}
              />
              {save.isError && <CategoryRuleError error={save.error} />}
              <Group justify="flex-end">
                <Button
                  variant="default"
                  onClick={() => {
                    setDraft(null);
                    save.reset();
                  }}
                >
                  {t('common.cancel')}
                </Button>
                <Button
                  loading={saveLoading}
                  disabled={draft.trim() === ''}
                  onClick={() => {
                    // Mantine не блокує кнопку, доки не спрацював поріг `loading` (usePendingLoading).
                    if (save.isPending) return;
                    save.mutate(draft.trim());
                  }}
                >
                  {t('common.save')}
                </Button>
              </Group>
            </Stack>
          ) : (
            <Stack gap="xs">
              <Code block>{dto.expression}</Code>
              {dto.updatedAt !== null && (
                <Text size="xs" c="dimmed">
                  {t('methodologies.categoryRuleUpdatedAt')} <Timestamp value={dto.updatedAt} />
                </Text>
              )}
            </Stack>
          )
        }
      </AsyncBoundary>

      <Modal
        opened={editable && confirmingDelete}
        onClose={() => setConfirmingDelete(false)}
        title={t('methodologies.categoryRuleDelete')}
      >
        <Stack gap="sm">
          <Text size="sm">{t('methodologies.categoryRuleDeleteHint')}</Text>
          <Group justify="flex-end">
            <Button variant="default" onClick={() => setConfirmingDelete(false)}>
              {t('common.cancel')}
            </Button>
            <Button
              color="statusError"
              loading={removeLoading}
              onClick={() => {
                if (remove.isPending) return;
                remove.mutate();
              }}
            >
              {t('common.delete')}
            </Button>
          </Group>
        </Stack>
      </Modal>
    </>
  );
}

/** Рядок помилки збереження правила: причина (як у тості) і підказка про лапки. */
function CategoryRuleError({ error }: { readonly error: unknown }): JSX.Element {
  const shown = problemText(error);

  return (
    <Stack gap="xs" role="alert">
      <Text size="sm" c="statusError">
        {shown.detail ?? shown.title}
      </Text>
      <Text size="xs" c="dimmed">
        {t('methodologies.categoryRuleErrorHint')}
      </Text>
    </Stack>
  );
}
