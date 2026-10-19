import { useState, type JSX } from 'react';
import { Badge, Button, Group, Text } from '@mantine/core';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import type { DocumentTableDto } from '@/api/types';
import { usePendingLoading } from '@/features/common/usePendingLoading';
import { localized } from '@/shared/i18n/localized';
import { t } from '@/shared/i18n';
import { Banner } from '@/shared/ui/Banner';
import { showApiError } from '@/shared/ui/notify';
import { listPermitBindings } from '../api/bindingsApi';
import { refreshPermitBinding } from '../api/bindingsRuntimeApi';
import { getProjectDocumentTemplate } from '../api/documentTemplateApi';
import { refreshSummary, warningKind, type RefreshSummary } from './bindingModel';
import { warningText } from './bindingText';
import { bindingsKey } from './bindingsKeys';

export interface PermitRefreshStripProps {
  readonly documentId: number;
  readonly periodKey: number;
  readonly projectId: number | null;
  /** Таблиці активного аркуша документа. */
  readonly tables: readonly DocumentTableDto[];
  /** Документ лише для читання (стан аркуша, закритий період, блокування, вузький екран). */
  readonly readOnly: boolean;
  /** Право `Template.View`: без нього перелік прив'язок недоступний, і смуги немає. */
  readonly canView: boolean;
}

interface Outcome {
  readonly tableName: string;
  readonly summary: RefreshSummary;
}

/**
 * «З дозволу» + «Оновити з дозволу» над сіткою документа (Permits, PM-11).
 *
 * ⚠ Документ не знає своїх прив'язок: перелік береться з версії шаблону ПРОЄКТУ (`document-template`), а таблиці
 * зіставляються за кодом. Документ на старій версії однаково знайде свою прив'язку за кодом: сервер за
 * `POST …/refresh` бере її з версії документа (`404`, якщо такої немає — показується відмова).
 * ⛔ Смуга мовчить, коли даних немає: відсутність права (`403`), прив'язок немає, шаблон не віддано.
 * ⚠ Права: дія вимкнена для документа лише для читання; чи можна додавати рядки саме в цю таблицю, вирішує
 * сервер (`403 ECR-ACCS-0403`) — відмова показується локалізованим текстом.
 */
export function PermitRefreshStrip({ documentId, periodKey, projectId, tables, readOnly, canView }: PermitRefreshStripProps): JSX.Element | null {
  const queryClient = useQueryClient();
  const [outcome, setOutcome] = useState<Outcome | null>(null);

  const template = useQuery({
    queryKey: ['projects', projectId, 'document-template'],
    queryFn: () => getProjectDocumentTemplate(projectId ?? 0),
    enabled: canView && projectId !== null,
    retry: false,
  });
  const versionId = template.data?.templateVersionId;

  const bindings = useQuery({
    queryKey: bindingsKey(versionId ?? 0),
    queryFn: () => listPermitBindings(versionId ?? 0),
    enabled: versionId !== undefined,
    retry: false,
  });

  const refresh = useMutation({
    mutationFn: ({ code }: { code: string; tableName: string }) => refreshPermitBinding(documentId, code, periodKey),
    onSuccess: async (result, variables) => {
      setOutcome({ tableName: variables.tableName, summary: refreshSummary(result) });
      // Сітка тримає зріз таблиці під власним ключем: без скидання нові рядки з'явилися б лише після перезавантаження.
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: ['table-slice'] }),
        queryClient.invalidateQueries({ queryKey: ['document-tables', documentId] }),
      ]);
    },
    onError: (error: unknown) => {
      setOutcome(null);
      showApiError(error);
    },
  });

  const refreshLoading = usePendingLoading(refresh.isPending);

  if (!canView) return null;

  const bound = (bindings.data?.items ?? [])
    .filter((binding) => binding.mode === 'Rows' && binding.isActive)
    .flatMap((binding) => {
      const table = tables.find((candidate) => candidate.tableCode === binding.sheetTableCode);

      return table === undefined ? [] : [{ binding, tableName: localized(table.tableNameL10n) || table.tableCode }];
    });

  if (bound.length === 0) return null;

  return (
    <section aria-label={t('permits.sheetBinding.doc.region')} data-testid="permit-refresh-strip">
      {bound.map(({ binding, tableName }) => (
        <Group key={binding.code} gap="sm" mb="xs" wrap="nowrap" align="center">
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
            loading={refreshLoading && refresh.variables?.code === binding.code}
            onClick={() => refresh.mutate({ code: binding.code, tableName })}
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
