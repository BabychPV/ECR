import type { JSX } from 'react';
import { Link } from 'react-router-dom';
import { t } from '@/shared/i18n';
import { can, useSession } from '@/shared/session/useSession';
import { PermitTypesPermission } from '../permitRights';
import { PermitsPaths } from './permitPaths';
import './permitsShell.css';

/** Розділ Permits, на якому стоїть людина. */
export type PermitsSection = 'permits' | 'types' | 'lists' | 'bindings';

interface SubnavItem {
  readonly id: PermitsSection;
  readonly to: string;
  readonly label: () => string;
  /** Посилання показується лише з цим правом (як і сам маршрут); без поля — з `Registry.View`. */
  readonly permission?: string;
}

// ⚠ Ключі — літерали в `t(...)` усередині замикань: сторож каталогу (`EndpointCoverageTests`)
// читає їх як текст, динамічний ключ він не бачить.
const items: readonly SubnavItem[] = [
  { id: 'permits', to: PermitsPaths.permits, label: () => t('nav.permits') },
  { id: 'types', to: PermitsPaths.types, label: () => t('permits.types.title'), permission: PermitTypesPermission },
  { id: 'lists', to: PermitsPaths.lists, label: () => t('permits.lists.title') },
  { id: 'bindings', to: PermitsPaths.bindings, label: () => t('permits.bindings.title') },
];

/**
 * Смуга посилань розділу Permits (макет `.pm-nav`): Permits · Permit types · Lists ·
 * Sheet bindings. Поточний розділ — `aria-current="page"`; усе решта — звичайні посилання,
 * тож клавіатурою вони досяжні Tab-ом без жодної додаткової розмітки.
 *
 * PM-06b: посилання, яке людина не відкриє (немає права маршруту), не малюється — глядач не
 * бачить розділу, що завжди закінчується відмовою. Поточний розділ лишається завжди.
 */
export function PermitsSubnav({ current }: { readonly current: PermitsSection }): JSX.Element {
  const session = useSession();
  const visible = items.filter(
    (item) => item.id === current || item.permission === undefined || can(session.data, item.permission),
  );

  return (
    <nav className="pm-nav" aria-label={t('permits.nav.aria')}>
      {visible.map((item) => (
        <Link key={item.id} to={item.to} aria-current={item.id === current ? 'page' : undefined}>
          {item.label()}
        </Link>
      ))}
    </nav>
  );
}
