import type { JSX, ReactNode } from 'react';
import { Center, Code, Skeleton, Stack, Text, Title, VisuallyHidden } from '@mantine/core';
import { EcrApiError } from '@/api/client';
import { ErrorAlert } from './ErrorAlert';
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

  /*
   * ⚠ Усі необов'язкові пропи явно приймають `undefined`. Під
   * `exactOptionalPropertyTypes` «поле відсутнє» і «поле є, воно `undefined`»
   * — різні типи, а на межі компонента це розрізнення нічого не дає: виклик
   * виду `emptyHint={x === null ? undefined : t('…')}` природний і читабельний,
   * і забороняти його означало б змушувати розкладати виклик на два.
   */

  /** Чи вважати наявні дані порожніми. */
  readonly isEmpty?: ((data: T) => boolean) | undefined;

  /** Заголовок порожнього стану: ЩО саме порожнє. */
  readonly emptyTitle?: string | undefined;

  /** Пояснення: ЧОМУ порожньо і що з цим робити. */
  readonly emptyHint?: string | undefined;

  /** Дія порожнього стану; ховається, коли права немає. */
  readonly emptyAction?: ReactNode | undefined;

  /** Форма скелета замість спінера. */
  readonly skeleton?: SkeletonShape | undefined;

  /** Повторити запит. */
  readonly onRetry?: (() => void) | undefined;

  /** Вміст стану «дані». */
  readonly children: (data: T) => ReactNode;
}

/**
 * П'ять станів подання одним місцем (`ФВ-14.21`, `D-130`, PR nav-arch №6/8,
 * розділ B3 директиви).
 *
 * ⛔ Було ЧОТИРИ стани (`loading`/`empty`/`error`/дані); ця картка додає
 * П'ЯТИЙ — `no-permission` — розпізнаючи `403` серед помилок ОКРЕМО від
 * решти (`NoPermissionState` нижче), а не як черговий випадок `ErrorAlert`.
 * `partial` (директива, B3, п'ятий стан) НЕ додає власного коду тут: сторінки
 * з кількома незалежними запитами (`HealthPage`, `MyGroupsPage`,
 * `SecurityPage`) уже монтують по одному `<AsyncBoundary>` НА ЗАПИТ — кожен
 * незалежно показує власний стан (одна секція вантажиться, поки сусідня вже
 * показує дані чи помилку), і другого механізму це не потребує: `partial` —
 * це властивість КОМПОЗИЦІЇ кількох меж, а не нове поле однієї межі.
 *
 * ⛔ І найважливіше — що **порожньо, помилка і немає-права ніколи не
 * виглядають однаково** (`ФВ-14.22`, розширено директивою на третій стан).
 * Ця вимога має ціну, і ціна вже сплачена: `A7-04` — дашборд здоров'я читав
 * `entries` замість `checks`, `Object.entries(undefined ?? {})` не падав, і
 * **порожній дашборд виглядав так само, як здорова система**. Дефект жив,
 * доки не запустили систему цілком.
 *
 * ⚠ Обгортка потрібна саме тому, що правило легко порушити ненавмисно:
 * `data?.items ?? []` у п'ятнадцяти областях — це п'ятнадцять місць, де
 * невдалий запит перетворюється на «даних немає».
 *
 * ⚠ Скелет замість спінера там, де відома розмітка (`ФВ-14.25`): він знімає
 * стрибок розмітки і показує, що саме зараз з'явиться.
 *
 * ⚠ Стан помилки малює `<ErrorAlert>` — той самий, що й у формах. Власна
 * подача тут означала б, що на одному екрані код помилки показується, а на
 * іншому ні.
 *
 * ⚠ Стан `no-permission` НЕ показує кнопку «повторити»: право не з'явиться
 * від повторного запиту того самого користувача, і кнопка, що нічого не
 * змінює, — це той самий тупиковий екран, якого директива (`ФВ-14.24`)
 * забороняє в іншу сторону. Текст і код помилки лишаються (директива B3:
 * «зрозуміле повідомлення, куди звернутись») — заголовок і код права беруться
 * з того самого каталогу, що вже несе відмову маршруту (`err.ECR-AUTH-0403`,
 * `AccessDeniedPage.tsx`, `Q-279`), а код помилки й кореляція — з відповіді
 * сервера (той самий `ErrorAlert`-патерн підтримки).
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
    // ⛔ `403` — ОКРЕМИЙ стан (директива B3: `no-permission`), перевіряється
    // ПЕРШИМ серед помилок: без цього розгалуження відмова в праві падала б
    // у загальний `ErrorAlert` — той самий текст і кнопка «повторити», що й
    // для мережевого збою чи 500, хоча повторний запит нічого не змінить.
    if (error instanceof EcrApiError && error.problem.status === 403) {
      return <NoPermissionState error={error} />;
    }

    return <ErrorAlert error={error} onRetry={onRetry} />;
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
 * Стан «немає права» на РІВНІ РЕСУРСУ (директива B3, `no-permission`) —
 * відмінний від гарда МАРШРУТУ (`AccessDeniedPage.tsx`, `Q-279`): там ідеться
 * про сторінку цілком (навігація ще до рендера), тут — про ОДИН запит
 * усередині сторінки, до якої в іншому решта права є (наприклад,
 * `MyGroupsPage`: перегляд чужих груп під `Security.ManageUsers`, коли
 * власна сторінка доступна всім).
 *
 * ⚠ Розкладка — та сама, що в `EmptyState` (`Center`/`Stack`/`Title
 * order={4}`), СВІДОМО не `<Alert>` (`ErrorAlert`): порожньо й немає-права —
 * обидва «нічого не показано», і однакова розкладка тут — навмисний сигнал
 * РОДИННОСТІ, не помилка копіювання. Відрізняють їх: (а) `role="alert"` і
 * КОД помилки з кореляцією (`EmptyState` цього не має — там нема чого
 * показувати підтримці), (б) заголовок і текст — сервером/каталогом
 * ЗАБОРОНИ, а не «тут порожньо». Від `ErrorAlert` цей стан відрізняє
 * відсутність кольорової рамки `Alert` і кнопки «повторити» — обидва
 * навмисно відсутні (коментар компонента вище).
 */
function NoPermissionState({ error }: { error: EcrApiError }): JSX.Element {
  // ⚠ `problem.title` завжди рядок (обов'язкове поле `EcrProblem`), але коли
  // сервер відповів БЕЗ структурованого тіла (проксі, шлюз — `problemOf()`,
  // `client.ts`), клієнт підставляє буквально `HTTP 403` — цей рядок не
  // «зрозуміле повідомлення» (директива B3), тому саме тут (і лише тут)
  // підміняється каталожним, той самий текст, що вже показує гард маршруту
  // (`err.ECR-AUTH-0403`, `AccessDeniedPage.tsx`, `Q-279`). `error.message`
  // (`EcrApiError`: `detail ?? title`) у РАЗ фолбеку так само не несе нічого
  // понад заголовок — показувати «HTTP 403» ДРУГИЙ раз рядком нижче не має
  // сенсу, тому пояснення тут просто немає (як `ErrorAlert`, де `hint`
  // необов'язковий).
  const isRawFallback = error.problem.title === `HTTP ${String(error.problem.status)}`;
  const title = isRawFallback ? t('err.ECR-AUTH-0403') : error.problem.title;

  return (
    <Center py="xl">
      <Stack gap="xs" align="center" maw={420} role="alert">
        <Title order={4}>{title}</Title>

        {!isRawFallback && (
          <Text size="sm" c="dimmed" ta="center">
            {error.message}
          </Text>
        )}

        {/* ⚠ Код і кореляція — те саме «куди звернутись», що й `ErrorAlert`
            (директива B3): підтримка знаходить цей самий запит у серверному
            журналі за кореляцією, навіть коли причина — брак права, а не збій. */}
        <Text size="xs">
          <Code>{error.problem.errorCode}</Code> <Code>{error.problem.correlationId}</Code>
        </Text>
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
