// App Insights browser singleton. Initialized once from a runtime-injected connection
// string (see TelemetryProvider). No-ops cleanly when no connection string is present
// (local dev), so call sites never need to guard.
'use client';

import { ApplicationInsights } from '@microsoft/applicationinsights-web';

let ai: ApplicationInsights | null = null;
let logSensitive = true;

export function initTelemetry(connectionString: string, sensitive: boolean): ApplicationInsights | null {
  if (ai || !connectionString) return ai;
  logSensitive = sensitive;
  ai = new ApplicationInsights({
    config: {
      connectionString,
      enableAutoRouteTracking: true,          // SPA route changes -> page views
      enableCorsCorrelation: true,            // correlate fetch() to the API
      enableRequestHeaderTracking: true,
      enableResponseHeaderTracking: true,
      disableFetchTracking: false,            // auto dependency tracking for fetch
      enableUnhandledPromiseRejectionTracking: true,
      autoTrackPageVisitTime: true,
    },
  });
  ai.loadAppInsights();
  ai.trackPageView();
  return ai;
}

/** Redact a value for logging unless sensitive logging is enabled. */
export function redact(value: string | undefined | null): string {
  if (logSensitive) return value ?? '';
  if (!value) return '[redacted]';
  return `[redacted](${value.length} chars)`;
}

export function trackEvent(name: string, properties?: Record<string, unknown>): void {
  ai?.trackEvent({ name }, properties as Record<string, string>);
}

export function trackTrace(message: string, properties?: Record<string, unknown>): void {
  ai?.trackTrace({ message }, properties as Record<string, string>);
}

export function trackException(error: unknown, properties?: Record<string, unknown>): void {
  ai?.trackException(
    { exception: error instanceof Error ? error : new Error(String(error)) },
    properties as Record<string, string>,
  );
}
