import type { JSX } from 'react';
import { Link } from 'react-router-dom';
import { t } from '@/shared/i18n';
import { PermitsPaths } from './permitPaths';
import './permitsShell.css';

/** Розділ Permits, на якому стоїть людина. */
export type PermitsSection = 'permits' | 'types' | 'lists' | 'bindings';

interface SubnavItem {
  readonly id: PermitsSection;
  readonly to: string;
  readonly label: () => string;
}

// ⚠ Ключі — літерали в `t(...)` усередині замикань: сторож каталогу (`EndpointCoverageTests`)
// читає їх як текст, динамічний ключ він не бачить.
const items: readonly SubnavItem[] = [
  { id: 'permits', to: PermitsPaths.permits, label: () => t('nav.permits') },
  { id: 'types', to: PermitsPaths.types, label: () => t('permits.types.title') },
  { id: 'lists', to: PermitsPaths.lists, label: () => t('permits.lists.title') },
  { id: 'bindings', to: PermitsPaths.bindings, label: () => t('permits.bindings.title') },
];

/**
 * Смуга посилань розділу Permits (макет `.pm-nav`): Permits · Permit types · Lists ·
 * Sheet bindings. Поточний розділ — `aria-current="page"`; усе решта — звичайні посилання,
 * тож клавіатурою вони досяжні Tab-ом без жодної додаткової розмітки.
 */
export function PermitsSubnav({ current }: { readonly current: PermitsSection }): JSX.Element {
  return (
    <nav className="pm-nav" aria-label={t('permits.nav.aria')}>
      {items.map((item) => (
        <Link key={item.id} to={item.to} aria-current={item.id === current ? 'page' : undefined}>
          {item.label()}
        </Link>
      ))}
    </nav>
  );
}
