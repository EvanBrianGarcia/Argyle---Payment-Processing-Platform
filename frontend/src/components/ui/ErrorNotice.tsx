import styles from './ErrorNotice.module.css';

interface ErrorNoticeProps {
  code: string;
  message: string;
  requestId?: string | null;
  traceId?: string | null;
  onRetry?: () => void;
}

export function ErrorNotice({
  code,
  message,
  requestId,
  traceId,
  onRetry,
}: ErrorNoticeProps) {
  return (
    <div role="alert" className={styles.notice}>
      <span className={styles.bar} aria-hidden="true" />
      <div className={styles.body}>
        <code className={styles.code}>{code}</code>
        <p className={styles.message}>{message}</p>
        {(requestId || traceId) && (
          <dl className={styles.meta}>
            {requestId && (
              <>
                <dt>Request ID</dt>
                <dd><code>{requestId}</code></dd>
              </>
            )}
            {traceId && (
              <>
                <dt>Trace ID</dt>
                <dd><code>{traceId}</code></dd>
              </>
            )}
          </dl>
        )}
        {onRetry && (
          <button type="button" onClick={onRetry} className={styles.retry}>
            Retry
          </button>
        )}
      </div>
    </div>
  );
}
