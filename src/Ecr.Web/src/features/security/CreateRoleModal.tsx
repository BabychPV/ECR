import { useState, type JSX } from 'react';
import { Badge, Button, Checkbox, Group, Modal, ScrollArea, Stack, Text, TextInput } from '@mantine/core';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import type { CreateRoleRequest, PermissionCatalogItem, RoleIdResponse } from '@/api/types';
import { PermissionCaption } from '@/features/security/PermissionCaption';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { LocalizedInput, hasAnyText, type LocalizedValue } from '@/shared/ui/LocalizedInput';
import { showApiError, showDone } from '@/shared/ui/notify';
import { t } from '@/shared/i18n';
import { useOpenGeneration } from '@/features/security/useOpenGeneration';

interface CreateRoleModalProps {
  opened: boolean;
  onClose: () => void;
  /** Повний каталог прав, відсортований за кодом. */
  permissions: PermissionCatalogItem[];
  permissionCatalogError: Error | null;
  onRetry: () => void;
}

/**
 * Діалог «New role» (`ФВ-6.3`).
 *
 * ⛔ Живий дефект (2026-09-24, замір на стенді): чернетка діалогу (код, назва
 * трьома мовами, права) була станом `SecurityPage`, тож КОЖЕН символ
 * перерендерював сторінку разом із матрицею ролей × 41 право — медіана
 * 97–120 мс на символ (dev) проти 28–52 мс у решті діалогів. Той самий клас
 * дефекту, що вже знятий на `TemplateVersionPage` (`LocalDraft.tsx`).
 * Тепер сторінка знає лише «відкрито/закрито», а чернетка живе в
 * `CreateRoleForm` нижче.
 *
 * ⚠ Скидання чернетки при Cancel/закритті (аудит-пас 5) тепер структурне:
 * кожне відкриття монтує нову, порожню форму (`useOpenGeneration`).
 */
export function CreateRoleModal({
  opened,
  onClose,
  permissions,
  permissionCatalogError,
  onRetry,
}: CreateRoleModalProps): JSX.Element {
  const generation = useOpenGeneration(opened);

  return (
    <Modal opened={opened} onClose={onClose} title={t('security.createRole')} size="lg">
      <CreateRoleForm
        key={generation}
        onClose={onClose}
        permissions={permissions}
        permissionCatalogError={permissionCatalogError}
        onRetry={onRetry}
      />
    </Modal>
  );
}

function CreateRoleForm({
  onClose,
  permissions,
  permissionCatalogError,
  onRetry,
}: Omit<CreateRoleModalProps, 'opened'>): JSX.Element {
  const queryClient = useQueryClient();
  const [roleCode, setRoleCode] = useState('');
  const [roleName, setRoleName] = useState<LocalizedValue>({});
  const [rolePermissions, setRolePermissions] = useState<string[]>([]);

  // ⚠ Права обираються з тих, що вже оголошені: вигадати право на клієнті
  // не можна, сервер приймає лише коди з каталогу.
  const createRole = useMutation({
    mutationFn: () =>
      apiFetch<RoleIdResponse>('/api/v1/roles', {
        method: 'POST',
        body: JSON.stringify({
          code: roleCode.trim(),
          nameL10n: roleName,
          permissionCodes: rolePermissions,
        } satisfies CreateRoleRequest),
      }),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['roles'] });
      onClose();
      showDone(t('security.roleCreated'));
    },
    onError: showApiError,
  });

  return (
    <>
      <TextInput
        label={t('security.roleCode')}
        description={t('security.roleCodeHint')}
        value={roleCode}
        onChange={(event) => setRoleCode(event.currentTarget.value)}
        data-autofocus
      />

      <LocalizedInput label={t('security.roleName')} value={roleName} onChange={setRoleName} />

      <Text size="sm" mt="sm" fw={600}>
        {t('security.permissions')}
      </Text>
      <Text size="xs" c="dimmed" mb="xs">
        {t('security.permissionsHint')}
      </Text>

      {/* ⚠ Перелік — це ПОВНИЙ каталог (директива №11, T2), а не перетин
          того, що вже оголошено в наявних ролях: право без жодного носія
          інакше не можна було б призначити НІКОМУ. */}
      {/* ⛔ Каталог не приїхав — причина замість порожнього місця, і «Зберегти»
          вимкнено: роль без прав, збережена тому, що прапорців не було, —
          не вибір адміністратора. */}
      <ErrorAlert error={permissionCatalogError} onRetry={onRetry} />
      <ScrollArea h={220}>
        <Stack gap="xs">
          {permissions.map((permission) => (
            <Checkbox
              key={permission.code}
              label={
                <Group gap="xs" wrap="nowrap">
                  {/* ⚠ Той самий підпис, що в матриці (`U-11`). */}
                  <PermissionCaption code={permission.code} />
                  {/* ⚠ Небезпечні позначені ОКРЕМО (ФВ-6.12, D-40). */}
                  {permission.isDangerous && (
                    <Badge size="xs" color="statusError" variant="light">
                      {t('security.dangerous', { count: 1 })}
                    </Badge>
                  )}
                </Group>
              }
              checked={rolePermissions.includes(permission.code)}
              onChange={(event) => {
                // ⛔ `event.currentTarget` React обнуляє одразу після
                // обробника, а під `StrictMode` апдейтер кличеться ДВІЧІ —
                // читати його ліниво в апдейтері означало `TypeError`.
                const checked = event.currentTarget.checked;

                setRolePermissions((current) =>
                  checked
                    ? [...current, permission.code]
                    : current.filter((code) => code !== permission.code),
                );
              }}
            />
          ))}
        </Stack>
      </ScrollArea>

      <Group justify="flex-end" mt="md">
        <Button variant="default" onClick={onClose}>
          {t('common.cancel')}
        </Button>
        <Button
          disabled={
            roleCode.trim().length === 0 || !hasAnyText(roleName) || permissionCatalogError !== null
          }
          loading={createRole.isPending}
          onClick={() => createRole.mutate()}
        >
          {t('common.save')}
        </Button>
      </Group>
    </>
  );
}
