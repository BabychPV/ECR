import { useState, type JSX } from 'react';
import { Badge, Button, Group, Loader, NumberInput, Stack, Tabs, Text } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useParams } from 'react-router-dom';
import { EcrApiError, apiEnqueue, apiFetch } from '@/api/client';
import type {
  DocumentPeriodRequest,
  DocumentSummary,
  DocumentTableDto,
  ExportRequest,
  SheetWorkflowRequest,
  ValidationMessageDto,
} from '@/api/types';
import { DocumentGrid } from '@/features/grid/DocumentGrid';
import { can, useSession } from '@/shared/session/useSession';
import { localized } from '@/shared/i18n/localized';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { PageHeader } from '@/shared/ui/PageHeader';
import { t } from '@/shared/i18n';

/**
 * Екран документа: вибір періоду, вкладки аркушів, таблиці, робочий процес.
 *
 * ⚠ Період — не фільтр показу, а **частина адреси даних**: екземпляри таблиць
 * і стан затвердження існують окремо на кожен період (R-A6). Тому зміна
 * періоду перечитує все, а не ховає рядки.
 *
 * ⛔ Таблиці беруться з `GET /api/v1/documents/{id}/tables`. До аудиту
 * (`A7-05`) екран чекав, що `GET /api/v1/documents/{id}` віддасть аркуші з
 * `tableInstanceId`, а той віддає `DocumentSummary` — бізнес-ключ і зведений
 * стан. Grid отримував `undefined` замість екземпляра таблиці.
 */
export function DocumentPage(): JSX.Element {
  const { id } = useParams();
  const documentId = Number(id);
  const queryClient = useQueryClient();
  const session = useSession();

  const [periodKey, setPeriodKey] = useState<number>(currentPeriodKey);
  const [sheet, setSheet] = useState<string | null>(null);

  const summary = useQuery({
    queryKey: ['document', documentId, periodKey],
    queryFn: () =>
      apiFetch<DocumentSummary>(`/api/v1/documents/${documentId}?periodKey=${periodKey}`),
  });

  const tables = useQuery({
    queryKey: ['document-tables', documentId, periodKey],
    queryFn: () =>
      apiFetch<DocumentTableDto[]>(
        `/api/v1/documents/${documentId}/tables?periodKey=${periodKey}`,
      ),
  });

  const validate = useMutation({
    mutationFn: () =>
      apiFetch<{ messages: ValidationMessageDto[] }>(
        `/api/v1/documents/${documentId}/validate`,
        {
          method: 'POST',
          // ⚠ Період — у ТІЛІ. До `A7-28` сервер читав його з рядка запиту, і
          // валідація мовчки йшла по періоду 0, відповідаючи «помилок немає».
          body: JSON.stringify({ periodKey } satisfies DocumentPeriodRequest),
        },
      ),
    onSuccess: (result) => {
      const errors = result.messages.filter((message) => message.severity === 'Error');

      notifications.show({
        color: errors.length === 0 ? 'green' : 'red',
        message:
          errors.length === 0
            ? t('document.validationClean')
            : t('document.validationErrors', { count: errors.length }),
      });
    },
  });

  const submit = useMutation({
    mutationFn: (sheetDefId: number) =>
      apiFetch(`/api/v1/documents/${documentId}/submit`, {
        method: 'POST',
        body: JSON.stringify({ sheetDefId, periodKey } satisfies SheetWorkflowRequest),
      }),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['document', documentId, periodKey] });
      notifications.show({ color: 'green', message: t('document.submitted') });
    },
    onError: (error) => {
      // ⚠ Причина показується як є: Submit при осиротілих рядках
      // (ECR-SUB-4221) — це не «помилка сервера», а перелік того, що треба
      // виправити.
      notifications.show({
        color: 'red',
        message: error instanceof EcrApiError ? error.message : String(error),
      });
    },
  });

  const exportBook = useMutation({
    mutationFn: () =>
      apiEnqueue(`/api/v1/documents/${documentId}/export`, {
        includeFormulas: true,
        includeStyles: true,
        language: session.data?.language ?? 'en',
        periodKey,
      } satisfies ExportRequest),
    onSuccess: (job) => {
      // ⚠ Довга операція повертає 202 з jobId; прогрес видно на екрані задач.
      notifications.show({ message: t('document.exportQueued', { job: job.jobId }) });
    },
  });

  if (summary.isPending || tables.isPending) return <Loader />;

  if (summary.isError || summary.data === undefined) {
    return <ErrorAlert error={summary.error} />;
  }

  const sheets = groupBySheet(tables.data ?? []);
  const active = sheets.find((s) => s.code === sheet) ?? sheets[0];

  // ⚠ Стан береться з `SheetStates` документа за КОДОМ аркуша: скалярного
  // статусу документа не існує (D-93) — аркуші за один період бувають у
  // різних станах одночасно.
  const state = active === undefined ? '' : (summary.data.sheetStates[active.code] ?? 'Draft');
  const readOnly = state === 'Submitted' || state === 'Approved';

  return (
    <Stack>
      <PageHeader
        title={summary.data.businessKey}
        actions={
          <Group gap="xs">
            <NumberInput
              size="xs"
              w={130}
              value={periodKey}
              onChange={(value) => setPeriodKey(typeof value === 'number' ? value : periodKey)}
            />
            <Button size="xs" variant="default" loading={validate.isPending} onClick={() => validate.mutate()}>
              {t('document.validate')}
            </Button>
            {can(session.data, 'Document.Export') && (
              <Button size="xs" variant="default" loading={exportBook.isPending} onClick={() => exportBook.mutate()}>
                {t('document.export')}
              </Button>
            )}
            {active !== undefined && !readOnly && (
              <Button size="xs" loading={submit.isPending} onClick={() => submit.mutate(active.sheetDefId)}>
                {t('document.submit')}
              </Button>
            )}
          </Group>
        }
      />

      <Tabs value={active?.code ?? null} onChange={setSheet}>
        <Tabs.List>
          {sheets.map((s) => (
            <Tabs.Tab key={s.code} value={s.code}>
              {s.name}{' '}
              <Badge size="xs" variant="light">
                {summary.data.sheetStates[s.code] ?? 'Draft'}
              </Badge>
            </Tabs.Tab>
          ))}
        </Tabs.List>
      </Tabs>

      {active === undefined ? (
        <Text c="dimmed">{t('document.noSheets')}</Text>
      ) : (
        active.tables.map((table) => (
          <Stack key={table.tableInstanceId} gap="xs">
            <Text fw={600}>{localized(table.tableNameL10n)}</Text>
            <DocumentGrid
              documentId={documentId}
              tableInstanceId={table.tableInstanceId}
              periodKey={periodKey}
              readOnly={readOnly}
            />
          </Stack>
        ))
      )}
    </Stack>
  );
}

/** Аркуш із його таблицями. */
interface SheetGroup {
  sheetDefId: number;
  code: string;
  name: string;
  ordinal: number;
  tables: DocumentTableDto[];
}

/**
 * Групує таблиці за аркушами.
 *
 * ⚠ Сервер віддає плоский перелік екземплярів: так його можна віддати одним
 * запитом і не вигадувати вкладену структуру, яка все одно розбирається на
 * клієнті.
 */
function groupBySheet(tables: DocumentTableDto[]): SheetGroup[] {
  const sheets = new Map<string, SheetGroup>();

  for (const table of tables) {
    const group = sheets.get(table.sheetCode) ?? {
      sheetDefId: table.sheetDefId,
      code: table.sheetCode,
      name: localized(table.sheetNameL10n) || table.sheetCode,
      ordinal: table.sheetOrdinal,
      tables: [],
    };

    group.tables.push(table);
    sheets.set(table.sheetCode, group);
  }

  return [...sheets.values()].sort((a, b) => a.ordinal - b.ordinal);
}

/**
 * Поточний період як `Year*100 + Sequence` (R-A6).
 *
 * ⚠ Це лише **початкове значення поля**, а не бізнес-правило: справжній
 * поточний період задає календар проєкту, і в квартальному проєкті номер
 * місяця йому не дорівнює. Користувач бачить число і може його змінити.
 */
function currentPeriodKey(): number {
  const now = new Date();

  return now.getUTCFullYear() * 100 + (now.getUTCMonth() + 1);
}
