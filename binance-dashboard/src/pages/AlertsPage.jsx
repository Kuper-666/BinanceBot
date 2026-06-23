import { useState } from 'react';
import { useTranslation } from 'react-i18next';

const MOCK_ALERTS = [
  { id: 1, time: '14:32:18', type: 'trade', severity: 'success', title: 'BTCUSDT BUY executed', body: 'Entry $106,500 — SL $104,370 (2%) / TP $112,890 (6%). Risk: 1.2%. Echelons: all passed.' },
  { id: 2, time: '14:30:02', type: 'news', severity: 'warning', title: 'SEC regulation framework announced', body: 'Impact: high (4/5). Affects: all pairs. Trading blocked for 5 min. Source: CoinDesk.' },
  { id: 3, time: '14:28:45', type: 'system', severity: 'warning', title: 'WebSocket reconnecting', body: 'Connection lost after 30s idle. Attempt 1/5 successful. Latency: 220ms.' },
  { id: 4, time: '14:15:33', type: 'risk', severity: 'danger', title: 'Order rejected — MIN_NOTIONAL', body: 'BTCUSDT limit order $4.20 below minimum $6.00. GridBot adjusted qty to 0.00006.' },
  { id: 5, time: '14:10:00', type: 'risk', severity: 'info', title: 'Daily drawdown report', body: 'P&L: +$12.40 (+1.0%). Max drawdown: -2.1%. Remaining risk budget: 9.0%.' },
  { id: 6, time: '14:05:00', type: 'system', severity: 'info', title: 'ML model loaded', body: 'FastTree v3 — trained 2026-06-22, accuracy 64.2%, 100 trees. Next retrain in 6d 14h.' },
  { id: 7, time: '14:00:00', type: 'system', severity: 'success', title: 'Bot started successfully', body: 'Version 1.10.4 — 7 pairs active, leverage 5x, all 3 echelons enabled.' },
  { id: 8, time: '13:52:00', type: 'trade', severity: 'success', title: 'SOLUSDT SELL closed (+2.22%)', body: 'Entry $182.50 → Exit $178.45. Duration: 3h 20m. Reason: Rising Volatility.' },
  { id: 9, time: '13:45:00', type: 'grid', severity: 'info', title: 'Grid orders placed', body: 'ETHUSDT — 8 BUY limits in $2,380-$2,520 range. Total investment: $240 (20% of balance).' },
  { id: 10, time: '13:30:00', type: 'news', severity: 'success', title: 'Bitcoin ETF inflows $1.2B', body: 'Impact: medium (3/5). Affects: BTC. Sentiment: positive. No trading restrictions.' },
  { id: 11, time: '13:15:00', type: 'risk', severity: 'danger', title: 'Correlated position warning', body: 'BTCUSDT + ETHUSDT correlation 0.87. Combined exposure: $480 (38% of balance). Consider reducing.' },
  { id: 12, time: '13:00:00', type: 'system', severity: 'warning', title: 'Config reload detected', body: 'trading_settings.json changed. Hot-reloaded 4 parameters. No restart required.' },
];

const TYPE_ICONS = { trade: '\u{1F4B0}', news: '\u{1F4F0}', system: '\u{2699}\uFE0F', risk: '\u{26A0}\uFE0F', grid: '\u{1F4CA}' };
const SEV_COLORS = { success: '#22c55e', info: '#3b82f6', warning: '#f59e0b', danger: '#ef4444' };
const SEV_BG = { success: '#0a2e1a', info: '#0a1a2e', warning: '#2e2a0a', danger: '#2e0a0a' };

const SEV_LABELS = { danger: 'sev_danger', warning: 'sev_warning', info: 'sev_info', success: 'sev_success' };

export default function AlertsPage() {
  const { t } = useTranslation();
  const [filter, setFilter] = useState('all');
  const [search, setSearch] = useState('');

  const filtered = MOCK_ALERTS.filter(a => {
    if (filter !== 'all' && a.severity !== filter) return false;
    if (search && !a.title.toLowerCase().includes(search.toLowerCase()) && !a.body.toLowerCase().includes(search.toLowerCase())) return false;
    return true;
  });

  return (
    <div style={{ display: 'grid', gap: '16px' }}>
      <div style={{ display: 'flex', gap: '8px', alignItems: 'center' }}>
        {['all', 'danger', 'warning', 'info', 'success'].map(sev => (
          <button key={sev} onClick={() => setFilter(sev)}
            style={{ padding: '6px 14px', borderRadius: '6px', border: 'none', cursor: 'pointer', fontSize: '13px', fontWeight: filter === sev ? 600 : 400,
              background: filter === sev ? (sev === 'all' ? '#333' : SEV_COLORS[sev] + '33') : '#1a1a1a',
              color: sev === 'all' ? '#fff' : SEV_COLORS[sev] || '#888',
              textTransform: 'uppercase' }}>
              {sev === 'all' ? t('all') : t(SEV_LABELS[sev])}
          </button>
        ))}
        <input type="text" value={search} onChange={e => setSearch(e.target.value)}
          placeholder={t('search') + '...'}
          style={{ marginLeft: 'auto', padding: '6px 12px', borderRadius: '6px', border: '1px solid #333', background: '#1a1a1a', color: '#e5e5e5', fontSize: '13px', width: '260px', outline: 'none' }} />
        <span style={{ fontSize: '12px', color: '#666' }}>{filtered.length} / {MOCK_ALERTS.length}</span>
      </div>

      <div style={{ display: 'grid', gap: '8px' }}>
        {filtered.map(a => (
          <div key={a.id} style={{ background: SEV_BG[a.severity], borderRadius: '8px', padding: '14px 18px', border: `1px solid ${SEV_COLORS[a.severity]}33`, display: 'grid', gridTemplateColumns: '44px 1fr auto', gap: '14px', alignItems: 'start' }}>
            <div style={{ fontSize: '22px', textAlign: 'center', lineHeight: 1 }}>{TYPE_ICONS[a.type] || '\u{1F514}'}</div>
            <div>
              <div style={{ display: 'flex', gap: '8px', alignItems: 'center', marginBottom: '4px' }}>
                <span style={{ fontSize: '14px', fontWeight: 600, color: '#fff' }}>{a.title}</span>
                <span style={{ fontSize: '10px', fontWeight: 600, padding: '2px 6px', borderRadius: '4px', background: SEV_COLORS[a.severity] + '33', color: SEV_COLORS[a.severity], textTransform: 'uppercase' }}>{a.severity}</span>
                <span style={{ fontSize: '10px', padding: '2px 6px', borderRadius: '4px', background: '#ffffff0d', color: '#888', textTransform: 'capitalize' }}>{a.type}</span>
              </div>
              <div style={{ fontSize: '12px', color: '#999', lineHeight: 1.5 }}>{a.body}</div>
            </div>
            <div style={{ fontSize: '12px', color: '#555', fontFamily: "'JetBrains Mono', monospace", whiteSpace: 'nowrap' }}>{a.time}</div>
          </div>
        ))}
      </div>
    </div>
  );
}
