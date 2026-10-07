import { memo, useState, type JSX } from 'react';
import { Checkbox, Text } from '@mantine/core';
import type { PermissionCatalogItem, RoleView } from '@/api/types';
import { PermissionCaption } from '@/features/security/PermissionCaption';
import { SecurityIcon } from '@/features/security/securityIcons';
import { groupPermissions, permissionGroupLabel, roleHas } from '@/features/security/permissionGroups';
import { roleLabel } from '@/features/security/roleLabel';
import { t } from '@/shared/i18n';
import './securityRoles.css';

interface RoleMatrixProps {
  roles: RoleView[];
  /** Повний каталог прав, відсортований за кодом; посилання має бути стабільним. */
  permissions: PermissionCatalogItem[];
}

/**
 * «Compare roles» — права рядками, ролі колонками (UI-37, макет
 * `screens-ops.js` `paintMatrix`: «Permissions are rows, roles are columns —
 * read across to see who can do one thing»).
 *
 * ✎ UI-37: до цього матриця стояла навпаки (ролі × 41 колонка прав) і була
 * єдиним поданням вкладки. Тепер це друге подання; дії над роллю переїхали в
 * шапку обраної ролі (`RoleBrowser`), тож тут лише читання.
 *
 * ⛔ `memo` — не прикраса: матриця — найдорожче дерево сторінки, і
 * перерендер `SecurityPage`, що не міняє ні ролей, ні каталогу, не має її
 * чіпати (живий дефект 2026-09-24: друк у «New role» — див.
 * `CreateRoleModal.tsx`).
 *
 * ⚠ Матриця показує **оголошені** права ролей; ефективні права користувача
 * рахує сервер (`/me`).
 */
export const RoleMatrix = memo(function RoleMatrix({ roles, permissions }: RoleMatrixProps): JSX.Element {
  const [onlyDiff, setOnlyDiff] = useState(false);
  const groups = groupPermissions(permissions);

  const differs = (code: string): boolean => {
    const first = roles[0] === undefined ? false : roleHas(roles[0], code);
    return roles.some((role) => roleHas(role, code) !== first);
  };

  const shownGroups = groups
    .map((group) => ({ ...group, items: onlyDiff ? group.items.filter((item) => differs(item.code)) : group.items }))
    .filter((group) => group.items.length > 0);
  const shown = shownGroups.reduce((sum, group) => sum + group.items.length, 0);

  return (
    <>
      <div className="ecr-sec-viewbar">
        <Text size="xs" c="dimmed" data-testid="role-matrix-hint">
          {t('security.compareHint', { shown, total: permissions.length, roles: roles.length })}
        </Text>
        <span className="ecr-sec-sp" />
        <Checkbox
          label={t('security.onlyDiff')}
          checked={onlyDiff}
          onChange={(event) => setOnlyDiff(event.currentTarget.checked)}
        />
      </div>

      {onlyDiff && shown === 0 ? (
        <Text size="sm" c="dimmed" data-testid="role-matrix-identical">
          {t('security.identicalRoles')}
        </Text>
      ) : (
        <div className="ecr-sec-matrix-wrap" tabIndex={0} role="region" aria-label={t('security.matrixLabel')}>
          <table className="ecr-sec-matrix">
            <thead>
              <tr>
                <th className="ecr-sec-pm" scope="col">
                  {t('security.permissionColumn')}
                </th>
                {roles.map((role) => (
                  <th key={role.id} scope="col" title={role.code} data-role-column={role.code}>
                    {roleLabel(role)}
                    {/* ⛔ Неактивну роль видно словом, а не прозорістю (знахідка 2/3). */}
                    {!role.isActive && <div>{t('security.inactive')}</div>}
                    {!role.isBuiltIn && <div>{t('security.custom')}</div>}
                  </th>
                ))}
              </tr>
            </thead>
            <tbody>
              {shownGroups.map((group) => [
                <tr key={`g:${group.group}`} className="ecr-sec-dom">
                  <th colSpan={roles.length + 1} scope="colgroup">
                    {permissionGroupLabel(group.group)}
                  </th>
                </tr>,
                ...group.items.map((item) => (
                  <tr key={item.code}>
                    {/* ⛔ Підпис права — з каталогу рядків, код другим рядком (`U-11`). */}
                    <th className="ecr-sec-pm" scope="row">
                      <span className="ecr-sec-tags">
                        <PermissionCaption code={item.code} />
                        {item.isDangerous && (
                          <span className="ecr-sec-danger" title={t('security.dangerousMark')}>
                            <SecurityIcon name="shieldAlert" />
                            <span className="ecr-sec-sr">{t('security.dangerousMark')}</span>
                          </span>
                        )}
                      </span>
                    </th>
                    {roles.map((role) => {
                      const has = roleHas(role, item.code);
                      const cell = has ? (item.isDangerous ? 'd' : 'y') : 'n';

                      return (
                        <td key={role.id} data-cell={cell} title={`${roleLabel(role)} — ${item.code}`}>
                          {has ? <SecurityIcon name={item.isDangerous ? 'shieldAlert' : 'check'} /> : '·'}
                          <span className="ecr-sec-sr">{has ? t('security.granted') : t('security.notGranted')}</span>
                        </td>
                      );
                    })}
                  </tr>
                )),
              ])}
            </tbody>
          </table>
        </div>
      )}
    </>
  );
});
