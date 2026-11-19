import { createContext, ReactNode, useContext, useEffect, useState } from 'react';
import { api, getToken, post, setToken } from './api';

export interface User { id: string; email: string; fullName: string; roles: string[]; hasPaymentMethod?: boolean }

interface AuthState {
  user: User | null;
  loading: boolean;
  login: (email: string, password: string) => Promise<void>;
  register: (email: string, password: string, fullName: string) => Promise<void>;
  logout: () => void;
  refresh: () => Promise<void>;
  hasRole: (...roles: string[]) => boolean;
}

export const STAFF_ROLES = ['Operator', 'ParkingAdmin', 'Technician', 'TariffManager', 'Finance', 'SysAdmin'];

const AuthContext = createContext<AuthState>(null!);

export function AuthProvider({ children }: { children: ReactNode }) {
  const [user, setUser] = useState<User | null>(null);
  const [loading, setLoading] = useState(true);

  const refresh = async () => {
    if (!getToken()) {
      setUser(null);
      return;
    }
    try {
      setUser(await api<User>('/api/auth/me'));
    } catch {
      setToken(null);
      setUser(null);
    }
  };

  useEffect(() => {
    refresh().finally(() => setLoading(false));
  }, []);

  const handle = async (promise: Promise<{ token: string }>) => {
    const { token } = await promise;
    setToken(token);
    await refresh();
  };

  const value: AuthState = {
    user,
    loading,
    login: (email, password) => handle(post('/api/auth/login', { email, password })),
    register: (email, password, fullName) => handle(post('/api/auth/register', { email, password, fullName })),
    logout: () => {
      setToken(null);
      setUser(null);
    },
    refresh,
    hasRole: (...roles) => !!user && roles.some((r) => user.roles.includes(r)),
  };

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

export const useAuth = () => useContext(AuthContext);
