import { useEffect, useRef, useState, type JSX, type ReactNode } from 'react';
import { Drawer, Group, Stack, Text } from '@mantine/core';
import { useMediaQuery } from '@mantine/hooks';
import { useUrlState } from '@/shared/ui/useUrlState';

/**
 * Права шторка з подробицями запису (`KIT.md` §6.9 `Drawer`, директива №15
 * §2, шар 2).
 *
 * ⛔ Дві вимоги, які й роблять її шторкою, а не ще одним діалогом:
 *
 *  1. **На широкому екрані сторінка лишається живою.** `withOverlay={false}`
 *     прибирає затемнення, але саме по собі нічого не гарантує: із пасткою
 *     фокуса й блокуванням прокрутки сторінка позаду мертва навіть без
 *     оверлея. Тому разом із оверлеєм знімаються `trapFocus`, `lockScroll` і
 *     `closeOnClickOutside` — інакше «шторка» була б модальним вікном без
 *     затемнення, тобто гіршим модальним вікном.
 *  2. **Адреса.** Відкрита шторка живе в `?panel=` (`ФВ-14.29`), тож її можна
 *     надіслати посиланням і зняти e2e-знімок. Стан НЕ в `useState`: він не
 *     переживає ані перезавантаження, ані «Назад».
 *
 * ⚠ Mantine зашиває `role="dialog"` і `aria-modal={true}` у `ModalBaseContent`
 * ПІСЛЯ розгортання чужих пропсів, тож пропсом їх не перекрити. ✎ batch-4 P3:
 * на широкому екрані `aria-modal` знімається з DOM ефектом нижче — немодальна
 * шторка більше не оголошується читалці модальним діалогом, а `role="dialog"`
 * (немодальний діалог — коректна роль) лишається. Вузький екран — модальний
 * шар, там атрибут повертається. Правило `L2` директиви (§0) очікує
 * `complementary` — з Mantine `Drawer` цього не досягти без власної копії
 * `ModalBase`.
 */

/** Ім'я параметра адреси. Одне місце на весь застосунок. */
const DetailPanelParam = 'panel';

/**
 * Межа «широкого» екрана.
 *
 * ⚠ 1200 px — з директиви дослівно. Значення експортоване, щоб тест перевіряв
 * ТУ САМУ умову, яку читає компонент, а не свою копію рядка.
 */
export const WideViewportQuery = '(min-width: 1200px)';

/** Читання й запис `?panel=` — для сторінки, що відкриває шторку. */
export function useDetailPanel(): [string | null, (id: string | null) => void] {
  return useUrlState(DetailPanelParam);
}

export interface DetailDrawerProps {
  /** Значення `?panel=`, за якого ця шторка відкрита. */
  readonly panelId: string;

  readonly title: string;
  readonly subtitle?: string | undefined;
  readonly badge?: ReactNode | undefined;

  /**
   * ⛔ Підпис кнопки закриття — обов'язковий проп, а не значення за
   * замовчуванням. Mantine малює її самим хрестиком; без `aria-label` вона
   * оголошується як «кнопка», і єдиний спосіб закрити шторку з клавіатури,
   * окрім `Esc`, стає безіменним. Рядок приходить ззовні, бо нових ключів
   * каталогу цей PR не заводить (`09-seed.sql` — у сусідньому PR).
   */
  readonly closeLabel: string;

  readonly footer?: ReactNode | undefined;
  readonly size?: string | undefined;

  /** ⚠ Викликається ПІСЛЯ прибирання `?panel=` з адреси. */
  readonly onClose?: (() => void) | undefined;

  readonly children?: ReactNode | undefined;
}

export function DetailDrawer({
  panelId,
  title,
  subtitle,
  badge,
  closeLabel,
  footer,
  size,
  onClose,
  children,
}: DetailDrawerProps): JSX.Element {
  const [panel, setPanel] = useDetailPanel();

  /*
   * ⚠ `getInitialValueInEffect: false` навмисно: із дефолтним `true` перший
   * рендер ЗАВЖДИ віддає запасне значення (вузький екран), і на широкому
   * екрані шторка на один кадр з'являлася б із затемненням — тобто сторінка
   * встигала б померти й ожити. Тут немає серверного рендера, тож розбіжності
   * гідратації, заради якої дефолт і існує, теж немає.
   */
  const wide = useMediaQuery(WideViewportQuery, false, { getInitialValueInEffect: false }) === true;

  const opened = panel === panelId;

  /*
   * ⛔ Відкривач запам'ятовується ТУТ, а не покладається на `returnFocus`
   * Mantine. Той ловить відкривач лише на ПЕРЕХОДІ `opened` усередині вже
   * змонтованої шторки і лише з пасткою фокуса. А `ListPage` (Jobs,
   * Consistency) монтує шторку вже відкритою й знімає її разом із `?panel=` —
   * і після Esc фокус падав на `<body>` (живий прогін пачки batch-2-a, 1100 px).
   * Читання `activeElement` у рендері — свідоме: на кадрі відкриття фокус ще
   * стоїть на кнопці, що змінила адресу.
   */
  const [wasOpened, setWasOpened] = useState(false);
  const [opener, setOpener] = useState<HTMLElement | null>(null);
  if (opened !== wasOpened) {
    setWasOpened(opened);
    if (opened) setOpener(focusedOutside(panelId));
  }

  /*
   * ⛔ WCAG 2.4.3 (a11y-pass): шторку можуть зняти й без `close` — `ListPage`
   * прибирає її з дерева разом із `?panel=` (кнопка «Назад», перехід за
   * адресою). Тоді фокус повертається при демонтажі за тим самим правилом.
   */
  const openerRef = useRef<HTMLElement | null>(null);
  openerRef.current = opened ? opener : null;
  useEffect(() => () => returnFocusTo(openerRef.current, panelId), [panelId]);

  /*
   * ✎ batch-4 P3: `aria-modal` — лише там, де шторка справді модальна. React не
   * повертає знятий атрибут сам (пропс Mantine не змінюється), тож спостерігач
   * лише ловить вміст, змонтований переходом пізніше за цей ефект.
   */
  useEffect(() => {
    if (!opened) return undefined;
    const sync = (): void => {
      for (const root of document.querySelectorAll('[data-panel]')) {
        if (root.getAttribute('data-panel') !== panelId) continue;
        for (const element of root.querySelectorAll('[role="dialog"]')) {
          if (wide) element.removeAttribute('aria-modal');
          else if (element.getAttribute('aria-modal') !== 'true') element.setAttribute('aria-modal', 'true');
        }
      }
    };
    sync();
    if (!wide) return undefined;
    const observer = new MutationObserver(sync);
    observer.observe(document.body, { subtree: true, childList: true, attributes: true, attributeFilter: ['aria-modal'] });
    return () => observer.disconnect();
  }, [opened, wide, panelId]);

  const close = (): void => {
    setPanel(null);
    onClose?.();
    returnFocusTo(opener, panelId);
  };

  return (
    <Drawer
      opened={opened}
      onClose={close}
      position="right"
      size={size ?? 'md'}
      withOverlay={!wide}
      trapFocus={!wide}
      lockScroll={!wide}
      closeOnClickOutside={!wide}
      returnFocus
      closeButtonProps={{ 'aria-label': closeLabel }}
      title={
        <Group gap="xs">
          <Text fw={600}>{title}</Text>
          {subtitle !== undefined && subtitle !== '' ? (
            <Text size="sm" c="dimmed">
              {subtitle}
            </Text>
          ) : null}
          {badge}
        </Group>
      }
      data-panel={panelId}
      data-wide={wide ? 'true' : 'false'}
    >
      <Stack gap="md">
        {children}

        {/* ⛔ `D15-06`: немає підвалу — немає й порожнього рядка кнопок. */}
        {footer !== undefined && footer !== null && footer !== false ? (
          <Group justify="flex-end" gap="xs">
            {footer}
          </Group>
        ) : null}
      </Stack>
    </Drawer>
  );
}

/** Що зараз у фокусі, якщо це не `<body>` і не сама шторка. */
function focusedOutside(panelId: string): HTMLElement | null {
  const active = document.activeElement;
  if (!(active instanceof HTMLElement) || active === document.body) return null;

  return insidePanel(active, panelId) ? null : active;
}

/**
 * Повертає фокус на відкривач після закриття.
 *
 * ⚠ Лише якщо фокус загубився (на `<body>`) або лишився в шторці, що
 * зникає. На широкому екрані сторінка жива: людина могла вже перейти до
 * іншого поля, і висмикувати її звідти назад на відкривач — гірше, ніж нічого.
 * Таймер — щоб шторка встигла зникнути з адреси й розмітки.
 */
function returnFocusTo(opener: HTMLElement | null, panelId: string): void {
  if (opener === null) return;

  window.setTimeout(() => {
    if (!opener.isConnected) return;

    const active = document.activeElement;
    const lost =
      active === null ||
      active === document.body ||
      !active.isConnected ||
      insidePanel(active, panelId);
    if (lost) opener.focus({ preventScroll: true });
  }, 0);
}

function insidePanel(element: Element, panelId: string): boolean {
  return element.closest('[data-panel]')?.getAttribute('data-panel') === panelId;
}
