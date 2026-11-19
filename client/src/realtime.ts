import { HubConnection, HubConnectionBuilder, LogLevel } from '@microsoft/signalr';
import { useEffect, useRef } from 'react';
import { getToken } from './api';

// Одне з’єднання SignalR на вкладку; перепідключається автоматично.
let connection: HubConnection | null = null;
let started: Promise<void> | null = null;

export function getConnection(): { connection: HubConnection; ready: Promise<void> } {
  if (!connection) {
    connection = new HubConnectionBuilder()
      .withUrl('/hubs/events', { accessTokenFactory: () => getToken() ?? '' })
      .withAutomaticReconnect()
      .configureLogging(LogLevel.Warning)
      .build();
    started = connection.start().catch((e) => console.warn('SignalR', e));
  }
  return { connection, ready: started! };
}

/** Перепідключення після входу/виходу, щоб сервер додав з’єднання до груп користувача. */
export async function resetConnection() {
  if (connection) await connection.stop();
  connection = null;
  started = null;
}

/** Підписка на повідомлення хаба на час життя компонента. */
export function useHubEvent<T>(event: string, handler: (data: T) => void) {
  const ref = useRef(handler);
  ref.current = handler;
  useEffect(() => {
    const { connection } = getConnection();
    const listener = (data: T) => ref.current(data);
    connection.on(event, listener);
    return () => connection.off(event, listener);
  }, [event]);
}

/** Спостереження за схемою конкретного паркінгу. */
export function useWatchParking(parkingId?: string) {
  useEffect(() => {
    if (!parkingId) return;
    const { connection, ready } = getConnection();
    ready.then(() => connection.invoke('WatchParking', parkingId)).catch(() => undefined);
    return () => {
      connection.invoke('UnwatchParking', parkingId).catch(() => undefined);
    };
  }, [parkingId]);
}
