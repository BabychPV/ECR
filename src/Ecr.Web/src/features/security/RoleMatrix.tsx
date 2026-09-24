import { memo, type JSX } from 'react';
import { Badge, ScrollArea, Table } from '@mantine/core';
import type { PermissionCatalogItem, RoleView } from '@/api/types';
import { PermissionCaption } from '@/features/security/PermissionCaption';
import { RoleActions } from '@/features/security/RoleActions';
import { t } from '@/shared/i18n';

interface RoleMatrixProps {
  roles: RoleView[];
  /** Повний каталог прав, відсортований за кодом; посилання має бути стабільним. */
  permissions: PermissionCatalogItem[];
  canManage: boolean;
}

/**
 * Матриця ролей × прав (`SecurityPage`, вкладка «Ролі»).
 *
 * ⛔ `memo` — не прикраса: матриця — найдорожче дерево сторінки (ролі × 41
 * право), і перерендер `SecurityPage`, що не міняє ні ролей, ні каталогу, не
 * має її чіпати (живий дефект 2026-09-24: друк у «New role» — див.
 * `CreateRoleModal.tsx`). Тому сторінка передає `permissions` через
 * `useMemo`, а не новим масивом на кожен рендер.
 *
 * ⚠ Матриця показує **оголошені** права ролей; ефективні права користувача
 * рахує сервер (`/me`).
 */
export const RoleMatrix = memo(function RoleMatrix({ roles, permissions, canManage }: RoleMatrixProps): JSX.Element {
  return (
    // ⛔ `ScrollArea` без фіксованої висоти обмежує лише ШИРИНУ: `overflowX`
    // на самій `<Table>` прокручував усю сторінку разом із навігацією, а
    // `position: sticky` першої колонки рахує від контейнера прокрутки.
    <ScrollArea type="auto" offsetScrollbars>
      <Table striped withTableBorder className="ecr-sticky-head ecr-sticky-first">
        <Table.Thead>
          <Table.Tr>
            <Table.Th>{t('security.role')}</Table.Th>
            {/* ⛔ Підпис колонки — з каталогу рядків, код лишається другим
                рядком (`U-11`, `permissionLabel.ts`). */}
            {permissions.map((permission) => (
              <Table.Th key={permission.code}>
                <PermissionCaption code={permission.code} />
              </Table.Th>
            ))}
          </Table.Tr>
        </Table.Thead>
        <Table.Tbody>
          {roles.map((role) => (
            <Table.Tr key={role.id} opacity={role.isActive ? 1 : 0.5}>
              <Table.Td>
                {role.code}
                {/* ⛔ `opacity` невидима читалці — бейдж дає той самий факт
                    текстом (UX-аудит, знахідка 2/3). */}
                {!role.isActive && (
                  <Badge ml="xs" size="xs" color="gray" variant="outline">
                    {t('security.inactive')}
                  </Badge>
                )}
                {role.isBuiltIn && (
                  <Badge ml="xs" size="xs" variant="light">
                    {t('security.builtIn')}
                  </Badge>
                )}

                {/* ⚠ Небезпечні права показуються ОКРЕМО (ФВ-6.12, D-40). */}
                {role.dangerousPermissions.length > 0 && (
                  <Badge ml="xs" size="xs" color="statusError" variant="light">
                    {t('security.dangerous', { count: role.dangerousPermissions.length })}
                  </Badge>
                )}

                {/* ⛔ Роль без жодного права інакше виглядала б однаково з
                    «навмисно вузькою роллю» (UI-аудит, lane 1). */}
                {role.permissions.length === 0 && role.dangerousPermissions.length === 0 && (
                  <Badge ml="xs" size="xs" color="statusWarning" variant="outline">
                    {t('security.noPermissions')}
                  </Badge>
                )}

                {/* Клонувати / перейменувати / видалити (`BE-14`). */}
                {canManage && <RoleActions role={role} />}
              </Table.Td>
              {permissions.map((permission) => (
                <Table.Td key={permission.code}>
                  {role.permissions.includes(permission.code) ? '✓' : ''}
                </Table.Td>
              ))}
            </Table.Tr>
          ))}
        </Table.Tbody>
      </Table>
    </ScrollArea>
  );
});
