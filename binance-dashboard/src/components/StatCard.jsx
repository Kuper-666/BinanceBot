export default function StatCard({ label, value, sub, color }) {
  return (
    <div style={{ background: '#141414', borderRadius: '8px', padding: '16px', border: '1px solid #222' }}>
      <div style={{ fontSize: '12px', color: '#888', marginBottom: '4px' }}>{label}</div>
      <div style={{ fontSize: '24px', fontWeight: 700, color: color || '#fff' }}>{value}</div>
      {sub && <div style={{ fontSize: '11px', color: '#666', marginTop: '2px' }}>{sub}</div>}
    </div>
  );
}
