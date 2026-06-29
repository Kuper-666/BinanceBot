import { useTranslation } from 'react-i18next';
import { BarChart, Bar, XAxis, YAxis, Tooltip, ResponsiveContainer, Cell, CartesianGrid } from 'recharts';

export default function TradesPage({ data }) {
  const { t } = useTranslation();
  const trades = Array.isArray(data.trades) ? data.trades : [];

  const pnlData = trades.map(tr => ({
    name: `${tr.pair.replace('USDT', '')} ${tr.action}`,
    pnl: tr.pnl,
    pair: tr.pair,
  }));

  return (
    <div style={{ display: 'grid', gap: '16px' }}>
      <div className="card">
        <h3>{t('pnl_chart')}</h3>
        <ResponsiveContainer width="100%" height={160}>
          <BarChart data={pnlData} margin={{ top: 10, right: 10, left: 10, bottom: 10 }}>
            <CartesianGrid strokeDasharray="3 3" stroke="#1a1a1a" />
            <XAxis dataKey="name" tick={{ fill: '#666', fontSize: 11 }} axisLine={false} tickLine={false} />
            <YAxis tick={{ fill: '#666', fontSize: 10 }} axisLine={false} tickLine={false} tickFormatter={v => `${v}%`} />
            <Tooltip
              contentStyle={{ background: '#1a1a1a', border: '1px solid #333', borderRadius: '6px', fontSize: '12px' }}
              formatter={(v) => [`${v > 0 ? '+' : ''}${v}%`, 'P&L']}
              labelStyle={{ color: '#888' }}
            />
            <Bar dataKey="pnl" radius={[4, 4, 0, 0]}>
              {pnlData.map((entry, i) => (
                <Cell key={i} fill={entry.pnl > 0 ? '#22c55e' : '#ef4444'} />
              ))}
            </Bar>
          </BarChart>
        </ResponsiveContainer>
      </div>

      <div className="card">
        <h3>{t('trade_history')}</h3>
        <div style={{ overflowX: 'auto' }}>
          <table>
            <thead><tr>
              <th style={{ textAlign: 'left' }}>{t('open_time')}</th>
              <th style={{ textAlign: 'left' }}>{t('pair')}</th>
              <th style={{ textAlign: 'center' }}>{t('signal')}</th>
              <th style={{ textAlign: 'right' }}>{t('entry_price')}</th>
              <th style={{ textAlign: 'right' }}>{t('exit_price')}</th>
              <th style={{ textAlign: 'right' }}>P&L</th>
              <th style={{ textAlign: 'center' }}>{t('duration')}</th>
              <th style={{ textAlign: 'left' }}>{t('reason')}</th>
            </tr></thead>
            <tbody>
              {trades.map((tr, i) => (
                <tr key={i}>
                  <td style={{ color: '#aaa' }}>{tr.time}</td>
                  <td style={{ fontWeight: 600 }}>{tr.pair}</td>
                  <td style={{ textAlign: 'center' }}>
                    <span className="badge" style={{
                      background: (tr.action === 'BUY' ? '#22c55e' : '#ef4444') + '22',
                      color: tr.action === 'BUY' ? '#22c55e' : '#ef4444',
                    }}>{tr.action}</span>
                  </td>
                  <td style={{ textAlign: 'right', fontFamily: 'monospace' }}>${tr.entry.toLocaleString()}</td>
                  <td style={{ textAlign: 'right', fontFamily: 'monospace' }}>${tr.exit.toLocaleString()}</td>
                  <td style={{ textAlign: 'right', color: tr.pnl > 0 ? '#22c55e' : '#ef4444', fontWeight: 600 }}>
                    {tr.pnl > 0 ? '+' : ''}{tr.pnl}%
                  </td>
                  <td style={{ textAlign: 'center', color: '#aaa' }}>{tr.duration}</td>
                  <td style={{ color: '#bbb', fontSize: '12px' }}>{tr.reason}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </div>
    </div>
  );
}
