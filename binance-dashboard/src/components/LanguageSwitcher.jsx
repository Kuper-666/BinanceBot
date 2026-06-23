import { useTranslation } from 'react-i18next';

const LANGUAGES = [
  { code: 'en', label: 'English', flag: '\u{1F1EC}\u{1F1E7}' },
  { code: 'ar', label: '\u0627\u0644\u0639\u0631\u0628\u064A\u0629', flag: '\u{1F1F8}\u{1F1E6}' },
  { code: 'es', label: 'Espa\u00F1ol', flag: '\u{1F1EA}\u{1F1F8}' },
  { code: 'zh', label: '\u4E2D\u6587', flag: '\u{1F1E8}\u{1F1F3}' },
  { code: 'ru', label: '\u0420\u0443\u0441\u0441\u043A\u0438\u0439', flag: '\u{1F1F7}\u{1F1FA}' },
  { code: 'fr', label: 'Fran\u00E7ais', flag: '\u{1F1EB}\u{1F1F7}' },
];

export default function LanguageSwitcher() {
  const { i18n } = useTranslation();

  const changeLang = (code) => {
    i18n.changeLanguage(code);
    localStorage.setItem('lang', code);
  };

  return (
    <div style={{ display: 'flex', gap: '8px', alignItems: 'center' }}>
      {LANGUAGES.map(l => (
        <button key={l.code} onClick={() => changeLang(l.code)}
          style={{ padding: '4px 8px', borderRadius: '4px', border: i18n.language === l.code ? '1px solid #22c55e' : '1px solid #333',
            background: i18n.language === l.code ? '#0a2e1a' : 'transparent', color: i18n.language === l.code ? '#22c55e' : '#888',
            cursor: 'pointer', fontSize: '13px', transition: 'all 0.2s' }}>
          {l.flag} {l.label}
        </button>
      ))}
    </div>
  );
}
