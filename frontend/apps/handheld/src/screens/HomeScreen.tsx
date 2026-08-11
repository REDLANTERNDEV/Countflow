import { useTranslation } from '@countflow/i18n';
import { Button, Code, Spinner } from '@countflow/ui';
import { useState } from 'react';
import { api } from '../api.js';

interface HomeScreenProps {
  displayName: string;
  operatorCode: string;
  roles: string[];
  onReceive: () => void;
  onOpenTask: (taskId: string) => void;
  onSignOut: () => void;
}

/**
 * What an operator sees between jobs.
 *
 * Two verbs and nothing else. A handheld menu that lists everything the system
 * can do is a menu nobody reads at the eighth hour of a shift, so the screen
 * offers the two things that are actually happening on an inbound dock: put
 * something in, or go and move something.
 */
export function HomeScreen(props: HomeScreenProps) {
  const { displayName, operatorCode, roles, onReceive, onOpenTask, onSignOut } = props;
  const { t } = useTranslation();

  const [finding, setFinding] = useState(false);
  const [errorCode, setErrorCode] = useState<string | null>(null);

  async function takeNext() {
    setFinding(true);
    setErrorCode(null);

    const { data, error } = await api.POST('/api/work/tasks/claim-next', { body: {} });

    setFinding(false);

    if (error || !data) {
      // "No work available" is not a failure — it is the answer, and on a quiet
      // shift it is the usual one.
      setErrorCode((error as { code?: string } | undefined)?.code ?? 'unknown');
      return;
    }

    onOpenTask(data.task.id);
  }

  return (
    <>
      <h1 className="m-0 mt-[var(--space-2)] text-[length:var(--text-2xl)] tracking-[var(--tracking-display)]">
        {displayName}
      </h1>
      <p className="mb-[var(--space-5)] mt-0 text-[var(--color-text-secondary)]">
        <Code>{operatorCode}</Code> · {roles.join(', ')}
      </p>

      <Button variant="primary" className="w-full" disabled={finding} onClick={() => void takeNext()}>
        {finding ? <Spinner label={t('work.takingJob')} /> : t('work.nextJob')}
      </Button>

      <Button className="w-full" onClick={onReceive}>
        {t('inbound.receive.title')}
      </Button>

      {errorCode ? (
        <div
          className="rounded-[var(--radius-surface)] border border-[var(--color-border-subtle)]
                     bg-[var(--color-surface-raised)] p-[var(--space-5)] text-center"
        >
          <p className="m-0 text-[var(--color-text-secondary)]">
            {t([`errors.${errorCode}`, 'errors.unknown'])}
          </p>
          {errorCode === 'work.no_work_available' ? (
            <p className="mb-0 mt-[var(--space-2)] text-[length:var(--text-sm)] text-[var(--color-text-tertiary)]">
              {t('work.noWorkHint')}
            </p>
          ) : null}
        </div>
      ) : null}

      <Button className="mt-auto w-full" onClick={onSignOut}>
        {t('common.signOut')}
      </Button>
    </>
  );
}
