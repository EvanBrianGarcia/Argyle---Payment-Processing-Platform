import { formatIsoUtc, relativeFromNow } from '../../lib/format/time';
import styles from './RelativeTime.module.css';

interface RelativeTimeProps {
  iso: string;
  now?: Date;
}

export function RelativeTime({ iso, now }: RelativeTimeProps) {
  const label = relativeFromNow(iso, now);
  const title = formatIsoUtc(iso);
  return (
    <time className={styles.time} dateTime={iso} title={title}>
      {label}
    </time>
  );
}
