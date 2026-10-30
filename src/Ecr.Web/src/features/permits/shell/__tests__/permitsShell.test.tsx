import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import type { ReactNode } from 'react';
import { PermitEditorHead, PermitStateStepper } from '../PermitEditorHead';
import { PermitsSubnav, type PermitsSection } from '../PermitsSubnav';
import { isPermitState, permitStateLabel, permitStateStep, permitStateTone, permitStates } from '../permitStatus';
import { PermitStateBadge } from '../../ui/PermitStateBadge';

/**
 * Каркас Permits (PM-06): смуга розділів, шапка редактора, стани.
 *
 * Мутаційний доказ (вручну, відновлено): `aria-current` на кожному посиланні замість лише
 * поточного валив «aria-current лише на поточному»; `permitStateTone('Expired')` → `neutral` валив
 * «стани й тони».
 */
vi.mock('@/shared/i18n', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/i18n')>()),
  t: (key: string) => `text of ${key}`,
}));

const session = vi.hoisted(() => ({ permissions: ['Registry.View', 'Permit.EditType'] as string[] }));
vi.mock('@/shared/session/useSession', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/session/useSession')>()),
  useSession: () => ({ data: { permissions: session.permissions } }),
}));

afterEach(cleanup);

function show(node: ReactNode): void {
  render(
    <MantineProvider>
      <MemoryRouter>{node}</MemoryRouter>
    </MantineProvider>,
  );
}

describe('PermitsSubnav', () => {
  const sections: PermitsSection[] = ['permits', 'types', 'lists', 'groups', 'bindings', 'nae'];

  it('шість посилань на свої адреси, в області з імʼям', () => {
    show(<PermitsSubnav current="permits" />);
    const nav = screen.getByRole('navigation', { name: 'text of permits.nav.aria' });
    const links = within(nav).getAllByRole('link');
    expect(links.map((a) => a.getAttribute('href'))).toEqual([
      '/admin/permits',
      '/admin/permits/types',
      '/admin/permits/lists',
      '/admin/permits/groups',
      '/admin/permits/bindings',
      '/admin/permits/nae',
    ]);
    expect(links.map((a) => a.textContent)).toEqual([
      'text of nav.permits',
      'text of permits.types.title',
      'text of permits.lists.title',
      'text of permits.groups.title',
      'text of permits.bindings.title',
      'text of permits.nae.title',
    ]);
  });

  it('PM-06b: глядач (лише Registry.View) не бачить посилання на типи, але поточний розділ не зникає', () => {
    session.permissions = ['Registry.View'];
    try {
      show(<PermitsSubnav current="permits" />);
      expect(screen.getAllByRole('link').map((a) => a.getAttribute('href'))).toEqual([
        '/admin/permits',
        '/admin/permits/lists',
        '/admin/permits/groups',
        '/admin/permits/bindings',
        '/admin/permits/nae',
      ]);
      cleanup();

      show(<PermitsSubnav current="types" />);
      expect(screen.getAllByRole('link').map((a) => a.getAttribute('href'))).toContain('/admin/permits/types');
    } finally {
      session.permissions = ['Registry.View', 'Permit.EditType'];
    }
  });

  it('PM-06b: без жодного права (профіль ще не приїхав) посилання на типи сховане', () => {
    session.permissions = [];
    try {
      show(<PermitsSubnav current="lists" />);
      expect(screen.queryByRole('link', { name: 'text of permits.types.title' })).toBeNull();
    } finally {
      session.permissions = ['Registry.View', 'Permit.EditType'];
    }
  });

  it.each(sections)('aria-current="page" лише на поточному розділі (%s)', (current) => {
    show(<PermitsSubnav current={current} />);
    const links = screen.getAllByRole('link');
    const marked = links.filter((a) => a.getAttribute('aria-current') === 'page');
    expect(marked).toHaveLength(1);
    expect(links.indexOf(marked[0]!)).toBe(sections.indexOf(current));
  });
});

describe('permitStatus', () => {
  it('стани й тони: Draft/Active нейтральні, Expired бляклий, невідомий — увага', () => {
    expect(permitStates).toEqual(['Draft', 'Active', 'Expired']);
    expect(permitStateTone('Draft')).toBe('neutral');
    expect(permitStateTone('Active')).toBe('neutral');
    expect(permitStateTone('Expired')).toBe('muted');
    expect(permitStateTone('Superseded')).toBe('warning');
  });

  it('підпис — з каталогу; невідомий стан показується як є, не порожнім', () => {
    expect(permitStateLabel('Active')).toBe('text of permits.state.Active');
    expect(permitStateLabel('Expired')).toBe('text of permits.state.Expired');
    expect(permitStateLabel('Superseded')).toBe('Superseded');
  });

  it('isPermitState і крок на лінії станів', () => {
    expect(isPermitState('Draft')).toBe(true);
    expect(isPermitState('draft')).toBe(false);
    expect([permitStateStep('Draft'), permitStateStep('Active'), permitStateStep('Expired')]).toEqual([0, 1, 2]);
    expect(permitStateStep('Nope')).toBe(-1);
  });

  it('бейдж: підпис словом, тон у data-атрибутах', () => {
    show(<PermitStateBadge state="Expired" />);
    const badge = screen.getByText('text of permits.state.Expired');
    expect(badge.closest('[data-permit-state]')?.getAttribute('data-permit-state')).toBe('Expired');
    expect(badge.closest('[data-status-tone]')?.getAttribute('data-status-tone')).toBe('muted');
  });
});

describe('PermitEditorHead', () => {
  it('єдиний h1 із фокусом, посилання «назад», підзаголовок, бейдж, стани, дії', () => {
    show(
      <PermitEditorHead
        back={{ label: 'Back to Permits', href: '/admin/permits' }}
        title="KZ06VCZ14825472"
        mono
        subtitle="Air emission permit"
        badge={<PermitStateBadge state="Active" />}
        progress={<PermitStateStepper state="Active" />}
        actions={<button type="button">Renew</button>}
      />,
    );

    const headings = screen.getAllByRole('heading');
    expect(headings).toHaveLength(1);
    expect(headings[0]?.textContent).toBe('KZ06VCZ14825472');
    expect(headings[0]?.getAttribute('tabindex')).toBe('-1');
    expect(document.activeElement).toBe(headings[0]);

    const back = screen.getByRole('link', { name: '← Back to Permits' });
    expect(back.getAttribute('href')).toBe('/admin/permits');
    expect(screen.getByText('Air emission permit')).toBeTruthy();
    expect(screen.getByText('text of permits.state.Active', { selector: '[data-permit-state] *, [data-permit-state]' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Renew' })).toBeTruthy();
  });

  it('без бейджа, станів і дій правий блок не малюється', () => {
    show(<PermitEditorHead back={{ label: 'Back', href: '/x' }} title="T" />);
    expect(document.querySelector('.pm-eh-ctx')).toBeNull();
    expect(screen.queryByText('Air emission permit')).toBeNull();
  });

  it('не забирає фокус, який людина вже поставила', () => {
    const outside = document.createElement('input');
    document.body.appendChild(outside);
    outside.focus();
    show(<PermitEditorHead back={{ label: 'Back', href: '/x' }} title="T" />);
    expect(document.activeElement).toBe(outside);
    outside.remove();
  });

  it('лінія станів: aria-current="step" лише на поточному, невідомий стан — на жодному', () => {
    show(<PermitStateStepper state="Active" />);
    const items = screen.getAllByRole('listitem');
    expect(items.map((li) => li.getAttribute('aria-current'))).toEqual([null, 'step', null]);
    expect(screen.getByRole('list', { name: 'text of permits.state.aria' })).toBeTruthy();
    cleanup();

    show(<PermitStateStepper state="Nope" />);
    expect(screen.getAllByRole('listitem').every((li) => li.getAttribute('aria-current') === null)).toBe(true);
  });
});
