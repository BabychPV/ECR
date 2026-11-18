import { lazy, Suspense, useState, type JSX } from 'react';
import { Button, Code, Skeleton, Stack, Table, Text, Textarea } from '@mantine/core';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { queryKeys } from '@/api/queryKeys';
import type { MethodologyPublicationDiff, PublishMethodologyRequest } from '@/api/types';
import { usePendingLoading } from '@/features/common/usePendingLoading';
import { t } from '@/shared/i18n';
import { ResizableModal } from '@/shared/ui/ResizableModal';
import { showDone } from '@/shared/ui/notify';
import { publishMethodologyVersion } from './api';
import { showPublishError } from './publishError';

/** A1-02: поле дати — за `import()` (`D-132`), той самий прийом, що `DocumentHeaderPanel`. */
const DateOnlyInput = lazy(async () => ({
  default: (await import('@/shared/dates/DateInputWithStyles')).DateOnlyInput,
}));

export interface PublishVersionFlow {
  /** Відкриває діалог публікації версії. */
  readonly open: (versionId: number) => void;
  /** Діалоги (публікація й diff результатів) — вставити в дерево сторінки. */
  readonly dialogs: JSX.Element;
}

/**
 * Публікація версії методології: діалог причини й дати, виклик і показ diff
 * результатів. Спільна для сторінки версій і сторінки скрипта.
 *
 * ⛔ Причина й дата обов'язкові на сервері (`ECR-CALC-0422`, ФВ-14.7); кнопка
 * вимкнена, доки вони порожні, — не з ввічливості, а щоб не вести на гарантовану
 * відмову. Сама заборона лишається доменною.
 *
 * ⛔ Diff РЕЗУЛЬТАТІВ, не тексту формул (ФВ-9.6): змінений рядок виразу не каже
 * нічого, змінена на 4 % емісія каже все.
 */
export function usePublishVersion(
  methodologyId: number,
  onPublished?: (versionId: number) => void,
): PublishVersionFlow {
  const queryClient = useQueryClient();
  const [publishing, setPublishing] = useState<{ versionId: number } | null>(null);
  const [publishReason, setPublishReason] = useState('');
  const [publishEffectiveFrom, setPublishEffectiveFrom] = useState('');
  const [publishDiff, setPublishDiff] = useState<MethodologyPublicationDiff | null>(null);

  const publish = useMutation({
    mutationFn: () =>
      publishMethodologyVersion(methodologyId, publishing?.versionId ?? 0, {
        changeReason: publishReason,
        // ⚠ `date`, не `Date`: `toISOString()` іде через UTC і ввечері
        // зсуває дату на добу назад.
        effectiveFrom: publishEffectiveFrom,
      } satisfies PublishMethodologyRequest),
    onSuccess: async (diff) => {
      const publishedId = publishing?.versionId ?? 0;
      await queryClient.invalidateQueries({
        queryKey: queryKeys.methodologies.versionsOf(methodologyId),
      });
      setPublishing(null);
      setPublishReason('');
      setPublishEffectiveFrom('');
      setPublishDiff(diff);
      showDone(t('methodologies.published'));
      onPublished?.(publishedId);
    },

    // ⚠ Публікація падає з переліком конкретних проблем — немає золотого
    // тесту (ФВ-9.12), незайнята дата, цикл формул тощо (`ECR-CALC-0422`,
    // `ECR-CALC-0409`). `showPublishError` показує ТЕКСТ відмови сервера, а не
    // узагальнене «не вдалося». ⚠ F-15/B-12: під назвою — перелік проблем.
    onError: showPublishError,
  });

  const publishLoading = usePendingLoading(publish.isPending);

  const dialogs = (
    <>
      <ResizableModal
        sizeKey="methodology.publish"
        opened={publishing !== null}
        onClose={() => setPublishing(null)}
        title={t('methodologies.publishTitle')}
      >
        <Stack gap="sm">
          <Textarea
            label={t('methodologies.reason')}
            description={t('methodologies.reasonHint')}
            value={publishReason}
            onChange={(event) => setPublishReason(event.currentTarget.value)}
            minRows={3}
            autosize
            data-autofocus
          />

          <Suspense fallback={<Skeleton height="var(--ecr-ctl-height)" />}>
            <DateOnlyInput
              label={t('methodologies.effectiveFrom')}
              description={t('methodologies.effectiveFromHint')}
              value={publishEffectiveFrom}
              onChange={setPublishEffectiveFrom}
            />
          </Suspense>

          <Button
            disabled={publishReason.trim().length === 0 || publishEffectiveFrom.length === 0}
            loading={publishLoading}
            onClick={() => {
              // ⛔ B149 (KIT §6.9): діалог змонтований завжди, «Cancel» причину й дату не скидає — кнопка закритого `Modal`
              // (~200 мс у DOM) публікувала версію вже після відмови.
              if (publishing === null || publish.isPending) return;
              publish.mutate();
            }}
          >
            {t('methodologies.publish')}
          </Button>
        </Stack>
      </ResizableModal>

      <ResizableModal
        sizeKey="methodology.publishDiff"
        opened={publishDiff !== null}
        onClose={() => setPublishDiff(null)}
        title={t('methodologies.diffTitle')}
        size="lg"
      >
        {publishDiff !== null && (
          <Stack gap="sm">
            <Text size="sm">
              {t('methodologies.diffNumeric')}: {publishDiff.numeric.before} → {publishDiff.numeric.after}
            </Text>
            <Text size="sm">
              {t('methodologies.diffCalendar')}: {publishDiff.calendar.before} → {publishDiff.calendar.after}
            </Text>

            <Text fw={600}>{t('methodologies.diffChanges')}</Text>
            {publishDiff.changes.length === 0 ? (
              <Text size="sm" c="dimmed">
                {t('methodologies.diffNone')}
              </Text>
            ) : (
              <Table striped withTableBorder>
                <Table.Thead>
                  <Table.Tr>
                    <Table.Th>{t('methodologies.testCode')}</Table.Th>
                    <Table.Th>{t('methodologies.output')}</Table.Th>
                    <Table.Th>{t('methodologies.before')}</Table.Th>
                    <Table.Th>{t('methodologies.after')}</Table.Th>
                  </Table.Tr>
                </Table.Thead>
                <Table.Tbody>
                  {publishDiff.changes.map((change) => (
                    <Table.Tr
                      key={`${change.testCode}:${change.outputCode}:${String(change.substanceEntryId)}`}
                    >
                      <Table.Td>{change.testCode}</Table.Td>
                      <Table.Td>{change.outputCode}</Table.Td>
                      <Table.Td>{change.before ?? '—'}</Table.Td>
                      <Table.Td>{change.after}</Table.Td>
                    </Table.Tr>
                  ))}
                </Table.Tbody>
              </Table>
            )}

            {(publishDiff.warnings ?? []).length > 0 && (
              <>
                <Text fw={600}>{t('methodologies.warnings')}</Text>
                <Code block>{(publishDiff.warnings ?? []).join('\n')}</Code>
              </>
            )}
          </Stack>
        )}
      </ResizableModal>
    </>
  );

  return { open: (versionId) => setPublishing({ versionId }), dialogs };
}
