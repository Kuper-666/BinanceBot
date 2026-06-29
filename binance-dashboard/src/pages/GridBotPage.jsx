import { useState } from 'react';
import { useTranslation } from 'react-i18next';

export default function GridBotPage({ data }) {
  const { t } = useTranslation();
  const grid = data.gridBot || { enabled: false, running: false, orders: [], pair: '', levels: 0, filledOrders: 0, rangeLow: 0, rangeHigh: 0 };
  const [selectedPair, setSelectedPair] = useState(grid.pair || '');

  if (!grid.enabled || !grid.running) {
    return (
      <div style={{ display: 'grid', gap: '16px' }}>
        <h3 style={{ fontSize: '16px', fontWeight: 700 }}>{t('grid_bot')}</h3>
        <div className="card" style={{ padding: '32px', textAlign: 'center' }}>
          <div style={{ fontSize: '36px', marginBottom: '12px' }}>🔲</div>
          <div style={{ fontSize: '14px', color: '#888', marginBottom: '8px' }}>{t('grid_bot')} {t('disabled')}</div>
          <div style={{ fontSize: '12px', color: '#666' }}>
            {t('grid_bot_not_running')}
          </div>
        </div>
      </div>
    );
  }

  return (
    <div style={{ display: 'grid', gap: '16px' }}>
      <div style={{ display: 'flex', gap: '12px', alignItems: 'center' }}>
        <h3 style={{ fontSize: '16px', fontWeight: 700 }}>{t('grid_bot')}</h3>
        <select value={selectedPair} onChange={e => setSelectedPair(e.target.value)}
          style={{ padding: '6px 12px', borderRadius: '6px', border: '1px solid #333', background: '#1a1a1a', color: '#e5e5e5', fontSize: '13px', outline: 'none' }}>
          <option>{grid.pair}</option>
        </select>
      </div>

      <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(180px, 1fr))', gap: '12px' }}>
        <div className="card">
          <div style={{ fontSize: '11px', color: '#888' }}>{t('grid_range')}</div>
          <div style={{ fontSize: '18px', fontWeight: 700 }}>${grid.rangeLow.toLocaleString()} — ${grid.rangeHigh.toLocaleString()}</div>
        </div>
        <div className="card">
          <div style={{ fontSize: '11px', color: '#888' }}>{t('grid_levels')}</div>
          <div style={{ fontSize: '18px', fontWeight: 700 }}>{grid.levels} <span style={{ fontSize: '12px', color: '#888' }}>({grid.filledOrders} {t('filled')})</span></div>
        </div>
        <div className="card">
          <div style={{ fontSize: '11px', color: '#888' }}>{t('investment')}</div>
          <div style={{ fontSize: '18px', fontWeight: 700 }}>${grid.investment} <span style={{ fontSize: '12px', color: '#888' }}>({grid.investmentPercent}%)</span></div>
        </div>
        <div className="card">
          <div style={{ fontSize: '11px', color: '#888' }}>{t('realized_pnl')}</div>
          <div style={{ fontSize: '18px', fontWeight: 700, color: '#22c55e' }}>+${grid.realizedPnl}</div>
        </div>
        <div className="card">
          <div style={{ fontSize: '11px', color: '#888' }}>{t('unrealized_pnl')}</div>
          <div style={{ fontSize: '18px', fontWeight: 700, color: '#22c55e' }}>+${grid.unrealizedPnl}</div>
        </div>
      </div>

      <div className="card">
        <h3>{t('grid_visualization')}</h3>
        <div style={{ overflowX: 'auto' }}>
          <div style={{ display: 'flex', alignItems: 'center', gap: '0', padding: '16px 0', minWidth: '600px' }}>
            {grid.orders.map((order, i) => {
              const currentPrice = data.pairs.find(p => p.pair === selectedPair)?.price || 0;
              const isCurrentPrice = order.level <= currentPrice && (i === grid.orders.length - 1 || grid.orders[i + 1].level > currentPrice);
              return (
                <div key={i} style={{ flex: 1, display: 'flex', flexDirection: 'column', alignItems: 'center', position: 'relative' }}>
                  <div style={{
                    width: '100%', height: '40px', display: 'flex', alignItems: 'center', justifyContent: 'center',
                    borderRadius: '4px', fontSize: '11px', fontWeight: 600,
                    background: order.status === 'filled' ? (order.side === 'BUY' ? '#22c55e22' : '#ef444422') : '#1a1a1a',
                    border: `1px solid ${order.status === 'filled' ? (order.side === 'BUY' ? '#22c55e' : '#ef4444') : '#333'}`,
                    color: order.status === 'filled' ? (order.side === 'BUY' ? '#22c55e' : '#ef4444') : '#888',
                  }}>
                    ${order.level.toLocaleString()}
                  </div>
                  <div style={{ fontSize: '9px', color: '#666', marginTop: '4px' }}>
                    {order.side} · {order.status === 'filled' ? order.filledAt : t('pending')}
                  </div>
                  {isCurrentPrice && (
                    <div style={{
                      position: 'absolute', top: '-8px', left: '50%', transform: 'translateX(-50%)',
                      background: '#f59e0b', color: '#000', fontSize: '9px', fontWeight: 700,
                      padding: '1px 6px', borderRadius: '3px', whiteSpace: 'nowrap',
                    }}>
                      {t('current')} ${currentPrice.toLocaleString()}
                    </div>
                  )}
                </div>
              );
            })}
          </div>
        </div>
      </div>

      <div className="card">
        <h3>{t('grid_orders')}</h3>
        <div style={{ overflowX: 'auto' }}>
          <table>
            <thead><tr>
              <th style={{ textAlign: 'right' }}>{t('price')}</th>
              <th style={{ textAlign: 'center' }}>{t('signal')}</th>
              <th style={{ textAlign: 'right' }}>{t('quantity')}</th>
              <th style={{ textAlign: 'center' }}>{t('status')}</th>
              <th style={{ textAlign: 'center' }}>{t('filled_time')}</th>
            </tr></thead>
            <tbody>
              {grid.orders.map((order, i) => (
                <tr key={i}>
                  <td style={{ textAlign: 'right', fontFamily: 'monospace' }}>${order.price.toLocaleString()}</td>
                  <td style={{ textAlign: 'center' }}>
                    <span className="badge" style={{
                      background: (order.side === 'BUY' ? '#22c55e' : '#ef4444') + '22',
                      color: order.side === 'BUY' ? '#22c55e' : '#ef4444',
                    }}>{order.side}</span>
                  </td>
                  <td style={{ textAlign: 'right', fontFamily: 'monospace' }}>{order.qty}</td>
                  <td style={{ textAlign: 'center' }}>
                    <span style={{ color: order.status === 'filled' ? '#22c55e' : '#f59e0b', fontSize: '12px' }}>
                      {order.status === 'filled' ? t('filled') : t('pending')}
                    </span>
                  </td>
                  <td style={{ textAlign: 'center', color: '#888' }}>{order.filledAt || '—'}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </div>
    </div>
  );
}
