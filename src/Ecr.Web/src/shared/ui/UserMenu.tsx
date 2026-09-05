import { useState, type JSX } from 'react';
import {
  Button,
  Divider,
  Group,
  Menu,
  SegmentedControl,
  Stack,
  Text,
  useMantineColorScheme,
} from '@mantine/core';
import { apiFetch } from '@/api/client';
import { t } from '@/shared/i18n';
import { applyDensity, density, setDensity, type Density } from '@/shared/theme/preferences';

/**
 * Профіль користувача: тема, щільність, вихід (`ФВ-14.14`, `ФВ-14.15`, `D-131`).
 *
 * ⚠ Обидва перемикачі — тут, а не в «Налаштуваннях»: окремої сторінки
 * налаштувань у системі немає, і заводити її заради двох перемикачів означало б
 * ще один пункт меню, який відкривають двічі за весь час роботи.
 */
export function UserMenu({ userName }: { userName: string }): JSX.Element {
  const { colorScheme, setColorScheme } = useMantineColorScheme();
  const [rows, setRows] = useState<Density>(density);

  function changeDensity(value: Density): void {
    setRows(value);
    setDensity(value);
    applyDensity(value);
  }

  return (
    <Menu shadow="md" width={260} position="bottom-end" withinPortal>
      <Menu.Target>
        {/*
         * ⚠ Кнопка, а не `<Text>` із `onClick`: клавіатурі потрібен елемент,
         * на який можна перейти табом і натиснути пробілом (`ФВ-14.19`), а
         * читалці — роль, що обіцяє дію.
         */}
        <Button variant="subtle" size="compact-sm">
          <span className="ecr-ellipsis">{userName}</span>
        </Button>
      </Menu.Target>

      <Menu.Dropdown>
        <Stack gap="xs" p="xs">
          <div>
            <Text size="xs" c="dimmed" mb={4} id="ecr-theme-label">
              {t('profile.theme')}
            </Text>

            {/*
             * ⚠ Три значення, а не перемикач «темна: так/ні». `auto` — це не
             * зайвий варіант, а стан за замовчуванням: людина, у якої система
             * увечері темніє, інакше отримувала б білий екран на весь монітор.
             */}
            <SegmentedControl
              fullWidth
              size="xs"
              value={colorScheme}
              onChange={(value) => {
                setColorScheme(value as 'light' | 'dark' | 'auto');
              }}
              aria-labelledby="ecr-theme-label"
              data={[
                { value: 'auto', label: t('profile.themeAuto') },
                { value: 'light', label: t('profile.themeLight') },
                { value: 'dark', label: t('profile.themeDark') },
              ]}
            />
          </div>

          <div>
            <Text size="xs" c="dimmed" mb={4} id="ecr-density-label">
              {t('profile.density')}
            </Text>

            <SegmentedControl
              fullWidth
              size="xs"
              value={rows}
              onChange={(value) => {
                changeDensity(value as Density);
              }}
              aria-labelledby="ecr-density-label"
              data={[
                { value: 'compact', label: t('profile.densityCompact') },
                { value: 'comfortable', label: t('profile.densityComfortable') },
              ]}
            />
          </div>
        </Stack>

        <Divider />

        <Menu.Item onClick={signOut}>
          <Group justify="space-between">{t('profile.logout')}</Group>
        </Menu.Item>
      </Menu.Dropdown>
    </Menu>
  );
}

/**
 * Вихід.
 *
 * ⛔ Перезавантаження сторінки, а не перехід роутером. Cookie сеансу гасить
 * сервер, але в пам'яті клієнта лишається кеш `react-query` з профілем і
 * даними попереднього користувача; на спільному комп'ютері в диспетчерській це
 * означало б, що наступний бачить чужі документи, доки не оновить вкладку.
 *
 * ⚠ Помилка виходу не блокує вихід: якщо сервер недоступний, користувач усе
 * одно має піти з екрана, а cookie протухне сама.
 */
async function signOut(): Promise<void> {
  try {
    await apiFetch<void>('/api/v1/logout', { method: 'POST' });
  } finally {
    window.location.assign('/login');
  }
}
