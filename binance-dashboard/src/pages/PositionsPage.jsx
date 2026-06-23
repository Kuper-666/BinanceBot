import { useTranslation } from 'react-i18next';

export default function PositionsPage({ data }) {
  const { t } = useTranslation();

  return (
    <div style={{ display: 'grid', gap: '16px' }}>
      <div style={{ display: 'flex', gap: '12px', alignItems: 'center' }}>
        <h3 style={{ fontSize: '16px', fontWeight: 700 }}>{t('open_positions')}</h3>
        <span className="badge" style={{ background: '#22c55e22', color: '#22c55e' }}>
          {data.positions.length} / {data.maxPositions}
        </span>
      </div>

      {data.positions.map((pos, i) => (
        <div key={i} className="card" style={{ borderLeft: `3px solid ${pos.side === 'LONG' ? '#22c55e' : '#ef4444'}` }}>
          <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'flex-start', marginBottom: '16px' }}>
            <div>
              <div style={{ display: 'flex', gap: '8px', alignItems: 'center', marginBottom: '4px' }}>
                <span style={{ fontSize: '18px', fontWeight: 700 }}>{pos.pair}</span>
                <span className="badge" style={{
                  background: (pos.side === 'LONG' ? '#22c55e' : '#ef4444') + '22',
                  color: pos.side === 'LONG' ? '#22c55e' : '#ef4444',
                }}>{pos.side}</span>
                <span className="badge" style={{ background: '#333', color: '#aaa' }}>{pos.leverage}x</span>
              </div>
              <div style={{ fontSize: '12px', color: '#888' }}>
                {t('opened')} {pos.openTime} · {pos.duration}
              </div>
            </div>
            <div style={{ textAlign: 'right' }}>
              <div style={{ fontSize: '20px', fontWeight: 700, color: pos.pnl >= 0 ? '#22c55e' : '#ef4444' }}>
                {pos.pnl >= 0 ? '+' : ''}${pos.pnl.toFixed(2)}
              </div>
              <div style={{ fontSize: '12px', color: pos.pnlPercent >= 0 ? '#22c55e' : '#ef4444' }}>
                {pos.pnlPercent >= 0 ? '+' : ''}{pos.pnlPercent.toFixed(2)}%
              </div>
            </div>
          </div>

          <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(140px, 1fr))', gap: '12px' }}>
            <div style={{ padding: '8px', background: '#1a1a1a', borderRadius: '6px' }}>
              <div style={{ fontSize: '10px', color: '#666' }}>{t('entry_price')}</div>
              <div style={{ fontSize: '14px', fontWeight: 600, fontFamily: 'monospace' }}>${pos.entry.toLocaleString()}</div>
            </div>
            <div style={{ padding: '8px', background: '#1a1a1a', borderRadius: '6px' }}>
              <div style={{ fontSize: '10px', color: '#666' }}>{t('current_price')}</div>
              <div style={{ fontSize: '14px', fontWeight: 600, fontFamily: 'monospace' }}>${pos.current.toLocaleString()}</div>
            </div>
            <div style={{ padding: '8px', background: '#1a1a1a', borderRadius: '6px' }}>
              <div style={{ fontSize: '10px', color: '#666' }}>{t('quantity')}</div>
              <div style={{ fontSize: '14px', fontWeight: 600 }}>{pos.qty}</div>
            </div>
            <div style={{ padding: '8px', background: '#1a1a1a', borderRadius: '6px' }}>
              <div style={{ fontSize: '10px', color: '#666' }}>{t('margin')}</div>
              <div style={{ fontSize: '14px', fontWeight: 600 }}>${pos.margin.toFixed(2)}</div>
            </div>
          </div>

          <div style={{ marginTop: '12px', display: 'grid', gridTemplateColumns: '1fr 1fr', gap: '8px' }}>
            <div style={{ padding: '8px', background: '#2e0a0a', borderRadius: '6px', border: '1px solid #ef444433' }}>
              <div style={{ fontSize: '10px', color: '#ef4444' }}>{t('stop_loss')}</div>
              <div style={{ fontSize: '13px', fontWeight: 600, color: '#ef4444' }}>${pos.sl.toLocaleString()} ({pos.slPercent}%)</div>
            </div>
            <div style={{ padding: '8px', background: '#0a2e1a', borderRadius: '6px', border: '1px solid #22c55e33' }}>
              <div style={{ fontSize: '10px', color: '#22c55e' }}>{t('take_profit')}</div>
              <div style={{ fontSize: '13px', fontWeight: 600, color: '#22c55e' }}>${pos.tp.toLocaleString()} (+{pos.tpPercent}%)</div>
            </div>
          </div>

          <div style={{ marginTop: '12px', display: 'flex', gap: '8px' }}>
            <div style={{ flex: 1, padding: '6px 8px', background: '#1a1a1a', borderRadius: '4px', fontSize: '11px' }}>
              <span style={{ color: '#666' }}>{t('echelon_1')}: </span>
              <span style={{ color: '#22c55e', fontWeight: 600 }}>{pos.echelons.adaptive}</span>
            </div>
            <div style={{ flex: 1, padding: '6px 8px', background: '#1a1a1a', borderRadius: '4px', fontSize: '11px' }}>
              <span style={{ color: '#666' }}>{t('echelon_2')}: </span>
              <span style={{ color: pos.echelons.validator > 0.6 ? '#22c55e' : '#f59e0b', fontWeight: 600 }}>{pos.echelons.validator}</span>
            </div>
            <div style={{ flex: 1, padding: '6px 8px', background: '#1a1a1a', borderRadius: '4px', fontSize: '11px' }}>
              <span style={{ color: '#666' }}>{t('echelon_3')}: </span>
              <span style={{ color: '#22c55e', fontWeight: 600 }}>{pos.echelons.newsSentinel}</span>
            </div>
          </div>

          <div style={{ marginTop: '12px', display: 'flex', gap: '8px', justifyContent: 'flex-end' }}>
            <button className="btn btn-outline btn-sm">{t('modify')}</button>
            <button className="btn btn-danger btn-sm">{t('close_position')}</button>
          </div>
        </div>
      ))}

      {data.positions.length === 0 && (
        <div className="card" style={{ textAlign: 'center', padding: '40px', color: '#666' }}>
          {t('no_open_positions')}
        </div>
      )}
    </div>
  );
}
