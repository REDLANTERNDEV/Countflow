import { useTranslation } from '@countflow/i18n';
import { Button, EmptyState } from '@countflow/ui';
import { Link } from '@tanstack/react-router';

/**
 * What an unknown URL renders, inside the shell so the navigation is still there.
 *
 * The gateway answers every path that is not a file with the console, so a
 * mistyped address reaches the router with a 200 and stops here. Without this the
 * router's built-in fallback printed a bare "Not Found" in English.
 */
export function NotFoundPage() {
  const { t } = useTranslation();

  return (
    <EmptyState
      title={t('notFound.title')}
      hint={t('notFound.hint')}
      action={
        <Button asChild size="sm" variant="secondary">
          <Link to="/">{t('notFound.home')}</Link>
        </Button>
      }
    />
  );
}
