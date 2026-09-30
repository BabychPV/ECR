import type { JSX } from 'react';
import { Card, Drawer, Group, Progress, Stack, Text } from '@mantine/core';
import { useQueryClient } from '@tanstack/react-query';
import type { JobSummary } from '@/api/types';
import { AsyncBoundary } from '@/shared/ui/AsyncBoundary';
import { StatusBadge } from '@/shared/ui/StatusBadge';
import { Timestamp } from '@/shared/ui/Timestamp';
import { t } from '@/shared/i18n';
import { jobKindLabel } from '@/features/workflow/jobLabel';
import { JobAttempt, JobDocumentLink, JobFailure, JobResultLink, JobRetry } from './JobFacts';
import { isActiveJob, isShownInMyTasks, myTaskMessage } from './myTasks';

/**
 * Шухляда «My tasks» — перелік ВЛАСНИХ фонових задач із шапки (`UI-07`).
 *
 * ⛔ Увесь цей модуль вантажиться лише динамічним `import()` з
 * `MyTasksLauncher.tsx` — той самий прийом і та сама причина, що в
 * `features/search/SearchLauncher.tsx`: оболонка входить у бюджет КОЖНОГО
 * маршруту (`D-132`), а `Drawer`, `Progress`, `StatusBadge`, `Timestamp`,
 * `AsyncBoundary` і `JobFacts` потрібні лише тому, хто шухляду відкрив.
 * Сторож — `__tests__/MyTasksLauncher.test.tsx` («модуль шухляди не обчислено
 * до першого відкриття»).
 *
 * ⛔ Це НЕ `shared/ui/DetailDrawer`, і причина не косметична: той тримає стан
 * у `?panel=` (`DetailPanelParam`), спільному на весь застосунок. Шухляда з
 * шапки живе ПОВЕРХ будь-якої сторінки, тож спільний параметр означав би, що
 * відкриття «My tasks» мовчки закриває шухляду рядка на сторінці під нею — і
 * навпаки. Стан тут локальний (`MyTasksLauncher`), в адресу не пишеться.
 *
 * ⚠ Тому ж вона лишається МОДАЛЬНОЮ (затемнення, пастка фокуса): немодальна
 * форма `DetailDrawer` існує, щоб сторінка позаду лишалася живою для роботи з
 * тим самим переліком. Тут сторінка позаду — будь-яка, зв'язку з нею немає, а
 * пастка фокуса — єдине, що робить `Esc` і `Tab` передбачуваними над чужим
 * екраном.
 */

/**
 * Адреса документа задачі.
 *
 * ⛔ Літерал, а не `generatePath(routes.documentDetail.path)`: `features` не
 * імпортують `app` (те саме правило вже зафіксоване в `JobFacts.tsx` і
 * реалізоване так само в `features/search/searchRoute.ts`). Розбіжність із
 * реєстром маршрутів ловить тест `__tests__/myTasksDocumentHref.test.ts` —
 * він звіряє цей рядок із `routes.documentDetail.path`.
 */
export function myTaskDocumentHref(documentId: number): string {
  return `/documents/${String(documentId)}`;
}

export interface MyTasksDrawerProps {
  readonly opened: boolean;
  readonly onClose: () => void;

  /**
   * Власні задачі В ТОМУ ПОРЯДКУ, у якому їх віддав сервер.
   *
   * ⚠ Сервер сортує за `UpdatedAt` спаданням (`JobProgressStore.cs:164`) —
   * тобто найновіша подія зверху. Пересортувати тут означало б показувати
   * інший порядок, ніж `#/admin/jobs`, маючи ту саму відповідь у кеші.
   */
  readonly jobs: readonly JobSummary[] | undefined;

  readonly isPending: boolean;
  readonly error: unknown;
  readonly onRetry: () => void;
}

export function MyTasksDrawer({
  opened,
  onClose,
  jobs,
  isPending,
  error,
  onRetry,
}: MyTasksDrawerProps): JSX.Element {
  return (
    <Drawer
      opened={opened}
      onClose={onClose}
      position="right"
      size="md"
      returnFocus
      closeButtonProps={{ 'aria-label': t('jobs.myTasksClose') }}
      title={<Text fw={600}>{t('jobs.myTasks')}</Text>}
      data-my-tasks=""
    >
      {/*
       * ⛔ `emptyHint` обов'язковий (`L10`): «порожньо» без пояснення читається
       * як збій. Тут порожнеча має конкретну й нетривіальну причину — задач
       * саме цього користувача ще не було, а не «немає прав» і не «фільтр
       * нічого не знайшов»; обидва інші стани малює сама межа окремо
       * (`ForbiddenState`, `ErrorAlert`).
       */}
      <AsyncBoundary<readonly JobSummary[]>
        isPending={isPending}
        error={error}
        data={jobs?.filter(isShownInMyTasks)}
        isEmpty={(list) => list.length === 0}
        emptyTitle={t('jobs.recentEmpty')}
        emptyHint={t('jobs.myTasksHint')}
        onRetry={onRetry}
      >
        {(list) => (
          <Stack gap="sm">
            {list.map((job) => (
              <MyTaskRow key={job.jobId} job={job} />
            ))}
          </Stack>
        )}
      </AsyncBoundary>
    </Drawer>
  );
}

/**
 * Один рядок шухляди.
 *
 * ⛔ Факти задачі малюють ті самі компоненти, що й картка та перелік на
 * `#/admin/jobs` (`JobFacts.tsx`): спроба, провал із кодом каталогу й
 * кореляцією, посилання на документ. Третя копія правил «коли показувати
 * спробу» і «що робити з `errorCode = null`» розійшлася б із двома наявними
 * при першій же правці.
 */
function MyTaskRow({ job }: { readonly job: JobSummary }): JSX.Element {
  const queryClient = useQueryClient();
  const active = isActiveJob(job.state);

  /*
   * ⚠ Момент рахується ПОЗА розміткою навмисно. `<Timestamp value={a ?? b} />`
   * ловить сторож `D15-09` (`eslint.config.js`): його селектор бачить праву
   * частину `??` у `JSXExpressionContainer` і не розрізняє, чи належить той
   * контейнер атрибуту. Тут це хибне спрацювання, але обходити сторожа
   * придушенням заради одного рядка гірше, ніж назвати величину іменем.
   *
   * ⚠ Постановка, а не старт: `createdAt` = `null` буває лише в задач за
   * розкладом, яких у ВЛАСНОМУ переліку не буває; запасне `startedAt` — на
   * випадок реалізації планувальника, що журналу не веде.
   */
  const queuedAt = job.createdAt ?? job.startedAt;
  const message = myTaskMessage(job);

  return (
    <Card withBorder padding="sm" data-my-task="" data-job-id={job.jobId}>
      <Stack gap="xs">
        <Group justify="space-between" wrap="nowrap">
          <Text size="sm" fw={500}>
            {jobKindLabel(job.jobCode)}
          </Text>
          <StatusBadge kind="job" state={job.state} />
        </Group>

        {/* ⚠ Смуга лише активним: у завершеної задачі «100 %» нічого не
            повідомляє, а у проваленої — прямо бреше про результат. */}
        {active && <Progress value={job.percent} animated={job.state === 'Running'} />}

        <JobAttempt attempt={job.attempt} maxAttempts={job.maxAttempts} />

        {/* ⛔ Текст уже перекладено сервером мовою читача — показується як є.
            `t()` над ним дав би `⟦…⟧` замість повідомлення. Виняток —
            ідентифікатор файлу експорту (F-27, `myTaskMessage`). */}
        {message !== null && <Text size="sm">{message}</Text>}

        <JobFailure
          state={job.state}
          errorCode={job.errorCode}
          correlationId={job.correlationId}
        />

        {/*
         * ⛔ UX-09, директива №11, T10 #40. `isOwnJob` — завжди `true`: цей
         * перелік приходить з `mine=true` (`Q-156`, `useMyTasks`), тобто
         * кожен рядок шухляди ВЖЕ є власним за побудовою — окремо ходити за
         * правом `System.ViewHealth` заради нього немає сенсу (`hasViewHealth`
         * нижче не впливає на результат, доки `isOwnJob` — `true`; докладніше
         * — `JobFacts.canRestartJob`).
         */}
        <Group gap="xs" wrap="nowrap">
          <JobRetry
            jobId={job.jobId}
            state={job.state}
            isOwnJob
            hasViewHealth={false}
            onRestarted={() => void queryClient.invalidateQueries({ queryKey: ['jobs'] })}
          />
          <JobResultLink resultUrl={job.resultUrl} />
        </Group>

        <Group gap="md" wrap="nowrap">
          <Text size="xs" c="dimmed">
            <Timestamp value={queuedAt} />
          </Text>
          <JobDocumentLink documentId={job.documentId} documentHrefOf={myTaskDocumentHref} />
        </Group>
      </Stack>
    </Card>
  );
}
