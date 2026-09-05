import { useState, type JSX } from 'react';
import { Button, Group, Loader, NumberInput, Stack, Tabs, Text } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useParams } from 'react-router-dom';
import { EcrApiError, apiEnqueue, apiFetch } from '@/api/client';
import { DocumentGrid } from '@/features/grid/DocumentGrid';
import { can, useSession } from '@/shared/session/useSession';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { PageHeader } from '@/shared/ui/PageHeader';
import { t } from '@/shared/i18n';

/** Аркуш документа з екземплярами таблиць. */
interface SheetDto {
  sheetDefId: number;
  code: string;
  name: string;
  ordinal: number;
  tables: { tableInstanceId: number; tableDefId: number; code: string; name: string }[];
  /** Стан робочого процесу за обраний період. */
  state: string;
}

interface DocumentDto {
  id: number;
  businessKey: string;
  projectId: number;
  templateVersionId: number;
  sheets: SheetDto[];
}

/**
 * Екран документа: вибір періоду, вкладки аркушів, таблиці, робочий процес.
 *
 * ⚠ Період — не фільтр показу, а **частина адреси даних**: екземпляри таблиць
 * і стан затвердження існують окремо на кожен період (R-A6). Тому зміна
 * періоду перечитує все, а не ховає рядки.
 */
export function DocumentPage(): JSX.Element {
  const { id } = useParams();
  const documentId = Number(id);
  const queryClient = useQueryClient();
  const session = useSession();

  const [periodKey, setPeriodKey] = useState<number>(currentPeriodKey);
  const [sheet, setSheet] = useState<string | null>(null);

  const document = useQuery({
    queryKey: ['document', documentId, periodKey],
    queryFn: () => apiFetch<DocumentDto>(`/api/v1/documents/${documentId}?periodKey=${periodKey}`),
  });

  const validate = useMutation({
    mutationFn: () =>
      apiFetch<{ messages: { severity: string; message: string }[] }>(
        `/api/v1/documents/${documentId}/validate`,
        { method: 'POST', body: JSON.stringify({ periodKey }) },
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
        body: JSON.stringify({ sheetDefId, periodKey }),
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
      }),
    onSuccess: (job) => {
      // ⚠ Довга операція повертає 202 з jobId; прогрес видно на екрані задач.
      notifications.show({ message: t('document.exportQueued', { job: job.jobId }) });
    },
  });

  if (document.isPending) return <Loader />;
  if (document.isError || document.data === undefined) return <ErrorAlert error={document.error} />;

  const sheets = [...document.data.sheets].sort((a, b) => a.ordinal - b.ordinal);
  const active = sheets.find((s) => s.code === sheet) ?? sheets[0];

  return (
    <Stack>
      <PageHeader
        title={document.data.businessKey}
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
            {active !== undefined && (
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
              {s.name} · {s.state}
            </Tabs.Tab>
          ))}
        </Tabs.List>
      </Tabs>

      {active === undefined ? (
        <Text c="dimmed">{t('document.noSheets')}</Text>
      ) : (
        active.tables.map((table) => (
          <Stack key={table.tableInstanceId} gap="xs">
            <Text fw={600}>{table.name}</Text>
            <DocumentGrid
              documentId={documentId}
              tableInstanceId={table.tableInstanceId}
              periodKey={periodKey}
              readOnly={active.state === 'Submitted' || active.state === 'Approved'}
            />
          </Stack>
        ))
      )}
    </Stack>
  );
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
