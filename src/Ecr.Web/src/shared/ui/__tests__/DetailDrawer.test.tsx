import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter, useLocation } from 'react-router-dom';
import type { JSX } from 'react';
import { theme } from '@/shared/theme/theme';
import { DetailDrawer, WideViewportQuery } from '@/shared/ui/DetailDrawer';

/**
 * `DetailDrawer` — директива №15 §2, шар 2: «`position="right"`,
 * `withOverlay={false}` на ≥ 1200 px (сторінка лишається живою), з оверлеєм на
 * вузьких; адреса — `?panel=` через чинний `useUrlState`».
 *
 * ⛔ «Сторінка лишається живою» — НЕ синонім «немає затемнення». Шторка з
 * пасткою фокуса й блокуванням прокрутки вбиває сторінку позаду так само
 * надійно, просто непомітно для ока. Тому на широкому екрані тут
 * перевіряється три різні речі: оверлея немає, фокус НЕ викрадено, клік по
 * кнопці позаду доходить до її обробника і шторку не закриває.
 */

const Panel = 'user-7';

/** Показує адресу поруч зі шторкою: стан має жити в `?panel=`, не в `useState`. */
function LocationProbe(): JSX.Element {
  const location = useLocation();

  return <span data-testid="search">{location.search}</span>;
}

function stubViewport(wide: boolean): void {
  vi.stubGlobal('matchMedia', (query: string) => ({
    matches: wide && query === WideViewportQuery,
    media: query,
    onchange: null,
    addEventListener: () => {},
    removeEventListener: () => {},
    addListener: () => {},
    removeListener: () => {},
    dispatchEvent: () => false,
  }));
}

/** Дає спрацювати відкладеному переносу фокуса пастки Mantine. */
async function settle(): Promise<void> {
  await new Promise((resolve) => {
    setTimeout(resolve, 0);
  });
  await new Promise((resolve) => {
    setTimeout(resolve, 0);
  });
}

function scene(url: string, onOutside: () => void, onClose: () => void): JSX.Element {
  return (
    <MantineProvider theme={theme}>
      <MemoryRouter initialEntries={[url]}>
        <button type="button" data-testid="outside" onClick={onOutside}>
          Still clickable
        </button>

        <DetailDrawer
          panelId={Panel}
          title="Ivanov, P."
          subtitle="p.ivanov"
          closeLabel="Close details"
          footer={<button type="button">Save</button>}
          onClose={onClose}
        >
          <p data-testid="panel-body">Last sign-in: 2026-09-18</p>
        </DetailDrawer>

        <LocationProbe />
      </MemoryRouter>
    </MantineProvider>
  );
}

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe('DetailDrawer — адреса керує шторкою (ФВ-14.29)', () => {
  it('L2: без ?panel= шторки немає (закрита за замовчуванням)', () => {
    stubViewport(false);
    render(scene('/admin/security', () => {}, () => {}));

    /*
     * ⚠ Перевіряється ВМІСТ, а не корінь: Mantine лишає порожній кореневий
     * вузол шторки в DOM завжди (`ModalBase` малює `Box` незалежно від
     * `opened`, умовний лише вміст). Заміряно — саме так, і твердження
     * «вузла `[data-panel]` немає» було б неправдою про бібліотеку, а не про
     * нашу поведінку.
     */
    expect(screen.queryByRole('dialog')).toBeNull();
    expect(screen.queryByText('Ivanov, P.')).toBeNull();
    expect(screen.queryByTestId('panel-body')).toBeNull();
    expect(document.querySelectorAll('.mantine-Drawer-overlay')).toHaveLength(0);
  });

  it('?panel=<інший> ЦЮ шторку не відкриває', () => {
    stubViewport(false);
    render(scene('/admin/security?panel=role-3', () => {}, () => {}));

    expect(screen.queryByRole('dialog')).toBeNull();
  });

  it('?panel=user-7 відкриває саме її, із заголовком і підзаголовком', () => {
    stubViewport(false);
    render(scene(`/admin/security?panel=${Panel}`, () => {}, () => {}));

    const drawer = screen.getByRole('dialog');

    expect(drawer.closest('[data-panel]')?.getAttribute('data-panel')).toBe(Panel);
    expect(screen.getByText('Ivanov, P.')).toBeDefined();
    expect(screen.getByText('p.ivanov')).toBeDefined();
    expect(screen.getByTestId('panel-body')).toBeDefined();
  });

  it('стан переживає перемальовування: він в адресі, а не в useState', () => {
    stubViewport(false);
    const view = render(scene(`/admin/security?panel=${Panel}`, () => {}, () => {}));

    expect(screen.getByTestId('search').textContent).toBe(`?panel=${Panel}`);

    view.rerender(scene(`/admin/security?panel=${Panel}`, () => {}, () => {}));

    expect(screen.getByRole('dialog')).toBeDefined();
    expect(screen.getByTestId('search').textContent).toBe(`?panel=${Panel}`);
  });

  it('закривання ПРИБИРАЄ параметр з адреси, а не лишає хвіст', async () => {
    stubViewport(false);
    const user = userEvent.setup();
    const onClose = vi.fn();

    render(scene(`/admin/security?panel=${Panel}`, () => {}, onClose));

    await user.click(screen.getByRole('button', { name: 'Close details' }));

    await waitFor(() => {
      expect(screen.getByTestId('search').textContent).toBe('');
    });
    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it('кнопка закривання має ім’я з пропа, а не лише значок', () => {
    stubViewport(false);
    render(scene(`/admin/security?panel=${Panel}`, () => {}, () => {}));

    expect(screen.getByRole('button', { name: 'Close details' })).toBeDefined();
  });
});

describe('DetailDrawer — ≥ 1200 px: сторінка лишається живою', () => {
  it('оверлея НЕМАЄ', () => {
    stubViewport(true);
    render(scene(`/admin/security?panel=${Panel}`, () => {}, () => {}));

    expect(screen.getByRole('dialog')).toBeDefined();
    expect(document.querySelector('[data-wide]')?.getAttribute('data-wide')).toBe('true');
    expect(document.querySelectorAll('.mantine-Drawer-overlay')).toHaveLength(0);
  });

  it('фокус НЕ викрадено: він лишається там, де був', async () => {
    stubViewport(true);
    render(scene(`/admin/security?panel=${Panel}`, () => {}, () => {}));

    const outside = screen.getByTestId('outside');
    outside.focus();

    await settle();

    /*
     * ⛔ Це і є «сторінка лишається живою». Прибрати лише затемнення й
     * лишити `trapFocus` означало б зробити модальне вікно без затемнення:
     * око бачить робочу сторінку, клавіатура з неї не виходить.
     */
    expect(document.activeElement).toBe(outside);
  });

  it('клік по кнопці ПОЗАДУ доходить до обробника і шторку не закриває', async () => {
    stubViewport(true);
    const user = userEvent.setup();
    const onOutside = vi.fn();

    render(scene(`/admin/security?panel=${Panel}`, onOutside, () => {}));

    await user.click(screen.getByTestId('outside'));

    /*
     * ⚠ Межа цього твердження названа чесно: у jsdom немає розкладки, тож
     * оверлей НЕ перехоплює кліки навіть тоді, коли він є (Mantine закриває
     * шторку з `onClick` самого оверлея, а не слухачем документа). Мутація
     * «прибрати всі чотири пропси» цей тест не завалила — завалила два
     * сусідні. Тобто справжній доказ «сторінка жива» — ВІДСУТНІСТЬ оверлея й
     * незачеплений фокус вище; цей тест лише не дає обробнику зникнути.
     */
    expect(onOutside).toHaveBeenCalledTimes(1);
    expect(screen.getByRole('dialog')).toBeDefined();
    expect(screen.getByTestId('search').textContent).toBe(`?panel=${Panel}`);
  });
});

describe('DetailDrawer — вузький екран: це модальний шар', () => {
  it('оверлей Є', () => {
    stubViewport(false);
    render(scene(`/admin/security?panel=${Panel}`, () => {}, () => {}));

    expect(document.querySelector('[data-wide]')?.getAttribute('data-wide')).toBe('false');
    expect(document.querySelectorAll('.mantine-Drawer-overlay')).toHaveLength(1);
  });

  it('фокус переходить УСЕРЕДИНУ шторки (пастка увімкнена)', async () => {
    stubViewport(false);
    render(scene(`/admin/security?panel=${Panel}`, () => {}, () => {}));

    const outside = screen.getByTestId('outside');
    outside.focus();

    await settle();

    const drawer = screen.getByRole('dialog');

    // Дзеркало до широкого випадку: там фокус лишався зовні — тут він
    // мусить бути всередині, інакше «оверлей є» нічого не доводить.
    await waitFor(() => {
      expect(drawer.contains(document.activeElement)).toBe(true);
    });
    expect(document.activeElement).not.toBe(outside);
  });
});

describe('DetailDrawer — елемент без даних не малюється (D15-06)', () => {
  it('без footer рядка кнопок немає; з footer — є', () => {
    stubViewport(false);

    const view = render(
      <MantineProvider theme={theme}>
        <MemoryRouter initialEntries={[`/x?panel=${Panel}`]}>
          <DetailDrawer panelId={Panel} title="Ivanov, P." closeLabel="Close details">
            <p data-testid="panel-body">body</p>
          </DetailDrawer>
        </MemoryRouter>
      </MantineProvider>,
    );

    expect(screen.queryByRole('button', { name: 'Save' })).toBeNull();

    view.rerender(
      <MantineProvider theme={theme}>
        <MemoryRouter initialEntries={[`/x?panel=${Panel}`]}>
          <DetailDrawer
            panelId={Panel}
            title="Ivanov, P."
            closeLabel="Close details"
            footer={<button type="button">Save</button>}
          >
            <p data-testid="panel-body">body</p>
          </DetailDrawer>
        </MemoryRouter>
      </MantineProvider>,
    );

    expect(screen.getByRole('button', { name: 'Save' })).toBeDefined();
  });

  it('порожній підзаголовок не малює порожнього рядка', () => {
    stubViewport(false);

    render(
      <MantineProvider theme={theme}>
        <MemoryRouter initialEntries={[`/x?panel=${Panel}`]}>
          <DetailDrawer panelId={Panel} title="Ivanov, P." subtitle="" closeLabel="Close details">
            <p>body</p>
          </DetailDrawer>
        </MemoryRouter>
      </MantineProvider>,
    );

    const title = screen.getByText('Ivanov, P.').parentElement;

    expect(title?.querySelectorAll('p')).toHaveLength(1);
  });
});
