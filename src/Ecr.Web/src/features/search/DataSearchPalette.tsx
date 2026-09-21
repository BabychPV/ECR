import { useId, useMemo, useState, type JSX, type KeyboardEvent } from 'react';
import { Box, Group, Loader, Modal, Stack, Text, TextInput, UnstyledButton } from '@mantine/core';
import { useDebouncedValue } from '@mantine/hooks';
import { useNavigate } from 'react-router-dom';
import { t } from '@/shared/i18n';
import { CodeText } from '@/shared/ui/CodeText';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { searchMinLength, useDataSearch, type SearchHit } from './api';
import { searchHitRoute, searchKinds, type SearchKind } from './searchRoute';

/**
 * Командна палітра: пошук даних (BE-19).
 *
 * ⛔ Модуль вантажиться ЛИШЕ через `import()` із `SearchLauncher.tsx`. Він
 * живе в оболонці, тобто статичний імпорт ліг би в КОЖЕН маршрут бюджету
 * `D-132`, а `PeriodsPage` уже біля межі.
 *
 * ⚠ Зібрано на `Modal` + `TextInput` з `@mantine/core`, а не на
 * `@mantine/spotlight` і не на `Combobox`: випадний блок `Combobox` —
 * плаваючий шар, якому всередині діалогу нема де плавати, а ролі
 * `combobox`/`listbox`/`group`/`option` тут задані явно й перевіряються
 * тестом, а не довіряються бібліотеці.
 */

/** Затримка між останнім натисканням і запитом: обмежувача частоти на сервері немає. */
export const SearchDebounceMs = 200;

export interface DataSearchPaletteProps {
  readonly opened: boolean;
  /** Закрито без вибору (Escape, тло, хрестик) — фокус треба повернути. */
  readonly onClose: () => void;
  /** Обрано збіг — далі фокусом керує перехід маршруту. */
  readonly onPicked: () => void;
}

interface Row {
  readonly hit: SearchHit;
  readonly route: string;
  readonly index: number;
}

interface Section {
  readonly kind: SearchKind;
  readonly rows: Row[];
}

/** Групи в сталому порядку; невідомий `kind` (немає маршруту) відкидається. */
function groupHits(hits: readonly SearchHit[]): Section[] {
  const sections: Section[] = [];
  let index = 0;

  for (const kind of searchKinds) {
    const rows: Row[] = [];
    for (const hit of hits) {
      if (hit.kind !== kind) continue;
      const route = searchHitRoute(hit);
      if (route === null) continue;
      rows.push({ hit, route, index });
      index += 1;
    }
    if (rows.length > 0) sections.push({ kind, rows });
  }

  return sections;
}

/** Заголовок групи — ті самі написи, що в навігації. Ключі літералами. */
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

export function DataSearchPalette({ opened, onClose, onPicked }: DataSearchPaletteProps): JSX.Element {
  const navigate = useNavigate();
  const baseId = useId();
  const listId = `${baseId}-list`;

  const [query, setQuery] = useState('');
  const [debounced] = useDebouncedValue(query, SearchDebounceMs);
  const search = useDataSearch(debounced);

  const sections = useMemo(() => groupHits(search.data ?? []), [search.data]);
  const rows = useMemo(() => sections.flatMap((section) => section.rows), [sections]);

  // Курсор прив'язаний до набору збігів: новий набір — курсор на першому.
  const [cursor, setCursor] = useState<{ data: unknown; index: number }>({
    data: undefined,
    index: 0,
  });
  const active =
    rows.length === 0 ? -1 : cursor.data === search.data ? Math.min(cursor.index, rows.length - 1) : 0;
  const moveTo = (index: number): void => setCursor({ data: search.data, index });

  const optionId = (index: number): string => `${baseId}-option-${String(index)}`;

  const reset = (): void => {
    setQuery('');
    setCursor({ data: undefined, index: 0 });
  };

  const close = (): void => {
    reset();
    onClose();
  };

  const pick = (row: Row): void => {
    reset();
    onPicked();
    void navigate(row.route);
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

  const term = query.trim();
  const tooShort = term.length < searchMinLength;
  const showList = !tooShort && !search.isError && rows.length > 0;

  let status: JSX.Element | null = null;
  if (tooShort) {
    status = <Text size="sm">{t('search.minLength', { min: searchMinLength })}</Text>;
  } else if (search.isError) {
    status = null;
  } else if (search.data === undefined) {
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
      title={t('search.open')}
      size="lg"
      yOffset="10vh"
      // ⚠ Фокус повертає `SearchLauncher`: лише він знає, чи закрито без вибору.
      returnFocus={false}
    >
      <Stack gap="sm">
        <TextInput
          data-autofocus
          aria-label={t('search.open')}
          placeholder={t('search.placeholder')}
          value={query}
          onChange={(event) => setQuery(event.currentTarget.value)}
          onKeyDown={handleKeyDown}
          role="combobox"
          aria-autocomplete="list"
          aria-expanded={showList}
          aria-controls={showList ? listId : undefined}
          aria-activedescendant={showList && active >= 0 ? optionId(active) : undefined}
          autoComplete="off"
        />

        {/* ⛔ Відмова — не «нічого не знайдено»: це два різні твердження. */}
        {!tooShort && search.isError && (
          <ErrorAlert error={search.error} onRetry={() => void search.refetch()} />
        )}

        <div role="status" aria-live="polite">
          {status}
        </div>

        {showList && (
          <Box id={listId} role="listbox" aria-label={t('search.open')}>
            {sections.map((section) => (
              <div
                key={section.kind}
                role="group"
                aria-labelledby={`${baseId}-group-${section.kind}`}
              >
                <Text id={`${baseId}-group-${section.kind}`} size="xs" fw={700} mt="xs">
                  {kindLabel(section.kind)}
                </Text>
                {section.rows.map((row) => (
                  <UnstyledButton
                    key={`${row.hit.kind}-${String(row.hit.id)}`}
                    component="div"
                    id={optionId(row.index)}
                    role="option"
                    aria-selected={row.index === active}
                    data-route={row.route}
                    tabIndex={-1}
                    onMouseMove={() => {
                      if (row.index !== active) moveTo(row.index);
                    }}
                    onClick={() => pick(row)}
                    w="100%"
                    px="sm"
                    py="xs"
                    bg={row.index === active ? 'var(--mantine-color-default-hover)' : 'transparent'}
                  >
                    <Group gap="sm" wrap="nowrap" justify="space-between">
                      <Text size="sm" truncate>
                        {row.hit.title}
                      </Text>
                      {row.hit.code !== row.hit.title && <CodeText>{row.hit.code}</CodeText>}
                    </Group>
                  </UnstyledButton>
                ))}
              </div>
            ))}
          </Box>
        )}
      </Stack>
    </Modal>
  );
}
