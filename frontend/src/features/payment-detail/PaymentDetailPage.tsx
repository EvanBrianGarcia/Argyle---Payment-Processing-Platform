import { useEffect } from 'react';
import { Link, useParams } from 'react-router-dom';
import { usePaymentDetail } from './usePaymentDetail';
import { EventTimeline } from './EventTimeline';
import { CaptureAction } from './CaptureAction';
import { NotFoundPage } from './NotFoundPage';
import { Money } from '../../components/ui/Money';
import { StatusBadge } from '../../components/ui/StatusBadge';
import { CopyButton } from '../../components/ui/CopyButton';
import { Skeleton } from '../../components/ui/Skeleton';
import { ErrorNotice } from '../../components/ui/ErrorNotice';
import { ApiError } from '../../lib/api/client';
import { env } from '../../lib/env';
import { formatIsoUtc } from '../../lib/format/time';
import styles from './PaymentDetailPage.module.css';

export function PaymentDetailPage() {
  const { id } = useParams<{ id: string }>();
  const { data, isLoading, isError, error } = usePaymentDetail(id);

  useEffect(() => {
    document.title = id ? `${id} — Argyle Payments` : 'Payment — Argyle';
  }, [id]);

  if (isError && error instanceof ApiError && error.status === 404) {
    return <NotFoundPage paymentId={id} />;
  }

  if (isError) {
    return (
      <div className={styles.page}>
        <div className={styles.inner}>
          <ErrorNotice
            code={error instanceof ApiError ? error.code : 'unknown_error'}
            message={error instanceof Error ? error.message : 'Could not load payment.'}
            requestId={error instanceof ApiError ? error.requestId : null}
            traceId={error instanceof ApiError ? error.traceId : null}
          />
        </div>
      </div>
    );
  }

  if (isLoading || !data) {
    return <DetailSkeleton />;
  }

  const curlCommand = `curl -H "Authorization: Bearer ${env.devBearerToken}" ${env.apiBaseUrl}/v1/payments/${data.id}`;

  return (
    <div className={styles.page}>
      <div className={styles.inner}>
        <Link to="/payments" className={styles.back}>
          ← Payments
        </Link>

        <header className={styles.titleRow}>
          <div>
            <p className={styles.eyebrow}>Payment</p>
            <p className={styles.identifier}>{data.id}</p>
          </div>
          <div className={styles.titleActions}>
            <CopyButton value={curlCommand} label="Copy as curl" copiedLabel="Copied" />
          </div>
        </header>

        <section className={styles.hero} aria-label="Payment summary">
          <div className={styles.heroTop}>
            <StatusBadge status={data.status} size="lg" />
            <Money amountMinor={data.amountMinor} currency={data.currency} size="lg" />
          </div>
          {data.customerReference && (
            <p className={styles.customer}>Customer ref: {data.customerReference}</p>
          )}
          <dl className={styles.metricStrip}>
            <div>
              <dt>Created</dt>
              <dd><time dateTime={data.createdAt}>{formatIsoUtc(data.createdAt)}</time></dd>
            </div>
            <div>
              <dt>Updated</dt>
              <dd><time dateTime={data.updatedAt}>{formatIsoUtc(data.updatedAt)}</time></dd>
            </div>
            <div>
              <dt>Merchant</dt>
              <dd>{data.metadata['merchant'] ?? 'mrc_test_argyle'}</dd>
            </div>
            <div>
              <dt>Trace ID</dt>
              <dd className={styles.code}>{data.metadata['traceId'] ?? '—'}</dd>
            </div>
          </dl>
          {data.status === 'Authorized' && (
            <div className={styles.heroActions}>
              <CaptureAction paymentId={data.id} />
            </div>
          )}
        </section>

        <div className={styles.twoColumn}>
          <section className={styles.timelinePanel} aria-label="Event timeline">
            <header className={styles.panelHeader}>
              <h2>Event timeline</h2>
              <span className={styles.muted}>{data.events.length} events</span>
            </header>
            <EventTimeline events={data.events} />
          </section>

          <aside className={styles.detailsPanel} aria-label="Details">
            <header className={styles.panelHeader}>
              <h2>Details</h2>
            </header>
            <dl className={styles.detailsList}>
              <div>
                <dt>Currency</dt>
                <dd>{data.currency}</dd>
              </div>
              <div>
                <dt>Status</dt>
                <dd>{data.status}</dd>
              </div>
              {Object.entries(data.metadata).map(([key, value]) => (
                <div key={key}>
                  <dt>{key}</dt>
                  <dd className={styles.code}>{value}</dd>
                </div>
              ))}
            </dl>
          </aside>
        </div>
      </div>
    </div>
  );
}

function DetailSkeleton() {
  return (
    <div className={styles.page} role="status" aria-label="Loading payment">
      <div className={styles.inner}>
        <Skeleton width={120} height={14} />
        <Skeleton width={360} height={28} />
        <div className={styles.hero}>
          <div className={styles.heroTop}>
            <Skeleton width={120} height={28} radius="full" />
            <Skeleton width={220} height={40} />
          </div>
          <Skeleton width={480} height={48} />
        </div>
        <div className={styles.twoColumn}>
          <div className={styles.timelinePanel}>
            <Skeleton width={140} height={20} />
            {Array.from({ length: 4 }).map((_, i) => (
              <Skeleton key={i} width="80%" height={14} />
            ))}
          </div>
          <div className={styles.detailsPanel}>
            {Array.from({ length: 6 }).map((_, i) => (
              <Skeleton key={i} width="70%" height={14} />
            ))}
          </div>
        </div>
      </div>
    </div>
  );
}
