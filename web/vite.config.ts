import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

export default defineConfig({
  plugins: [react()],
  // GitHub Pages uses the repository subpath; Docker serves the same app at /.
  base: process.env.VITE_BASE_PATH ?? "/hesabdari-perfect-/",
});
