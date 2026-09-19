import { useEffect, useState, type JSX, type ReactNode } from 'react';
import { Button, Group, List, Modal, Stack, Text, TextInput } from '@mantine/core';
import { t } from '@/shared/i18n';

/**
 * Підтвердження НЕЗВОРОТНОЇ дії (`KIT.md` §6.9 `ConfirmDialog`, правило `L6`).
 *
 * ⛔ Правило `L6` дослівно: «назва об'єкта в заголовку, дієслово на кнопці,
 * **фокус на Cancel**; тест: `document.activeElement` = Cancel».
 *
 * ⚠ Фокус на БЕЗПЕЧНІЙ дії — не дрібниця смаку. Діалог, у якому `Enter`
 * одразу виконує незворотне, гірший за відсутність діалогу: він створює
 * відчуття захисту, якого немає. Людина, що звикла підтверджувати
 * «Enter'ом», видалить роль, закриє період і опублікує версію, жодного разу
 * не прочитавши, ЩО саме вона підтверджує.
 *
 * ⛔ Сьогодні підтвердження збирають руками на сторінках
 * (`PeriodsPage.tsx` — архівація, `MethodologyVersionsPage.tsx` — видалення
 * формули): `<Modal>` + `<Text>` + `<Group>` із двома кнопками, БЕЗ
 * `data-autofocus`. У такій формі пастка фокуса Mantine бере перший
 * фокусований вузол у DOM — а це хрестик у шапці діалогу, а не «Скасувати».
 * Тобто захист, який правило `L6` описує, там просто не реалізований, і
 * жоден тест цього не ловив. Саме цю форму компонент і замінює.
 */
export interface ConfirmConsequence {
  readonly text: string;

  /**
   * ⚠ «Примітка» — не наслідок, а уточнення (бляклим). Наслідки й примітки
   * розділені навмисно: список, де все однаково жирне, читають по діагоналі.
   */
  readonly note?: boolean | undefined;
}

export interface TypeToConfirm {
  /** Рядок, який треба ввести дослівно (зазвичай — назва об'єкта). */
  readonly value: string;

  /**
   * ⛔ Підпис поля ОБОВ'ЯЗКОВИЙ, і тому це об'єкт, а не окремий проп-рядок:
   * поле без `label` — порушення `ФВ-14.20`, яке зупиняє лінтер. Placeholder
   * не рахується: він зникає при першому ж символі.
   */
  readonly label: string;
}

export interface ConfirmModalProps {
  readonly opened: boolean;

  /** ⚠ Із НАЗВОЮ ОБ'ЄКТА: «Delete role “Night shift”?», не «Are you sure?». */
  readonly title: string;

  readonly text?: ReactNode | undefined;
  readonly consequences?: readonly (string | ConfirmConsequence)[] | undefined;

  /** ⚠ ДІЄСЛОВО на кнопці підтвердження: «Delete role», не «OK». */
  readonly verb: string;

  /** ⚠ За замовчуванням — чинний ключ каталогу `common.cancel`. */
  readonly cancelLabel?: string | undefined;

  /** ⚠ За замовчуванням `true`: цей діалог існує саме для незворотного. */
  readonly danger?: boolean | undefined;

  readonly typeToConfirm?: TypeToConfirm | undefined;

  /**
   * Зовнішня причина, з якої підтверджувати НЕМА СЕНСУ.
   *
   * ⚠ Проп існує тому, що так уже зроблено на місці: `PeriodsPage.tsx`
   * блокує архівацію, доки є відкриті періоди, — сервер однаково відмовить
   * `409 ECR-PRD-0409`, і підтвердження, заздалегідь приречене на відмову,
   * гірше за недоступну кнопку. Без цього пропа компонент не замінив би
   * чинний діалог, а став би другим поруч.
   */
  readonly confirmDisabled?: boolean | undefined;

  readonly isPending?: boolean | undefined;
  readonly onConfirm: () => void;
  readonly onClose: () => void;
}

function asConsequence(item: string | ConfirmConsequence): ConfirmConsequence {
  return typeof item === 'string' ? { text: item } : item;
}

export function ConfirmModal({
  opened,
  title,
  text,
  consequences,
  verb,
  cancelLabel,
  danger = true,
  typeToConfirm,
  confirmDisabled,
  isPending,
  onConfirm,
  onClose,
}: ConfirmModalProps): JSX.Element {
  const [typed, setTyped] = useState('');

  /*
   * ⚠ Поле очищається при КОЖНОМУ відкритті — той самий аргумент, що і в
   * `ReasonModal`: назва об'єкта, набрана для ПОПЕРЕДНЬОГО видалення, робить
   * кнопку наступного видалення активною ще до того, як людина прочитала
   * заголовок.
   */
  useEffect(() => {
    if (opened) setTyped('');
  }, [opened]);

  const cancel = cancelLabel ?? t('common.cancel');
  const items = consequences ?? [];
  const hasText = text !== undefined && text !== null && text !== '' && text !== false;

  // Порівняння ДОСЛІВНЕ (з точністю до країв рядка): у цьому й сенс поля —
  // змусити прочитати назву, а не натиснути «так».
  const typeBlocked = typeToConfirm !== undefined && typed.trim() !== typeToConfirm.value;

  return (
    <Modal
      opened={opened}
      onClose={onClose}
      title={title}
      /*
       * ⚠ Хрестик СВІДОМО лишається з власним ім'ям Mantine («Close»), а не
       * отримує підпис «Скасувати». Дві кнопки з однаковим іменем у тому
       * самому діалозі читалка оголошує однаково, і перелік елементів
       * перестає їх розрізняти. Тут, на відміну від `DetailDrawer`, хрестик
       * не єдиний спосіб вийти: поруч стоїть названа кнопка скасування, на
       * якій і стоїть фокус.
       */
    >
      <Stack gap="sm">
        {hasText ? <Text size="sm">{text}</Text> : null}

        {/*
         * ⛔ `D15-06`: порожній `consequences[]` НЕ дає порожнього списку.
         * `<List>` без елементів — це відступ і маркер порожнечі там, де
         * читач очікує змісту.
         */}
        {items.length > 0 ? (
          <List size="sm" spacing="xs" data-testid="confirm-consequences">
            {items.map((item) => {
              const consequence = asConsequence(item);

              return (
                <List.Item key={consequence.text}>
                  <Text size="sm" {...(consequence.note === true ? { c: 'dimmed' } : {})}>
                    {consequence.text}
                  </Text>
                </List.Item>
              );
            })}
          </List>
        ) : null}

        {typeToConfirm !== undefined ? (
          <TextInput
            label={typeToConfirm.label}
            value={typed}
            onChange={(event) => setTyped(event.currentTarget.value)}
            data-testid="confirm-type-to-confirm"
          />
        ) : null}

        <Group justify="flex-end" gap="xs">
          {/*
           * ⛔ `data-autofocus` стоїть тут і ТІЛЬКИ тут. Пастка фокуса Mantine
           * (`@mantine/hooks/use-focus-trap`) шукає `[data-autofocus]` першим;
           * не знайшовши — бере перший фокусований вузол, тобто хрестик у
           * шапці. Перенести атрибут на кнопку підтвердження = зробити
           * `Enter` виконавцем незворотної дії.
           */}
          <Button variant="default" data-autofocus onClick={onClose} data-testid="confirm-cancel">
            {cancel}
          </Button>

          <Button
            {...(danger ? { color: 'statusError' } : {})}
            loading={isPending === true}
            disabled={typeBlocked || confirmDisabled === true}
            onClick={onConfirm}
            data-testid="confirm-verb"
          >
            {verb}
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
}
