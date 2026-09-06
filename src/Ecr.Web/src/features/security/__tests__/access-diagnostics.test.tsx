import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import type { AccessDiagnosticsView } from '@/api/types';
import { AccessDiagnosticsPanel } from '@/features/security/AccessDiagnosticsPanel';

/**
 * Панель «Мої групи» (`H-21`).
 *
 * ⛔ Перевіряється не оформлення, а рівно те, чим панель відрізняється від
 * тиші, яку вона лікує: SID, що НІЧОГО не дав, мусить бути на екрані. Панель,
 * яка показує самі лише збіги, у людини без прав порожня — тобто той самий
 * порожній екран, тільки під іншою назвою.
 */
const Empty: AccessDiagnosticsView = {
  userId: 4,
  userName: 'ivanov',
  provider: 'Windows',
  principalSid: 'S-1-5-21-100-200-300-500',
  groupsFromTicket: true,
  groups: [],
  unmatchedSids: [],
  personalRoleCodes: [],
  effectiveRoleCodes: [],
  expiredRoleCodes: [],
  groupAssignmentsInSystem: [],
};

function show(view: AccessDiagnosticsView): void {
  render(
    <MantineProvider>
      <AccessDiagnosticsPanel view={view} />
    </MantineProvider>,
  );
}

describe('Діагностика доступу', () => {
  it('показує SID, який НЕ дав жодної ролі', () => {
    show({
      ...Empty,
      groups: [
        { sid: 'S-1-5-21-1001', matched: true, roleCodes: ['DataEntry'] },
        { sid: 'S-1-5-21-1002', matched: false, roleCodes: [] },
      ],
      unmatchedSids: ['S-1-5-21-1002'],
      effectiveRoleCodes: ['DataEntry'],
    });

    // ⛔ Головне твердження кроку: саме промах і є відповіддю на «чому в мене
    // немає доступу» — з ним ідуть до відділу AD.
    expect(screen.getByText('S-1-5-21-1002')).toBeDefined();
    expect(screen.getByText('S-1-5-21-1001')).toBeDefined();

    const alerts = screen.getAllByRole('alert');
    expect(alerts.map((a) => a.textContent).join(' ')).toContain('myGroups.unmatched');
  });

  it('порожній перелік груп чужого запису ПОЯСНЮЄТЬСЯ, а не мовчить', () => {
    // ⛔ Квитка чужої сесії в нас немає (`P-02`). Без пояснення порожнеча
    // читалася б як «людина ні в яких групах не перебуває» — неправда, якої
    // екран проти тиші допускати не має права.
    show({ ...Empty, groupsFromTicket: false });

    const alerts = screen.getAllByRole('alert');
    expect(alerts.map((a) => a.textContent).join(' ')).toContain('myGroups.notMine');
  });

  it('квиток без жодного SID групи названий окремо від «немає ролей»', () => {
    show(Empty);

    const alerts = screen.getAllByRole('alert');
    expect(alerts.map((a) => a.textContent).join(' ')).toContain('myGroups.noSids');
  });

  it('підміна, що скінчилася, названа окремо від відсутності ролі', () => {
    show({
      ...Empty,
      groups: [{ sid: 'S-1-5-21-1001', matched: false, roleCodes: [] }],
      unmatchedSids: ['S-1-5-21-1001'],
      expiredRoleCodes: ['Approver'],
    });

    // «Роль була, вчора скінчилася» лікується продовженням призначення;
    // «ролі не було ніколи» — заведенням нового.
    expect(screen.getByText(/myGroups\.expired/)).toBeDefined();
  });

  it('каталог груп показується лише тоді, коли сервер його віддав', () => {
    show({
      ...Empty,
      groupsFromTicket: false,
      groupAssignmentsInSystem: [{ sid: 'S-1-5-21-7777', roleCodes: ['Approver'] }],
    });

    expect(screen.getByText('S-1-5-21-7777')).toBeDefined();
  });
});
