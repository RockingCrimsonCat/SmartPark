import { FormEvent, useState } from 'react';
import { useAuth } from '../auth';

/** Вхід і реєстрація (сценарій UC01). */
export default function LoginPage() {
  const { login, register } = useAuth();
  const [mode, setMode] = useState<'login' | 'register'>('login');
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [fullName, setFullName] = useState('');
  const [error, setError] = useState('');

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    setError('');
    try {
      if (mode === 'login') await login(email, password);
      else await register(email, password, fullName);
    } catch (err) {
      setError((err as Error).message);
    }
  };

  return (
    <form className="card narrow" onSubmit={submit}>
      <h2>{mode === 'login' ? 'Вхід' : 'Реєстрація водія'}</h2>
      {mode === 'register' && (
        <label>ПІБ<input value={fullName} onChange={(e) => setFullName(e.target.value)} required /></label>
      )}
      <label>E-mail<input type="email" value={email} onChange={(e) => setEmail(e.target.value)} required /></label>
      <label>Пароль<input type="password" value={password} onChange={(e) => setPassword(e.target.value)} required minLength={8} /></label>
      {error && <p className="error">{error}</p>}
      <button type="submit">{mode === 'login' ? 'Увійти' : 'Зареєструватися'}</button>
      <button type="button" className="link" onClick={() => setMode(mode === 'login' ? 'register' : 'login')}>
        {mode === 'login' ? 'Немає облікового запису? Зареєструватися' : 'Вже є обліковий запис? Увійти'}
      </button>
    </form>
  );
}
