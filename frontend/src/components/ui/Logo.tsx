import styles from './Logo.module.css';

export function Logo() {
  return (
    <div className={styles.lockup} aria-label="Argyle Payments">
      <svg
        className={styles.mark}
        width="28"
        height="28"
        viewBox="0 0 28 28"
        aria-hidden="true"
      >
        <path d="M14 2 L26 14 L14 26 L2 14 Z" fill="var(--color-primary)" />
        <path
          d="M14 7 L21 14 L14 21 L7 14 Z"
          fill="var(--color-secondary-container)"
        />
      </svg>
      <div className={styles.text}>
        <span className={styles.wordmark}>argyle</span>
        <span className={styles.sublabel}>PAYMENTS</span>
      </div>
    </div>
  );
}
