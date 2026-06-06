import { useEffect } from 'react';
import { usePaymentsList } from './usePaymentsList';
import { useQueryParamFilter } from '../filters/useQueryParamFilter';
import { StatusRail } from './StatusRail';
import { StatusFilterChips } from './StatusFilterChips';
import { PaymentsTable } from './PaymentsTable';
import { Skeleton } from '../../components/ui/Skeleton';
import { ErrorNotice } from '../../components/ui/ErrorNotice';
import { ApiError } from '../../lib/api/client';
import styles from './PaymentsListPage.module.css';

export function PaymentsListPage() {
  const { filters, setStatus, setCursor } = useQueryParamFilter();
  const limit = 20;
  const { data, isLoading, isError, error, refetch } = usePaymentsList({
    status: filters.status,
    cursor: filters.cursor,
    limit,
  });

  useEffect(() => {
    document.title = filters.status
      ? `Payments — ${filters.status} — Argyle`
      : 'Payments — Argyle';
  }, [filters.status]);

  const payments = data?.data ?? [];
  const hasNextCursor = !!data?.nextCursor;

  return (
    <div className={styles.page}>
      <div className={styles.inner}>
        <header className={styles.header}>
          <p className={styles.eyebrow}>Overview</p>
          <h1 className={styles.title}>
            Payments <strong>in the last 24 hours.</strong>
          </h1>
          <div className={styles.headerActions}>
            <button type="button" className={styles.ghostButton}>
              Export CSV
            </button>
            <button type="button" className={styles.primaryButton}>
              + New payment
            </button>
          </div>
        </header>

        <StatusRail
          payments={payments}
          activeStatus={filters.status}
          onSelect={setStatus}
        />

        <div className={styles.filterBar}>
          <StatusFilterChips activeStatus={filters.status} onSelect={setStatus} />
          <div className={styles.filterMeta}>
            <span>Last 24 hours</span>
            <span aria-hidden="true">·</span>
            <span>Newest first</span>
          </div>
        </div>

        <section className={styles.tablePanel} aria-label="Payments">
          {isLoading && <ListSkeleton />}
          {isError && (
            <div className={styles.notice}>
              <ErrorNotice
                code={error instanceof ApiError ? error.code : 'unknown_error'}
                message={error instanceof Error ? error.message : 'Could not load payments.'}
                requestId={error instanceof ApiError ? error.requestId : null}
                onRetry={() => refetch()}
              />
            </div>
          )}
          {!isLoading && !isError && payments.length === 0 && (
            <EmptyState />
          )}
          {!isLoading && !isError && payments.length > 0 && (
            <PaymentsTable payments={payments} />
          )}

          <footer className={styles.footer}>
            <span className={styles.muted}>
              {payments.length > 0 ? `Showing ${payments.length}` : 'No results'}
            </span>
            <div className={styles.pagination}>
              <button type="button" className={styles.ghostButton} disabled>
                ← Previous
              </button>
              <button
                type="button"
                className={styles.ghostButton}
                disabled={!hasNextCursor}
                onClick={() => data?.nextCursor && setCursor(data.nextCursor)}
              >
                Next →
              </button>
            </div>
          </footer>
        </section>
      </div>
    </div>
  );
}

function ListSkeleton() {
  return (
    <div className={styles.skeletonBody} role="status" aria-label="Loading payments">
      {Array.from({ length: 5 }).map((_, i) => (
        <div key={i} className={styles.skeletonRow}>
          <Skeleton width={220} height={14} />
          <Skeleton width={120} height={14} />
          <Skeleton width={88} height={20} radius="full" />
          <Skeleton width={60} height={14} />
          <Skeleton width={60} height={14} />
          <Skeleton width={100} height={14} />
        </div>
      ))}
    </div>
  );
}

function EmptyState() {
  return (
    <div className={styles.empty} role="status">
      <div className={styles.emptyWash} aria-hidden="true" />
      <p className={styles.emptyTitle}>No payments match this filter.</p>
      <p className={styles.emptyHint}>Try All, or POST one via the API.</p>
    </div>
  );
}
