import { useEffect, useRef, useState } from 'react';
import { useMantineColorScheme, type MantineColorScheme } from '@mantine/core';
import { useQuery } from '@tanstack/react-query';
import { loadCatalog, onLanguageChosen, setLanguage, storedLanguage } from '@/shared/i18n';
import {
  applyDensity,
  onDensityChosen,
  setDensity,
  storedDensity,
} from '@/shared/theme/preferences';
import { getPreferences } from './api';
import { PreferenceSync, reportFailure, type PreferenceBinding } from './sync';

/** Ключ запиту налаштувань. */
export const PreferencesQueryKey = ['me', 'preferences'] as const;

/**
 * Ключ, під яким Mantine тримає тему в `localStorage`
 * (`localStorageColorSchemeManager` за замовчуванням).
 */
const ColorSchemeStorageKey = 'mantine-color-scheme-value';

const Schemes: readonly MantineColorScheme[] = ['light', 'dark', 'auto'];

function isScheme(value: unknown): value is MantineColorScheme {
  return Schemes.includes(value as MantineColorScheme);
}

function storedScheme(): MantineColorScheme | undefined {
  try {
    const raw = globalThis.localStorage?.getItem(ColorSchemeStorageKey);
    return isScheme(raw) ? raw : undefined;
  } catch {
    return undefined;
  }
}

/** Код мови з реєстру: `en`, `kz`, … Довільний рядок у `setLanguage` не йде. */
const LanguagePattern = /^[a-z]{2,3}$/;

/**
 * Синхронізує щільність, мову й тему з сервером (`BE-20`).
 *
 * ⛔ `enabled` — лише після входу. Анонім на сторінці входу працює локально й
 * не робить жодного запиту.
 *
 * ⚠ localStorage лишається кешем першого рендера (без блимання); значення
 * сервера приходить пізніше й перемагає. Відмова `GET`/`PUT` мовчазна.
 *
 * @returns Номер покоління: зростає, коли застосовано значення сервера, яке
 * компонент із власним станом (перемикач щільності) мусить перечитати.
 */
export function usePreferenceSync(enabled: boolean): number {
  const { colorScheme, setColorScheme } = useMantineColorScheme();
  const setSchemeRef = useRef(setColorScheme);
  const [generation, setGeneration] = useState(0);

  useEffect(() => {
    setSchemeRef.current = setColorScheme;
  }, [setColorScheme]);

  const [sync] = useState(() => {
    const bindings: PreferenceBinding[] = [
      {
        key: 'density',
        stored: () => storedDensity() ?? undefined,
        apply: (value) => {
          if (value !== 'compact' && value !== 'comfortable') return false;
          setDensity(value);
          applyDensity(value);
          return true;
        },
      },
      {
        key: 'language',
        stored: () => storedLanguage() ?? undefined,
        apply: (value) => {
          if (typeof value !== 'string' || !LanguagePattern.test(value)) return false;
          setLanguage(value);
          void loadCatalog(value, 'private');
          return true;
        },
      },
      {
        key: 'theme',
        stored: storedScheme,
        apply: (value) => {
          if (!isScheme(value)) return false;
          setSchemeRef.current(value);
          return true;
        },
      },
    ];

    return new PreferenceSync(bindings);
  });

  const query = useQuery({
    queryKey: PreferencesQueryKey,
    queryFn: getPreferences,
    enabled,
    retry: false,
    staleTime: Infinity,
    refetchOnWindowFocus: false,
  });

  // Вибір людини → сервер. Лише поки користувач увійшов.
  useEffect(() => {
    if (!enabled) return undefined;

    const offDensity = onDensityChosen((value) => {
      sync.changed('density', value);
    });
    const offLanguage = onLanguageChosen((value) => {
      sync.changed('language', value);
    });

    return () => {
      offDensity();
      offLanguage();
    };
  }, [enabled, sync]);

  // Тема живе в Mantine: зміну видно лише як новий `colorScheme`.
  // ⚠ Перше значення — це кеш першого рендера, а не вибір: його не пишемо.
  const lastScheme = useRef(colorScheme);
  useEffect(() => {
    if (lastScheme.current === colorScheme) return;
    lastScheme.current = colorScheme;
    if (enabled) sync.changed('theme', colorScheme);
  }, [colorScheme, enabled, sync]);

  // Відповідь сервера застосовується рівно раз за сеанс.
  const reconciled = useRef(false);
  useEffect(() => {
    if (!enabled || reconciled.current || query.data === undefined) return;
    reconciled.current = true;

    const applied = sync.reconcile(query.data);
    if (applied.includes('density')) setGeneration((value) => value + 1);
  }, [enabled, query.data, sync]);

  useEffect(() => {
    if (query.error !== null) reportFailure('GET', query.error);
  }, [query.error]);

  return generation;
}
