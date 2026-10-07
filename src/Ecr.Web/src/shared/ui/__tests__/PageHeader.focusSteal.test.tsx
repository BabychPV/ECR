import { describe, it, expect, beforeEach, vi } from 'vitest';
import { render } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';

/**
 * Шапка сторінки бере фокус при монтуванні, але не відбирає той, що людина вже поставила сама
 * (e2e `gridPasteAfterEditor`: `focusin` на заголовку роззброює вставку; `navbarCollapse`: Enter на
 * кнопці меню, а фокус падає на `h1`). Модуль перезавантажується в кожному тесті: «перше
 * монтування після завантаження» — стан модуля.
 */

async function mount(): Promise<HTMLElement> {
  const { PageHeader } = await import('@/shared/ui/PageHeader');
  const view = render(
    <MantineProvider>
      <MemoryRouter>
        <main>
          <PageHeader title="Periods" />
        </main>
      </MemoryRouter>
    </MantineProvider>,
  );

  return view.getByRole('heading', { level: 1 });
}

function focusButton(parent: HTMLElement): HTMLButtonElement {
  const button = document.createElement('button');
  parent.appendChild(button);
  button.focus();

  return button;
}

beforeEach(() => {
  vi.resetModules();
  document.body.innerHTML = '';
});

describe('PageHeader: фокус, який людина вже поставила', () => {
  it('нічого не сфокусовано — заголовок бере фокус', async () => {
    const heading = await mount();

    expect(document.activeElement).toBe(heading);
  });

  it('фокус у змісті сторінки (комірка сітки) — заголовок його не забирає', async () => {
    const { PageHeader } = await import('@/shared/ui/PageHeader');
    const host = document.createElement('div');
    document.body.appendChild(host);
    const main = document.createElement('main');
    host.appendChild(main);
    const cell = focusButton(main);
    const slot = document.createElement('div');
    main.appendChild(slot);

    const view = render(
      <MantineProvider>
        <MemoryRouter>
          <PageHeader title="Periods" />
        </MemoryRouter>
      </MantineProvider>,
      { container: slot },
    );

    // ⛔ Мутація «безумовний фокус» (стара поведінка) віддає фокус заголовку.
    expect(document.activeElement).toBe(cell);
    expect(view.getByRole('heading', { level: 1 })).not.toBe(document.activeElement);
  });

  it('кнопка меню поза змістом, ПЕРШЕ монтування після завантаження — фокус лишається', async () => {
    const { PageHeader } = await import('@/shared/ui/PageHeader');
    const nav = document.createElement('nav');
    document.body.appendChild(nav);
    const toggle = focusButton(nav);
    const host = document.createElement('main');
    document.body.appendChild(host);

    render(
      <MantineProvider>
        <MemoryRouter>
          <PageHeader title="Periods" />
        </MemoryRouter>
      </MantineProvider>,
      { container: host },
    );

    expect(document.activeElement).toBe(toggle);
  });

  it('фокус на посиланні меню при ПЕРЕХОДІ (не перше монтування) — заголовок його бере', async () => {
    const { PageHeader } = await import('@/shared/ui/PageHeader');
    const first = render(
      <MantineProvider>
        <MemoryRouter>
          <main>
            <PageHeader title="Periods" />
          </main>
        </MemoryRouter>
      </MantineProvider>,
    );
    first.unmount();

    const nav = document.createElement('nav');
    document.body.appendChild(nav);
    const link = document.createElement('a');
    link.href = '#';
    nav.appendChild(link);
    link.focus();
    expect(document.activeElement).toBe(link);

    const second = render(
      <MantineProvider>
        <MemoryRouter>
          <main>
            <PageHeader title="Units" />
          </main>
        </MemoryRouter>
      </MantineProvider>,
    );

    expect(document.activeElement).toBe(second.getByRole('heading', { level: 1 }));
  });
});
