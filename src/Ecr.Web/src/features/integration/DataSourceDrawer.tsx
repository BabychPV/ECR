import { Suspense, useState, type JSX } from 'react';
import { Badge, Button, Group, Loader, Stack, Tabs } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { EcrApiError } from '@/api/client';
import { formatNumber } from '@/shared/format';
import { t } from '@/shared/i18n';
import { ConfirmModal } from '@/shared/ui/ConfirmModal';
import { DetailDrawer } from '@/shared/ui/DetailDrawer';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { localized } from '@/shared/i18n/localized';
import { KeyValue, type KeyValueItem } from '@/shared/ui/KeyValue';
import { DataSourceFormModal, DataSourceScheduleTab } from './lazyDataSourceForm';
import { TestDataSourceModal } from './TestDataSourceModal';
import { deleteDataSource, type DataSource } from './dataSourceApi';
import { DataSourcesQueryKey } from './dataSourcesKey';

/**
 * Назва з'єднання мовою користувача; немає жодної — код.
 *
 * ⚠ `nameL10n` у `DataSourceView` — ПЛОСКА мапа мов, а не `{ values }`, як
 * у решті `…L10n`; тому обгортка тут, а не зміна `localized`.
 */
export function dataSourceName(source: DataSource): string {
  const name = localized({ values: source.nameL10n });

  return name.length > 0 ? name : source.code;
}

/**
 * `409 dataSourceChanged` → чинна версія рядка з тіла відмови; `null` — відмова
 * не про версію (або сервер версії не назвав).
 *
 * ⚠ Тут, а не у формі: форма — окремий лінивий чанк і сама імпортує звідси.
 */
export function freshVersionOf(error: unknown): string | null {
  if (!(error instanceof EcrApiError) || error.problem.status !== 409) return null;
  if (error.problem.extensions2?.['messageKey'] !== 'err.ECR-JOB-0409.dataSourceChanged') return null;

  const rowVersion = error.problem.extensions2['rowVersion'];

  return typeof rowVersion === 'string' && rowVersion.length > 0 ? rowVersion : null;
}

/**
 * Пари «підпис → значення» вкладки Connection.
 *
 * ⛔ Поля секрету немає й не буде: сервер його не віддає, а сховище секретів
 * прибрано рішенням людини (Q15-06, службовий обліковий запис). `hasSecret`
 * — лише ознака, і лише коли `true`: `false` — це норма для службового
 * запису, а не «бракує налаштування», і рядок «секрету немає» читався б як
 * попередження, якого немає (`D15-06`).
 *
 * ⚠ `null` у `secondaryEndpoint`/`catalog` рядка не дає: `KeyValue` сам не
 * малює пару без значення.
 */
export function connectionItems(source: DataSource): KeyValueItem[] {
  return [
    { label: t('sources.transport'), value: source.transport },
    { label: t('sources.endpoint'), value: source.endpoint, mono: true },
    { label: t('sources.secondaryEndpoint'), value: source.secondaryEndpoint, mono: true },
    { label: t('sources.catalog'), value: source.catalog, mono: true },
    { label: t('sources.maxParallel'), value: formatNumber(source.maxParallel) },
    { label: t('sources.entities'), value: formatNumber(source.sourceEntities) },
    { label: t('sources.schedules'), value: formatNumber(source.collectionSchedules) },
    { label: t('sources.hasSecret'), value: source.hasSecret ? t('sources.hasSecretYes') : null },
  ];
}

/**
 * Шухляда з'єднання (директива №15 §3 `UI-09`, шар 2).
 *
 * ⚠ Вкладки дві — Connection і Schedule (розклади збору сутностей цього
 * з'єднання, `DataSourceScheduleTab`).
 *
 * ⛔ Багатокрокової «Check configuration» із макета тут немає: сервер цих
 * кроків не віддає, а елемент без даних не малюється.
 */
export function DataSourceDrawer({
  source,
  canManage,
  onDeleted,
}: {
  readonly source: DataSource;

  /**
   * `Integration.Manage`: без нього кнопок проби, правки й видалення НЕМАЄ,
   * а не вимкнені.
   */
  readonly canManage: boolean;

  /** З'єднання видалено — шухляду треба закрити. */
  readonly onDeleted: () => void;
}): JSX.Element {
  const queryClient = useQueryClient();
  const [testing, setTesting] = useState(false);
  const [editing, setEditing] = useState(false);
  const [confirming, setConfirming] = useState(false);

  /*
   * ⛔ Видалення не каскадне: з'єднання, на яке спираються сутності збору або
   * розклади, сервер не видаляє — `409 ECR-JOB-0409`
   * (`err.ECR-JOB-0409.dataSourceInUse`) з лічильниками. Причина показується
   * в шухляді (`ErrorAlert` несе локалізований сервером текст із числами), а
   * не «не вдалося»; перелік при цьому НЕ перечитується — нічого не змінилося.
   *
   * ⛔ `If-Match` — версія рядка, який людина БАЧИТЬ у шухляді й підтвердила.
   * Хтось змінив з'єднання тим часом — `409 dataSourceChanged`: причина
   * лишається в шухляді, перелік перечитується (шухляда показує чинний рядок),
   * а повторне видалення — лише новим підтвердженням уже над ним.
   */
  const remove = useMutation({
    mutationFn: (rowVersion: string) => deleteDataSource(source.id, rowVersion),
    onSuccess: () => {
      setConfirming(false);
      void queryClient.invalidateQueries({ queryKey: DataSourcesQueryKey });
      notifications.show({ message: t('sources.deleted') });
      onDeleted();
    },
    onError: (error) => {
      setConfirming(false);
      if (freshVersionOf(error) !== null) {
        void queryClient.invalidateQueries({ queryKey: DataSourcesQueryKey });
      }
    },
  });

  return (
    <>
      <DetailDrawer
        panelId={source.code}
        title={dataSourceName(source)}
        subtitle={source.code}
        badge={
          source.isActive ? undefined : (
            <Badge variant="outline" color="gray">
              {t('sources.inactive')}
            </Badge>
          )
        }
        closeLabel={t('sources.closeDetails')}
        footer={
          canManage ? (
            <Group gap="xs" justify="space-between" w="100%">
              <Button
                variant="subtle"
                color="statusError"
                onClick={() => setConfirming(true)}
                data-delete-connection=""
              >
                {t('sources.deleteConnection')}
              </Button>

              <Group gap="xs">
                <Button variant="default" onClick={() => setEditing(true)} data-edit-connection="">
                  {t('sources.editConnection')}
                </Button>

                <Button onClick={() => setTesting(true)} data-test-connection="">
                  {t('sources.testConnection')}
                </Button>
              </Group>
            </Group>
          ) : undefined
        }
      >
        {remove.error !== null && (
          <Stack mb="sm" data-delete-failure="">
            <ErrorAlert error={remove.error} />
          </Stack>
        )}

        <Tabs defaultValue="connection" keepMounted={false}>
          <Tabs.List>
            <Tabs.Tab value="connection">{t('sources.connection')}</Tabs.Tab>
            <Tabs.Tab value="schedule">{t('sources.tabSchedule')}</Tabs.Tab>
          </Tabs.List>

          <Tabs.Panel value="connection" pt="sm">
            <KeyValue items={connectionItems(source)} />
          </Tabs.Panel>

          {/* ⚠ `L2`: закрита за замовчуванням, і `keepMounted={false}` означає,
              що розклади не читаються, доки вкладку не відкрили. */}
          <Tabs.Panel value="schedule" pt="sm">
            <Suspense fallback={<Loader size="sm" />}>
              <DataSourceScheduleTab source={source} />
            </Suspense>
          </Tabs.Panel>
        </Tabs>
      </DetailDrawer>

      {canManage && (
        <>
          <TestDataSourceModal
            opened={testing}
            sourceId={source.id}
            sourceName={dataSourceName(source)}
            onClose={() => setTesting(false)}
          />

          {editing && (
            <Suspense fallback={null}>
              <DataSourceFormModal opened source={source} onClose={() => setEditing(false)} />
            </Suspense>
          )}

          <ConfirmModal
            opened={confirming}
            title={t('sources.deleteTitle', { name: dataSourceName(source) })}
            verb={t('sources.deleteConnection')}
            isPending={remove.isPending}
            onConfirm={() => remove.mutate(source.rowVersion)}
            onClose={() => setConfirming(false)}
          />
        </>
      )}
    </>
  );
}
