import { useState, type JSX } from 'react';
import { Button, Group, Modal, Stack } from '@mantine/core';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useParams } from 'react-router-dom';
import { queryKeys } from '@/api/queryKeys';
import type { TemplateCard } from '@/api/types';
import {
  archiveTemplate,
  dependentWorkCount,
  renameTemplate,
  restoreTemplate,
} from '@/features/templates/templateApi';
import { templateCardKey, useTemplateCard } from '@/features/templates/templateCardQuery';
import { TemplateVersionsSection } from '@/features/templates/TemplateVersionsSection';
import { localized } from '@/shared/i18n/localized';
import { can, useSession } from '@/shared/session/useSession';
import { AsyncBoundary } from '@/shared/ui/AsyncBoundary';
import { CodeText } from '@/shared/ui/CodeText';
import { ConfirmModal } from '@/shared/ui/ConfirmModal';
import { KeyValue } from '@/shared/ui/KeyValue';
import { LocalizedInput, hasAnyText, type LocalizedValue } from '@/shared/ui/LocalizedInput';
import { PageHeader, type HeaderAction } from '@/shared/ui/PageHeader';
import { useUrlState } from '@/shared/ui/useUrlState';
import { showApiError, showDone, showUndo } from '@/shared/ui/notify';
import { t } from '@/shared/i18n';

/**
 * Картка шаблону (`/admin/templates/:id`, крок `UI-09` директиви №15 §4).
 *
 * ⛔ До цієї сторінки маршрут `/admin/templates/:id` існував у реєстрі, але
 * ЛИСТА не мав: голий шлях рендерив порожній `Outlet`, а `templateApi.ts`
 * (`fetchTemplateCard`/`renameTemplate`/`archiveTemplate`/`restoreTemplate`)
 * мав у всьому дереві рівно одного споживача — власний тест. Тобто
 * архівування шаблону було реалізоване з обох боків і недосяжне з продукту.
 *
 * ⛔ **Кнопки архівування немає, доки не приїхало число залежних.** Це не
 * питання порядку рендера: дія, показана раніше за число, заради якого її
 * натискають, — рішення наосліп (`templateApi.ts`: «лічильник залежних
 * приходить ТІЄЮ САМОЮ відповіддю... екран не має показувати кнопку раніше,
 * ніж число»). Обидві дії шапки живуть усередині `card.data !== undefined`, і
 * саме тому `PageHeader` отримує їх умовно, а не малює завжди з `disabled`:
 * недоступна кнопка каже «тобі не можна», а тут правда інша — «ще невідомо».
 *
 * ⛔ **Код не редагується ніде.** Це бізнес-ключ, на який посилаються проєкти
 * (`templates.codeHint`), і `templateApi.ts` навмисно не має дії, що його
 * змінює. Тому він показаний `CodeText`, а не `TextInput` із `readOnly`:
 * заблоковане поле виглядає як тимчасова перешкода, моноширинний текст — як
 * властивість об'єкта.
 *
 * ⚠ **Undo тут не є мовчазним обманом** — рідкісний випадок, у якому §6 його
 * і дозволяє: сервер уміє «назад» однією дією (`POST …/restore`), тож кнопка
 * в тості кличе справжній запит, а не малює скасування, якого не буде. Форма
 * — КОМПЕНСАЦІЯ (`notify.ts`: дія вже виконана, «назад» її відмінює), не
 * відкладання: архівування не має сенсу тримати вісім секунд у повітрі, бо
 * воно нічого не руйнує — наявні документи працюють далі.
 *
 * ⚠ Стан діалогів — в АДРЕСІ (`?dialog=rename|archive`, правило `L2`): і
 * підтвердження, і форма перейменування переживають перезавантаження, а
 * посилання на відкритий діалог можна надіслати колезі. Локальний `useState`
 * тут дав би два різні джерела правди про те саме.
 */
export function TemplateCardPage(): JSX.Element {
  const { id } = useParams();
  const templateId = Number(id);

  const queryClient = useQueryClient();
  const session = useSession();
  const card = useTemplateCard(templateId);

  const [dialog, setDialog] = useUrlState('dialog');

  /*
   * ⚠ `null` означає «форму ще не чіпали», а не «назва порожня»: чернетка
   * засівається з поточної назви нижче. Без цього розрізнення діалог,
   * відкритий адресою напряму (`?dialog=rename` у вставленому посиланні),
   * показував би порожні поля — тобто пропонував би стерти назву тому, хто
   * просто пішов за посиланням.
   */
  const [draft, setDraft] = useState<LocalizedValue | null>(null);

  const editable = can(session.data, 'Template.Edit');

  /**
   * Оновлює картку відповіддю сервера й сусідній перелік шаблонів.
   *
   * ⚠ Картка пишеться ВІДПОВІДДЮ (`setQueryData`), а перелік лише
   * інвалідується: відповідь на дію — це вже свіжий стан саме цього шаблону,
   * а от перелік несе ще й чужі рядки, яких ця відповідь не описує.
   */
  async function applyCard(updated: TemplateCard): Promise<void> {
    queryClient.setQueryData(templateCardKey(templateId), updated);
    await queryClient.invalidateQueries({ queryKey: queryKeys.templates.list() });
  }

  const restore = useMutation({
    mutationFn: () => restoreTemplate(templateId),
    onSuccess: async (updated) => {
      await applyCard(updated);
      showDone(t('templates.restored'));
    },
    onError: showApiError,
  });

  const archive = useMutation({
    mutationFn: () => archiveTemplate(templateId),
    onSuccess: async (updated) => {
      await applyCard(updated);

      /*
       * ⛔ «Назад» пропонується ЛИШЕ звідси — з успішної гілки. Показати його
       * після відмови (`409 ECR-TMPL-0409`: шаблон уже архівований) означало
       * б запропонувати скасувати дію, якої не сталося: натиснувши, людина
       * надіслала б `POST …/restore` і повернула б в обіг шаблон, якого сама
       * не архівувала.
       */
      void showUndo(
        t('templates.archived'),
        () => {
          restore.mutate();
        },
        undefined,
        t('templates.undo'),
      );
    },
    onError: showApiError,

    // ⚠ Діалог закривається в обох випадках: причину відмови несе тост
    // (`showApiError` — текст сервера з кодом), а підтвердження, що лишилося
    // висіти над ним, ховало б саме цей текст.
    onSettled: () => {
      setDialog(null);
    },
  });

  const rename = useMutation({
    mutationFn: (nameL10n: LocalizedValue) => renameTemplate(templateId, nameL10n),
    onSuccess: async (updated) => {
      await applyCard(updated);
      setDraft(null);
      setDialog(null);
      showDone(t('templates.renamed'));
    },
    onError: showApiError,
  });

  const data = card.data;
  const name = data === undefined ? '' : localized(data.nameL10n);

  /*
   * ⚠ Заголовок сторінки: назва шаблону, а доки її немає — назва РІВНЯ
   * («Template»), не порожній рядок. Порожній `<h3>` — це `empty-heading` в
   * axe і німий орієнтир для читалки, яка саме на заголовок і переводить
   * фокус після переходу (`PageHeader`).
   *
   * ⚠ Назва, порожня в УСІХ мовах, теж можлива (сервер вимагає непорожньою
   * лише одну, і сторінка не знає, чи вона тієї мови) — тоді показується код:
   * він є завжди й однозначно називає шаблон.
   */
  const heading = name.length > 0 ? name : (data?.code ?? t('templates.card'));

  const currentName: LocalizedValue = data?.nameL10n.values ?? {};
  const renameValue = draft ?? currentName;

  /*
   * ⛔ ОДИН вимикач на обидві дії шапки, і в ньому `data !== undefined` —
   * тобто «картка приїхала», а отже приїхало й число залежних: воно живе в
   * ТІЙ САМІЙ відповіді (`templateApi.ts`). `undefined`, а не кнопка з
   * `disabled`: заблокована кнопка каже «тобі не можна», а правда тут інша —
   * «ще невідомо, скільки роботи від цього шаблону залежить».
   *
   * ⚠ Умова зведена в одне місце навмисно: доки вона стояла двічі, її можна
   * було послабити в одній із копій і не зачепити жодного твердження.
   */
  const showActions = data !== undefined && editable;

  const isActive = data?.isActive ?? true;

  const primary: HeaderAction | undefined = showActions
    ? {
        label: t('templates.rename'),
        onClick: () => {
          setDraft(data?.nameL10n.values ?? {});
          setDialog('rename');
        },
      }
    : undefined;

  const secondary: readonly HeaderAction[] | undefined = showActions
    ? [
        isActive
          ? {
              label: t('templates.archive'),
              onClick: () => {
                setDialog('archive');
              },
            }
          : {
              label: t('templates.restore'),
              onClick: () => {
                restore.mutate();
              },
            },
      ]
    : undefined;

  return (
    <>
      {/*
       * ⛔ U-19: окремого «← Templates» під шапкою більше немає. Хлібна
       * крихта «Templates / <код>» (`routes.adminTemplateSection`,
       * `AppLayout`) уже веде туди ж і стоїть рядком вище — два «назад» одне
       * під одним змушували вибирати між однаковими діями.
       */}
      <PageHeader
        title={heading}
        meta={data !== undefined && !data.isActive ? t('templates.archivedHint') : undefined}
        primary={primary}
        secondary={secondary}
      />

      {/*
       * ⛔ `L10`: відмова — це `ErrorAlert` із причиною сервера, кодом і
       * «повторити», а не порожня картка. Різниця тут не теоретична: порожня
       * картка з кнопками «перейменувати» і «архівувати» пропонувала б діяти
       * над шаблоном, якого, можливо, не існує (`404 ECR-TMPL-0404`).
       *
       * ⚠ `skeleton="form"` — під шапкою з'явиться короткий перелік пар
       * «підпис → значення», а не таблиця.
       */}
      <AsyncBoundary
        isPending={card.isPending}
        error={card.error}
        data={data}
        skeleton="form"
        onRetry={() => void card.refetch()}
      >
        {(loaded) => (
          <Stack gap="md">
            <KeyValue
              items={[
                {
                  label: t('templates.code'),
                  value: <CodeText>{loaded.code}</CodeText>,
                  hint: t('templates.codeHint'),
                },
                {
                  /*
                   * ⛔ ОДНЕ число — сума робіт, що спираються на шаблон
                   * (`dependentWorkCount`). Версії в нього не входять
                   * навмисно: вони належать самому шаблону, а питання перед
                   * архівуванням — «скільки ЧУЖОГО на нього спирається».
                   */
                  label: t('templates.dependentWork'),
                  value: String(dependentWorkCount(loaded)),
                  hint: t('templates.dependentBreakdown', {
                    projects: loaded.dependents.projects,
                    documents: loaded.dependents.documents,
                  }),
                },
              ]}
            />

            {/* ⛔ U-19: версії шаблону з переходом до структури кожної —
                раніше до редактора структури можна було дійти лише назад
                через перелік шаблонів. */}
            <TemplateVersionsSection templateId={templateId} editable={editable} />
          </Stack>
        )}
      </AsyncBoundary>

      {/*
       * ⛔ Підтвердження монтується лише разом із карткою: заголовок діалогу
       * зобов'язаний нести НАЗВУ об'єкта (`L6`), а до приїзду відповіді її
       * немає — діалог «Архівувати „“?» гірший за його відсутність.
       */}
      {data !== undefined && (
        <ConfirmModal
          opened={dialog === 'archive'}
          title={t('templates.archiveTitle', { name: heading })}
          text={t('templates.archiveText')}
          consequences={[
            t('templates.archiveDependents', { count: dependentWorkCount(data) }),
            { text: t('templates.archiveNote'), note: true },
          ]}
          verb={t('templates.archive')}
          isPending={archive.isPending}
          onConfirm={() => archive.mutate()}
          onClose={() => setDialog(null)}
        />
      )}

      <Modal
        opened={dialog === 'rename'}
        onClose={() => {
          setDraft(null);
          setDialog(null);
        }}
        title={t('templates.rename')}
      >
        {/* ⚠ Код у цій формі відсутній — і це не пропуск, а сама вимога:
            змінити бізнес-ключ не можна, і поле, яке сервер однаково
            відхилив би, гірше за його відсутність. */}
        <LocalizedInput
          label={t('templates.name')}
          description={t('templates.nameHint')}
          value={renameValue}
          onChange={setDraft}
        />

        <Group justify="flex-end" mt="md" gap="xs">
          <Button
            variant="default"
            onClick={() => {
              setDraft(null);
              setDialog(null);
            }}
          >
            {t('common.cancel')}
          </Button>
          <Button
            disabled={!hasAnyText(renameValue)}
            loading={rename.isPending}
            onClick={() => rename.mutate(renameValue)}
          >
            {t('common.save')}
          </Button>
        </Group>
      </Modal>
    </>
  );
}
