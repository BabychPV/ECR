import { useEffect, useState, type JSX } from 'react';
import { Button, Group, NumberInput, Select, Switch, Table } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { EcrApiError, apiFetch } from '@/api/client';
import type { ReplaceGrantsRequest, ResourceGrantDto, RoleView } from '@/api/types';
import { AsyncBoundary } from '@/shared/ui/AsyncBoundary';
import { t } from '@/shared/i18n';

/**
 * Ресурсні гранти ролі: до ЧОГО саме вона відкриває доступ.
 *
 * ⛔ Панель з'явилася після `A7-22`. Права відповідають на питання «що людина
 * вміє», гранти — «до чого»; без другої відповіді перша не відкриває нічого.
 * Доти гранти не міг створити ніхто: таблиця `sec.ResourceGrant` існувала від
 * Етапу 3, правило «IsDeny виграє завжди» було реалізоване в читанні, а
 * записати грант не було чим. Користувач із усіма 38 правами бачив ПОРОЖНІЙ
 * перелік проєктів.
 *
 * ⚠ Набір зберігається ЦІЛКОМ, а не по одному запису. Гранти — це відповідь на
 * питання «що покриває ця роль», і вона має бути видима одним поглядом;
 * часткові правки лишають стан, у якому джерело доступу не відновлюється
 * (ФВ-6.6).
 */
export function GrantsPanel({ roles }: { roles: RoleView[] }): JSX.Element {
  const [roleId, setRoleId] = useState<number | null>(null);
  const [draft, setDraft] = useState<ResourceGrantDto[]>([]);
  const queryClient = useQueryClient();

  const grants = useQuery({
    queryKey: ['grants', roleId],
    queryFn: () => apiFetch<ResourceGrantDto[]>(`/api/v1/roles/${roleId ?? 0}/grants`),
    enabled: roleId !== null,
  });

  // ⚠ Чернетка синхронізується з відповіддю сервера, а не заводиться раз:
  // інакше перемикання ролі показувало б гранти попередньої.
  useEffect(() => {
    if (grants.data !== undefined) setDraft(grants.data);
  }, [grants.data]);

  const save = useMutation({
    mutationFn: (next: ResourceGrantDto[]) =>
      apiFetch(`/api/v1/roles/${roleId ?? 0}/grants`, {
        method: 'PUT',
        body: JSON.stringify({ grants: next } satisfies ReplaceGrantsRequest),
      }),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['grants', roleId] });
      notifications.show({ color: 'green', message: t('grants.saved') });
    },
    onError: (error) => {
      notifications.show({
        color: 'red',
        message: error instanceof EcrApiError ? error.message : String(error),
      });
    },
  });

  return (
    <>
      <Group mb="sm" gap="xs">
        <Select
          size="xs"
          miw={200}
          label={t('security.role')}
          placeholder={t('grants.pickRole')}
          data={roles.map((r) => ({ value: String(r.id), label: r.code }))}
          value={roleId === null ? null : String(roleId)}
          onChange={(value) => setRoleId(value === null ? null : Number(value))}
        />

        {roleId !== null && (
          <>
            <Button
              size="xs"
              variant="default"
              onClick={() =>
                setDraft([
                  ...draft,
                  { resourceKind: 'Project', resourceId: 0, level: 'Read', isDeny: false },
                ])
              }
            >
              {t('grants.add')}
            </Button>

            <Button size="xs" loading={save.isPending} onClick={() => save.mutate(draft)}>
              {t('common.save')}
            </Button>
          </>
        )}
      </Group>

      {/*
       * ⚠ Перелік редагується як ЧЕРНЕТКА (`draft`), а завантажене значення
       * лише наповнює її. Тому обгортка дивиться на запит, а таблиця малює
       * чернетку: інакше щойно доданий рядок зникав би, доки не збережено.
       *
       * ⛔ Порожній стан НЕ ховає кнопку «додати»: роль без грантів — це
       * звичайний початок роботи, а не збій. Ховати дію тут означало б
       * зробити перший грант недосяжним (`A7-22`).
       */}
      <AsyncBoundary<ResourceGrantDto[]>
        isPending={roleId !== null && grants.isPending}
        error={grants.error}
        data={roleId === null ? undefined : grants.data}
        isEmpty={() => draft.length === 0}
        emptyTitle={roleId === null ? t('grants.pickRole') : t('grants.empty')}
        emptyHint={roleId === null ? t('grants.pickRoleHint') : t('grants.emptyHint')}
        skeleton="table"
        onRetry={() => void grants.refetch()}
      >
        {() => (
        <Table striped withTableBorder className="ecr-sticky-head">
          <Table.Thead>
            <Table.Tr>
              <Table.Th>{t('grants.kind')}</Table.Th>
              <Table.Th>{t('grants.resource')}</Table.Th>
              <Table.Th>{t('grants.level')}</Table.Th>
              <Table.Th>{t('grants.deny')}</Table.Th>
              <Table.Th />
            </Table.Tr>
          </Table.Thead>
          <Table.Tbody>
            {draft.map((grant, index) => (
              <Table.Tr key={`${grant.resourceKind}:${grant.resourceId}:${index}`}>
                <Table.Td>
                  {/*
                   * ⚠ Тут `aria-label`, а не `label`: підпис уже стоїть у
                   * заголовку колонки і видно очима. Але читалка НЕ пов'язує
                   * `<th>` з полем усередині `<td>` — для неї це просто
                   * безіменний список, і без імені користувач чує «поле зі
                   * списком» чотири рази підряд.
                   */}
                  <Select
                    size="xs"
                    miw={110}
                    aria-label={`${t('grants.kind')} ${index + 1}`}
                    data={['Project', 'Sheet', 'Table', 'Column']}
                    value={grant.resourceKind}
                    onChange={(value) =>
                      value !== null && replace(index, { ...grant, resourceKind: value as never })
                    }
                  />
                </Table.Td>
                <Table.Td>
                  <NumberInput
                    size="xs"
                    miw={90}
                    aria-label={`${t('grants.resource')} ${index + 1}`}
                    value={grant.resourceId}
                    onChange={(value) =>
                      replace(index, {
                        ...grant,
                        resourceId: typeof value === 'number' ? value : 0,
                      })
                    }
                  />
                </Table.Td>
                <Table.Td>
                  <Select
                    size="xs"
                    miw={110}
                    aria-label={`${t('grants.level')} ${index + 1}`}
                    data={['Read', 'Write', 'Submit', 'Approve', 'Manage']}
                    value={grant.level}
                    onChange={(value) =>
                      value !== null && replace(index, { ...grant, level: value as never })
                    }
                  />
                </Table.Td>
                <Table.Td>
                  {/* ⚠ Заборона перекриває будь-який дозвіл будь-якого рівня
                      (ФВ-6.6) — тому вона окремим перемикачем, а не «рівнем
                      None»: рівень і заборона — різні речі. */}
                  <Switch
                    size="xs"
                    aria-label={`${t('grants.deny')} ${index + 1}`}
                    checked={grant.isDeny}
                    onChange={(event) =>
                      replace(index, { ...grant, isDeny: event.currentTarget.checked })
                    }
                  />
                </Table.Td>
                <Table.Td>
                  <Button
                    size="compact-xs"
                    color="red"
                    variant="subtle"
                    onClick={() => setDraft(draft.filter((_, i) => i !== index))}
                  >
                    {t('grants.remove')}
                  </Button>
                </Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>
        )}
      </AsyncBoundary>
    </>
  );

  function replace(index: number, grant: ResourceGrantDto): void {
    setDraft(draft.map((item, i) => (i === index ? grant : item)));
  }
}
