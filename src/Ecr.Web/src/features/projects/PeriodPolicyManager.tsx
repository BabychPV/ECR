import { useState, type JSX } from 'react';
import { Button, Group, Modal, NumberInput, Stack, Table, TextInput } from '@mantine/core';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import type { CreatePeriodPolicyRequest, PeriodPolicyDto, UpdatePeriodPolicyRequest } from '@/api/types';
import { AsyncBoundary } from '@/shared/ui/AsyncBoundary';
import { showApiError, showDone } from '@/shared/ui/notify';
import { t } from '@/shared/i18n';

/**
 * CRUD політик періодів (T6/#37).
 *
 * ⛔ До цього завести чи змінити політику можна було лише сідингом або рукою
 * DBA: `GET …/period-policies` уже існував (`A7-56`, форма створення проєкту
 * має з чого вибирати), а запису не було зовсім. Наслідок — річний пільговий
 * строк проєкту (`Project.YearGraceOffsetDays`) стояв літералом `45` завжди,
 * незалежно від обраної політики: змінити його інакше, ніж редагуючи
 * `09-seed.sql` і перерозгортаючи базу, не можна було нічим.
 *
 * ⚠ Зміна offsets наявної політики НЕ перераховує межі проєктів, які вже на
 * неї посилаються, автоматично: наступний `GET …/periods` (ідемпотентний)
 * підхопить нові значення сам.
 */
export function PeriodPolicyManager(): JSX.Element {
  const queryClient = useQueryClient();
  const [opened, setOpened] = useState(false);

  // Чернетка нового рядка — коду і чотирьох offsets.
  const [draftCode, setDraftCode] = useState('');
  const [draftOpen, setDraftOpen] = useState<number | null>(0);
  const [draftGrace, setDraftGrace] = useState<number | null>(15);
  const [draftHardClose, setDraftHardClose] = useState<number | null>(45);
  const [draftYearGrace, setDraftYearGrace] = useState<number | null>(45);

  const policies = useQuery({
    queryKey: ['period-policies'],
    queryFn: () => apiFetch<PeriodPolicyDto[]>('/api/v1/projects/period-policies'),
    enabled: opened,
  });

  const refresh = async (): Promise<void> => {
    await queryClient.invalidateQueries({ queryKey: ['period-policies'] });
  };

  const create = useMutation({
    mutationFn: () =>
      apiFetch<PeriodPolicyDto>('/api/v1/projects/period-policies', {
        method: 'POST',
        body: JSON.stringify({
          code: draftCode.trim(),
          openOffsetDays: draftOpen ?? 0,
          graceOffsetDays: draftGrace ?? 0,
          hardCloseOffsetDays: draftHardClose ?? 0,
          yearGraceOffsetDays: draftYearGrace ?? 0,
        } satisfies CreatePeriodPolicyRequest),
      }),
    onSuccess: async () => {
      await refresh();
      setDraftCode('');
      showDone(t('periods.policyCreated'));
    },

    // ⚠ Код зайнятий (`ECR-PRD-4091`) або пільговий строк довший за жорстке
    // закриття (`ECR-PRD-4225`) приходять поясненням, а не «не вдалося
    // зберегти»: обидві помилки виправні прямо тут.
    onError: showApiError,
  });

  const [editing, setEditing] = useState<Record<number, UpdatePeriodPolicyRequest>>({});

  const update = useMutation({
    mutationFn: (id: number) =>
      apiFetch<PeriodPolicyDto>(`/api/v1/projects/period-policies/${id}`, {
        method: 'PUT',
        body: JSON.stringify(editing[id]),
      }),
    onSuccess: async () => {
      await refresh();
      showDone(t('periods.policyUpdated'));
    },
    onError: showApiError,
  });

  const draftFor = (policy: PeriodPolicyDto): UpdatePeriodPolicyRequest =>
    editing[policy.id] ?? {
      openOffsetDays: policy.openOffsetDays,
      graceOffsetDays: policy.graceOffsetDays,
      hardCloseOffsetDays: policy.hardCloseOffsetDays,
      yearGraceOffsetDays: policy.yearGraceOffsetDays,
    };

  const setDraft = (id: number, patch: Partial<UpdatePeriodPolicyRequest>, policy: PeriodPolicyDto): void => {
    setEditing({ ...editing, [id]: { ...draftFor(policy), ...patch } });
  };

  return (
    <>
      <Button size="xs" variant="default" onClick={() => setOpened(true)}>
        {t('periods.managePolicies')}
      </Button>

      <Modal
        opened={opened}
        onClose={() => setOpened(false)}
        title={t('periods.managePolicies')}
        size="xl"
      >
        {/*
         * ⛔ Невдалий запит політик раніше виглядав ТОЧНІСІНЬКО як «політик ще
         * не заведено»: `policies.data ?? []` ковтав помилку мовчки, і форма
         * створення нижче лишалася повністю робочою — адмін міг завести
         * ДРУГУ політику з тим самим кодом, бо перша «не завантажилася», а не
         * «не існує» (`Q-254`, той самий клас дефекту, що й `A7-04`).
         *
         * ⚠ `emptyTitle`/`emptyHint` навмисно НЕ задані: власний рядок
         * каталогу для «політик ще немає» довелося б додавати в
         * `09-seed.sql` — файл поза межею цієї картки (DDL/seed —
         * ексклюзивна власність оркестратора). `AsyncBoundary` без цих
         * пропів сам показує вже наявний і навантажений
         * `t('state.emptyTitle')` — цього достатньо, щоб порожній стан НЕ
         * малював `role="alert"` і був відрізненний від помилки.
         */}
        <AsyncBoundary<PeriodPolicyDto[]>
          isPending={opened && policies.isPending}
          error={policies.error}
          data={opened ? policies.data : undefined}
          isEmpty={(all) => all.length === 0}
          skeleton="table"
          onRetry={() => void policies.refetch()}
        >
          {(all) => (
            <Table striped>
              <Table.Thead>
                <Table.Tr>
                  <Table.Th>{t('periods.policyCode')}</Table.Th>
                  <Table.Th>{t('periods.policyOpenOffset')}</Table.Th>
                  <Table.Th>{t('periods.policyGraceOffset')}</Table.Th>
                  <Table.Th>{t('periods.policyHardClose')}</Table.Th>
                  <Table.Th>{t('periods.policyYearGrace')}</Table.Th>
                  <Table.Th />
                </Table.Tr>
              </Table.Thead>
              <Table.Tbody>
                {all.map((policy) => {
                  const draft = draftFor(policy);

                  return (
                    <Table.Tr key={policy.id}>
                      <Table.Td>{policy.code}</Table.Td>
                      <Table.Td>
                        {/* ⚠ Підпис колонки вже стоїть у `Table.Th`: `aria-label`,
                            а не видимий `label`, — інакше кожна клітинка була б
                            удвічі вищою за сам текст числа. */}
                        <NumberInput
                          size="xs"
                          aria-label={t('periods.policyOpenOffset')}
                          value={draft.openOffsetDays}
                          onChange={(value) =>
                            setDraft(policy.id, { openOffsetDays: typeof value === 'number' ? value : 0 }, policy)
                          }
                        />
                      </Table.Td>
                      <Table.Td>
                        <NumberInput
                          size="xs"
                          min={0}
                          aria-label={t('periods.policyGraceOffset')}
                          value={draft.graceOffsetDays}
                          onChange={(value) =>
                            setDraft(policy.id, { graceOffsetDays: typeof value === 'number' ? value : 0 }, policy)
                          }
                        />
                      </Table.Td>
                      <Table.Td>
                        <NumberInput
                          size="xs"
                          min={0}
                          aria-label={t('periods.policyHardClose')}
                          value={draft.hardCloseOffsetDays}
                          onChange={(value) =>
                            setDraft(
                              policy.id, { hardCloseOffsetDays: typeof value === 'number' ? value : 0 }, policy,
                            )
                          }
                        />
                      </Table.Td>
                      <Table.Td>
                        <NumberInput
                          size="xs"
                          min={0}
                          aria-label={t('periods.policyYearGrace')}
                          value={draft.yearGraceOffsetDays}
                          onChange={(value) =>
                            setDraft(
                              policy.id, { yearGraceOffsetDays: typeof value === 'number' ? value : 0 }, policy,
                            )
                          }
                        />
                      </Table.Td>
                      <Table.Td>
                        <Button
                          size="compact-xs"
                          variant="subtle"
                          loading={update.isPending && update.variables === policy.id}
                          onClick={() => update.mutate(policy.id)}
                        >
                          {t('common.save')}
                        </Button>
                      </Table.Td>
                    </Table.Tr>
                  );
                })}
              </Table.Tbody>
            </Table>
          )}
        </AsyncBoundary>

        {/* ⛔ Форма нової політики — окремим блоком, а не рядком таблиці:
            рядок таблиці має Id, якого в чернетки ще немає. */}
        <Stack gap="xs" mt="lg">
          <TextInput
            label={t('periods.policyCode')}
            value={draftCode}
            onChange={(event) => setDraftCode(event.currentTarget.value)}
          />

          <Group grow>
            <NumberInput
              label={t('periods.policyOpenOffset')}
              value={draftOpen ?? ''}
              onChange={(value) => setDraftOpen(typeof value === 'number' ? value : null)}
            />
            <NumberInput
              label={t('periods.policyGraceOffset')}
              min={0}
              value={draftGrace ?? ''}
              onChange={(value) => setDraftGrace(typeof value === 'number' ? value : null)}
            />
            <NumberInput
              label={t('periods.policyHardClose')}
              min={0}
              value={draftHardClose ?? ''}
              onChange={(value) => setDraftHardClose(typeof value === 'number' ? value : null)}
            />
            <NumberInput
              label={t('periods.policyYearGrace')}
              min={0}
              value={draftYearGrace ?? ''}
              onChange={(value) => setDraftYearGrace(typeof value === 'number' ? value : null)}
            />
          </Group>

          <Group justify="flex-end">
            {/* ⛔ Заблоковано і на невдалому запиті переліку теж: без нього
                адмін бачив би порожню таблицю й міг завести ДРУГУ політику з
                кодом, що вже зайнятий, — просто тому, що перша не
                завантажилася, а не тому, що її немає (`Q-254`). */}
            <Button
              disabled={draftCode.trim().length === 0 || Boolean(policies.error)}
              loading={create.isPending}
              onClick={() => create.mutate()}
            >
              {t('periods.policyCreate')}
            </Button>
          </Group>
        </Stack>

        <Group justify="flex-end" mt="md">
          <Button variant="default" onClick={() => setOpened(false)}>
            {t('common.cancel')}
          </Button>
        </Group>
      </Modal>
    </>
  );
}
