import type { JSX, ReactNode } from 'react';
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
 * ⚠ Що зробити не вдалося, і це названо, а не замовчано: Mantine зашиває
 * `role="dialog"` і `aria-modal={true}` у `ModalBaseContent` ПІСЛЯ розгортання
 * чужих пропсів, тож немодальна шторка однаково оголошується читалці як
 * модальний діалог. Правило `L2` директиви (§0) очікує
 * `queryByRole('complementary')` — з Mantine `Drawer` цього не досягти без
 * власної копії `ModalBase`. Питання винесено у звіт PR.
 */

/** Ім'я параметра адреси. Одне місце на весь застосунок. */
export const DetailPanelParam = 'panel';

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

  const close = (): void => {
    setPanel(null);
    onClose?.();
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
