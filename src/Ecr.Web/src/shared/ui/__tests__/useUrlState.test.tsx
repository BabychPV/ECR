import { describe, it, expect } from 'vitest';
import { renderHook, act, render } from '@testing-library/react';
import type { JSX, ReactNode } from 'react';
import { MemoryRouter, RouterProvider, createMemoryRouter, useLocation } from 'react-router-dom';
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
  it('дві окремі useUrlState-сеттери в одному обробнику компонуються (обидві зміни живі)', () => {
    // ⛔ Мутаційний доказ КОРЕНЯ дефекту (`DocumentsPage.tsx`, поле «Period»):
    // другий сеттер у тому самому обробнику будував запит зі знімка `searchParams`
    // ДО першого й відкочував `periodKey` — адреса лишалась порожньою. Тепер
    // сеттери будують від живої адреси (див. `pending` у `useUrlState.ts`).
    const { result } = renderHook(
      () => ({
        period: useUrlNumber('periodKey'),
        cursor: useUrlState('cursor'),
        location: useLocation(),
      }),
      { wrapper: wrapper('/?cursor=abc') },
    );

    act(() => {
      result.current.period[1](9);
      result.current.cursor[1](null);
    });

    expect(result.current.location.search).toBe('?periodKey=9');
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

describe('Сеттери будують адресу від ЖИВОЇ локації, а не від знімка останнього рендеру', () => {
  // ⛔ Корінь: `setSearchParams((current) => …)` у react-router 7 віддає в `current` знімок
  // `searchParams` з ОСТАННЬОГО РЕНДЕРУ хука (`useMemo` над `location.search`), а не поточну
  // адресу. Сеттер, викликаний із застарілого замикання (слухач, зареєстрований один раз;
  // дерево, яке ще не перерендерилось після зміни іншого параметра), перебудовував запит зі
  // старого знімка й МОВЧКИ відкочував щойно змінений параметр.

  it('сеттер із застарілого замикання не відкочує параметр, змінений після його рендеру', () => {
    const { result } = renderHook(
      () => ({ a: useUrlState('a'), b: useUrlState('b'), location: useLocation() }),
      { wrapper: wrapper('/x?keep=1') },
    );

    // Замикання «до зміни» — як у слухача клавіш, зареєстрованого один раз.
    const staleSetB = result.current.b[1];

    act(() => {
      result.current.a[1]('1');
    });
    act(() => {
      staleSetB('2');
    });

    const search = new URLSearchParams(result.current.location.search);
    expect(search.get('keep')).toBe('1');
    expect(search.get('a')).toBe('1');
    expect(search.get('b')).toBe('2');
  });

  it('useUrlParamsSetter із застарілого замикання теж не відкочує чужий параметр', () => {
    const { result } = renderHook(
      () => ({ a: useUrlState('a'), setUrlParams: useUrlParamsSetter(), location: useLocation() }),
      { wrapper: wrapper('/x') },
    );

    const staleSetParams = result.current.setUrlParams;

    act(() => {
      result.current.a[1]('1');
    });
    act(() => {
      staleSetParams({ b: 2, cursor: null });
    });

    expect(result.current.location.search).toBe('?a=1&b=2');
  });

  it('дата-роутер (як у застосунку): два сеттери поспіль до коміту рендеру — обидві зміни живі', async () => {
    let api: { setA: (v: string | null) => void; setB: (v: number | null) => void } | null = null;
    function Probe(): null {
      const [, setA] = useUrlState('a');
      const [, setB] = useUrlNumber('b');
      api = { setA, setB };
      return null;
    }
    const router = createMemoryRouter([{ path: '/x', element: <Probe /> }], {
      initialEntries: ['/x?keep=1'],
    });
    render(<RouterProvider router={router} />);

    await act(async () => {
      api?.setA('1');
      api?.setB(7);
      await Promise.resolve();
    });

    const search = new URLSearchParams(router.state.location.search);
    expect(search.get('keep')).toBe('1');
    expect(search.get('a')).toBe('1');
    expect(search.get('b')).toBe('7');
  });
});
