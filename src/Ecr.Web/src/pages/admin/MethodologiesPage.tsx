import { useState, type JSX } from 'react';
import {
  Badge,
  Button,
  Code,
  Group,
  Modal,
  ScrollArea,
  Select,
  Stack,
  Table,
  Text,
  TextInput,
} from '@mantine/core';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Link } from 'react-router-dom';
import { apiFetch } from '@/api/client';
import { queryKeys } from '@/api/queryKeys';
import type {
  MethodologyDto,
  MethodologyKind,
  SimulateMethodologyRequest,
  SimulationResultDto,
} from '@/api/types';
import { createMethodology } from '@/features/methodologies/api';
import { MethodologyPackageImport } from '@/features/methodologies/MethodologyPackageImport';
import { localized } from '@/shared/i18n/localized';
import { can, useSession } from '@/shared/session/useSession';
import { AsyncBoundary } from '@/shared/ui/AsyncBoundary';
import { PageHeader } from '@/shared/ui/PageHeader';
import { PeriodPicker } from '@/shared/ui/PeriodPicker';
import { Timestamp } from '@/shared/ui/Timestamp';
import { showApiError, showDone } from '@/shared/ui/notify';
import { t } from '@/shared/i18n';
import { usePendingLoading } from '@/features/common/usePendingLoading';

/**
 * Перелік методологій: заведення, імпорт пакета, прогін без запису й вхід у
 * конфігуратор версій.
 *
 * ⛔ L9-43: публікації тут БІЛЬШЕ НЕМАЄ — і це не втрата. Перелік за побудовою
 * містить лише ОПУБЛІКОВАНІ версії (`ListMethodologiesHandler.Map` відкидає
 * решту), тож кнопка «Опублікувати» з умовою `status !== 'Published'` не
 * з'являлась ніколи, а з нею мертвими були діалог причини/дати, мутація й
 * діалог diff. Публікація з причиною, датою чинності й diff результатів живе
 * на екрані версій (`MethodologyVersionsPage.tsx`) — там, де версію доводять
 * до готовності.
 */
export function MethodologiesPage(): JSX.Element {
  const queryClient = useQueryClient();
  const session = useSession();
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
    queryKey: queryKeys.methodologies.list(),
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
      await queryClient.invalidateQueries({ queryKey: queryKeys.methodologies.list() });
      setCreating(false);
      setCode('');
      setName('');
      setGroup('');
      showDone(t('methodologies.created'));
    },
    onError: showApiError,
  });

  // ⚠ `ФВ-14.26`: спінер на кнопці — лише після 100 мс дії, не з першого кадру.
  const createLoading = usePendingLoading(create.isPending);

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

  const simulateLoading = usePendingLoading(simulate.isPending);

  return (
    <>
      <PageHeader
        title={t('methodologies.title')}
        actions={
          <Group gap="xs">
            {/* Імпорт пакета з AF заводить і формули, і константи — тому обидва права. */}
            {can(session.data, 'Calculation.EditFormula') && can(session.data, 'Calculation.EditConstant') && (
              <MethodologyPackageImport />
            )}
            {can(session.data, 'Calculation.EditFormula') && (
              <Button variant="default" onClick={() => setCreating(true)}>
                {t('methodologies.newMethodology')}
              </Button>
            )}
          </Group>
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
                          {/* ⛔ Шаблонний рядок тут РОЗІБРАНО на вузли, а не
                              замінено на `formatDate(...)` всередині нього.
                              Обидва варіанти чесні, але вони платять різним:
                              `formatDate` у рядку дає читабельний текст і
                              ВТРАЧАЄ точне значення — його нема де лишити, бо
                              рядок не має атрибутів. Тут же підстановка була в
                              JSX-дітях `Badge`, а не в параметрі `t()` (як
                              `periods.reopenedUntil` у `PeriodsPage`), тож
                              вузол вкладається без жодної втрати: `<time
                              datetime>` лишається в розмітці. Втрачати
                              точність там, де її можна не втрачати, підстав
                              немає. */}
                          {version.effectiveFrom === null ? (
                            ''
                          ) : (
                            <>
                              {' · '}
                              {/* ⚠ `dateOnly`: та сама календарна дата набуття
                                  чинності, що й у таблиці версій. */}
                              <Timestamp value={version.effectiveFrom} dateOnly />
                            </>
                          )}
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
            loading={createLoading}
            onClick={() => {
              if (create.isPending) return;
              create.mutate();
            }}
          >
            {t('methodologies.create')}
          </Button>
        </Stack>
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
        {/* ⛔ UI-06: `NumberInput` → `PeriodPicker` (`DIRECTIVE-15-FRONTEND.md:129`).
            `simulationPeriod` — локальний стан діалогу, ніколи не `null`
            (стартує з `currentPeriodKey()`); `value ?? simulationPeriod`
            зберігає стару поведінку очищеного поля — лишає попередній вибір.

            ⚠ `description` тут раніше ніс `methodologies.simulatePeriodHint`
            («нічого не зберігається»), а `PeriodPicker` власний `description`
            уже віддає під підпис періоду мовою інтерфейсу — тексти не можуть
            стояти в одному місці одночасно. Підказка не загублена, а винесена
            окремим рядком під контролом.

            ⚠ `data-autofocus` НЕ перенесено: `PeriodPicker` не проксує довільні
            HTML-атрибути на внутрішній `NumberInput` (лише `id`), а розширювати
            його API заради одного місця — поза межами цього завдання. Судження:
            втрата дрібна (перший фокус у діалозі просто не встановлюється
            автоматично), задокументовано тут і в звіті PR. */}
        <PeriodPicker
          value={simulationPeriod}
          onChange={(value) => setSimulationPeriod(value ?? simulationPeriod)}
        />
        <Text size="xs" c="dimmed" mt="xs">
          {t('methodologies.simulatePeriodHint')}
        </Text>

        <Button
          mt="md"
          loading={simulateLoading}
          onClick={() => {
            // ⛔ L9-37: спінер (`usePendingLoading`) з'являється лише після 100 мс —
            // до того кнопка активна, і подвійний клік/Enter слав два прогони.
            if (simulate.isPending) return;
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
