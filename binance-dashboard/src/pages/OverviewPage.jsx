import { useTranslation } from 'react-i18next';
import { AreaChart, Area, XAxis, YAxis, Tooltip, ResponsiveContainer, BarChart, Bar, Cell } from 'recharts';
import StatCard from '../components/StatCard';
import { signalColor, riskColor, sentimentIcon } from '../data';

const CustomTooltip = ({ active, payload, label }) => {
  if (!active || !payload?.length) return null;
  return (
    <div style={{ background: '#1a1a1a', border: '1px solid #333', borderRadius: '6px', padding: '8px 12px', fontSize: '12px' }}>
      <div style={{ color: '#888' }}>{label}</div>
      <div style={{ color: '#22c55e', fontWeight: 600 }}>${payload[0].value.toLocaleString()}</div>
    </div>
  );
};

const VolumeBar = ({ pairs }) => {
  const data = pairs.map(p => ({ name: p.pair.replace('USDT', ''), volume: p.volume }));
  return (
    <ResponsiveContainer width="100%" height={120}>
      <BarChart data={data} margin={{ top: 0, right: 0, left: 0, bottom: 0 }}>
        <XAxis dataKey="name" tick={{ fill: '#666', fontSize: 10 }} axisLine={false} tickLine={false} />
        <Tooltip
          contentStyle={{ background: '#1a1a1a', border: '1px solid #333', borderRadius: '6px', fontSize: '12px' }}
          formatter={(v) => [`${v}x`, 'Volume']}
          labelStyle={{ color: '#888' }}
        />
        <Bar dataKey="volume" radius={[3, 3, 0, 0]}>
          {data.map((entry, i) => (
            <Cell key={i} fill={entry.volume > 1.5 ? '#f59e0b' : entry.volume > 1 ? '#22c55e' : '#333'} />
          ))}
        </Bar>
      </BarChart>
    </ResponsiveContainer>
  );
};

export default function OverviewPage({ data }) {
  const { t } = useTranslation();

  return (
    <div style={{ display: 'grid', gap: '20px' }}>
      <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(180px, 1fr))', gap: '12px' }}>
        <StatCard label={t('balance')} value={`$${data.balance.toLocaleString()}`} color="#22c55e" />
        <StatCard label={t('pnl')} value={`+$${data.pnl}`} sub={`${data.pnlPercent}%`} color="#22c55e" />
        <StatCard label={t('win_rate')} value={`${data.winRate}%`} />
        <StatCard label={t('max_drawdown')} value={`-${data.maxDrawdown}%`} color="#ef4444" />
        <StatCard label={t('trades')} value={data.totalTrades} />
        <StatCard label={t('positions')} value={`${data.openPositions}/${data.maxPositions}`} />
      </div>

      <div className="page-grid-2">
        <div className="card">
          <h3>{t('equity_curve')}</h3>
          <ResponsiveContainer width="100%" height={180}>
            <AreaChart data={data.equity} margin={{ top: 5, right: 5, left: 5, bottom: 5 }}>
              <defs>
                <linearGradient id="eqGrad" x1="0" y1="0" x2="0" y2="1">
                  <stop offset="0%" stopColor="#22c55e" stopOpacity={0.3} />
                  <stop offset="100%" stopColor="#22c55e" stopOpacity={0} />
                </linearGradient>
              </defs>
              <XAxis dataKey="time" tick={{ fill: '#666', fontSize: 10 }} axisLine={false} tickLine={false} />
              <YAxis tick={{ fill: '#666', fontSize: 10 }} axisLine={false} tickLine={false} domain={['dataMin - 20', 'dataMax + 20']} />
              <Tooltip content={<CustomTooltip />} />
              <Area type="monotone" dataKey="value" stroke="#22c55e" strokeWidth={2} fill="url(#eqGrad)" />
            </AreaChart>
          </ResponsiveContainer>
        </div>

        <div className="card">
          <h3>{t('volume_analysis')}</h3>
          <VolumeBar pairs={data.pairs} />
          <div style={{ marginTop: '16px' }}>
            <h3 style={{ fontSize: '12px', marginBottom: '8px' }}>{t('echelon_1')}</h3>
            {Object.entries(data.echelons).map(([key, val]) => (
              <div key={key} style={{ display: 'flex', justifyContent: 'space-between', padding: '4px 0', borderBottom: '1px solid #1a1a1a' }}>
                <span style={{ fontSize: '12px', color: '#aaa' }}>{key === 'adaptive' ? t('echelon_1') : key === 'validator' ? t('echelon_2') : t('echelon_3')}</span>
                <span style={{ fontSize: '11px', color: val ? '#22c55e' : '#ef4444' }}>{val ? t('enabled') : t('disabled')}</span>
              </div>
            ))}
          </div>
          <div style={{ marginTop: '12px' }}>
            <h3 style={{ fontSize: '12px', marginBottom: '8px' }}>{t('news_sentiment')}</h3>
            {data.news.slice(0, 3).map((n, i) => (
              <div key={i} style={{ padding: '3px 0', fontSize: '11px', color: '#999', borderBottom: '1px solid #1a1a1a' }}>
                {sentimentIcon(n.sentiment)} {n.title.substring(0, 40)}...
              </div>
            ))}
          </div>
        </div>
      </div>

      <div className="card">
        <h3>{t('signals')}</h3>
        <div style={{ overflowX: 'auto' }}>
          <table>
            <thead><tr>
              <th style={{ textAlign: 'left' }}>{t('pair')}</th>
              <th style={{ textAlign: 'right' }}>{t('price')}</th>
              <th style={{ textAlign: 'center' }}>{t('signal')}</th>
              <th style={{ textAlign: 'center' }}>{t('rsi')}</th>
              <th style={{ textAlign: 'center' }}>{t('ai_score')}</th>
              <th style={{ textAlign: 'center' }}>{t('risk_level')}</th>
            </tr></thead>
            <tbody>
              {data.pairs.map(p => (
                <tr key={p.pair}>
                  <td style={{ fontWeight: 600 }}>{p.pair}</td>
                  <td style={{ textAlign: 'right', fontFamily: 'monospace' }}>${p.price.toLocaleString()}</td>
                  <td style={{ textAlign: 'center' }}>
                    <span className="badge" style={{ background: signalColor(p.signal) + '22', color: signalColor(p.signal) }}>
                      {t(p.signal)}
                    </span>
                  </td>
                  <td style={{ textAlign: 'center', color: p.rsi < 30 ? '#22c55e' : p.rsi > 70 ? '#ef4444' : '#ccc' }}>{p.rsi.toFixed(1)}</td>
                  <td style={{ textAlign: 'center' }}>
                    <div style={{ width: '40px', height: '6px', background: '#222', borderRadius: '3px', margin: '0 auto', overflow: 'hidden' }}>
                      <div style={{ width: `${p.aiScore * 100}%`, height: '100%', background: '#22c55e', borderRadius: '3px' }} />
                    </div>
                  </td>
                  <td style={{ textAlign: 'center' }}>
                    <span style={{ color: riskColor(p.risk), fontSize: '12px' }}>{t(p.risk + '_risk')}</span>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </div>
    </div>
  );
}
