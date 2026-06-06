import styles from './Skeleton.module.css';

interface SkeletonProps {
  width?: string | number;
  height?: string | number;
  radius?: 'sm' | 'md' | 'full';
  label?: string;
}

export function Skeleton({ width = '100%', height = 12, radius = 'sm', label }: SkeletonProps) {
  const style = {
    width: typeof width === 'number' ? `${width}px` : width,
    height: typeof height === 'number' ? `${height}px` : height,
  };
  return (
    <span
      role={label ? 'status' : 'presentation'}
      aria-label={label}
      className={`${styles.skeleton} ${styles[`radius-${radius}`]}`}
      style={style}
    />
  );
}
