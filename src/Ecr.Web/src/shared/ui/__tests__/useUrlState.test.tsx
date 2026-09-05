import { describe, it, expect } from 'vitest';
import { renderHook, act } from '@testing-library/react';
import type { JSX, ReactNode } from 'react';
import { MemoryRouter, useLocation } from 'react-router-dom';
import { useUrlNumber, useUrlState } from '@/shared/ui/useUrlState';

function wrapper(initial: string) {
  return function Wrapper({ children }: { children: ReactNode }): JSX.Element {
    return <MemoryRouter initialEntries={[initial]}>{children}</MemoryRouter>;
  };
}

describe('Фільтр живе в адресі сторінки (ФВ-14.29)', () => {
  it('ФВ-14.29: початкове значення береться з адреси', () => {
    const { result } = renderHook(() => useUrlState('code'), {
      wrapper: wrapper('/admin/registries?code=EQUIP'),
    });

    // ⛔ Саме це й означає «екран можна надіслати колезі»: посилання
    // відкривається в тому самому вигляді, а не з проханням обрати щось наново.
    expect(result.current[0]).toBe('EQUIP');
  });

  it('зміна значення потрапляє в адресу', () => {
    const { result } = renderHook(
      () => ({ state: useUrlState('code'), location: useLocation() }),
      { wrapper: wrapper('/admin/registries') },
    );

    act(() => {
      result.current.state[1]('SUBST');
    });

    expect(result.current.location.search).toBe('?code=SUBST');
  });

  it('порожнє значення прибирає параметр, а не лишає хвіст', () => {
    const { result } = renderHook(
      () => ({ state: useUrlState('code'), location: useLocation() }),
      { wrapper: wrapper('/admin/registries?code=EQUIP') },
    );

    act(() => {
      result.current.state[1](null);
    });

    // ⚠ Адреса з порожніми хвостами накопичує їх і стає нечитабельною за три
    // зміни фільтра — а її ж і надсилають колезі.
    expect(result.current.location.search).toBe('');
  });

  it('інші параметри не зачіпаються', () => {
    const { result } = renderHook(
      () => ({ state: useUrlState('code'), location: useLocation() }),
      { wrapper: wrapper('/admin/registries?code=EQUIP&tab=users') },
    );

    act(() => {
      result.current.state[1]('SUBST');
    });

    expect(result.current.location.search).toContain('tab=users');
    expect(result.current.location.search).toContain('code=SUBST');
  });

  it('числовий параметр розбирається', () => {
    const { result } = renderHook(() => useUrlNumber('periodKey'), {
      wrapper: wrapper('/?periodKey=202601'),
    });

    expect(result.current[0]).toBe(202601);
  });

  it('нечислове значення в адресі трактується як відсутнє, а не як NaN', () => {
    const { result } = renderHook(() => useUrlNumber('periodKey'), {
      wrapper: wrapper('/?periodKey=abc'),
    });

    // ⛔ Адресу правлять руками, і `?periodKey=abc` не має ламати екран:
    // `NaN` у ключі запиту дав би запит `periodKey=NaN` і 400 від сервера.
    expect(result.current[0]).toBeNull();
  });
});
