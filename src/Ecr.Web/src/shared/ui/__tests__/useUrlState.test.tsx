import { describe, it, expect } from 'vitest';
import { renderHook, act } from '@testing-library/react';
import type { JSX, ReactNode } from 'react';
import { MemoryRouter, useLocation } from 'react-router-dom';
import { useUrlNumber, useUrlParamsSetter, useUrlState } from '@/shared/ui/useUrlState';

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

  it('ПОРОЖНІЙ параметр (`?periodKey=`) — теж відсутній, а не нуль (§10.8)', () => {
    const { result } = renderHook(() => useUrlNumber('periodKey'), {
      wrapper: wrapper('/?periodKey='),
    });

    // ⛔ Мутаційний доказ: `Number('')` — це `0`, і він СКІНЧЕННИЙ, тож
    // `Number.isFinite` пропускав його як справжній період. Екран мовчки йшов
    // по періоду 0 замість того, щоб узяти свій дефолт
    // (`DocumentPage.tsx`: `urlPeriod ?? currentPeriodKey()`), — і сервер
    // відповідав «помилок немає» на періоді, якого не існує (той самий
    // `A7-28`, лише з іншого боку).
    expect(result.current[0]).toBeNull();
  });

  it('пробіли замість значення — так само відсутній параметр', () => {
    const { result } = renderHook(() => useUrlNumber('periodKey'), {
      wrapper: wrapper('/?periodKey=%20%20'),
    });

    expect(result.current[0]).toBeNull();
  });
});

describe('UI-аудит, lane 3: useUrlParamsSetter — кілька параметрів одним переходом', () => {
  it('дві окремі useUrlState-сеттери в одному обробнику губили ОБИДВІ зміни', () => {
    // ⛔ Мутаційний доказ КОРЕНЯ дефекту (`DocumentsPage.tsx`, поле «Period»):
    // виклик двох НЕЗАЛЕЖНИХ сеттерів `useUrlState` синхронно в одному
    // обробнику не компонується — `setSearchParams`, викликаний двічі
    // поспіль, губить обидві зміни, не лише другу.
    const { result } = renderHook(
      () => ({
        period: useUrlNumber('periodKey'),
        cursor: useUrlState('cursor'),
        location: useLocation(),
      }),
      { wrapper: wrapper('/') },
    );

    act(() => {
      result.current.period[1](9);
      result.current.cursor[1](null);
    });

    expect(result.current.location.search).toBe('');
  });

  it('useUrlParamsSetter оновлює кілька параметрів одним переходом', () => {
    const { result } = renderHook(
      () => ({ setUrlParams: useUrlParamsSetter(), location: useLocation() }),
      { wrapper: wrapper('/?cursor=abc') },
    );

    act(() => {
      result.current.setUrlParams({ periodKey: 9, cursor: null });
    });

    expect(result.current.location.search).toBe('?periodKey=9');
  });

  it('порожнє/null значення прибирає параметр, інші лишаються', () => {
    const { result } = renderHook(
      () => ({ setUrlParams: useUrlParamsSetter(), location: useLocation() }),
      { wrapper: wrapper('/?periodKey=9&tab=roles') },
    );

    act(() => {
      result.current.setUrlParams({ periodKey: null, cursor: 'xyz' });
    });

    expect(result.current.location.search).toContain('tab=roles');
    expect(result.current.location.search).toContain('cursor=xyz');
    expect(result.current.location.search).not.toContain('periodKey');
  });
});
