import { lazy, Suspense, type JSX } from 'react';
import { Button, Code, Skeleton, Stack, Table, Text, Textarea } from '@mantine/core';
import type { MethodologyPublicationDiff } from '@/api/types';
import { t } from '@/shared/i18n';
import { ResizableModal } from '@/shared/ui/ResizableModal';

/** A1-02: поле дати — за `import()` (`D-132`), той самий прийом, що `DocumentHeaderPanel`. */
const DateOnlyInput = lazy(async () => ({
  default: (await import('@/shared/dates/DateInputWithStyles')).DateOnlyInput,
}));

/**
 * Діалоги публікації версії (причина й дата) і diff результатів — окремий лінивий чанк (`D-132`): сторінка скрипта
 * методології стояла на межі 250 КБ, а діалоги потрібні лише після натискання «Publish».
 */
export function PublishVersionDialogs({
  publishing,
  publishReason,
  publishEffectiveFrom,
  publishDiff,
  publishLoading,
  isPending,
  onReason,
  onEffectiveFrom,
  onClosePublishing,
  onCloseDiff,
  onPublish,
}: {
  readonly publishing: { readonly versionId: number } | null;
  readonly publishReason: string;
  readonly publishEffectiveFrom: string;
  readonly publishDiff: MethodologyPublicationDiff | null;
  readonly publishLoading: boolean;
  readonly isPending: boolean;
  readonly onReason: (value: string) => void;
  readonly onEffectiveFrom: (value: string) => void;
  readonly onClosePublishing: () => void;
  readonly onCloseDiff: () => void;
  readonly onPublish: () => void;
}): JSX.Element {
  return (
    <>
      <ResizableModal
        sizeKey="methodology.publish"
        opened={publishing !== null}
        onClose={onClosePublishing}
        title={t('methodologies.publishTitle')}
      >
        <Stack gap="sm">
          <Textarea
            label={t('methodologies.reason')}
            description={t('methodologies.reasonHint')}
            value={publishReason}
            onChange={(event) => onReason(event.currentTarget.value)}
            minRows={3}
            autosize
            data-autofocus
          />

          <Suspense fallback={<Skeleton height="var(--ecr-ctl-height)" />}>
            <DateOnlyInput
              label={t('methodologies.effectiveFrom')}
              description={t('methodologies.effectiveFromHint')}
              value={publishEffectiveFrom}
              onChange={onEffectiveFrom}
            />
          </Suspense>

          <Button
            disabled={publishReason.trim().length === 0 || publishEffectiveFrom.length === 0}
            loading={publishLoading}
            onClick={() => {
              // ⛔ B149 (KIT §6.9): діалог змонтований завжди, «Cancel» причину й дату не скидає — кнопка закритого `Modal`
              // (~200 мс у DOM) публікувала версію вже після відмови.
              if (publishing === null || isPending) return;
              onPublish();
            }}
          >
            {t('methodologies.publish')}
          </Button>
        </Stack>
      </ResizableModal>

      <ResizableModal
        sizeKey="methodology.publishDiff"
        opened={publishDiff !== null}
        onClose={onCloseDiff}
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
}
