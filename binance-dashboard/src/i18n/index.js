import i18n from 'i18next';
import { initReactI18next } from 'react-i18next';
import en from './en.json';
import ar from './ar.json';
import es from './es.json';
import zh from './zh.json';
import ru from './ru.json';
import fr from './fr.json';

i18n.use(initReactI18next).init({
  resources: { en: { translation: en }, ar: { translation: ar }, es: { translation: es }, zh: { translation: zh }, ru: { translation: ru }, fr: { translation: fr } },
  lng: localStorage.getItem('lang') || 'en',
  fallbackLng: 'en',
  interpolation: { escapeValue: false }
});

export default i18n;
