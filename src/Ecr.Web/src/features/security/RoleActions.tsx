import { useState, type JSX } from 'react';
import { Alert, Badge, Button, Group, Modal, Text, TextInput } from '@mantine/core';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { EcrApiError, apiFetch } from '@/api/client';
import type { components } from '@/api/schema';
import type { RoleIdResponse, RoleView } from '@/api/types';
import { showApiError, showDone } from '@/shared/ui/notify';
import { t } from '@/shared/i18n';

// ⚠ Прямо зі схеми: `api/types.ts` — спільний файл поза межами цієї підзадачі.
type RenameRoleRequest = components['schemas']['RenameRoleRequest'];

/** Роль не видаляється: на ній тримаються призначення або гранти. */
const ROLE_CONFLICT = 'ECR-SEC-0409';

/** Що тримається на ролі, якщо відмова саме про це. */
export interface RoleUsage {
  assignments: number;
  grants: number;
}

/**
 * Лічильники з відмови видалення ролі (директива №15, `BE-14`).
 *
 * ⛔ `null`, а не нулі, коли відмова інша: `ECR-SEC-0409` — це ще й «роль
 * вбудована» та «код зайнято», і там лічильників немає. Нулі стверджували б
 * факт, якого сервер не казав.
 *
 * ⚠ Числа в розширеннях сервер пише рядками — звідси `Number(...)`.
 */
export function roleUsage(error: unknown): RoleUsage | null {
  if (!(error instanceof EcrApiError) || error.problem.errorCode !== ROLE_CONFLICT) return null;

  const assignments = Number(error.problem.extensions2?.['assignments']);
  const grants = Number(error.problem.extensions2?.['grants']);

  return Number.isFinite(assignments) && Number.isFinite(grants) ? { assignments, grants } : null;
}

type Action = 'clone' | 'rename' | 'delete';

/**
 * Дії над роллю в рядку матриці прав: клонувати, перейменувати, видалити.
 *
 * ⚠ Вбудована роль отримує лише «клонувати»: сервер відмовив би на решту
 * (`409`), і кнопка, що завжди завершується відмовою, — це загадка, а не дія.
 */
export function RoleActions({ role }: { role: RoleView }): JSX.Element {
  const queryClient = useQueryClient();
  const [action, setAction] = useState<Action | null>(null);
  const [code, setCode] = useState('');

  const close = (): void => {
    setAction(null);
    setCode('');
    remove.reset();
  };

  const done = async (message: string): Promise<void> => {
    await queryClient.invalidateQueries({ queryKey: ['roles'] });
    close();
    showDone(message);
  };

  const clone = useMutation({
    mutationFn: () =>
      apiFetch<RoleIdResponse>(`/api/v1/roles/${role.id}/clone`, {
        method: 'POST',
        body: JSON.stringify({ code: code.trim() } satisfies RenameRoleRequest),
      }),
    onSuccess: () => done(t('security.roleCloned')),
    onError: showApiError,
  });

  const rename = useMutation({
    mutationFn: () =>
      apiFetch<void>(`/api/v1/roles/${role.id}/code`, {
        method: 'PUT',
        body: JSON.stringify({ code: code.trim() } satisfies RenameRoleRequest),
      }),
    onSuccess: () => done(t('security.roleRenamed')),
    onError: showApiError,
  });

  // ⚠ `onError` немає навмисно, а `handled` каже про це страхувальній сітці
  // (`app/queryClient.ts`): відмову показує сам діалог. «Роль використовується»
  // — відповідь по суті, і повторення того самого запиту нічого не змінить.
  const remove = useMutation<void, Error>({
    meta: { handled: true },
    mutationFn: () => apiFetch<void>(`/api/v1/roles/${role.id}`, { method: 'DELETE' }),
    onSuccess: () => done(t('security.roleDeleted')),
  });

  const usage = roleUsage(remove.error);
  const open = (next: Action): void => {
    setCode(next === 'rename' ? role.code : '');
    setAction(next);
  };

  return (
    <>
      <Group gap="xs" mt="xs" wrap="nowrap">
        <Button size="compact-xs" variant="subtle" onClick={() => open('clone')}>
          {t('security.cloneRole')}
        </Button>
        {!role.isBuiltIn && (
          <>
            <Button size="compact-xs" variant="subtle" onClick={() => open('rename')}>
              {t('security.renameRole')}
            </Button>
            <Button size="compact-xs" variant="subtle" color="statusError" onClick={() => open('delete')}>
              {t('common.delete')}
            </Button>
          </>
        )}
      </Group>

      <Modal
        opened={action === 'clone' || action === 'rename'}
        onClose={close}
        title={`${t(action === 'rename' ? 'security.renameRole' : 'security.cloneRole')} · ${role.code}`}
      >
        <TextInput
          label={t('security.newRoleCode')}
          value={code}
          onChange={(event) => setCode(event.currentTarget.value)}
          data-autofocus
        />
        <Group justify="flex-end" mt="md">
          <Button variant="default" onClick={close}>
            {t('common.cancel')}
          </Button>
          <Button
            disabled={code.trim().length === 0 || code.trim() === role.code}
            loading={clone.isPending || rename.isPending}
            onClick={() => (action === 'rename' ? rename.mutate() : clone.mutate())}
          >
            {t('common.save')}
          </Button>
        </Group>
      </Modal>

      <Modal opened={action === 'delete'} onClose={close} title={`${t('common.delete')} · ${role.code}`}>
        {remove.error === null ? (
          <Text size="sm">{t('security.deleteRoleConfirm', { code: role.code })}</Text>
        ) : (
          <Alert color="statusWarning" title={t('security.roleDeleteRefused')}>
            <Text size="sm">{remove.error.message}</Text>
            {usage !== null && (
              <Group gap="xs" mt="xs">
                <Badge color="statusWarning">
                  {t('security.roleAssignments')}: {usage.assignments}
                </Badge>
                <Badge color="statusWarning">
                  {t('security.grants')}: {usage.grants}
                </Badge>
              </Group>
            )}
          </Alert>
        )}
        <Group justify="flex-end" mt="md">
          <Button variant="default" onClick={close}>
            {t('common.cancel')}
          </Button>
          {/* ⛔ Після відмови кнопки «видалити» вже немає: повтор дав би ту саму
              відповідь. Спершу зняти призначення й гранти. */}
          {remove.error === null && (
            <Button color="statusError" loading={remove.isPending} onClick={() => remove.mutate()}>
              {t('common.delete')}
            </Button>
          )}
        </Group>
      </Modal>
    </>
  );
}
