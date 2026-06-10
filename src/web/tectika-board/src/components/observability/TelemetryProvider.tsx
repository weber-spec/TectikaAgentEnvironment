'use client';

import { useEffect } from 'react';
import { initTelemetry } from '@/lib/telemetry';

export function TelemetryProvider({
  connectionString,
  logSensitiveContent,
  children,
}: {
  connectionString: string;
  logSensitiveContent: boolean;
  children: React.ReactNode;
}) {
  useEffect(() => {
    initTelemetry(connectionString, logSensitiveContent);
  }, [connectionString, logSensitiveContent]);

  return <>{children}</>;
}
