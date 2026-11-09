import { Suspense, lazy, useState, type JSX } from 'react';
import { Button, Checkbox, SegmentedControl } from '@mantine/core';
import type { PermissionCatalogItem, RoleView } from '@/api/types';
import { PermissionCaption } from '@/features/security/PermissionCaption';
import { RoleActions } from '@/features/security/RoleActions';
import { SecurityIcon } from '@/features/security/securityIcons';
import {
  groupPermissions,
  permissionGroupLabel,
  roleHas,
  type PermissionGroup,
} from '@/features/security/permissionGroups';
import { roleLabel } from '@/features/security/roleLabel';
import { permissionLabel } from '@/features/security/permissionLabel';
import { Banner } from '@/shared/ui/Banner';
import { useUrlState } from '@/shared/ui/useUrlState';
import { t } from '@/shared/i18n';
import './securityRoles.css';

/**
 * Порівняння ролей — за `import()`: відкривається рідко, а це найбільша
 * таблиця екрана (права × ролі). Вкладка ролей за замовчуванням показує одну
 * роль, і платити за матрицю кожен відвідувач не має (`D-132`).
 */
const RoleMatrix = lazy(async () => ({
  default: (await import('@/features/security/RoleMatrix')).RoleMatrix,
}));

/** Перегляд впливу і збереження — окремим чанком: відкривається лише після правки прав. */
const RolePermissionsReview = lazy(async () => ({
  default: (await import('@/features/security/RolePermissionsReview')).RolePermissionsReview,
}));

interface RoleBrowserProps {
  readonly roles: RoleView[];
  /** Повний каталог прав, відсортований за кодом; посилання стабільне. */
  readonly permissions: PermissionCatalogItem[];
  readonly canManage: boolean;
}

/**
 * Вкладка «Ролі»: список ролей зліва, обрана роль справа з правами в
 * розгортних групах; друге подання — «Compare roles» (UI-37, макет
 * `docs/design/hybrid/screens-ops.js` `secRoles`, `KIT.md` §6 `Segmented`,
 * `Collapsible`, `Banner`).
 *
 * ⚠ Обрана роль і подання — в адресі (`?role=<код>`, `?view=compare`), як у
 * макеті: «подивись права Approver» — це посилання, а не інструкція.
 *
 * ✎ (v) рішення людини 08.11: права ВЛАСНОЇ ролі правляться прапорцями, як у макеті (`paintDetail`, `edit`):
 * «Edit permissions» → прапорці → смуга незбережених змін → «Review and save…» — спершу вплив на носіїв
 * (`RolePermissionsReview`), окреме підтвердження небезпечних прав, `PUT /roles/{id}/permissions` з `If-Match`.
 * Вбудована роль — замок і причина текстом (банер), кнопки правки немає; сервер теж відмовляє (409).
 *
 * ⚠ Що з макета не перенесено, бо цього немає у відповіді `GET /roles`:
 * опис ролі (`description`), лічильник «N users have this role», пояснення,
 * ЧОМУ право небезпечне (`DANGER_WHY`). Замість опису під назвою — код ролі.
 */
export function RoleBrowser({ roles, permissions, canManage }: RoleBrowserProps): JSX.Element {
  const [rawView, setView] = useUrlState('view');
  const view = rawView === 'compare' ? 'compare' : 'role';

  return (
    <>
      <div className="ecr-sec-viewbar">
        <SegmentedControl
          aria-label={t('security.viewLabel')}
          value={view}
          onChange={(next) => setView(next === 'compare' ? 'compare' : null)}
          data={[
            { value: 'role', label: t('security.viewOne') },
            { value: 'compare', label: t('security.viewCompare') },
          ]}
        />
      </div>

      {view === 'compare' ? (
        <Suspense fallback={null}>
          <RoleMatrix roles={roles} permissions={permissions} />
        </Suspense>
      ) : (
        <RolePair roles={roles} permissions={permissions} canManage={canManage} />
      )}
    </>
  );
}

function RolePair({ roles, permissions, canManage }: RoleBrowserProps): JSX.Element {
  const [rawRole, setRole] = useUrlState('role');
  // ⚠ Невідомий код в адресі (роль видалено, посилання застаріле) — перша роль,
  // а не порожня права частина: список зліва однаково показує, що обрано.
  const selected = roles.find((role) => role.code === rawRole) ?? roles[0];
  const catalog = new Set(permissions.map((permission) => permission.code));

  return (
    <div className="ecr-sec-roles">
      <ul className="ecr-sec-role-list" aria-label={t('security.roles')}>
        {roles.map((role) => {
          const current = role.id === selected?.id;
          const dangerous = dangerousOf(role, permissions);

          return (
            <li
              key={role.id}
              className="ecr-sec-role-row"
              data-current={String(current)}
              data-role-item={role.code}
            >
              <button
                type="button"
                className="ecr-sec-role"
                aria-current={current ? 'true' : undefined}
                onClick={() => setRole(role.code)}
              >
                <span className="ecr-sec-nm">{roleLabel(role)}</span>
                {/* ⛔ Без назви `roleLabel` уже дає код — другий раз той самий код шум. */}
                {roleLabel(role) !== role.code && <span className="ecr-sec-ds">{role.code}</span>}
                <RoleTags role={role} />
              </button>

              {/* ⚠ Щит — число небезпечних прав (ФВ-6.12, `D-40`), а не
                  кнопка: перелік цих прав і так видно праворуч позначкою
                  «Dangerous» біля кожного. */}
              {dangerous > 0 && (
                <span
                  className="ecr-sec-shield"
                  role="img"
                  aria-label={t('security.dangerous', { count: dangerous })}
                  title={t('security.dangerous', { count: dangerous })}
                >
                  <SecurityIcon name="shieldAlert" />
                  {dangerous}
                </span>
              )}
            </li>
          );
        })}
      </ul>

      {selected !== undefined && (
        <RoleDetail role={selected} permissions={permissions} canManage={canManage} catalog={catalog} />
      )}
    </div>
  );
}

/** Небезпечні права ролі: з відповіді сервера плюс ті, що каталог позначає небезпечними. */
function dangerousOf(role: RoleView, permissions: readonly PermissionCatalogItem[]): number {
  const codes = new Set(role.dangerousPermissions);
  for (const permission of permissions) {
    if (permission.isDangerous && roleHas(role, permission.code)) codes.add(permission.code);
  }
  return codes.size;
}

function RoleTags({ role }: { readonly role: RoleView }): JSX.Element {
  return (
    <span className="ecr-sec-tags">
      <span className="ecr-sec-tag">
        <SecurityIcon name={role.isBuiltIn ? 'lock' : 'pencil'} size={12} />
        {role.isBuiltIn ? t('security.builtIn') : t('security.custom')}
      </span>

      {/* ⛔ `opacity` невидима читалці — неактивність словом (UX-аудит, знахідка 2/3).
          ⚠ Словом-позначкою, а не сірим `Badge variant="outline"`: той на
          поверхні списку не тягне AA (axe `color-contrast`, 06.10). */}
      {!role.isActive && (
        <span className="ecr-sec-tag" data-tone="inactive">
          {t('security.inactive')}
        </span>
      )}

      {/* ⛔ Роль без жодного права інакше виглядала б однаково з «навмисно
          вузькою роллю» (UI-аудит, lane 1). */}
      {role.permissions.length === 0 && role.dangerousPermissions.length === 0 && (
        <span className="ecr-sec-tag" data-tone="warning">
          {t('security.noPermissions')}
        </span>
      )}
    </span>
  );
}

interface RoleDetailProps {
  readonly role: RoleView;
  readonly permissions: PermissionCatalogItem[];
  readonly canManage: boolean;
  readonly catalog: ReadonlySet<string>;
}

/** Чернетка прав ролі: від чого почали (для звірки з переглядом) і що зараз позначено. */
interface PermissionDraft {
  readonly roleId: number;
  readonly base: readonly string[];
  readonly perms: ReadonlySet<string>;
}

/** Додані й прибрані права чернетки відносно її основи. */
export function draftDiff(draft: PermissionDraft): { added: string[]; removed: string[] } {
  const base = new Set(draft.base);

  return {
    added: [...draft.perms].filter((code) => !base.has(code)).sort(),
    removed: [...draft.base].filter((code) => !draft.perms.has(code)).sort(),
  };
}

function RoleDetail({ role, permissions, canManage, catalog }: RoleDetailProps): JSX.Element {
  // ⚠ Право ролі, якого немає в каталозі (каталог старший за роль чи навпаки),
  // не губиться: окрема група «Other» знизу. Інакше роль показувалася б
  // вужчою, ніж вона є.
  const unknown = role.permissions.filter((code) => !catalog.has(code));
  const groups = groupPermissions(permissions);
  const dangerousCodes = new Set(permissions.filter((p) => p.isDangerous).map((p) => p.code));

  const [stored, setDraft] = useState<PermissionDraft | null>(null);
  const [reviewing, setReviewing] = useState(false);
  // ⚠ Чернетка належить ролі, з якої почали: перемкнули роль — правка іншої не показується.
  const draft = stored !== null && stored.roleId === role.id ? stored : null;
  const editable = canManage && !role.isBuiltIn;
  const diff = draft === null ? null : draftDiff(draft);
  const changes = diff === null ? 0 : diff.added.length + diff.removed.length;
  const addedDangerous = diff === null ? [] : diff.added.filter((code) => dangerousCodes.has(code));

  const startEditing = (): void => {
    const base = [...new Set([...role.permissions, ...role.dangerousPermissions])].sort();
    setDraft({ roleId: role.id, base, perms: new Set(base) });
  };

  const toggle = (code: string, on: boolean): void => {
    if (draft === null) return;
    const perms = new Set(draft.perms);
    if (on) perms.add(code);
    else perms.delete(code);
    setDraft({ ...draft, perms });
  };

  return (
    <section className="ecr-sec-detail" aria-label={roleLabel(role)} data-testid="role-detail">
      <div className="ecr-sec-head">
        <div className="ecr-sec-grow">
          <div className="ecr-sec-title">
            <h2>{roleLabel(role)}</h2>
            {roleLabel(role) !== role.code && <code className="ecr-sec-code">{role.code}</code>}
            <RoleTags role={role} />
          </div>
          {!role.isBuiltIn && <p className="ecr-sec-muted">{t('security.customReadOnly')}</p>}
        </div>

        {editable && draft === null && (
          <Button
            variant="default"
            leftSection={<SecurityIcon name="pencil" size={14} />}
            onClick={startEditing}
            data-testid="role-edit-permissions"
          >
            {t('security.editPermissions')}
          </Button>
        )}
        {canManage && <RoleActions role={role} placement="header" />}
      </div>

      {draft !== null && diff !== null && (
        <Banner
          tone="info"
          testId="role-permissions-dirty"
          title={
            changes === 0 ? t('security.editingPermissionsTitle') : t('security.unsavedPermissionChanges', { count: changes })
          }
          text={
            changes === 0
              ? t('security.editingPermissionsHint')
              : [
                  [...diff.added.map((code) => `+ ${permissionLabel(code)}`), ...diff.removed.map((code) => `− ${permissionLabel(code)}`)].join(' · '),
                  addedDangerous.length > 0 ? t('security.unsavedDangerousNote', { count: addedDangerous.length }) : '',
                ]
                  .filter((part) => part !== '')
                  .join(' — ')
          }
          actions={[
            { label: t('security.discardPermissionChanges'), onClick: () => setDraft(null) },
            ...(changes === 0
              ? []
              : [{ label: t('security.reviewAndSave'), onClick: () => setReviewing(true), variant: 'filled' as const }]),
          ]}
        />
      )}

      {draft !== null && reviewing && (
        <Suspense fallback={null}>
          <RolePermissionsReview
            role={role}
            base={draft.base}
            next={[...draft.perms]}
            onClose={() => setReviewing(false)}
            onSaved={() => {
              setReviewing(false);
              setDraft(null);
            }}
          />
        </Suspense>
      )}

      {role.isBuiltIn && (
        <Banner
          tone="info"
          icon={<SecurityIcon name="lock" size={16} />}
          title={t('security.builtInReadOnlyTitle')}
          text={t('security.builtInReadOnlyText')}
          testId="role-builtin-banner"
        />
      )}

      {groups.map((group) => (
        <PermissionGroupBlock
          key={`${role.id}:${group.group}`}
          role={role}
          group={group}
          draft={draft}
          onToggle={toggle}
        />
      ))}

      {unknown.length > 0 && (
        <PermissionGroupBlock
          key={`${role.id}:other`}
          role={role}
          group={{
            group: '',
            items: unknown.map((code) => ({ code, group: '', isDangerous: role.dangerousPermissions.includes(code) })),
          }}
          draft={draft}
          onToggle={toggle}
        />
      )}
    </section>
  );
}

function PermissionGroupBlock({
  role,
  group,
  draft,
  onToggle,
}: {
  readonly role: RoleView;
  readonly group: PermissionGroup;
  readonly draft: PermissionDraft | null;
  readonly onToggle: (code: string, on: boolean) => void;
}): JSX.Element {
  const has = (code: string): boolean => (draft === null ? roleHas(role, code) : draft.perms.has(code));
  const granted = group.items.filter((item) => has(item.code));
  const dangerous = granted.filter((item) => item.isDangerous).length;
  // ⚠ Розгорнуто за замовчуванням ті групи, де роль щось має: порожні групи
  // згорнуті, щоб надане було видно без прокрутки (макет розгортає одну).
  const [open, setOpen] = useState(granted.length > 0);
  const id = `role-group-${role.id}-${group.group || 'other'}`;
  const title = group.group === '' ? t('security.domainOther') : permissionGroupLabel(group.group);

  return (
    <div className="ecr-sec-group" data-permission-group={group.group}>
      <button
        type="button"
        className="ecr-sec-group-h"
        aria-expanded={open}
        aria-controls={id}
        onClick={() => setOpen((value) => !value)}
      >
        <SecurityIcon name="chevronR" />
        <span>{title}</span>
        <small>
          {t('security.groupSummary', { granted: granted.length, total: group.items.length })}
          {dangerous > 0 && ` · ${t('security.groupDangerous', { count: dangerous })}`}
        </small>
      </button>

      {open && (
        <ul className="ecr-sec-group-b" id={id}>
          {group.items.map((item) => {
            const granted = has(item.code);

            if (draft !== null) {
              return (
                <li
                  key={item.code}
                  className="ecr-sec-perm"
                  data-editing="true"
                  data-granted={String(granted)}
                  data-permission={item.code}
                >
                  <Checkbox
                    checked={granted}
                    aria-label={permissionLabel(item.code)}
                    onChange={(event) => onToggle(item.code, event.currentTarget.checked)}
                  />
                  <PermissionCaption code={item.code} />
                  {item.isDangerous ? (
                    <span className="ecr-sec-danger">
                      <SecurityIcon name="shieldAlert" />
                      {t('security.dangerousMark')}
                    </span>
                  ) : (
                    <span />
                  )}
                  <span />
                </li>
              );
            }

            return (
              <li key={item.code} className="ecr-sec-perm" data-granted={String(granted)} data-permission={item.code}>
                <SecurityIcon name={granted ? 'check' : 'minus'} />
                <PermissionCaption code={item.code} />
                {item.isDangerous ? (
                  <span className="ecr-sec-danger">
                    <SecurityIcon name="shieldAlert" />
                    {t('security.dangerousMark')}
                  </span>
                ) : (
                  <span />
                )}
                <span className="ecr-sec-sr">{granted ? t('security.granted') : t('security.notGranted')}</span>
              </li>
            );
          })}
        </ul>
      )}
    </div>
  );
}
