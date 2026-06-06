import styles from './SkipToMain.module.css';

export function SkipToMain() {
  return (
    <a href="#main" className={styles.link}>
      Skip to main content
    </a>
  );
}
