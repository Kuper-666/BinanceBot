import { NavLink } from 'react-router-dom';
import { useTranslation } from 'react-i18next';

const TABS = ['overview', 'signals', 'positions', 'trades', 'grid-bot', 'analytics', 'backtest', 'settings', 'logs', 'alerts'];

export default function NavBar() {
  const { t } = useTranslation();

  return (
    <nav style={{ display: 'flex', gap: '4px', padding: '8px 24px', background: '#111', borderBottom: '1px solid #222', overflowX: 'auto' }}>
      {TABS.map(tab => (
        <NavLink key={tab} to={`/${tab}`}
          style={({ isActive }) => ({
            padding: '8px 14px', borderRadius: '6px', border: 'none', whiteSpace: 'nowrap',
            background: isActive ? '#22c55e' : 'transparent',
            color: isActive ? '#000' : '#888',
            cursor: 'pointer', fontWeight: isActive ? 600 : 400,
            fontSize: '12px', textDecoration: 'none', textTransform: 'capitalize',
          })}>
          {t(tab)}
        </NavLink>
      ))}
    </nav>
  );
}
