import type { JSX } from 'react';
import { Badge, Button, Group, Table, Text } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { useMutation, useQuery } from '@tanstack/react-query';
import { EcrApiError, apiEnqueue, apiFetch } from '@/api/client';
import type { CollectRequest, SourceEntityStatus } from '@/api/types';
import { can, useSession } from '@/shared/session/useSession';
import { humanizeJobId } from '@/features/workflow/jobLabel';
import { AsyncBoundary } from '@/shared/ui/AsyncBoundary';
import { PageHeader } from '@/shared/ui/PageHeader';
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
    <>
      <PageHeader title={t('sources.title')} />
      <AsyncBoundary<SourceEntityStatus[]>
        isPending={sources.isPending}
        error={sources.error}
        data={sources.data}
        isEmpty={(all) => all.length === 0}
        emptyTitle={t('sources.empty')}
        emptyHint={t('sources.emptyHint')}
        skeleton="table"
        onRetry={() => void sources.refetch()}
      >
        {(all) => (
        <Table striped highlightOnHover className="ecr-sticky-head">
          <Table.Thead>
            <Table.Tr>
              <Table.Th>{t('sources.entity')}</Table.Th>
              <Table.Th>{t('sources.transport')}</Table.Th>
              <Table.Th>{t('sources.lastRun')}</Table.Th>
              <Table.Th>{t('sources.gap')}</Table.Th>
              <Table.Th />
            </Table.Tr>
          </Table.Thead>
          <Table.Tbody>
            {all.map((source) => (
              <Table.Tr
                key={source.id}
                opacity={source.isActive ? 1 : 0.5}
                // ⚠ `opacity` — це ЛИШЕ пікселі. Зчитувач екрана не читає
                // прозорість, тож неактивне джерело звучало б так само, як
                // активне — рівно те, чого ФВ-14 (доступність) забороняє:
                // стан, видимий оком, має мати й носія для того, хто його
                // не бачить.
                aria-label={
                  source.isActive
                    ? undefined
                    : `${source.displayName ?? source.code} — ${t('sources.inactive')}`
                }
              >
                <Table.Td>
                  {source.displayName ?? source.code}
                  {!source.isActive && (
                    <Badge ml="xs" size="xs" variant="outline" color="gray">
                      {t('sources.inactive')}
                    </Badge>
                  )}
                  <Text size="xs" c="dimmed">
                    {source.entityPath ?? source.code}
                  </Text>
                </Table.Td>
                <Table.Td>
                  <Badge variant="light">{source.transport}</Badge>
                </Table.Td>
                <Table.Td>
                  {source.lastRun === null ? (
                    <Text c="dimmed">{t('sources.never')}</Text>
                  ) : (
                    <Group gap="xs">
                      {/* ⛔ Тут стояло `status === 'Succeeded' ? statusSuccess :
                          statusWarning` — тобто ПРОВАЛ збору (`Failed`)
                          малювався попередженням, тим самим кольором, що й
                          часткова відповідь (`Degraded`). Оператор бачив
                          «жовтеньке» там, де даних немає зовсім.
                          `collectionRun` у таблиці набору розрізняє ці три
                          стани і дає `Failed` тон `danger`. */}
                      <StatusBadge kind="collectionRun" state={source.lastRun.status} />
                      <Text size="xs">{source.lastRun.pointsRetrieved}</Text>
                    </Group>
                  )}
                </Table.Td>
                <Table.Td>
                  {source.oldestGap === null ? (
                    <Text c="dimmed">—</Text>
                  ) : (
                    <Badge color="statusError" variant="light">
                      {/* ⛔ Тут друкувався сирий ISO (`2026-09-19T18:51:58.275`)
                          просто в бейдж — і саме в колонці, яку шапка файла
                          називає важливішою за «останній прогін».

                          ⚠ Година ПОТРІБНА, тобто не `dateOnly`. Клітинка
                          відповідає на «з якого моменту даних немає», а
                          відповідь на неї — дія в сусідній клітинці: збір за
                          вікно. Прогалина шукається за 45 днів назад
                          (`CollectionStore.GapLookbackDays`), і найсвіжіша
                          починається СЬОГОДНІ: без години «19 вер.» читалося б
                          як «увесь день порожній», хоча порожні дві години.
                          Це не `PeriodsPage`, де межа відповідає на «який це
                          місяць» і година в ній зайва.

                          ⛔ А `precise` тут НЕ треба, хоч це теж «прогалина».
                          Аргумент із `MappingGaps.lastSeenUtc` не переноситься:
                          там КОЛОНКА сусідніх міток телеметрії, які
                          відрізняються одна від одної секундою, і без секунд
                          два різні виміри зливаються в один. Тут — одне
                          значення на джерело в огляді здоров'я, порівнювати
                          його із сусідом за секунду нема з чим, а питання
                          («скільки вже немає даних») міряється годинами й
                          днями. Секунда на екрані читалася б як важливість.
                          Точне значення нікуди не зникає — воно в
                          `dateTime`/`title`, звідки його й копіюють у тикет.

                          ⚠ Гілка `null` лишається своя, а не `fallback`
                          самого `Timestamp`: «покриття суцільне» — це добра
                          новина, і малювати її тим самим червоним бейджем,
                          що й прогалину, означало б збрехати кольором. */}
                      <Timestamp value={source.oldestGap} />
                    </Badge>
                  )}
                </Table.Td>
                <Table.Td>
                  {can(session.data, 'Integration.Manage') && (
                    <Button
                      size="compact-xs"
                      variant="default"
                      // ⛔ Аудит 2026-09-16 §10.8: тут стояло голе
                      // `collect.isPending` — ОДНЕ значення однієї мутації на
                      // весь перелік. Збір одного джерела крутив спінер на
                      // ВСІХ кнопках і — через `disabled: disabled || loading`
                      // (`Button.mjs` Mantine) — блокував збір решти, хоча
                      // причини серіалізувати їх немає: кожен збір — окрема
                      // фонова задача зі власним `jobId`.
                      //
                      // ⚠ `collect.variables` — саме те, чим його викликали, і
                      // React Query тримає це значення доки запит у дорозі;
                      // окремий стан «який рядок зараз збирається» був би
                      // другою копією того самого факту.
                      loading={collect.isPending && collect.variables === source.id}
                      onClick={() => collect.mutate(source.id)}
                    >
                      {t('sources.collect')}
                    </Button>
                  )}
                </Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>
        )}
      </AsyncBoundary>
    </>
  );
}
