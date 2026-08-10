import { useEffect, useState } from 'react';

/**
 * Whether the device currently has a network.
 *
 * Surfaced permanently in the header rather than as a transient toast: an
 * operator needs to know at a glance whether their last scan actually reached
 * the server, and warehouse coverage drops out often enough that a popup would
 * become noise they learn to dismiss.
 */
export function useConnectivity(): boolean {
  const [online, setOnline] = useState(navigator.onLine);

  useEffect(() => {
    const goOnline = () => setOnline(true);
    const goOffline = () => setOnline(false);

    window.addEventListener('online', goOnline);
    window.addEventListener('offline', goOffline);

    return () => {
      window.removeEventListener('online', goOnline);
      window.removeEventListener('offline', goOffline);
    };
  }, []);

  return online;
}
