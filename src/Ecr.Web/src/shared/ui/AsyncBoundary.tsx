import type { JSX, ReactNode } from 'react';
import {
  Alert,
  Button,
  Center,
  Group,
  Skeleton,
  Stack,
  Text,
  Title,
  VisuallyHidden,
} from '@mantine/core';
import { EcrApiError } from '@/api/client';
import { t } from '@/shared/i18n';

/** Форма скелета: що саме зараз з'явиться. */
export type SkeletonShape = 'table' | 'form' | 'none';

interface AsyncBoundaryProps<T> {
  /** Чи триває завантаження. */
  readonly isPending: boolean;

  /** Помилка запиту; `null` — запит удався. */
  readonly error: unknown;

  /** Дані; `undefined` доки не завантажено. */
  readonly data: T | undefined;

  /** Чи вважати наявні дані порожніми. */
  readonly isEmpty?: (data: T) => boolean;

  /** Заголовок порожнього стану: ЩО саме порожнє. */
  readonly emptyTitle?: string;

  /** Пояснення: ЧОМУ порожньо і що з цим робити. */
  readonly emptyHint?: string;

  /** Дія порожнього стану; ховається, коли права немає. */
  readonly emptyAction?: ReactNode;

  /** Форма скелета замість спінера. */
  readonly skeleton?: SkeletonShape;

  /** Повторити запит. */
  readonly onRetry?: () => void;

  /** Вміст стану «дані». */
  readonly children: (data: T) => ReactNode;
}

/**
 * Чотири стани подання одним місцем (`ФВ-14.21`, `D-130`).
 *
 * ⛔ Стани саме ЧОТИРИ, і найважливіше — що **порожньо і помилка ніколи не
 * виглядають однаково** (`ФВ-14.22`). Ця вимога має ціну, і ціна вже сплачена:
 * `A7-04` — дашборд здоров'я читав `entries` замість `checks`,
 * `Object.entries(undefined ?? {})` не падав, і **порожній дашборд виглядав
 * так само, як здорова система**. Дефект жив, доки не запустили систему цілком.
 *
 * ⚠ Обгортка потрібна саме тому, що правило легко порушити ненавмисно:
 * `data?.items ?? []` у п'ятнадцяти областях — це п'ятнадцять місць, де
 * невдалий запит перетворюється на «даних немає».
 *
 * ⚠ Скелет замість спінера там, де відома розмітка (`ФВ-14.25`): він знімає
 * стрибок розмітки і показує, що саме зараз з'явиться.
 */
export function AsyncBoundary<T>({
  isPending,
  error,
  data,
  isEmpty,
  emptyTitle,
  emptyHint,
  emptyAction,
  skeleton = 'none',
  onRetry,
  children,
}: AsyncBoundaryProps<T>): JSX.Element {
  // ⛔ Порядок перевірок значущий. Помилка йде ПЕРШОЮ: невдалий запит теж
  // лишає `data` порожнім, і перевірка порожнечі раніше показала б «даних
  // немає» там, де насправді сервер відмовив.
  if (error !== null && error !== undefined) {
    return <ErrorState error={error} onRetry={onRetry} />;
  }

  if (isPending) {
    return <LoadingState shape={skeleton} />;
  }

  if (data === undefined) {
    // Не помилка і не дані: запит іще не робили (наприклад, не обрано проєкт).
    return <EmptyState title={emptyTitle} hint={emptyHint} action={emptyAction} />;
  }

  if (isEmpty?.(data) === true) {
    return <EmptyState title={emptyTitle} hint={emptyHint} action={emptyAction} />;
  }

  return <>{children(data)}</>;
}

/**
 * Стан помилки: що сталося, стабільний код, вихід (`ФВ-14.24`).
 *
 * ⛔ Тупикових екранів не буває. Без дії «повторити» користувач має єдиний
 * доступний хід — перезавантажити сторінку, і саме так він і зробить.
 */
function ErrorState({
  error,
  onRetry,
}: {
  error: unknown;
  onRetry: (() => void) | undefined;
}): JSX.Element {
  const apiError = error instanceof EcrApiError ? error : null;

  return (
    <Alert color="red" title={t('state.errorTitle')} role="alert">
      <Stack gap="xs">
        {/* ⚠ Текст СЕРВЕРА, а не власний узагальнений: «не вдалося
            завантажити» не каже нічого, а «період закрито» каже все. */}
        <Text size="sm">{apiError?.message ?? t('state.errorUnknown')}</Text>

        {apiError !== null && (
          // ⚠ Код і кореляція показуються ЗАВЖДИ: з ними звернення в підтримку
          // займає хвилину, без них — листування. Ідентифікатор генерує клієнт
          // (`CORRELATION_HEADER`), і це єдине, що зшиває скаргу з логом.
          <Text size="xs" c="dimmed" ff="monospace">
            {`${apiError.problem.errorCode} / ${apiError.problem.correlationId}`}
          </Text>
        )}

        {onRetry !== undefined && (
          <Group gap="xs">
            <Button size="xs" variant="default" onClick={onRetry}>
              {t('common.retry')}
            </Button>
          </Group>
        )}
      </Stack>
    </Alert>
  );
}

/**
 * Порожній стан: пояснює і пропонує дію (`ФВ-14.23`).
 *
 * ⚠ Не «Немає даних», а «у цьому проєкті ще немає документів» плюс кнопка,
 * якщо право є. Порожній екран без пояснення виглядає як несправність — і
 * половина звернень у підтримку саме про це.
 */
function EmptyState({
  title,
  hint,
  action,
}: {
  title: string | undefined;
  hint: string | undefined;
  action: ReactNode;
}): JSX.Element {
  return (
    <Center py="xl">
      <Stack gap="xs" align="center" maw={420}>
        <Title order={4}>{title ?? t('state.emptyTitle')}</Title>

        {hint !== undefined && (
          <Text size="sm" c="dimmed" ta="center">
            {hint}
          </Text>
        )}

        {action}
      </Stack>
    </Center>
  );
}

/**
 * Стан завантаження: скелет там, де відома розмітка (`ФВ-14.25`).
 *
 * ⚠ Спінер лишається для дій НЕвідомої тривалості. Скелет таблиці показує, що
 * з'явиться таблиця, і не дає розмітці стрибнути під курсором.
 */
function LoadingState({ shape }: { shape: SkeletonShape }): JSX.Element {
  if (shape === 'none') {
    return (
      <Center py="xl" role="status" aria-busy="true">
        <Text size="sm" c="dimmed">
          {t('common.loading')}
        </Text>
      </Center>
    );
  }

  const rows = shape === 'table' ? 8 : 4;

  return (
    <Stack gap="xs" role="status" aria-busy="true">
      {/*
       * ⚠ Скелет сам собою читалці НЕ ЧУТНИЙ: це порожні прямокутники без
       * тексту. Прихований напис — єдине, що відрізняє «вантажиться» від
       * «нічого немає» для того, хто не бачить екрана.
       */}
      <VisuallyHidden>{t('common.loading')}</VisuallyHidden>
      <Skeleton height={28} radius="sm" />
      {Array.from({ length: rows }, (_, index) => (
        <Skeleton key={index} height={shape === 'table' ? 24 : 36} radius="sm" />
      ))}
    </Stack>
  );
}
