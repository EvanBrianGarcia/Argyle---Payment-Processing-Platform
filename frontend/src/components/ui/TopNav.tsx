import { NavLink } from 'react-router-dom';
import { Logo } from './Logo';
import { env } from '../../lib/env';
import styles from './TopNav.module.css';

const NAV = [
  { to: '/payments', label: 'Payments' },
  { to: '/refunds', label: 'Refunds' },
  { to: '/reports', label: 'Reports' },
  { to: '/settings', label: 'Settings' },
] as const;

export function TopNav() {
  return (
    <header className={styles.nav} role="banner">
      <div className={styles.inner}>
        <Logo />
        <nav aria-label="Main" className={styles.links}>
          {NAV.map((item) => (
            <NavLink
              key={item.to}
              to={item.to}
              className={({ isActive }) =>
                isActive ? `${styles.link} ${styles.linkActive}` : styles.link
              }
              end={item.to === '/payments'}
            >
              {item.label}
            </NavLink>
          ))}
        </nav>
        <div className={styles.controls}>
          <div className={styles.search} role="search">
            <span className={styles.searchIcon} aria-hidden="true">⌕</span>
            <input
              type="search"
              placeholder="Search transactions..."
              aria-label="Search transactions"
              className={styles.searchInput}
            />
            <kbd className={styles.kbd} aria-hidden="true">/</kbd>
          </div>
          <span className={styles.envPill} aria-label="Environment">
            {env.envLabel}
          </span>
          <div className={styles.avatar} aria-hidden="true">A</div>
        </div>
      </div>
    </header>
  );
}
