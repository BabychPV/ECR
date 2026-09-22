import { lazy, Suspense, useState, type JSX } from 'react';
import { Badge, Button, Text } from '@mantine/core';
import { t } from '@/shared/i18n';
import { useMyTasks } from './useMyTasks';
import { activeJobCount } from './myTasks';

/**
 * Вхід у шухляду «My tasks» із шапки застосунку (`UI-07`, `UX-09`).
 *
 * ⛔ Права тут НЕ перевіряються, і це не недогляд. Директива №15, бекенд,
 * §BE-08: «шухляда «My tasks» у шапці (усі ролі)». Показувати чужі задачі
 * кнопка не може за побудовою: запит іде з `mine=true`, і власника бере сервер
 * із сеансу (`JobsController.List`), а не клієнт із параметра. Умова виду
 * `can(me, 'System.ViewHealth')` сховала б від більшості користувачів рівно
 * той перелік, який `Q-156` навмисно звільнив від цього права, — тобто
 * повернула б `W-09`: «закрив вкладку — готовий файл недосяжний».
 *
 * ⛔ Сама шухляда — ЛИШЕ динамічним `import()`. Оболонка потрапляє в бюджет
 * кожного маршруту (`D-132`), тож у статичному бандлі лишається тільки цей
 * файл: кнопка, позначка й запит переліку. Статичний імпорт `MyTasksDrawer`
 * тут — дефект, і його ловить `__tests__/MyTasksLauncher.test.tsx`.
 */
const loadDrawer = () => import('./MyTasksDrawer');

const MyTasksDrawer = lazy(async () => ({
  default: (await loadDrawer()).MyTasksDrawer,
}));

/** Прогрів чанка за наміром (наведення/фокус); відмова — не привід падати. */
function prefetchDrawer(): void {
  loadDrawer().catch(() => undefined);
}

export function MyTasksLauncher(): JSX.Element {
  /*
   * ⛔ `L2`: шухляда закрита за замовчуванням — початкове значення `false`, і
   * жодного шляху, яким вона відкрилася б сама. Стан локальний, не в адресі:
   * пояснення — у шапці `MyTasksDrawer.tsx` (спільний `?panel=` закривав би
   * шухляду сторінки під нею).
   */
  const [opened, setOpened] = useState(false);

  // Шухляда монтується з першим відкриттям і далі лишається (анімація закриття).
  const [requested, setRequested] = useState(false);

  const tasks = useMyTasks();
  const active = activeJobCount(tasks.data);

  const label = t('jobs.myTasks');

  return (
    <>
      <Button
        variant="default"
        size="xs"
        aria-label={label}
        aria-haspopup="dialog"
        onClick={() => {
          setRequested(true);
          setOpened(true);
        }}
        onMouseEnter={prefetchDrawer}
        onFocus={prefetchDrawer}
        leftSection={<TasksIcon />}
        rightSection={
          /*
           * ⚠ Позначка має ВЛАСНУ доступну назву, а не саме лише число:
           * читалка інакше оголосила б кнопку як «My tasks 2», і «2» нічого не
           * означало б. Число лишається видимим — це і є індикатор із `UX-09`.
           *
           * ⛔ Нуль не малюється зовсім. Позначка «0» — це шум, що змушує
           * щоразу її читати, аби пересвідчитись: нічого не відбувається.
           */
          active > 0 ? (
            <Badge
              size="sm"
              circle
              color="brand"
              aria-label={t('jobs.myTasksActive', { n: active })}
              data-my-tasks-active={String(active)}
            >
              {active}
            </Badge>
          ) : null
        }
      >
        <Text span size="xs" visibleFrom="sm">
          {label}
        </Text>
      </Button>

      {requested && (
        <Suspense fallback={null}>
          <MyTasksDrawer
            opened={opened}
            onClose={() => setOpened(false)}
            jobs={tasks.data}
            isPending={tasks.isPending}
            error={tasks.error}
            onRetry={() => void tasks.refetch()}
          />
        </Suspense>
      )}
    </>
  );
}

/** Перелік із галочкою — той самий лінійний стиль, що в `app/navIcons.tsx`. */
function TasksIcon(): JSX.Element {
  return (
    <svg
      width={16}
      height={16}
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth={1.75}
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden="true"
      focusable="false"
    >
      <path d="M4 7h11" />
      <path d="M4 12h8" />
      <path d="M4 17h6" />
      <path d="m16 15 2 2 4-4" />
    </svg>
  );
}
