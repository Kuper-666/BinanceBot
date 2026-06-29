import { useState, useMemo } from 'react';
import { useTranslation } from 'react-i18next';

const MOCK_LOGS = [
  { time: '14:32:18', level: 'info', source: 'TradingService', message: 'BUY BTCUSDT @ $106,500 — SL: $104,370, TP: $112,890' },
  { time: '14:32:18', level: 'info', source: 'AiRiskEngine', message: 'Risk calculated: 1.2% (confidence 0.78, balance $1,247)' },
  { time: '14:32:17', level: 'debug', source: 'SignalValidator', message: 'BTCUSDT score=0.78 — RSI(38.2)<35 OK, MACD aligned, BB width 3.1% OK' },
  { time: '14:32:16', level: 'info', source: 'AdaptiveAgent', message: 'BTCUSDT adaptive factor=1.12 — ATR ratio 0.98, volume 1.34x, vol 1.8%' },
  { time: '14:32:15', level: 'debug', source: 'Indicators', message: 'BTCUSDT: SMA9=106,820 SMA21=106,340 RSI=38.2 MACD=+0.0042 BB=3.1%' },
  { time: '14:30:00', level: 'info', source: 'NewsSentinel', message: 'Fetched 12 articles — 3 positive, 2 negative, 7 neutral. No high-impact blocks.' },
  { time: '14:28:45', level: 'warn', source: 'WebSocket', message: 'Reconnect attempt 1/5 — connection dropped (timeout 5000ms)' },
  { time: '14:28:42', level: 'info', source: 'WebSocket', message: 'Connected to wss://fstream.binance.com/ws' },
  { time: '14:25:10', level: 'info', source: 'GridBot', message: 'Placed 8 BUY limit orders for ETHUSDT (range $2,380-$2,520)' },
  { time: '14:20:00', level: 'info', source: 'TradingService', message: 'Trading loop tick #1247 — 7 pairs scanned, 2 signals above threshold' },
  { time: '14:15:33', level: 'error', source: 'BinanceClient', message: 'Order rejected: MIN_NOTIONAL — attempted $4.20, minimum $6.00' },
  { time: '14:15:32', level: 'debug', source: 'BinanceClient', message: 'POST /fapi/v1/order — BTCUSDT BUY LIMIT qty=0.00004 @ $106,500' },
  { time: '14:10:00', level: 'info', source: 'RiskManager', message: 'Daily P&L: +$12.40 (1.0% of balance). Max drawdown: -2.1%' },
  { time: '14:05:00', level: 'info', source: 'MlModelManager', message: 'ML model loaded: FastTree v3 (trained 2026-06-22, accuracy 64.2%)' },
  { time: '14:00:00', level: 'info', source: 'TradingService', message: 'Bot started — version 1.10.4, pairs: 7, leverage: 5x' },
  { time: '14:00:00', level: 'info', source: 'Config', message: 'Loaded config.json — DPAPI decryption OK, 3 echelons enabled' },
];

const LEVEL_COLORS = { info: '#22c55e', debug: '#6b7280', warn: '#f59e0b', error: '#ef4444' };
const LEVEL_KEYS = { info: 'log_info', debug: 'log_debug', warn: 'log_warn', error: 'log_error' };

export default function LogsPage({ data }) {
  const { t } = useTranslation();
  const [filter, setFilter] = useState('all');
  const [search, setSearch] = useState('');

  const allLogs = useMemo(() => {
    if (data?.logs) {
      const raw = data.logs;
      if (Array.isArray(raw)) return raw;
      if (typeof raw === 'string' && raw.length > 0) {
        return raw.split('\n').filter(Boolean).map(line => {
          const m = line.match(/^\[?(\d{2}:\d{2}:\d{2})\]?\s*(.*)$/);
          const msg = m ? m[2] : line;
          let level = 'info';
          if (msg.includes('❌') || msg.includes('ОШИБКА') || msg.includes('ERROR')) level = 'error';
          else if (msg.includes('⚠️') || msg.includes('WARNING')) level = 'warn';
          else if (msg.includes('DEBUG') || msg.includes('DBG')) level = 'debug';
          return { time: m ? m[1] : '', level, source: 'Bot', message: msg };
        });
      }
    }
    return MOCK_LOGS;
  }, [data?.logs]);

  const filtered = useMemo(() => allLogs.filter(log => {
    if (filter !== 'all' && log.level !== filter) return false;
    if (search && !log.message.toLowerCase().includes(search.toLowerCase()) && !log.source.toLowerCase().includes(search.toLowerCase())) return false;
    return true;
  }), [allLogs, filter, search]);

  return (
    <div style={{ display: 'grid', gap: '16px' }}>
      <div style={{ display: 'flex', gap: '8px', alignItems: 'center' }}>
          {['all', 'error', 'warn', 'info', 'debug'].map(level => (
          <button key={level} onClick={() => setFilter(level)}
            style={{ padding: '6px 14px', borderRadius: '6px', border: 'none', cursor: 'pointer', fontSize: '13px', fontWeight: filter === level ? 600 : 400,
              background: filter === level ? (level === 'all' ? '#333' : LEVEL_COLORS[level] + '33') : '#1a1a1a',
              color: filter === 'all' ? '#fff' : level === 'all' ? '#fff' : LEVEL_COLORS[level] || '#888',
              textTransform: 'uppercase' }}>
              {level === 'all' ? t('all') : t(LEVEL_KEYS[level])}
          </button>
        ))}
        <input type="text" value={search} onChange={e => setSearch(e.target.value)}
          placeholder={t('search') + '...'}
          style={{ marginLeft: 'auto', padding: '6px 12px', borderRadius: '6px', border: '1px solid #333', background: '#1a1a1a', color: '#e5e5e5', fontSize: '13px', width: '260px', outline: 'none' }} />
        <span style={{ fontSize: '12px', color: '#666' }}>{filtered.length} / {allLogs.length}</span>
      </div>

      <div style={{ background: '#141414', borderRadius: '8px', border: '1px solid #222', overflow: 'hidden' }}>
        <div style={{ display: 'grid', gridTemplateColumns: '80px 50px 130px 1fr', fontSize: '12px', fontFamily: "'JetBrains Mono', 'Fira Code', monospace" }}>
          {filtered.map((log, i) => (
            <div key={i} style={{ display: 'contents' }}>
              <span style={{ padding: '8px 12px', color: '#666', borderBottom: '1px solid #1a1a1a' }}>{log.time}</span>
              <span style={{ padding: '8px 8px', color: LEVEL_COLORS[log.level], borderBottom: '1px solid #1a1a1a', fontWeight: 600 }}>{t(LEVEL_KEYS[log.level])}</span>
              <span style={{ padding: '8px 12px', color: '#a78bfa', borderBottom: '1px solid #1a1a1a' }}>{log.source}</span>
              <span style={{ padding: '8px 12px', color: '#d4d4d4', borderBottom: '1px solid #1a1a1a', wordBreak: 'break-word' }}>{log.message}</span>
            </div>
          ))}
        </div>
      </div>
    </div>
  );
}
