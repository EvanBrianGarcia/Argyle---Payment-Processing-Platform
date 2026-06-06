import { Link } from 'react-router-dom';
import styles from './NotFoundPage.module.css';

interface NotFoundPageProps {
  paymentId?: string;
}

export function NotFoundPage({ paymentId }: NotFoundPageProps) {
  return (
    <div className={styles.page}>
      <div className={styles.inner}>
        <div className={styles.wash} aria-hidden="true" />
        <div className={styles.content} role="status">
          <code className={styles.code}>payment_not_found</code>
          <h1 className={styles.title}>
            {paymentId
              ? `Payment ${paymentId} was not found.`
              : 'This page does not exist.'}
          </h1>
          <Link to="/payments" className={styles.back}>
            ← Back to payments
          </Link>
        </div>
      </div>
    </div>
  );
}
