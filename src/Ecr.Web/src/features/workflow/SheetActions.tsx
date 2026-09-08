import { useEffect, useRef, useState, type JSX } from 'react';
import { Button, Group } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiEnqueue, apiFetch } from '@/api/client';
import type {
  ApproveSheetRequest,
  DocumentPeriodRequest,
  JobStatus,
  ReopenDocumentRequest,
  SheetWorkflowRequest,
} from '@/api/types';
import { can, useSession } from '@/shared/session/useSession';
import { ReasonModal } from '@/shared/ui/ReasonModal';
import { showApiError, showDone } from '@/shared/ui/notify';
import { outcomeOf, pollInterval } from './jobFollow';
import { isAllowed, type WorkflowAction } from './transitions';
import { t } from '@/shared/i18n';

/** Аркуш, над яким виконуються дії робочого процесу. */
export interface SheetActionsProps {
  /** Документ. */
  documentId: number;
  /** Аркуш; гранулярність робочого процесу — `аркуш × період` (D-38). */
  sheetDefId: number;
  /** Період. */
  periodKey: number;
  /** Поточний стан аркуша за цей період. */
  state: string;
}

/**
 * Робочий процес аркуша: подати, затвердити, відхилити, повернути, перерахувати.
 *
 * ⛔ Компонент з'явився після аудиту, і знахідка була найдорожчою за проєкт:
 * **із сорока дій запису дев'ятнадцять не мали в інтерфейсі жодної кнопки**, і
 * серед них `POST /documents/{id}/approve`. Тобто робочий процес обривався на
 * поданні: документ можна було подати і не можна затвердити, а без `Approved`
 * дані не стають дійсними (`ФВ-5.14`) і не потрапляють у звіти для регулятора
 * (`ФВ-10.11`).
 *
 * ⚠ Тести API при цьому були зелені й праві: вони доводять, що ендпоінт
 * працює, а не що до нього веде кнопка. Жоден зі сторожів цього не бачив за
 * побудовою — усі йшли від клієнта до сервера. Тепер є тринадцятий, який іде
 * назустріч.
 *
 * ⛔ Кнопка показується за ДВОМА умовами: стан аркуша (`transitions.ts`) і
 * право. Стан каже, чи є що робити; право — чи цій людині можна. Маршрут
 * погодження рахує сервер (`IAccessDecisionService.CanApproveAsync`), і
 * клієнт його не відтворює: друга реалізація правил доступу розійшлася б із
 * першою і показувала б дозвіл там, де сервер відмовляє.
 */
export function SheetActions({
  documentId,
  sheetDefId,
  periodKey,
  state,
}: SheetActionsProps): JSX.Element {
  const queryClient = useQueryClient();
  const session = useSession();

  // Яка дія чекає на причину; `null` — діалог закритий.
  const [asking, setAsking] = useState<'reject' | 'reopen' | null>(null);

  /** Перечитує стан документа після кожної зміни робочого процесу. */
  const refresh = async (): Promise<void> => {
    await queryClient.invalidateQueries({ queryKey: ['document', documentId, periodKey] });
  };

  const submit = useMutation({
    mutationFn: () =>
      apiFetch(`/api/v1/documents/${documentId}/submit`, {
        method: 'POST',
        body: JSON.stringify({ sheetDefId, periodKey } satisfies SheetWorkflowRequest),
      }),
    onSuccess: async () => {
      await refresh();
      showDone(t('document.submitted'));
    },
    // ⚠ Причина показується як є: Submit при осиротілих рядках
    // (`ECR-SUB-4221`) — це не «помилка сервера», а перелік того, що треба
    // виправити.
    onError: showApiError,
  });

  const decide = useMutation({
    mutationFn: (verdict: { approved: boolean; reason: string | null }) =>
      apiFetch(`/api/v1/documents/${documentId}/approve`, {
        method: 'POST',
        body: JSON.stringify({
          sheetDefId,
          periodKey,
          approved: verdict.approved,
          reason: verdict.reason,
        } satisfies ApproveSheetRequest),
      }),
    onSuccess: async (_result, verdict) => {
      await refresh();
      setAsking(null);
      showDone(verdict.approved ? t('workflow.approved') : t('workflow.rejected'));
    },
    onError: showApiError,
  });

  const reopen = useMutation({
    mutationFn: (reason: string) =>
      apiFetch(`/api/v1/documents/${documentId}/reopen`, {
        method: 'POST',
        body: JSON.stringify({ sheetDefId, periodKey, reason } satisfies ReopenDocumentRequest),
      }),
    onSuccess: async () => {
      await refresh();
      setAsking(null);
      showDone(t('workflow.reopened'));
    },
    // ⚠ Найчастіша відмова тут — `ECR-PRD-4223`: період закрито, і спершу
    // треба відкрити період, а це інше право (`D-67`). Текст веде саме туди.
    onError: showApiError,
  });

  /**
   * Перерахунок: постановка в чергу і **стеження за нею**.
   *
   * ⛔ Тут була одна нотифікація «поставлено в чергу як `4f2c…`» — і на цьому
   * все (директива №09 `W8` п.7). Перерахунок іде у фон, і зворотного зв'язку
   * не було жодного: оператор не дізнавався ні коли числа оновилися, ні що
   * задача впала. Він бачив GUID і не мав куди його ввести — той самий
   * дефект, який експорт уже пройшов (`ExportButton.tsx`), і виправлений тим
   * самим прийомом.
   *
   * ⚠ Сітка перечитується САМЕ на завершенні, а не на постановці в чергу:
   * оновити її одразу означало б показати старі числа під написом
   * «перераховано».
   */
  const [recalcJobId, setRecalcJobId] = useState<string | null>(null);

  const recalculate = useMutation({
    mutationFn: () =>
      apiEnqueue(`/api/v1/documents/${documentId}/recalculate`, {
        periodKey,
      } satisfies DocumentPeriodRequest),
    onSuccess: (job) => {
      setRecalcJobId(job.jobId);
      showDone(t('workflow.recalcQueued', { job: job.jobId }));
    },
    onError: showApiError,
  });

  const recalcJob = useQuery({
    queryKey: ['job', recalcJobId],
    queryFn: () => apiFetch<JobStatus>(`/api/v1/jobs/${encodeURIComponent(recalcJobId ?? '')}`),
    enabled: recalcJobId !== null,

    // ⚠ Правило опитування — у чистому модулі `jobFollow.ts`: саме його не
    // було, і саме його треба перевіряти окремо від компонента.
    refetchInterval: (query) => pollInterval(query.state.data?.state),

    // ⚠ `retry: false` і мовчазна зупинка на відмові: `GET /jobs/{id}` вимагає
    // окремого права (`System.ViewHealth`, `Q-156`), і оператор без нього має
    // лишитися з поставленою задачею, а не з червоним сповіщенням про право,
    // якого він не просив.
    retry: false,
  });

  /*
   * ⛔ Стан задачі, якого НЕ ВДАЛОСЯ прочитати, — це не «ще виконується».
   * `GET /jobs/{id}` вимагає окремого права (`System.ViewHealth`, `Q-156`), і
   * без нього кнопка крутилася б вічно: оператор бачив би «перераховується»
   * на задачі, стан якої йому просто не показують. Тому відмова читання
   * зупиняє стеження мовчки — задача поставлена, і про це вже сказано.
   */
  const outcome = recalcJobId === null
    ? null
    : outcomeOf(recalcJob.data?.state, recalcJob.isError);

  const recalcRunning = outcome === 'running';

  // ⛔ Про КІНЕЦЬ повідомляється рівно один раз на задачу: опитування триває
  // кілька тактів після завершення (React Query віддає ті самі дані з кешу), і
  // без цієї позначки оператор отримав би серію однакових сповіщень.
  const reported = useRef<string | null>(null);

  useEffect(() => {
    if (recalcJobId === null || outcome === null || outcome === 'running') return;

    // Стан прочитати не вдалося — мовчимо: задача поставлена, і про це вже
    // сказано; повідомляти про право, якого оператор не просив, нема сенсу.
    if (outcome === 'unknown') return;
    if (reported.current === recalcJobId) return;

    reported.current = recalcJobId;

    if (outcome === 'succeeded') {
      showDone(t('workflow.recalcDone'));

      // ⚠ Сітка перечитується САМЕ тут: оновити її на постановці в чергу
      // означало б показати старі числа під написом «перераховано».
      void queryClient.invalidateQueries({ queryKey: ['table-slice'] });
      void queryClient.invalidateQueries({ queryKey: ['document', documentId, periodKey] });

      return;
    }

    notifications.show({
      color: 'red',
      message: recalcJob.data?.message ?? t('workflow.recalcFailed'),
    });
  }, [recalcJobId, outcome, recalcJob.data?.message, queryClient, documentId, periodKey]);

  const me = session.data;

  return (
    <>
      <Group gap="xs">
        {can(me, 'Calculation.Recalculate') && (
          <Button
            size="xs"
            variant="default"
            loading={recalculate.isPending || recalcRunning}
            onClick={() => recalculate.mutate()}
          >
            {recalcRunning ? t('workflow.recalcRunning') : t('workflow.recalculate')}
          </Button>
        )}

        {isAllowed('submit', state) && (
          <Button size="xs" loading={submit.isPending} onClick={() => submit.mutate()}>
            {t('document.submit')}
          </Button>
        )}

        {/* ⛔ Затвердження і відхилення — пара, і показуються разом. Кнопка
            «Затвердити» без «Відхилити» перетворює погодження на формальність:
            єдиний спосіб не затвердити — не натиснути нічого, і аркуш висить
            у `Submitted` без жодного сліду причини. */}
        {isAllowed('approve', state) && (
          <Button
            size="xs"
            color="green"
            loading={decide.isPending}
            onClick={() => decide.mutate({ approved: true, reason: null })}
          >
            {t('workflow.approve')}
          </Button>
        )}

        {isAllowed('reject', state) && (
          <Button size="xs" color="red" variant="light" onClick={() => setAsking('reject')}>
            {t('workflow.reject')}
          </Button>
        )}

        {/* ⚠ Повернення в роботу — окреме небезпечне право (`ФВ-6.12`): воно
            дає змогу змінити вже подані числа. Тому і кнопка окрема, і
            причина обов'язкова. */}
        {isAllowed('reopen', state) && can(me, 'Document.Reopen') && (
          <Button size="xs" variant="light" onClick={() => setAsking('reopen')}>
            {t('workflow.reopen')}
          </Button>
        )}
      </Group>

      <ReasonModal
        opened={asking === 'reject'}
        title={t('workflow.rejectTitle')}
        label={t('workflow.reason')}
        description={t('workflow.rejectHint')}
        confirmLabel={t('workflow.reject')}
        isPending={decide.isPending}
        onConfirm={(reason) => decide.mutate({ approved: false, reason })}
        onClose={() => setAsking(null)}
      />

      <ReasonModal
        opened={asking === 'reopen'}
        title={t('workflow.reopenTitle')}
        label={t('workflow.reason')}
        description={t('workflow.reopenHint')}
        confirmLabel={t('workflow.reopen')}
        isPending={reopen.isPending}
        onConfirm={(reason) => reopen.mutate(reason)}
        onClose={() => setAsking(null)}
      />
    </>
  );
}

/** Чи має аркуш у цьому стані бути доступним для правки. */
export function isEditable(state: string): boolean {
  // Подане і затверджене не редагується: щоб змінити числа, аркуш повертають
  // у роботу окремою дією з причиною (`ФВ-5.20a`).
  return !isAllowed('approve', state) && !isAllowed('reopen', state);
}

export type { WorkflowAction };
