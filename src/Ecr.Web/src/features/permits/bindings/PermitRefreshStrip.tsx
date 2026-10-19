import { useState, type JSX } from 'react';
import { Badge, Button, Group, Text } from '@mantine/core';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import type { DocumentTableDto } from '@/api/types';
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

  return (
    <section aria-label={t('permits.sheetBinding.doc.region')} data-testid="permit-refresh-strip">
      {bound.map(({ code, table, tableName }) => (
        <Group key={code} gap="sm" mb="xs" wrap="nowrap" align="center">
          <Badge variant="light" data-testid="from-permit-badge">
            {t('permits.sheetBinding.doc.fromPermit')}
          </Badge>
          <Text size="sm" style={{ flex: 1 }}>
            {t('permits.sheetBinding.doc.tableLine', { name: tableName })}
          </Text>
          <Button
            size="xs"
            variant="default"
            disabled={readOnly}
            title={readOnly ? t('permits.sheetBinding.doc.readOnly') : undefined}
            loading={refreshLoading && refresh.variables?.code === code}
            onClick={() => refresh.mutate({ code, tableName, sheetDefId: table.sheetDefId })}
          >
            {t('permits.sheetBinding.doc.refresh')}
          </Button>
        </Group>
      ))}

      {outcome === null ? null : (
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
      )}
    </section>
  );
}
