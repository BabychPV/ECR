import { lazy, Suspense, useCallback, useEffect, useRef, useState, type JSX } from 'react';
import { Button, Text } from '@mantine/core';
import { t } from '@/shared/i18n';
import { belongsToSomeoneElse, isPaletteHotkey } from './paletteHotkey';

/**
 * Вхід у командну палітру з оболонки: кнопка в шапці й `Ctrl+K`/`⌘K`.
 *
 * ⛔ Сама палітра — ЛИШЕ динамічним `import()`. Оболонка потрапляє в кожен
 * маршрут бюджету `D-132`, тож у статичному бандлі лишається тільки цей файл:
 * кнопка й слухач клавіші. Статичний імпорт `DataSearchPalette` тут — дефект,
 * і його ловить `__tests__/SearchLauncher.lazy.test.tsx` («модуль палітри не
 * обчислено до першого відкриття»).
 */
const loadPalette = () => import('./DataSearchPalette');

const DataSearchPalette = lazy(async () => ({
  default: (await loadPalette()).DataSearchPalette,
}));

/** Прогрів чанка за наміром (наведення/фокус на кнопці); відмова — не привід падати. */
function prefetchPalette(): void {
  loadPalette().catch(() => undefined);
}

const isApple =
  typeof navigator !== 'undefined' && /Mac|iPhone|iPad/i.test(navigator.userAgent);

/** Кнопка пошуку в шапці і гаряча клавіша палітри. */
export function SearchLauncher(): JSX.Element {
  const [opened, setOpened] = useState(false);
  // Палітра монтується з першим відкриттям і далі лишається (анімація закриття).
  const [requested, setRequested] = useState(false);
  const returnTo = useRef<HTMLElement | null>(null);

  const open = useCallback((): void => {
    const current = document.activeElement;
    returnTo.current =
      current instanceof HTMLElement && current !== document.body ? current : null;
    setRequested(true);
    setOpened(true);
  }, []);

  const finish = useCallback((restoreFocus: boolean): void => {
    setOpened(false);
    const target = returnTo.current;
    returnTo.current = null;

    // ⚠ Після того, як діалог відпустить пастку фокуса, — тому наступним тактом.
    if (restoreFocus && target !== null) {
      setTimeout(() => {
        if (target.isConnected) target.focus();
      }, 0);
    }
  }, []);

  useEffect(() => {
    const onKeyDown = (event: KeyboardEvent): void => {
      if (!isPaletteHotkey(event) || belongsToSomeoneElse(event)) return;

      // ⚠ Інакше браузер забере `Ctrl+K` собі (пошук в адресному рядку).
      event.preventDefault();
      open();
    };

    window.addEventListener('keydown', onKeyDown);
    return () => window.removeEventListener('keydown', onKeyDown);
  }, [open]);

  const label = t('search.open');

  return (
    <>
      <Button
        variant="default"
        size="xs"
        aria-label={label}
        aria-haspopup="dialog"
        aria-keyshortcuts="Control+K Meta+K"
        onClick={open}
        onMouseEnter={prefetchPalette}
        onFocus={prefetchPalette}
        leftSection={<SearchIcon />}
        rightSection={
          // ⚠ Не `Kbd`: це ще один компонент у статичному бандлі кожного маршруту.
          <Text span size="xs" ff="monospace" visibleFrom="sm" aria-hidden="true">
            {isApple ? '⌘K' : 'Ctrl K'}
          </Text>
        }
      >
        <Text span size="xs" visibleFrom="sm">
          {label}
        </Text>
      </Button>

      {requested && (
        <Suspense fallback={null}>
          <DataSearchPalette
            opened={opened}
            onClose={() => finish(true)}
            onPicked={() => finish(false)}
          />
        </Suspense>
      )}
    </>
  );
}

/** Лупа — той самий лінійний стиль, що в `app/navIcons.tsx`. */
function SearchIcon(): JSX.Element {
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
      <circle cx="11" cy="11" r="7" />
      <path d="m20 20-3.5-3.5" />
    </svg>
  );
}
