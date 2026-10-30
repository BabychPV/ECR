import { useId, useState, type JSX, type ReactNode } from 'react';
import { Group, Stack, Title, UnstyledButton } from '@mantine/core';
import { useQueryClient } from '@tanstack/react-query';
import { MeQueryKey, type MeDto } from '@/shared/session/useSession';
import { readSectionChoices, writeSectionChoice } from './methodologySections';

/** Шеврон контуром — той самий, що в `CalculationResultsPanel` (без бібліотеки іконок у чанку). */
function Chevron({ expanded }: { readonly expanded: boolean }): JSX.Element {
  return (
    <svg
      width={14}
      height={14}
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth={1.75}
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden="true"
      focusable="false"
    >
      <path d={expanded ? 'M6 9l6 6 6-6' : 'M9 6l6 6-6 6'} />
    </svg>
  );
}

interface MethodologySectionProps {
  /** Стабільний ідентифікатор секції — ключ запам'ятованого вибору й `data-section`. */
  readonly sectionId: string;
  /** Заголовок секції (вже перекладений). */
  readonly title: string;
  /** Дії праворуч від заголовка (кнопка «Add …»); видимі й у згорнутому стані. */
  readonly actions?: ReactNode;
  /** Стан без запам'ятованого вибору. */
  readonly defaultExpanded?: boolean;
  readonly children: ReactNode;
}

/**
 * Згортувана секція сторінки версії методології (`KIT.md` §4 «Прогресивне
 * розкриття», `kit.js` `Collapsible`).
 *
 * ⚠ Кнопка ВСЕРЕДИНІ заголовка (шаблон «акордеон», як `DocumentHeaderPanel`):
 * читалка чує рівень заголовка і стан `aria-expanded`, а `aria-controls`
 * указує на тіло. Тіло ховається атрибутом `hidden`, а не розмонтовується:
 * запити панелі й чернетки діалогів переживають згортання, а повернення не
 * перезапитує дані.
 *
 * ⛔ За замовчуванням РОЗГОРНУТО: панелі змісту версії навмисно стоять на
 * одному екрані (коментар у `MethodologyVersionsPage.tsx` перед
 * `MethodologyConstantsPanel`) — згорнуті з першого відкриття, вони виглядали б
 * необов'язковими. Згортає людина сама, і її вибір пам'ятається.
 */
export function MethodologySection({
  sectionId,
  title,
  actions,
  defaultExpanded = true,
  children,
}: MethodologySectionProps): JSX.Element {
  const queryClient = useQueryClient();
  // ⚠ Користувач — з кешу сесії, без власного запиту: секцій на сторінці дев'ять,
  // і жодна не має права ходити мережею лише заради ключа сховища.
  const userId = queryClient.getQueryData<MeDto>(MeQueryKey)?.userId;
  const [expanded, setExpanded] = useState<boolean>(
    () => readSectionChoices(userId)[sectionId] ?? defaultExpanded,
  );
  const baseId = useId();
  const headingId = `${baseId}-h`;
  const bodyId = `${baseId}-b`;

  return (
    <section aria-labelledby={headingId} data-section={sectionId} data-expanded={expanded}>
      <Stack gap="sm">
        <Group justify="space-between" wrap="nowrap" gap="xs">
          <Title order={2} size="h5" id={headingId} style={{ minWidth: 0 }}>
            <UnstyledButton
              aria-expanded={expanded}
              aria-controls={bodyId}
              data-testid={`methodology-section-toggle-${sectionId}`}
              onClick={() => {
                const next = !expanded;
                writeSectionChoice(userId, sectionId, next);
                setExpanded(next);
              }}
              style={{
                display: 'inline-flex',
                alignItems: 'center',
                gap: 'var(--mantine-spacing-xs)',
                maxWidth: '100%',
                font: 'inherit',
              }}
            >
              <Chevron expanded={expanded} />
              <span>{title}</span>
            </UnstyledButton>
          </Title>
          {actions}
        </Group>

        <div id={bodyId} hidden={!expanded}>
          <Stack gap="sm">{children}</Stack>
        </div>
      </Stack>
    </section>
  );
}
