import { useId, useMemo, useState, type JSX, type KeyboardEvent } from 'react';
import {
  Box,
  Group,
  Loader,
  Modal,
  Text,
  TextInput,
  UnstyledButton,
  useComputedColorScheme,
  useMantineColorScheme,
} from '@mantine/core';
import { useDebouncedValue } from '@mantine/hooks';
import { useNavigate } from 'react-router-dom';
import { NavIcon } from '@/shared/ui/navIcons';
import { t } from '@/shared/i18n';
import { CodeText } from '@/shared/ui/CodeText';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { setNavbarCollapsed, useNavbarCollapsed } from '@/shared/theme/navbarCollapse';
import { applyDensity, setDensity, useDensity } from '@/shared/theme/preferences';
import { searchMinLength, type SearchHit } from './api';
import { matchCommands, type CommandItem, type PaletteScreenGroup } from './commandItems';
import { searchHitRoute, searchKinds, type SearchKind } from './searchRoute';
import { useRateLimitedSearch } from './useRateLimitedSearch';
import './CommandPalette.css';

/**
 * Командна палітра (UI-30): екрани, дії оболонки й пошук даних (BE-19).
 * Макет — `docs/design/hybrid/kit.js` `openPalette`, `index.html` `.palette`.
 *
 * ⛔ Модуль вантажиться ЛИШЕ через `import()` із `SearchLauncher.tsx`. Він
 * живе в оболонці, тобто статичний імпорт ліг би в КОЖЕН маршрут бюджету
 * `D-132`, а `PipelinePage` уже біля межі.
 *
 * ⚠ Зібрано на `Modal` + `TextInput` з `@mantine/core`, а не на
 * `@mantine/spotlight` і не на `Combobox`: випадний блок `Combobox` —
 * плаваючий шар, якому всередині діалогу нема де плавати, а ролі
 * `combobox`/`listbox`/`group`/`option` тут задані явно й перевіряються
 * тестом, а не довіряються бібліотеці.
 *
 * ⛔ Область видимості: екрани — лише ті, що в меню користувача (`groups` з
 * `AppLayout`); дані — лише те, що повернув `GET /api/v1/search`. Ні
 * лічильників, ні станів, ні власних запитів палітра не робить.
 */

/**
 * Затримка між останнім натисканням і запитом. Межу частоти сервера
 * (`ECR-REQ-0429`, 30 за 10 с) обробляє `useRateLimitedSearch`.
 */
export const SearchDebounceMs = 200;

export interface DataSearchPaletteProps {
  readonly opened: boolean;
  /** Закрито без переходу (Escape, тло, виконана дія) — фокус треба повернути. */
  readonly onClose: () => void;
  /** Обрано екран чи збіг — далі фокусом керує перехід маршруту. */
  readonly onPicked: () => void;
  /** Групи меню з пунктами, дозволеними цьому користувачеві (UI-12). */
  readonly groups?: readonly PaletteScreenGroup[];
}

const NoGroups: readonly PaletteScreenGroup[] = [];

/** Рядок переліку: екран, дія або збіг даних — з наскрізним номером. */
interface Row {
  readonly id: string;
  readonly title: string;
  readonly hint: string;
  readonly code?: string | undefined;
  readonly icon?: string | undefined;
  readonly route?: string | undefined;
  readonly run?: (() => void) | undefined;
  readonly index: number;
}

interface Section {
  readonly key: string;
  readonly label: string;
  readonly rows: Row[];
}

/** Заголовок групи даних — ті самі написи, що в навігації. Ключі літералами. */
function kindLabel(kind: SearchKind): string {
  switch (kind) {
    case 'document':
      return t('nav.documents');
    case 'template':
      return t('nav.templates');
    case 'registry':
      return t('nav.registries');
  }
}

const KindIcon: Record<SearchKind, string> = {
  document: 'documents',
  template: 'templates',
  registry: 'registries',
};

/** Дії оболонки: тема, щільність, меню. Підпис каже, ЩО станеться. */
function useShellActions(): CommandItem[] {
  const { setColorScheme } = useMantineColorScheme();
  const scheme = useComputedColorScheme('light', { getInitialValueInEffect: false });
  const density = useDensity();
  const collapsed = useNavbarCollapsed();

  return [
    scheme === 'dark'
      ? { id: 'action-theme', section: 'actions', title: t('palette.action.themeLight'), hint: '', run: () => setColorScheme('light') }
      : { id: 'action-theme', section: 'actions', title: t('palette.action.themeDark'), hint: '', run: () => setColorScheme('dark') },
    density === 'comfortable'
      ? {
          id: 'action-density',
          section: 'actions',
          title: t('palette.action.densityCompact'),
          hint: '',
          run: () => {
            setDensity('compact');
            applyDensity('compact');
          },
        }
      : {
          id: 'action-density',
          section: 'actions',
          title: t('palette.action.densityComfortable'),
          hint: '',
          run: () => {
            setDensity('comfortable');
            applyDensity('comfortable');
          },
        },
    collapsed
      ? { id: 'action-menu', section: 'actions', title: t('palette.action.expandMenu'), hint: '', run: () => setNavbarCollapsed(false) }
      : { id: 'action-menu', section: 'actions', title: t('palette.action.collapseMenu'), hint: '', run: () => setNavbarCollapsed(true) },
  ];
}

export function DataSearchPalette({ opened, onClose, onPicked, groups = NoGroups }: DataSearchPaletteProps): JSX.Element {
  const navigate = useNavigate();
  const baseId = useId();
  const listId = `${baseId}-list`;

  const [query, setQuery] = useState('');
  const [debounced] = useDebouncedValue(query, SearchDebounceMs);
  const { search, waitSeconds, failed, retry } = useRateLimitedSearch(opened, query, debounced);

  const screens = useMemo<CommandItem[]>(
    () =>
      groups.flatMap((group) => {
        const groupLabel = t(group.labelKey);
        return group.items.map((screen) => ({
          id: `screen-${screen.path}`,
          section: 'screens' as const,
          title: t(screen.handle.labelKey),
          hint: groupLabel,
          icon: screen.handle.icon,
          run: () => void navigate(screen.path),
        }));
      }),
    [groups, navigate],
  );
  const actions = useShellActions();

  const term = query.trim();
  const tooShort = term.length < searchMinLength;

  const sections = useMemo<Section[]>(() => {
    const result: Section[] = [];
    let index = 0;
    const add = (key: string, label: string, rows: Omit<Row, 'index'>[]): void => {
      if (rows.length === 0) return;
      result.push({ key, label, rows: rows.map((row) => ({ ...row, index: index++ })) });
    };

    const commands = matchCommands([...screens, ...actions], query);
    add(
      'screens',
      t('palette.screens'),
      commands.filter((item) => item.section === 'screens'),
    );

    // ⚠ Дані — лише поки запит довгий для пошуку: застарілі збіги попереднього
    // тексту після стирання не лишаються.
    const hits: readonly SearchHit[] = tooShort ? [] : (search.data ?? []);
    for (const kind of searchKinds) {
      const rows: Omit<Row, 'index'>[] = [];
      for (const hit of hits) {
        if (hit.kind !== kind) continue;
        const route = searchHitRoute(hit);
        if (route === null) continue;
        rows.push({
          id: `${hit.kind}-${String(hit.id)}`,
          title: hit.title,
          hint: '',
          code: hit.code !== hit.title ? hit.code : undefined,
          icon: KindIcon[kind],
          route,
        });
      }
      add(kind, kindLabel(kind), rows);
    }

    add(
      'actions',
      t('palette.actions'),
      commands.filter((item) => item.section === 'actions'),
    );
    return result;
  }, [screens, actions, query, tooShort, search.data]);

  const rows = useMemo(() => sections.flatMap((section) => section.rows), [sections]);

  // Курсор прив'язаний до рядка, а не до номера: дані, що догрузилися, не
  // зсувають обраний екран. Обраного рядка більше немає — курсор на першому.
  const [activeId, setActiveId] = useState<string | null>(null);
  const found = rows.findIndex((row) => row.id === activeId);
  const active = rows.length === 0 ? -1 : Math.max(found, 0);
  const moveTo = (index: number): void => setActiveId(rows[index]?.id ?? null);

  const optionId = (index: number): string => `${baseId}-option-${String(index)}`;

  const reset = (): void => {
    setQuery('');
    setActiveId(null);
  };

  const close = (): void => {
    reset();
    onClose();
  };

  const pick = (row: Row): void => {
    reset();
    if (row.route !== undefined) {
      onPicked();
      void navigate(row.route);
      return;
    }
    // Екран: перехід веде фокус сам. Дія: палітра закривається, фокус — назад.
    if (row.id.startsWith('screen-')) onPicked();
    else onClose();
    row.run?.();
  };

  const handleKeyDown = (event: KeyboardEvent<HTMLInputElement>): void => {
    if (rows.length === 0) return;

    if (event.key === 'ArrowDown') {
      event.preventDefault();
      moveTo((active + 1) % rows.length);
    } else if (event.key === 'ArrowUp') {
      event.preventDefault();
      moveTo((active - 1 + rows.length) % rows.length);
    } else if (event.key === 'Enter') {
      const row = rows[active];
      if (row === undefined) return;
      event.preventDefault();
      pick(row);
    }
  };

  const showList = rows.length > 0;

  // Рядок стану говорить лише про ПОШУК ДАНИХ; екрани й дії видно в переліку.
  let status: JSX.Element | null = null;
  if (term.length === 0) {
    status = null;
  } else if (tooShort) {
    status = <Text size="sm">{t('search.minLength', { min: searchMinLength })}</Text>;
  } else if (waitSeconds !== null) {
    // ⛔ Межа частоти — не «нічого не знайдено» і не аварія: непомітний рядок.
    status = (
      <Text size="sm" c="dimmed">
        {t('search.rateLimited', { seconds: waitSeconds })}
      </Text>
    );
  } else if (failed) {
    status = null;
  } else if (search.isError || search.data === undefined) {
    status = (
      <Group gap="xs">
        <Loader size="xs" />
        <Text size="sm">{t('common.loading')}</Text>
      </Group>
    );
  } else if (rows.length === 0) {
    status = <Text size="sm">{t('search.empty')}</Text>;
  }

  return (
    <Modal
      opened={opened}
      onClose={close}
      title={t('palette.title')}
      withCloseButton={false}
      size={600}
      yOffset="10vh"
      padding={0}
      classNames={{ content: 'ecr-palette', header: 'ecr-palette-title', body: 'ecr-palette-main' }}
      // ⚠ Фокус повертає `SearchLauncher`: лише він знає, чи закрито без вибору.
      returnFocus={false}
    >
      <TextInput
        data-autofocus
        classNames={{ root: 'ecr-palette-in' }}
        variant="unstyled"
        leftSection={<SearchGlyph />}
        rightSection={<kbd aria-hidden="true">Esc</kbd>}
        rightSectionWidth={48}
        aria-label={t('search.open')}
        placeholder={t('search.placeholder')}
        value={query}
        onChange={(event) => {
          setQuery(event.currentTarget.value);
          setActiveId(null);
        }}
        onKeyDown={handleKeyDown}
        role="combobox"
        aria-autocomplete="list"
        aria-expanded={showList}
        aria-controls={showList ? listId : undefined}
        aria-activedescendant={showList && active >= 0 ? optionId(active) : undefined}
        autoComplete="off"
      />

      <div className="ecr-palette-body">
        {/* ⛔ Відмова — не «нічого не знайдено»: це два різні твердження. */}
        {!tooShort && failed && <ErrorAlert error={search.error} onRetry={retry} />}

        <div role="status" aria-live="polite">
          {status}
        </div>

        {showList && (
          <Box id={listId} role="listbox" aria-label={t('search.open')}>
            {sections.map((section) => (
              <div key={section.key} role="group" aria-labelledby={`${baseId}-group-${section.key}`}>
                <Text
                  id={`${baseId}-group-${section.key}`}
                  className="ecr-palette-group"
                  size="xs"
                  fw={600}
                  c="dimmed"
                  tt="uppercase"
                >
                  {section.label}
                </Text>
                {section.rows.map((row) => (
                  <UnstyledButton
                    key={row.id}
                    component="div"
                    id={optionId(row.index)}
                    className="ecr-palette-option"
                    role="option"
                    aria-selected={row.index === active}
                    data-route={row.route}
                    tabIndex={-1}
                    onMouseMove={() => {
                      if (row.index !== active) moveTo(row.index);
                    }}
                    onClick={() => pick(row)}
                  >
                    <Group gap="sm" wrap="nowrap">
                      <span className="ecr-palette-icon" aria-hidden="true">
                        {row.icon !== undefined ? <NavIcon name={row.icon} /> : <BoltGlyph />}
                      </span>
                      <Text size="sm" truncate>
                        {row.title}
                      </Text>
                      {/* Група меню — одразу за назвою, як у макеті (`.pi small`). */}
                      {row.hint !== '' && (
                        <Text size="xs" c="dimmed" truncate>
                          {row.hint}
                        </Text>
                      )}
                      {row.code !== undefined && (
                        <Box ml="auto">
                          <CodeText>{row.code}</CodeText>
                        </Box>
                      )}
                    </Group>
                  </UnstyledButton>
                ))}
              </div>
            ))}
          </Box>
        )}
      </div>

      <div className="ecr-palette-foot" aria-hidden="true">
        <span>
          <kbd>↑</kbd> <kbd>↓</kbd> {t('palette.hintMove')}
        </span>
        <span>
          <kbd>Enter</kbd> {t('palette.hintOpen')}
        </span>
      </div>
    </Modal>
  );
}

function SearchGlyph(): JSX.Element {
  return (
    <svg width={16} height={16} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={1.75} strokeLinecap="round" strokeLinejoin="round" aria-hidden="true" focusable="false">
      <circle cx="11" cy="11" r="7" />
      <path d="m20 20-3.5-3.5" />
    </svg>
  );
}

/** Дія — блискавка, як `bolt` у макеті. */
function BoltGlyph(): JSX.Element {
  return (
    <svg width={16} height={16} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={1.75} strokeLinecap="round" strokeLinejoin="round" aria-hidden="true" focusable="false">
      <path d="M13 3 4 14h7l-1 7 9-11h-7z" />
    </svg>
  );
}
