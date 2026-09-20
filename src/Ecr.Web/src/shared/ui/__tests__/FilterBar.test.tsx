import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import type { JSX } from 'react';
import { MemoryRouter, useLocation } from 'react-router-dom';
import { MantineProvider } from '@mantine/core';
import { FilterBar, type FilterSpec } from '@/shared/ui/FilterBar';
import { testTheme } from '@/test/render';

/**
 * `FilterBar` (директива №15 §2, Шар 3; `KIT.md` §6.3).
 *
 * ⛔ Перевіряється те, заради чого компонент існує: вибір користувача живе В
 * АДРЕСІ (`ФВ-14.29`), а не в `useState`. Тому кожне твердження дивиться на
 * `location.search`, а не на значення поля: поле показує потрібне й тоді, коли
 * в адресу не потрапило нічого (саме так і виглядав дефект `DocumentsPage`,
 * UI-аудит lane 3 — поле «Period» набирало значення на екрані, а жоден запит
 * його не бачив).
 */

const states: FilterSpec = {
  id: 'state',
  label: 'Стан',
  options: [
    { value: 'Draft', label: 'Чернетка' },
    { value: 'Submitted', label: 'Подано' },
  ],
};

/** Адреса, як її бачить маршрутизатор просто зараз. */
let search = '';

function Probe(): JSX.Element {
  search = useLocation().search;

  return <></>;
}

function show(node: JSX.Element, initial = '/admin/documents'): void {
  render(
    <MantineProvider theme={testTheme}>
      <MemoryRouter initialEntries={[initial]}>
        {node}
        <Probe />
      </MemoryRouter>
    </MantineProvider>,
  );
}

afterEach(() => {
  cleanup();
  search = '';
});

describe('Значення фільтра живе в адресі (ФВ-14.29)', () => {
  it('введене в пошук ПОТРАПЛЯЄ в адресу', () => {
    show(<FilterBar search={{ label: 'Пошук' }} />);

    fireEvent.change(screen.getByLabelText('Пошук'), { target: { value: 'EQUIP' } });

    /*
     * ⛔ Головне твердження файлу. Мутаційний доказ: у `SearchField`
     * (`FilterBar.tsx`) замініть `useUrlState(param)` на
     * `useState<string | null>(null)` — поле поводиться на екрані ДОСЛІВНО
     * так само (значення набирається, `getByDisplayValue` знаходить його), а
     * цей рядок падає з `expected '' to be '?q=EQUIP'`.
     */
    expect(search).toBe('?q=EQUIP');
  });

  it('значення ЧИТАЄТЬСЯ з адреси при відкритті', () => {
    show(<FilterBar search={{ label: 'Пошук' }} />, '/admin/documents?q=EQUIP');

    // ⛔ Саме це й означає «екран можна надіслати колезі»: посилання
    // відкривається в тому самому вигляді, а не з проханням обрати наново.
    expect(screen.getByLabelText('Пошук')).toHaveProperty('value', 'EQUIP');
  });

  it('обраний варіант переліку потрапляє в адресу СВОЇМ імʼям параметра', () => {
    show(<FilterBar filters={[states]} />);

    fireEvent.click(screen.getByLabelText('Стан'));
    fireEvent.click(screen.getByRole('option', { name: 'Чернетка' }));

    // ⛔ В адресу йде ЗНАЧЕННЯ сервера (`Draft`), а не підпис («Чернетка»):
    // підпис міняється з мовою інтерфейсу, і надіслане колезі посилання
    // перестало б працювати в нього іншою мовою.
    expect(search).toBe('?state=Draft');
  });

  it('варіант переліку читається з адреси підписом, а не кодом', () => {
    show(<FilterBar filters={[states]} />, '/admin/documents?state=Submitted');

    expect(screen.getByLabelText('Стан')).toHaveProperty('value', 'Подано');
  });

  it('порожнє значення ПРИБИРАЄ параметр, а не лишає хвіст', () => {
    show(<FilterBar search={{ label: 'Пошук' }} />, '/admin/documents?q=EQUIP');

    fireEvent.change(screen.getByLabelText('Пошук'), { target: { value: '' } });

    expect(search).toBe('');
  });

  it('чужі параметри адреси не зачіпаються', () => {
    show(<FilterBar search={{ label: 'Пошук' }} />, '/admin/documents?panel=row-7');

    fireEvent.change(screen.getByLabelText('Пошук'), { target: { value: 'EQUIP' } });

    expect(search).toContain('panel=row-7');
    expect(search).toContain('q=EQUIP');
  });
});

describe('«Clear filters» зʼявляється сам — і лише коли є що чистити', () => {
  const clearLabel = 'Зняти фільтри';

  it('на чистій адресі кнопки НЕМАЄ', () => {
    show(<FilterBar search={{ label: 'Пошук' }} filters={[states]} clearLabel={clearLabel} />);

    /*
     * ⛔ Мутаційний доказ: замініть `hasValues` на `true` — кнопка з'явиться
     * на порожній адресі, і цей рядок падає. Кнопка, яка нічого не чистить, —
     * це той самий тупиковий елемент, що й «повторити» на відмові в праві.
     */
    expect(screen.queryByRole('button', { name: clearLabel })).toBeNull();
  });

  it('щойно в адресі зʼявляється значення — кнопка зʼявляється САМА', () => {
    show(<FilterBar search={{ label: 'Пошук' }} filters={[states]} clearLabel={clearLabel} />);

    fireEvent.change(screen.getByLabelText('Пошук'), { target: { value: 'EQUIP' } });

    // ⛔ Мутаційний доказ у другий бік: зробіть `hasValues` сталим `false` —
    // цей рядок падає з «Unable to find an accessible element».
    expect(screen.getByRole('button', { name: clearLabel })).toBeDefined();
  });

  it('значення з адреси при відкритті так само показує кнопку', () => {
    show(
      <FilterBar search={{ label: 'Пошук' }} filters={[states]} clearLabel={clearLabel} />,
      '/admin/documents?state=Draft',
    );

    expect(screen.getByRole('button', { name: clearLabel })).toBeDefined();
  });

  it('кнопка чистить ОБИДВА параметри за ОДИН перехід і зникає', () => {
    show(
      <FilterBar search={{ label: 'Пошук' }} filters={[states]} clearLabel={clearLabel} />,
      '/admin/documents?q=EQUIP&state=Draft&panel=row-7',
    );

    fireEvent.click(screen.getByRole('button', { name: clearLabel }));

    /*
     * ⛔ Мутаційний доказ: замініть один виклик `setParams({…})` на два
     * послідовні сеттери `useUrlState` в тому самому обробнику — обидві зміни
     * ГУБЛЯТЬСЯ (`useUrlState.ts`, UI-аудит lane 3), адреса лишається
     * `?q=EQUIP&state=Draft&panel=row-7`, і цей рядок падає.
     */
    expect(search).toBe('?panel=row-7');

    // ⚠ `?panel=` лишається навмисно: відкрита шухляда належить ЕКРАНУ, а не
    // рядку фільтрів, і «Clear filters», що її зачиняє, — не те, про що
    // просив користувач.
    expect(screen.queryByRole('button', { name: clearLabel })).toBeNull();
  });
});

describe('onChange і D15-06', () => {
  it('onChange отримує значення З АДРЕСИ, включно з тими, що були при відкритті', () => {
    const changed = vi.fn();

    show(
      <FilterBar search={{ label: 'Пошук' }} filters={[states]} onChange={changed} />,
      '/admin/documents?state=Draft',
    );

    expect(changed).toHaveBeenLastCalledWith({ state: 'Draft' });

    fireEvent.change(screen.getByLabelText('Пошук'), { target: { value: 'EQ' } });

    expect(changed).toHaveBeenLastCalledWith({ state: 'Draft', q: 'EQ' });
  });

  it('onChange НЕ кличеться повторно, коли значення не змінилися', () => {
    const changed = vi.fn();

    show(<FilterBar search={{ label: 'Пошук' }} onChange={changed} />);

    const before = changed.mock.calls.length;

    fireEvent.change(screen.getByLabelText('Пошук'), { target: { value: '' } });

    // ⛔ Мутаційний доказ: приберіть рядок-підпис із залежностей ефекту
    // (`[signature]` → без масиву) — обробник викликатиметься на КОЖЕН рендер,
    // і це число зросте.
    expect(changed.mock.calls.length).toBe(before);
  });

  it('порожній рядок фільтрів не малюється зовсім (D15-06)', () => {
    const { container } = render(
      <MantineProvider theme={testTheme}>
        <MemoryRouter>
          <FilterBar />
        </MemoryRouter>
      </MantineProvider>,
    );

    // Порожня обгортка не коштує «нічого»: вона з'їдає відступ і збиває
    // розкладку сторінки-переліку.
    expect(container.querySelector('[data-filter-bar]')).toBeNull();
  });
});
