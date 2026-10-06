import { useState, type JSX } from 'react';
import { Button, Group, Modal, TextInput } from '@mantine/core';
import { rawJobId } from '@/features/workflow/jobLabel';
import { t } from '@/shared/i18n';

/**
 * «Find a job by id» — друга дія екрана задач (UI-28).
 *
 * ⚠ До UI-28 поле ідентифікатора стояло над журналом, і екран починався з
 * поля, а не з переліку. Макет (`screens-ops.js` `/admin/jobs`) поля не має
 * зовсім — задачу відкривають рядком. Але ідентифікатор і далі приходить
 * вставленим із чужого повідомлення («подивись, чому впало»), тож пошук за
 * ним лишився — діалогом із шапки, а не полем над таблицею.
 *
 * ⚠ ЛІНИВИЙ чанк: діалог потрібен рідко.
 *
 * ⛔ Enter у полі робить те саме, що кнопка: ідентифікатор найчастіше
 * вставляють, і природний наступний рух — Enter, не миша.
 */
export default function FindJobModal({
  onPick,
  onClose,
}: {
  readonly onPick: (jobId: string) => void;
  readonly onClose: () => void;
}): JSX.Element {
  const [input, setInput] = useState('');

  const pick = (): void => {
    if (input.trim().length === 0) return;
    onPick(rawJobId(input));
  };

  return (
    <Modal opened onClose={onClose} title={t('jobs.findById')}>
      <TextInput
        label={t('jobs.id')}
        placeholder={t('jobs.pick')}
        description={t('jobs.pickHint')}
        value={input}
        onChange={(event) => setInput(event.currentTarget.value)}
        onKeyDown={(event) => {
          if (event.key === 'Enter') pick();
        }}
        data-autofocus
      />

      <Group justify="flex-end" mt="md">
        <Button variant="default" onClick={onClose}>
          {t('common.cancel')}
        </Button>
        <Button disabled={input.trim().length === 0} onClick={pick}>
          {t('jobs.watch')}
        </Button>
      </Group>
    </Modal>
  );
}
