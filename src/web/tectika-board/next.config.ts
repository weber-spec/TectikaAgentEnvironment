import type { NextConfig } from "next";

// Allow reaching the dev server from non-localhost origins (e.g. the WSL2 VM IP or
// a LAN address) by setting ALLOWED_DEV_ORIGINS="172.21.x.x,192.168.x.x". Without it,
// Next.js dev only trusts localhost.
const allowed = process.env.ALLOWED_DEV_ORIGINS?.split(",").map(s => s.trim()).filter(Boolean);

const nextConfig: NextConfig = {
  // Emit a self-contained server bundle (.next/standalone) for a lean container image.
  output: "standalone",
  ...(allowed?.length ? { allowedDevOrigins: allowed } : {}),
};

export default nextConfig;
