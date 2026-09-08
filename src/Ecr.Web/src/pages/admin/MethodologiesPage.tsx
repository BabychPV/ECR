import { useState, type JSX } from 'react';
import {
  Badge,
  Button,
  Code,
  Group,
  Modal,
  NumberInput,
  ScrollArea,
  Select,
  Stack,
  Table,
  Text,
  TextInput,
  Textarea,
} from '@mantine/core';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Link } from 'react-router-dom';
import { apiFetch } from '@/api/client';
import type {
  MethodologyDto,
  MethodologyKind,
  MethodologyPublicationDiff,
  PublishMethodologyRequest,
  SimulateMethodologyRequest,
  SimulationResultDto,
} from '@/api/types';
import { createMethodology } from '@/features/methodologies/api';
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

  // ⛔ Diff публікації БІЛЬШЕ НЕ ВИКИДАЄТЬСЯ. Сервер віддає його рівно тому, що
  // публікація — найнебезпечніша операція системи: вона змінює числа, які вже
  // подані регуляторові (`ФВ-9.6`). Клієнт мовчки ковтав відповідь, тож той,
  // хто щойно натиснув кнопку, не бачив ані зміни режиму, ані розбіжностей на
  // золотому наборі, ані попереджень публікації — тобто саме те, заради чого
  // diff і рахується.
  const [diff, setDiff] = useState<MethodologyPublicationDiff | null>(null);

  // Заведення методології з нуля: доти ідентифікатор методології не було
  // звідки взяти взагалі, і конфігуратор версій працював лише над тим, що
  // завіз офлайновий генератор тестових даних.
  const [creating, setCreating] = useState(false);
  const [code, setCode] = useState('');
  const [name, setName] = useState('');
  const [kind, setKind] = useState<MethodologyKind>('DataDriven');
  const [group, setGroup] = useState('');

  // Яку версію проганяємо; `null` — діалог симуляції закритий.
  const [simulating, setSimulating] = useState<{ id: number; versionId: number } | null>(null);
  const [simulationPeriod, setSimulationPeriod] = useState(currentPeriodKey());
  const [simulationResult, setSimulationResult] = useState<SimulationResultDto | null>(null);

  const methodologies = useQuery({
    queryKey: ['methodologies'],
    queryFn: () => apiFetch<MethodologyDto[]>('/api/v1/methodologies'),
  });

  const create = useMutation({
    mutationFn: () =>
      createMethodology({
        code,
        nameL10n: { en: name },
        kind,
        group: group.trim() === '' ? null : group,
      }),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['methodologies'] });
      setCreating(false);
      setCode('');
      setName('');
      setGroup('');
      showDone(t('methodologies.created'));
    },
    onError: showApiError,
  });

  const publish = useMutation({
    mutationFn: (target: { id: number; versionId: number; reason: string; from: string }) =>
      apiFetch<MethodologyPublicationDiff>(`/api/v1/methodologies/${target.id}/versions/${target.versionId}/publish`, {
        method: 'POST',
        body: JSON.stringify({
          changeReason: target.reason,
          // ⚠ `date` дає рівно `YYYY-MM-DD` — форму `DateOnly` сервера. Через
          // `Date` тут проходити не можна: `toISOString()` переводить у UTC і
          // ввечері зсуває дату на добу назад.
          effectiveFrom: target.from,
        } satisfies PublishMethodologyRequest),
      }),
    onSuccess: async (published) => {
      await queryClient.invalidateQueries({ queryKey: ['methodologies'] });
      setPublishing(null);
      setReason('');
      setEffectiveFrom('');

      // ⚠ Diff показується ПІСЛЯ публікації, а не замість неї: перед нею те
      // саме питання відповідає прогін без запису (`ФВ-13.5`, кнопка поруч).
      // Тут — звіт про те, що щойно змінилося в числах.
      setDiff(published);
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
      <PageHeader
        title={t('methodologies.title')}
        actions={
          can(session.data, 'Calculation.EditFormula') && (
            <Button variant="default" onClick={() => setCreating(true)}>
              {t('methodologies.newMethodology')}
            </Button>
          )
        }
      />
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
                    {/* ⛔ Вхід у конфігуратор версій. Без нього чернетки не
                        видно НІДЕ: цей перелік за побудовою показує лише
                        опубліковані версії — те, чим рахують, — і кнопка
                        «Опублікувати» поруч стояла для версій, яких він не
                        містить (`ФВ-9.15`). */}
                    <Button
                      size="compact-xs"
                      variant="light"
                      component={Link}
                      to={`/admin/methodologies/${String(methodology.id)}/versions`}
                    >
                      {t('methodologies.versionsTitle')}
                    </Button>

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
        opened={creating}
        onClose={() => setCreating(false)}
        title={t('methodologies.newMethodologyTitle')}
      >
        <Stack gap="sm">
          <TextInput
            label={t('methodologies.code')}
            description={t('methodologies.methodologyCodeHint')}
            value={code}
            onChange={(event) => setCode(event.currentTarget.value)}
            data-autofocus
          />

          <TextInput
            label={t('methodologies.name')}
            value={name}
            onChange={(event) => setName(event.currentTarget.value)}
          />

          {/* ⛔ Природа методології — несуче поле, а не описове. `Bespoke`-модулі
              правила прив'язки не мають у принципі, а `Library` не рахує ні для
              кого — і саме на цьому тримається перевірка публікації. */}
          <Select
            label={t('methodologies.kind')}
            description={t('methodologies.kindHint')}
            allowDeselect={false}
            value={kind}
            data={[
              { value: 'DataDriven', label: 'DataDriven' },
              { value: 'Bespoke', label: 'Bespoke' },
              { value: 'Library', label: 'Library' },
            ]}
            onChange={(value) =>
              setKind(value === 'Bespoke' ? 'Bespoke' : value === 'Library' ? 'Library' : 'DataDriven')
            }
          />

          <TextInput
            label={t('methodologies.group')}
            description={t('methodologies.groupHint')}
            value={group}
            onChange={(event) => setGroup(event.currentTarget.value)}
          />

          <Button
            disabled={code.trim().length === 0 || name.trim().length === 0}
            loading={create.isPending}
            onClick={() => create.mutate()}
          >
            {t('methodologies.create')}
          </Button>
        </Stack>
      </Modal>

      <Modal
        opened={diff !== null}
        onClose={() => setDiff(null)}
        title={t('methodologies.diffTitle')}
        size="lg"
      >
        {diff !== null && (
          <Stack gap="sm">
            {/* ⛔ Обидва режими — обов'язково (`D-78`): їх зміна не видна в
                жодному рядку формули, а числа змінюються всі — 3.3 % між Actual і
                Fixed360 на тих самих даних. */}
            <Text size="sm">
              {t('methodologies.diffNumeric')}: {diff.numeric.before} → {diff.numeric.after}
            </Text>
            <Text size="sm">
              {t('methodologies.diffCalendar')}: {diff.calendar.before} → {diff.calendar.after}
            </Text>

            <Text fw={600}>{t('methodologies.diffChanges')}</Text>
            {diff.changes.length === 0 ? (
              <Text size="sm" c="dimmed">
                {t('methodologies.diffNone')}
              </Text>
            ) : (
              <Table striped withTableBorder>
                <Table.Thead>
                  <Table.Tr>
                    <Table.Th>{t('methodologies.testCode')}</Table.Th>
                    <Table.Th>{t('methodologies.output')}</Table.Th>
                    <Table.Th>{t('methodologies.before')}</Table.Th>
                    <Table.Th>{t('methodologies.after')}</Table.Th>
                  </Table.Tr>
                </Table.Thead>
                <Table.Tbody>
                  {diff.changes.map((change) => (
                    <Table.Tr key={`${change.testCode}:${change.outputCode}:${String(change.substanceEntryId)}`}>
                      <Table.Td>{change.testCode}</Table.Td>
                      <Table.Td>{change.outputCode}</Table.Td>
                      <Table.Td>{change.before ?? '—'}</Table.Td>
                      <Table.Td>{change.after}</Table.Td>
                    </Table.Tr>
                  ))}
                </Table.Tbody>
              </Table>
            )}

            {/* ⚠ Попередження не валять публікацію, але й мовчати про них
                не можна: оголошений і невжитий аргумент найчастіше означає
                описку в імені токена. */}
            {(diff.warnings ?? []).length > 0 && (
              <>
                <Text fw={600}>{t('methodologies.warnings')}</Text>
                <Code block>{(diff.warnings ?? []).join('\n')}</Code>
              </>
            )}
          </Stack>
        )}
      </Modal>

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
