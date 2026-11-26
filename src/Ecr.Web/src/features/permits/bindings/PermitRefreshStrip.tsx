import { NoticeSlot } from '@/features/documents/notices/NoticeSlot';
import { useState, type JSX } from 'react';
import { Badge, Button, Group, Popover, Stack, Text, UnstyledButton, VisuallyHidden } from '@mantine/core';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import type { DocumentTableDto } from '@/api/types';
import { DisabledReason } from '@/features/common/DisabledReason';
import { invalidateSlices } from '@/features/grid/sliceCache';
import { usePendingLoading } from '@/features/common/usePendingLoading';
import { localized } from '@/shared/i18n/localized';
import { t } from '@/shared/i18n';
import { Banner } from '@/shared/ui/Banner';
import { showApiError } from '@/shared/ui/notify';
import { refreshPermitBinding } from '../api/bindingsRuntimeApi';
import { RowsMode, refreshSummary, warningKind, type RefreshSummary } from './bindingModel';
import { warningText } from './bindingText';

export interface PermitRefreshStripProps {
  readonly documentId: number;
  readonly periodKey: number;
  /** Таблиці активного аркуша; прив'язку несе сама відповідь `…/tables` (`permitBinding`, PM-11b). */
  readonly tables: readonly DocumentTableDto[];
  /** Документ лише для читання (стан аркуша, закритий період, блокування, вузький екран). */
  readonly readOnly: boolean;
}

interface Outcome {
  readonly tableName: string;
  readonly summary: RefreshSummary;
}

/**
 * «З дозволу» + «Оновити з дозволу» над сіткою документа (Permits, PM-11).
 *
 * ⚠ Жодних додаткових запитів: чи прив'язана таблиця, сервер каже в `DocumentTableDto.permitBinding`
 * (код, режим, тип дозволу) разом зі списком таблиць, який сторінка вже читає. Тому смуга доступна кожному, хто
 * бачить документ, а не лише `Template.View`. Показується для режиму `Rows` (у `Formulas` рядків немає).
 * ⚠ Права: дія вимкнена для документа лише для читання; чи можна додавати рядки саме в цю таблицю, вирішує
 * сервер (`403 ECR-ACCS-0403`) — відмова показується локалізованим текстом.
 */
export function PermitRefreshStrip({ documentId, periodKey, tables, readOnly }: PermitRefreshStripProps): JSX.Element | null {
  const queryClient = useQueryClient();
  const [outcome, setOutcome] = useState<Outcome | null>(null);
  const refresh = useMutation({
    mutationFn: ({ code }: { code: string; tableName: string; sheetDefId: number }) => refreshPermitBinding(documentId, code, periodKey),
    onSuccess: async (result, variables) => {
      setOutcome({ tableName: variables.tableName, summary: refreshSummary(result) });
      // Зрізи саме цього аркуша (CL-02), а не весь префікс ['table-slice']: інакше йдуть усі змонтовані зрізи.
      await Promise.all([
        invalidateSlices(queryClient, { documentId, periodKey, sheetDefId: variables.sheetDefId }),
        queryClient.invalidateQueries({ queryKey: ['document-tables', documentId] }),
      ]);
    },
    onError: (error: unknown) => {
      setOutcome(null);
      showApiError(error);
    },
  });

  const refreshLoading = usePendingLoading(refresh.isPending);

  const bound = tables.flatMap((table) =>
    table.permitBinding != null && table.permitBinding.mode === RowsMode
      ? [{ code: table.permitBinding.code, table, tableName: localized(table.tableNameL10n) || table.tableCode }]
      : [],
  );
  if (bound.length === 0) return null;

  // ✎ DOCUX L8 (№11): «з дозволу» — ЧИП у рядку стану, а не рядок на кожну таблицю над сіткою: одна таблиця — чип і
  // «Refresh from permit» поруч; кілька — чип зі спливаючим переліком (рядок на таблицю, своя кнопка).
  const row = ({ code, table, tableName }: (typeof bound)[number]): JSX.Element => (
    <Group key={code} gap="xs" wrap="nowrap" align="center">
      <VisuallyHidden>{t('permits.sheetBinding.doc.tableLine', { name: tableName })}</VisuallyHidden>
      {bound.length > 1 && (
        <Text size="sm" style={{ flex: 1 }} aria-hidden="true">
          {tableName}
        </Text>
      )}
      {/* ⛔ U-6 (аудит 2026-11): `DisabledReason` — кнопка у фокусі, причина в `aria-describedby`, клік гаситься. */}
      <DisabledReason reason={readOnly ? t('permits.sheetBinding.doc.readOnly') : null}>
        <Button
          size="xs"
          variant="subtle"
          title={t('permits.sheetBinding.doc.tableLine', { name: tableName })}
          loading={refreshLoading && refresh.variables?.code === code}
          onClick={() => refresh.mutate({ code, tableName, sheetDefId: table.sheetDefId })}
        >
          {t('permits.sheetBinding.doc.refresh')}
        </Button>
      </DisabledReason>
    </Group>
  );
  const chip = (
    <Badge variant="light" data-testid="from-permit-badge">
      {bound.length > 1 ? `${t('permits.sheetBinding.doc.fromPermit')} (${String(bound.length)})` : t('permits.sheetBinding.doc.fromPermit')}
    </Badge>
  );

  return (
    <section aria-label={t('permits.sheetBinding.doc.region')} data-testid="permit-refresh-strip" className="ecr-permit-chip">
      {bound.length === 1 ? (
        <Group gap="xs" wrap="nowrap" align="center">
          {chip}
          {bound.map(row)}
        </Group>
      ) : (
        <Popover position="bottom-start" withArrow shadow="md" width={360}>
          <Popover.Target>
            <UnstyledButton aria-label={t('permits.sheetBinding.doc.region')} data-testid="from-permit-menu">
              {chip}
            </UnstyledButton>
          </Popover.Target>
          <Popover.Dropdown>
            <Stack gap="xs">{bound.map(row)}</Stack>
          </Popover.Dropdown>
        </Popover>
      )}

      {outcome === null ? null : (
        // ✎ DOCUX L7 (пріоритет 8): підсумок оновлення — у рядку повідомлень; подробиці — у Status.
        <NoticeSlot
          id="permit-refresh-result"
          priority={8}
          tone={outcome.summary.warnings.length > 0 || outcome.summary.keptKeys.length > 0 ? 'warning' : 'info'}
          text={
            outcome.summary.unchanged
              ? t('permits.sheetBinding.doc.unchanged', { name: outcome.tableName })
              : t('permits.sheetBinding.doc.result', { name: outcome.tableName, added: outcome.summary.added, updated: outcome.summary.updated })
          }
          onDismiss={() => setOutcome(null)}
          testId="notice-permit-refresh"
        >
        <Banner
          tone={outcome.summary.warnings.length > 0 || outcome.summary.keptKeys.length > 0 ? 'warning' : 'success'}
          testId="permit-refresh-result"
          dismiss={{ label: t('permits.sheetBinding.doc.dismiss'), onDismiss: () => setOutcome(null) }}
          text={
            <>
              <Text size="sm">
                {outcome.summary.unchanged
                  ? t('permits.sheetBinding.doc.unchanged', { name: outcome.tableName })
                  : t('permits.sheetBinding.doc.result', {
                      name: outcome.tableName,
                      added: outcome.summary.added,
                      updated: outcome.summary.updated,
                    })}
              </Text>
              {outcome.summary.keptKeys.length > 0 ? (
                <Text size="sm">{t('permits.sheetBinding.doc.kept', { count: outcome.summary.keptKeys.length, keys: outcome.summary.keptKeys.join(', ') })}</Text>
              ) : null}
              {outcome.summary.warnings.map((warning) => (
                <Text size="sm" key={`${warning.code}:${warning.text}`}>
                  {warningText(warningKind(warning))}
                </Text>
              ))}
            </>
          }
        />
        </NoticeSlot>
      )}
    </section>
  );
}
