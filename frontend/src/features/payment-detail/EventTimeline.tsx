import type { PaymentEvent } from '../../lib/api/types';
import { formatIsoUtc } from '../../lib/format/time';
import styles from './EventTimeline.module.css';

interface EventTimelineProps {
  events: PaymentEvent[];
}

export function EventTimeline({ events }: EventTimelineProps) {
  if (events.length === 0) {
    return <p className={styles.empty}>No events recorded.</p>;
  }

  const sorted = [...events].sort(
    (a, b) => new Date(a.at).getTime() - new Date(b.at).getTime(),
  );

  return (
    <ol className={styles.timeline}>
      {sorted.map((event, idx) => (
        <li key={event.id} className={styles.item}>
          <span
            className={`${styles.dot} ${styles[`dot-${event.toStatus.toLowerCase()}`]}`}
            aria-hidden="true"
          />
          {idx < sorted.length - 1 && <span className={styles.connector} aria-hidden="true" />}
          <div className={styles.body}>
            <p className={styles.transition}>
              <span>{event.fromStatus ?? '—'}</span>
              <span aria-hidden="true" className={styles.arrow}>→</span>
              <span>{event.toStatus}</span>
            </p>
            <p className={styles.actor}>{event.actor}</p>
            <p className={styles.reason}>{event.reason}</p>
            {Object.keys(event.payload).length > 0 && (
              <details className={styles.payload}>
                <summary>Payload</summary>
                <pre className={styles.payloadPre}>
                  <code>{JSON.stringify(event.payload, null, 2)}</code>
                </pre>
              </details>
            )}
            <p className={styles.timestamp}>
              <time dateTime={event.at}>{formatIsoUtc(event.at)}</time>
            </p>
          </div>
        </li>
      ))}
    </ol>
  );
}
