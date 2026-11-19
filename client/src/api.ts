// HTTP-клієнт REST API сервера з JWT-токеном.

const TOKEN_KEY = 'smartpark.token';

export function getToken(): string | null {
  try {
    return localStorage.getItem(TOKEN_KEY);
  } catch {
    return null;
  }
}

export function setToken(token: string | null) {
  try {
    if (token) localStorage.setItem(TOKEN_KEY, token);
    else localStorage.removeItem(TOKEN_KEY);
  } catch {
    /* сховище недоступне – токен живе лише до перезавантаження */
  }
}

export async function api<T = unknown>(path: string, options: RequestInit = {}): Promise<T> {
  const headers = new Headers(options.headers);
  if (options.body && !headers.has('Content-Type')) headers.set('Content-Type', 'application/json');
  const token = getToken();
  if (token) headers.set('Authorization', `Bearer ${token}`);

  const response = await fetch(path, { ...options, headers });
  if (response.status === 401 && token) setToken(null);
  if (!response.ok) {
    let message = `Помилка ${response.status}`;
    try {
      const body = await response.json();
      message = body.error ?? body.title ?? message;
    } catch {
      /* тіло без JSON */
    }
    throw new Error(message);
  }
  if (response.status === 204) return undefined as T;
  return (await response.json()) as T;
}

export const post = <T = unknown>(path: string, body?: unknown) =>
  api<T>(path, { method: 'POST', body: body === undefined ? undefined : JSON.stringify(body) });

export const put = <T = unknown>(path: string, body?: unknown) =>
  api<T>(path, { method: 'PUT', body: JSON.stringify(body) });

export const del = (path: string) => api(path, { method: 'DELETE' });

export function formatDate(value?: string | null) {
  return value ? new Date(value).toLocaleString('uk-UA') : '—';
}

export function money(value?: number | null) {
  return `${(value ?? 0).toFixed(2)} грн`;
}

// ---------- Типи DTO ----------
export interface Occupancy { total: number; free: number; occupied: number; reserved: number; unavailable: number }
export interface ParkingSummary {
  id: string; name: string; city: string; address: string; latitude: number; longitude: number;
  openingHours: string; status: string; occupancy: Occupancy;
}
export interface Spot { id: string; code: string; type: string; status: string; statusReason?: string }
export interface Level { id: string; name: string; number: number; spots: Spot[] }
export interface DeviceInfo { id: string; code: string; type: string; status: string; lastHeartbeat?: string }
export interface ParkingDetails extends ParkingSummary { levels: Level[]; devices: DeviceInfo[] }
export interface Vehicle { id: string; plate: string; brand: string; isElectric: boolean }
export interface TariffVersion {
  id: string; validFrom: string; dayRate: number; nightRate: number; nightStartHour: number; nightEndHour: number;
  freeMinutes: number; dailyMax: number; subscriptionMonthPrice: number; createdBy: string;
}
export interface Tariff { id: string; parkingId: string; name: string; isArchived: boolean; versions: TariffVersion[] }
export interface NotificationItem { id: string; title: string; text: string; isRead?: boolean; createdAt: string }
