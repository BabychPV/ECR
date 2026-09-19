import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen, cleanup, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import type { ReactNode } from 'react';
import { PageHeader } from '@/shared/ui/PageHeader';
import { RouteAnnouncer } from '@/shared/ui/RouteAnnouncer';

/**
 * Розширення шапки сторінки (директива №15, §2, Шар 2):
 * `badge`, `count`, `meta`, `back`, `primary`, `secondary[≤2]`, `more[]`.
 *
 * ⛔ Цей файл НЕ дублює чинних перевірок фокуса й оголошення — вони лежать у
 * `pages/__tests__/ChangePasswordPage.pageHeader.test.tsx` і лишилися без
 * жодної правки; саме це й було умовою прийняття розширення. Тут перевіряється
 * рівно те, чого раніше не було, плюс один сторож: що нові пропи не з'їли
 * фокус і оголошення (останній `describe`).
 *
 * ⚠ Усі підписи приходять ПРОПАМИ. Ключів під них у серверному каталозі
 * (`09-seed.sql`) немає, а `t()` на неіснуючий ключ завалив би сторожа
 * `Кожен_рядок_якого_просить_клієнт_є_в_каталозі`.
 */
function show(node: ReactNode): void {
  render(
    <MantineProvider>
      <MemoryRouter>
        <div data-testid="host">{node}</div>
      </MemoryRouter>
    </MantineProvider>,
  );
}

afterEach(() => {
  cleanup();
});

describe('PageHeader: назва, значок, лічильник, пояснення', () => {
  it('badge і count стоять поруч із назвою', () => {
    show(<PageHeader title="Документи" badge={<span>Чернетка</span>} count={42} />);

    expect(screen.getByRole('heading', { name: 'Документи' })).toBeDefined();
    expect(screen.getByText('Чернетка')).toBeDefined();
    expect(screen.getByText('42')).toBeDefined();
  });

  it('meta — окремий рядок пояснення, а не частина назви', () => {
    show(<PageHeader title="Документи" meta="Період січень 2026, 12 аркушів" />);

    // ⛔ Мутаційний доказ на підміну: якби `meta` домалювалася в заголовок
    // (найпростіший спосіб «зробити, щоб текст був на екрані»), доступне ім'я
    // заголовка містило б її — і цей `getByRole` за точним іменем не знайшов
    // би нічого.
    expect(screen.getByRole('heading', { name: 'Документи' })).toBeDefined();
    expect(screen.getByText('Період січень 2026, 12 аркушів')).toBeDefined();
  });

  it('back — посилання з адресою, а не декоративна стрілка', () => {
    show(<PageHeader title="Аркуш 3" back={{ label: 'Back to Documents', href: '/documents' }} />);

    const link = screen.getByRole('link', { name: '← Back to Documents' });
    expect(link.getAttribute('href')).toBe('/documents');
  });

  /**
   * ⛔ `D15-06`: елемент без даних не малюється.
   *
   * ⚠ Перевіряється не «немає тексту», а немає ПОРОЖНІХ ВУЗЛІВ. Саме так це
   * ламається насправді: обгортка `<Group>` під `badge`/`count` чи `<Stack>`
   * під `meta`, намальована беззастережно, тексту не додає — вона додає
   * відступ навколо нічого і збиває `justify="space-between"`, і помітити це
   * можна лише очима на екрані.
   *
   * ⛔ Мутаційний доказ (обидва боки): зробіть у `PageHeader.tsx` обгортку
   * `titleRow` безумовною (`<Group gap="xs">{headingNode}{badge}…</Group>`
   * замість тернарного вибору) — цей тест падає зі списком порожніх вузлів,
   * а всі інші лишаються зеленими. Приберіть перевірку `shown()` — так само.
   */
  it('без badge/count/meta/дій у шапці немає жодного порожнього вузла', () => {
    show(<PageHeader title="Документи" />);

    const host = screen.getByTestId('host');
    const empty = Array.from(host.querySelectorAll('*'))
      .filter((el) => el.children.length === 0 && (el.textContent ?? '').trim() === '')
      .map((el) => `${el.tagName.toLowerCase()}.${el.className}`);

    expect(empty).toEqual([]);
  });

  it('порожній badge (умовний рендер викликача) теж не лишає обгортки', () => {
    // ⚠ `badge={false}` — звичайний вигляд виклику `badge={isDraft && <…/>}`.
    // Перевірка на `!== undefined` цього не ловить, і саме тому в компоненті
    // стоїть `shown()`, а не порівняння з `undefined`.
    show(<PageHeader title="Документи" badge={false} />);

    const host = screen.getByTestId('host');
    const empty = Array.from(host.querySelectorAll('*')).filter(
      (el) => el.children.length === 0 && (el.textContent ?? '').trim() === '',
    );

    expect(empty).toEqual([]);
  });
});

describe('PageHeader: дії', () => {
  it('primary — окрема кнопка, і вона `filled` (L1)', async () => {
    const create = vi.fn();
    show(<PageHeader title="Документи" primary={{ label: 'Створити', onClick: create }} />);

    const button = screen.getByRole('button', { name: 'Створити' });

    // ⚠ `L1` (одна головна дія на екран) перевіряється тестом ЕКРАНА по
    // `data-variant="filled"`; атрибут з'являється лише від явного пропа, тому
    // він і заданий явно в компоненті.
    expect(button.getAttribute('data-variant')).toBe('filled');

    await userEvent.click(button);
    expect(create).toHaveBeenCalledOnce();
  });

  it('чинний проп `actions` і далі малюється — розширення нічого не забрало', () => {
    show(
      <PageHeader
        title="Документи"
        actions={<button type="button">Власна дія</button>}
        primary={{ label: 'Створити' }}
      />,
    );

    expect(screen.getByRole('button', { name: 'Власна дія' })).toBeDefined();
    expect(screen.getByRole('button', { name: 'Створити' })).toBeDefined();
  });
});

/**
 * ⛔ Головна межа розширення, і доводиться саме вона: ДВІ другорядні дії
 * лишаються кнопками, ТРЕТЯ забирає з видноти всі три.
 *
 * ⚠ Обидва боки межі в одному описі навмисно: тест лише на «три — у меню»
 * лишився б зеленим і в компоненті, який ховає в меню взагалі все, а тест
 * лише на «дві — кнопки» — у тому, який не ховає нічого.
 */
describe('PageHeader: межа secondary ≤ 2', () => {
  const exportAction = { label: 'Експорт', onClick: vi.fn() };
  const printAction = { label: 'Друк', onClick: vi.fn() };
  const two = [exportAction, printAction];
  const three = [exportAction, printAction, { label: 'Архів', onClick: vi.fn() }];

  it("дві secondary лишаються кнопками, меню не з'являється", () => {
    show(<PageHeader title="Документи" secondary={two} />);

    expect(screen.getByRole('button', { name: 'Експорт' })).toBeDefined();
    expect(screen.getByRole('button', { name: 'Друк' })).toBeDefined();
    expect(screen.queryByRole('button', { name: 'More' })).toBeNull();
  });

  it('три secondary зникають із видноти — лишається одна кнопка меню', () => {
    show(<PageHeader title="Документи" secondary={three} />);

    expect(screen.queryByRole('button', { name: 'Експорт' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Друк' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Архів' })).toBeNull();

    // ⛔ І це не «третя поїхала»: на видноті рівно ОДНА кнопка — меню.
    expect(screen.getAllByRole('button')).toHaveLength(1);
    expect(screen.getByRole('button', { name: 'More' })).toBeDefined();
  });

  it('усі три доступні в меню — жодна не загубилася по дорозі', async () => {
    const archive = vi.fn();
    show(
      <PageHeader
        title="Документи"
        secondary={[exportAction, printAction, { label: 'Архів', onClick: archive }]}
      />,
    );

    await userEvent.click(screen.getByRole('button', { name: 'More' }));

    await waitFor(() => {
      expect(screen.getByRole('menuitem', { name: 'Експорт' })).toBeDefined();
    });

    expect(screen.getByRole('menuitem', { name: 'Друк' })).toBeDefined();

    await userEvent.click(screen.getByRole('menuitem', { name: 'Архів' }));
    expect(archive).toHaveBeenCalledOnce();
  });

  it('more[] іде в меню завжди, навіть коли secondary лишилися кнопками', async () => {
    const purge = vi.fn();
    show(<PageHeader title="Документи" secondary={two} more={[{ label: 'Очистити', onClick: purge }]} />);

    // Обидві secondary — на видноті…
    expect(screen.getByRole('button', { name: 'Експорт' })).toBeDefined();

    // …а меню все одно є, бо в ньому `more`.
    await userEvent.click(screen.getByRole('button', { name: 'More' }));

    await waitFor(() => {
      expect(screen.getByRole('menuitem', { name: 'Очистити' })).toBeDefined();
    });

    // ⛔ І secondary в меню НЕ продубльовані: інакше та сама дія була б
    // доступна двома шляхами одночасно.
    expect(screen.queryByRole('menuitem', { name: 'Експорт' })).toBeNull();

    await userEvent.click(screen.getByRole('menuitem', { name: 'Очистити' }));
    expect(purge).toHaveBeenCalledOnce();
  });

  it('підпис кнопки меню задається пропом — літерал лише за замовчуванням', () => {
    show(<PageHeader title="Документи" more={[{ label: 'Очистити' }]} moreLabel="Ще" />);

    expect(screen.getByRole('button', { name: 'Ще' })).toBeDefined();
    expect(screen.queryByRole('button', { name: 'More' })).toBeNull();
  });
});

/**
 * ⛔ Сторож розширення: нові пропи не мають права коштувати фокуса й
 * оголошення. Чинні тести перевіряють це на шапці БЕЗ нових пропів —
 * тобто саме той шлях, яким розширення й могло б їх зламати, там не
 * проходить.
 */
describe('PageHeader: фокус і оголошення переживають нові пропи', () => {
  it('із back/badge/meta/дій заголовок і далі бере фокус і оголошується', async () => {
    render(
      <MantineProvider>
        <MemoryRouter>
          <RouteAnnouncer />
          {/*
            ⚠ Назва тут НЕ повторює жодної з попередніх у цьому файлі
            навмисно: `announceRoute` дедуплікує за текстом у модульній
            змінній, і повторна назва дала б порожню область оголошень —
            тобто тест падав би не на своїй причині.
          */}
          <PageHeader
            title="Зведення за IV квартал"
            back={{ label: 'Back to Documents', href: '/documents' }}
            badge={<span>Чернетка</span>}
            count={7}
            meta="Період січень 2026"
            primary={{ label: 'Зберегти' }}
            secondary={[{ label: 'Експорт' }, { label: 'Друк' }]}
            more={[{ label: 'Очистити' }]}
          />
        </MemoryRouter>
      </MantineProvider>,
    );

    const heading = screen.getByRole('heading', { name: 'Зведення за IV квартал' });

    expect(document.activeElement).toBe(heading);
    expect(heading.getAttribute('tabindex')).toBe('-1');

    await waitFor(() => {
      expect(screen.getByRole('status').textContent).toBe('Зведення за IV квартал');
    });
  });
});
