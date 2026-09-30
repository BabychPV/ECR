import { useState, type JSX } from 'react';
import { Badge, Button, Group, Table, Text } from '@mantine/core';
import { Link, useParams } from 'react-router-dom';
import { t } from '@/shared/i18n';
import { can, useSession } from '@/shared/session/useSession';
import { AsyncBoundary } from '@/shared/ui/AsyncBoundary';
import { Banner } from '@/shared/ui/Banner';
import { ErrorAlert, TechnicalDetails } from '@/shared/ui/ErrorAlert';
import { PageHeader } from '@/shared/ui/PageHeader';
import { ReasonModal } from '@/shared/ui/ReasonModal';
import type { RegistryImpactResponse } from './api';
import {
  RecalculateImpactedPermission,
  useImpactRecalculation,
  useRegistryImpact,
  type ImpactRecalculation,
} from './useImpactRecalculation';

/** Адреса сторінки «Вплив правки довідника». */
export function registryImpactPath(code: string): string {
  return `/admin/registries/${encodeURIComponent(code)}/impact`;
}

/**
 * Документи відкритих періодів, зачеплені правкою довідника (RT-25), і явна дія «Перерахувати».
 *
 * ⛔ Перерахунок ніколи не автоматичний (`R-14`): перелік — лише читання, задачу ставить людина
 * кнопкою з обов'язковою причиною. Відмови (403/422) показує `ErrorAlert` за каталогом помилок.
 */
export function RegistryImpactPage(): JSX.Element {
  const { code = '' } = useParams();
  const impact = useRegistryImpact(code);
  const session = useSession();
  // ⚠ Кнопка — лише з правом, яке вимагає сам ендпоінт, а не тим, що відкриває екран.
  const recalculates = can(session.data, RecalculateImpactedPermission);
  const [asking, setAsking] = useState(false);
  const run = useImpactRecalculation(code);

  return (
    <>
      <PageHeader
        title={t('registries.impact.title')}
        actions={
          <Group gap="xs">
            <Button
              component={Link}
              to={`/admin/registries/${encodeURIComponent(code)}/entries`}
              size="xs"
              variant="default"
            >
              {t('registries.impact.backToData')}
            </Button>
            {recalculates && (
              <Button
                size="xs"
                loading={run.isStarting}
                disabled={run.outcome === 'running' || (impact.data?.total ?? 0) === 0}
                onClick={() => setAsking(true)}
              >
                {t('registries.impact.recalculate')}
              </Button>
            )}
          </Group>
        }
      />

      <Text size="sm" c="dimmed" mb="xs">
        {t('registries.impact.hint', { code })}
      </Text>

      <ErrorAlert error={run.startError} />
      <RunStatus run={run} />

      <ReasonModal
        opened={asking}
        title={t('registries.impact.recalculate')}
        label={t('workflow.reason')}
        description={t('registries.impact.reasonHint')}
        confirmLabel={t('registries.impact.recalculate')}
        isPending={run.isStarting}
        onConfirm={(reason) => {
          setAsking(false);
          run.start(reason);
        }}
        onClose={() => setAsking(false)}
      />

      <AsyncBoundary<RegistryImpactResponse>
        isPending={impact.isPending}
        error={impact.error}
        data={impact.data}
        isEmpty={(page) => page.items.length === 0}
        emptyTitle={t('registries.impact.empty')}
        emptyHint={t('registries.impact.emptyHint')}
        skeleton="table"
        onRetry={() => void impact.refetch()}
      >
        {(page) => (
          <>
            <Text size="sm" mb="xs" data-impact-total="">
              {t('registries.impact.total', { total: page.total })}
            </Text>
            {page.truncated && <Banner tone="warning" text={t('registries.impact.truncated')} />}
            <Table striped className="ecr-sticky-head">
              <Table.Thead>
                <Table.Tr>
                  <Table.Th>{t('registries.impact.document')}</Table.Th>
                  <Table.Th>{t('registries.impact.period')}</Table.Th>
                  <Table.Th>{t('registries.impact.periodState')}</Table.Th>
                  <Table.Th>{t('registries.impact.via')}</Table.Th>
                </Table.Tr>
              </Table.Thead>
              <Table.Tbody>
                {page.items.map((item) => (
                  <Table.Tr key={`${String(item.documentId)}:${String(item.periodKey)}`}>
                    {/* ⚠ `data-allow-dotted`: бізнес-ключ і `methodology:<код>` — ДАНІ, не ключі каталогу. */}
                    <Table.Td data-allow-dotted>{item.businessKey}</Table.Td>
                    <Table.Td>{item.periodKey}</Table.Td>
                    <Table.Td>
                      <Badge
                        size="sm"
                        variant="light"
                        color={item.periodState === 'Grace' ? 'statusWarning' : 'statusSuccess'}
                      >
                        {item.periodState === 'Grace'
                          ? t('registries.impact.stateGrace')
                          : t('registries.impact.stateOpen')}
                      </Badge>
                    </Table.Td>
                    <Table.Td data-allow-dotted>{item.via.join(', ')}</Table.Td>
                  </Table.Tr>
                ))}
              </Table.Tbody>
            </Table>
          </>
        )}
      </AsyncBoundary>
    </>
  );
}

/** Стан задачі перерахунку; `unknown` — окремий: «стан прочитати не вдалося» не є «виконується». */
function RunStatus({ run }: { run: ImpactRecalculation }): JSX.Element | null {
  if (run.outcome === null) return null;

  // ⚠ Ключі літералами: сторожі каталогу шукають виклик `t('…')`.
  const tone = {
    running: { color: 'gray', label: t('registries.impact.running') },
    succeeded: {
      color: 'statusSuccess',
      label: t('registries.impact.succeeded'),
    },
    failed: { color: 'statusError', label: t('registries.impact.failed') },
    unknown: { color: 'statusWarning', label: t('registries.impact.unknown') },
  }[run.outcome];

  return (
    <Group gap="xs" mb="xs" role="status" data-outcome={run.outcome}>
      <Badge size="sm" variant="light" color={tone.color}>
        {tone.label}
      </Badge>
      {run.failure !== null && run.failure !== '' && (
        <TechnicalDetails label={t('common.technicalDetails')}>{run.failure}</TechnicalDetails>
      )}
    </Group>
  );
}
