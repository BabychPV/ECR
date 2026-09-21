import type { JSX } from 'react';
import { Badge, Button, Group, Text } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { useMutation, useQuery } from '@tanstack/react-query';
import { useInRouterContext } from 'react-router-dom';
import { EcrApiError, apiEnqueue, apiFetch } from '@/api/client';
import { DataSourcesTable } from '@/features/integration/DataSourcesTable';
import type { CollectRequest, SourceEntityStatus } from '@/api/types';
import { can, useSession } from '@/shared/session/useSession';
import { humanizeJobId } from '@/features/workflow/jobLabel';
import { DataTable } from '@/shared/ui/DataTable';
import { ListPage } from '@/shared/ui/ListPage';
import { StatusBadge } from '@/shared/ui/StatusBadge';
import { Timestamp } from '@/shared/ui/Timestamp';
import { t } from '@/shared/i18n';

/**
 * Конфігуратор джерел і ручний запуск збору.
 *
 * ⛔ Запису в зовнішнє джерело тут немає і не буде: PI AF — **виключно
 * джерело** (D-44).
 *
 * ⚠ Колонка «прогалина» важливіша за колонку «останній прогін». Ознака
 * здоров'я інтеграції — журнал покриття, а не тиша: джерело, яке щоночі
 * успішно віддає нуль точок, і джерело, яке віддає дані, ззовні виглядають
 * однаково (ІНТ-3.3).
 */
export function SourcesPage(): JSX.Element {
  const session = useSession();

  /*
   * ⚠ З'єднання (`UI-09`) — СЕКЦІЄЮ під сутностями, а не вкладкою `?tab=`:
   * обидва переліки потрібні разом (лічильник сутностей у з'єднанні читається
   * поруч із самими сутностями), а вкладка ховала б один із них.
   *
   * ⚠ Секція читає `?panel=`, тобто потребує маршрутизатора. У застосунку він
   * є завжди; його немає лише в чотирьох старших тестах сутностей, які
   * рендерять сторінку без `MemoryRouter`. Без цієї перевірки вони падали б
   * не на своєму твердженні, а на «useSearchParams() outside a <Router>».
   */
  const routed = useInRouterContext();

  const sources = useQuery({
    queryKey: ['sources'],
    queryFn: () => apiFetch<SourceEntityStatus[]>('/api/v1/sources'),
  });

  const collect = useMutation({
    mutationFn: (id: number) => {
      const to = new Date();
      const from = new Date(to.getTime() - 7 * 24 * 60 * 60 * 1000);

      return apiEnqueue(`/api/v1/sources/${id}/collect`, {
        fromUtc: from.toISOString(),
        toUtc: to.toISOString(),
      } satisfies CollectRequest);
    },
    onSuccess: (job) => {
      // ⚠ 202 з jobId: збір ходить по мережі до чужої системи, і його
      // тривалість визначає не наш код.
      // ⛔ Аудит-пас 8, lane6, п.8: людський вигляд у тості, сам `jobId` — не.
      notifications.show({ message: t('sources.queued', { job: humanizeJobId(job.jobId) }) });
    },
    onError: (error) => {
      notifications.show({
        color: 'statusError',
        message: error instanceof EcrApiError ? error.message : String(error),
      });
    },
  });

  return (
    <ListPage
      header={{ title: t('sources.title') }}
      table={
        <DataTable<SourceEntityStatus>
          columns={[
            {
              key: 'entity',
              label: t('sources.entity'),
              minWidth: 220,
              sortValue: (source) => source.displayName ?? source.code,
              render: (source) => (
                <>
                  {source.displayName ?? source.code}
                  {!source.isActive && (
                    <Badge ml="xs" size="xs" variant="outline" color="gray">
                      {t('sources.inactive')}
                    </Badge>
                  )}
                  <Text size="xs" c="dimmed">
                    {source.entityPath ?? source.code}
                  </Text>
                </>
              ),
            },
            {
              key: 'transport',
              label: t('sources.transport'),
              render: (source) => <Badge variant="light">{source.transport}</Badge>,
            },
            {
              key: 'lastRun',
              label: t('sources.lastRun'),
              sortValue: (source) => source.lastRun?.status ?? null,
              render: (source) =>
                source.lastRun === null ? (
                  <Text c="dimmed">{t('sources.never')}</Text>
                ) : (
                  <Group gap="xs">
                    {/* ⛔ Тут стояло `status === 'Succeeded' ? statusSuccess :
                        statusWarning` — тобто ПРОВАЛ збору (`Failed`)
                        малювався попередженням, тим самим кольором, що й
                        часткова відповідь (`Degraded`). Оператор бачив
                        «жовтеньке» там, де даних немає зовсім. */}
                    <StatusBadge kind="collectionRun" state={source.lastRun.status} />
                    <Text size="xs">{source.lastRun.pointsRetrieved}</Text>
                  </Group>
                ),
            },
            {
              key: 'oldestGap',
              label: t('sources.gap'),
              render: (source) =>
                source.oldestGap === null ? (
                  <Text c="dimmed">—</Text>
                ) : (
                  <Badge color="statusError" variant="light" miw="fit-content">
                    {/* ⚠ Година ПОТРІБНА, тобто не `dateOnly`. Клітинка
                        відповідає на «з якого моменту даних немає», а
                        відповідь на неї — дія в сусідній клітинці: збір за
                        вікно. Прогалина шукається за 45 днів назад
                        (`CollectionStore.GapLookbackDays`), і найсвіжіша
                        починається СЬОГОДНІ: без години «19 вер.» читалося б
                        як «увесь день порожній», хоча порожні дві години.

                        ⚠ Гілка `null` лишається своя, а не `fallback` самого
                        `Timestamp`: «покриття суцільне» — це добра новина, і
                        малювати її тим самим червоним бейджем, що й
                        прогалину, означало б збрехати кольором. */}
                    <Timestamp value={source.oldestGap} />
                  </Badge>
                ),
            },
            {
              key: 'actions',
              label: '',
              sortable: false,
              render: (source) =>
                can(session.data, 'Integration.Manage') ? (
                  <Button
                    size="compact-xs"
                    variant="default"
                    // ⛔ Аудит 2026-09-16 §10.8: тут стояло голе
                    // `collect.isPending` — ОДНЕ значення однієї мутації на
                    // весь перелік. Збір одного джерела крутив спінер на ВСІХ
                    // кнопках і — через `disabled: disabled || loading`
                    // (`Button.mjs` Mantine) — блокував збір решти.
                    loading={collect.isPending && collect.variables === source.id}
                    onClick={() => collect.mutate(source.id)}
                  >
                    {t('sources.collect')}
                  </Button>
                ) : null,
            },
          ]}
          rows={sources.data}
          rowKey={(source) => String(source.id)}
          isPending={sources.isPending}
          error={sources.error}
          onRetry={() => void sources.refetch()}
          emptyTitle={t('sources.empty')}
          emptyHint={t('sources.emptyHint')}
          /*
           * ⚠ `opacity={0.5}` на рядку неактивного джерела НЕ перенесено, і це
           * свідоме рішення, а не втрата при переїзді. Прозорість 50 % ділить
           * контраст тексту навпіл — тобто робить рівно протилежне тому, задля
           * чого `UI-01` піднімав `borderStrong` з 1.53:1 до 3.17:1. Сенс
           * «джерело вимкнене» несуть два канали, що лишилися: бейдж
           * `sources.inactive` у першій колонці (видимий) і `rowLabel` нижче
           * (чутний). Третій канал, який псує читабельність, зайвий.
           *
           * ⛔ `rowLabel` — не косметика: зчитувач екрана не читає прозорості,
           * і до цієї правки неактивне джерело звучало б так само, як активне,
           * якби бейджа не було.
           */
          rowLabel={(source) =>
            source.isActive
              ? null
              : `${source.displayName ?? source.code} — ${t('sources.inactive')}`
          }
        />
      }
    >
      {routed && <DataSourcesTable />}
    </ListPage>
  );
}
