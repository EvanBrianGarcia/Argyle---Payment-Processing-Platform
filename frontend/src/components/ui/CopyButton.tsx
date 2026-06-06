import { useState } from 'react';
import styles from './CopyButton.module.css';

interface CopyButtonProps {
  value: string;
  label?: string;
  copiedLabel?: string;
  variant?: 'secondary' | 'ghost';
}

export function CopyButton({
  value,
  label = 'Copy',
  copiedLabel = 'Copied',
  variant = 'secondary',
}: CopyButtonProps) {
  const [copied, setCopied] = useState(false);

  async function handleClick() {
    try {
      await navigator.clipboard.writeText(value);
      setCopied(true);
      window.setTimeout(() => setCopied(false), 1200);
    } catch {
      // Clipboard API unavailable — fall back silently.
    }
  }

  return (
    <button
      type="button"
      onClick={handleClick}
      className={`${styles.button} ${styles[variant]}`}
      aria-live="polite"
    >
      {copied ? copiedLabel : label}
    </button>
  );
}
