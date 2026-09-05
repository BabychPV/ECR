import { useState, type JSX } from 'react';
import {
  Badge,
  Button,
  Code,
  Group,
  Modal,
  NumberInput,
  ScrollArea,
  Stack,
  Table,
  Text,
  TextInput,
  Textarea,
} from '@mantine/core';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import type {
  MethodologyDto,
  PublishMethodologyRequest,
  SimulateMethodologyRequest,
  SimulationResultDto,
} from '@/api/types';
import { localized } from '@/shared/i18n/localized';
import { can, useSession } from '@/shared/session/useSession';
import { AsyncBoundary } from '@/shared/ui/AsyncBoundary';
import { PageHeader } from '@/shared/ui/PageHeader';
import { showApiError, showDone } from '@/shared/ui/notify';
import { t } from '@/shared/i18n';

/**
 * Конфігуратор методологій: версії, публікація, симуляція.
 *
 * ⛔ Публікація потребує **причини**, **дати набуття чинності** і **зеленого
 * тесту** (ФВ-9.12), а автор останньої правки опублікувати не може (D-40).
 * Форма вимагає причини не з ввічливості: без неї журнал змін методології
 * показує «щось змінилося», і через рік ніхто не пояснить, чому число за
 * минулий рік перерахувалося.
 *
 * ⛔ Поле дати з'явилося після `A7-11`. Діалог збирав саму лише причину, а
 * сервер відхиляє публікацію без дати (`ECR-CALC-0422`) ПЕРШОЮ ж перевіркою —
 * тобто кнопка «Опублікувати» не спрацьовувала жодного разу. Дата не має
 * значення за замовчуванням саме тому, що вона визначає, ЯКІ ПЕРІОДИ
 * перерахуються: підставити «сьогодні» мовчки означало б обрати межу
 * перерахунку за користувача.
 */
export function MethodologiesPage(): JSX.Element {
  const queryClient = useQueryClient();
  const session = useSession();
  const [publishing, setPublishing] = useState<{ id: number; versionId: number } | null>(null);
  const [reason, setReason] = useState('');
  const [effectiveFrom, setEffectiveFrom] = useState('');

  // Яку версію проганяємо; `null` — діалог симуляції закритий.
  const [simulating, setSimulating] = useState<{ id: number; versionId: number } | null>(null);
  const [simulationPeriod, setSimulationPeriod] = useState(currentPeriodKey());
  const [simulationResult, setSimulationResult] = useState<SimulationResultDto | null>(null);

  const methodologies = useQuery({
    queryKey: ['methodologies'],
    queryFn: () => apiFetch<MethodologyDto[]>('/api/v1/methodologies'),
  });

  const publish = useMutation({
    mutationFn: (target: { id: number; versionId: number; reason: string; from: string }) =>
      apiFetch(`/api/v1/methodologies/${target.id}/versions/${target.versionId}/publish`, {
        method: 'POST',
        body: JSON.stringify({
          changeReason: target.reason,
          // ⚠ `date` дає рівно `YYYY-MM-DD` — форму `DateOnly` сервера. Через
          // `Date` тут проходити не можна: `toISOString()` переводить у UTC і
          // ввечері зсуває дату на добу назад.
          effectiveFrom: target.from,
        } satisfies PublishMethodologyRequest),
      }),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['methodologies'] });
      setPublishing(null);
      setReason('');
      setEffectiveFrom('');
      showDone(t('methodologies.published'));
    },
    onError: showApiError,
  });

  /**
   * Прогін методології без запису (`ФВ-13.5`).
   *
   * ⛔ Дії не було в інтерфейсі (`A7-39`), і саме вона відповідає на
   * питання, заради якого існує публікація: «що зміниться в числах».
   * Без неї єдиним способом дізнатися наслідок було опублікувати —
   * тобто зробити зміну незворотною до того, як її побачили.
   *
   * ⚠ Доступна з правом на ПЕРЕГЛЯД, а не на публікацію: прогін нічого не
   * зберігає, і ховати його за небезпечним правом означало б змусити
   * автора просити публікатора «просто подивитися».
   */
  const simulate = useMutation({
    mutationFn: (target: { id: number; versionId: number }) =>
      apiFetch<SimulationResultDto>(`/api/v1/methodologies/${target.id}/simulate`, {
        method: 'POST',
        body: JSON.stringify({
          methodologyVersionId: target.versionId,
          periodKey: simulationPeriod,
        } satisfies SimulateMethodologyRequest),
      }),
    onSuccess: setSimulationResult,
    onError: showApiError,
  });

  return (
    <>
      <PageHeader title={t('methodologies.title')} />
      <AsyncBoundary<MethodologyDto[]>
        isPending={methodologies.isPending}
        error={methodologies.error}
        data={methodologies.data}
        isEmpty={(all) => all.length === 0}
        emptyTitle={t('methodologies.empty')}
        emptyHint={t('methodologies.emptyHint')}
        skeleton="table"
        onRetry={() => void methodologies.refetch()}
      >
        {(all) => (
        <Table striped className="ecr-sticky-head">
          <Table.Thead>
            <Table.Tr>
              <Table.Th>{t('methodologies.code')}</Table.Th>
              <Table.Th>{t('methodologies.versions')}</Table.Th>
            </Table.Tr>
          </Table.Thead>
          <Table.Tbody>
            {all.map((methodology) => (
              <Table.Tr key={methodology.id}>
                <Table.Td>
                  {localized(methodology.nameL10n)}{' '}
                  <Text span c="dimmed">
                    ({methodology.code})
                  </Text>
                </Table.Td>
                <Table.Td>
                  <Group gap="xs">
                    {methodology.versions.map((version) => (
                      <Group key={version.id} gap="xs">
                        <Badge variant={version.status === 'Published' ? 'filled' : 'light'}>
                          {version.versionNumber} · {version.level}
                          {version.effectiveFrom === null ? '' : ` · ${version.effectiveFrom}`}
                        </Badge>

                        {/* ⚠ Режим і рівень трасування видно поруч із версією:
                            саме вони визначають, чи зміняться числа при
                            публікації, і саме їх порівнюють у diff публікації. */}
                        <Badge variant="outline" size="sm">
                          {version.numericMode} · {version.traceLevel}
                        </Badge>

                        {/* ⚠ Прогін доступний для БУДЬ-ЯКОЇ версії, і
                            опублікованої теж: питання «що дала б ця
                            версія на цьому періоді» законне й після
                            публікації — саме так звіряють розбіжність. */}
                        {can(session.data, 'Calculation.View') && (
                          <Button
                            size="compact-xs"
                            variant="subtle"
                            onClick={() =>
                              setSimulating({ id: methodology.id, versionId: version.id })
                            }
                          >
                            {t('methodologies.simulate')}
                          </Button>
                        )}

                        {version.status !== 'Published' &&
                          can(session.data, 'Calculation.Publish') && (
                            <Button
                              size="compact-xs"
                              variant="default"
                              onClick={() =>
                                setPublishing({ id: methodology.id, versionId: version.id })
                              }
                            >
                              {t('methodologies.publish')}
                            </Button>
                          )}
                      </Group>
                    ))}
                  </Group>
                </Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>
        )}
      </AsyncBoundary>

      <Modal
        opened={publishing !== null}
        onClose={() => setPublishing(null)}
        title={t('methodologies.publishTitle')}
      >
        <Textarea
          label={t('methodologies.reason')}
          description={t('methodologies.reasonHint')}
          value={reason}
          onChange={(event) => setReason(event.currentTarget.value)}
          minRows={3}
          autosize
        />

        <TextInput
          mt="sm"
          type="date"
          label={t('methodologies.effectiveFrom')}
          description={t('methodologies.effectiveFromHint')}
          value={effectiveFrom}
          onChange={(event) => setEffectiveFrom(event.currentTarget.value)}
        />

        <Button
          mt="md"
          disabled={reason.trim().length === 0 || effectiveFrom.length === 0}
          loading={publish.isPending}
          onClick={() => {
            if (publishing !== null) {
              publish.mutate({ ...publishing, reason, from: effectiveFrom });
            }
          }}
        >
          {t('methodologies.publish')}
        </Button>
      </Modal>

      <Modal
        opened={simulating !== null}
        onClose={() => {
          setSimulating(null);
          setSimulationResult(null);
        }}
        title={t('methodologies.simulate')}
        size="lg"
      >
        <NumberInput
          label={t('documents.period')}
          description={t('methodologies.simulatePeriodHint')}
          value={simulationPeriod}
          onChange={(value) =>
            setSimulationPeriod(typeof value === 'number' ? value : simulationPeriod)
          }
          data-autofocus
        />

        <Button
          mt="md"
          loading={simulate.isPending}
          onClick={() => {
            if (simulating !== null) simulate.mutate(simulating);
          }}
        >
          {t('methodologies.run')}
        </Button>

        {simulationResult !== null && (
          <Stack gap="xs" mt="md">
            <Text fw={600}>{t('methodologies.outputs')}</Text>
            <Table striped withTableBorder>
              <Table.Thead>
                <Table.Tr>
                  <Table.Th>{t('methodologies.output')}</Table.Th>
                  <Table.Th>{t('methodologies.value')}</Table.Th>
                  <Table.Th>{t('methodologies.diff')}</Table.Th>
                </Table.Tr>
              </Table.Thead>
              <Table.Tbody>
                {Object.entries(simulationResult.outputs).map(([key, value]) => (
                  <Table.Tr key={key}>
                    <Table.Td>{key}</Table.Td>
                    <Table.Td>{value}</Table.Td>
                    {/* ⛔ Розбіжність із опублікованою версією — головна
                        колонка цієї таблиці. Саме вона відповідає на
                        питання «чи зміняться числа», заради якого прогін і
                        робиться; самі значення без неї нічого не кажуть. */}
                    <Table.Td>{simulationResult.diffWithPublished[key] ?? '—'}</Table.Td>
                  </Table.Tr>
                ))}
              </Table.Tbody>
            </Table>

            {simulationResult.trace.length > 0 && (
              <>
                <Text fw={600}>{t('methodologies.trace')}</Text>
                {/* ⚠ Трасування показується як є і не форматується: це
                    послідовність кроків обчислення, і будь-яка «краса» тут
                    заважає звірити її з формулою. */}
                <ScrollArea h={200}>
                  <Code block>{simulationResult.trace.join('\n')}</Code>
                </ScrollArea>
              </>
            )}
          </Stack>
        )}
      </Modal>
    </>
  );
}

/**
 * Поточний період як `Year*100 + Sequence` (R-A6).
 *
 * ⚠ Лише **початкове значення поля**: справжній поточний період задає
 * календар проєкту, і в квартальному номер місяця йому не дорівнює.
 */
function currentPeriodKey(): number {
  const now = new Date();

  return now.getUTCFullYear() * 100 + (now.getUTCMonth() + 1);
}
