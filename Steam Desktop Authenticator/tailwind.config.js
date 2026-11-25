/**
 * Regenerate ui/assets/css/app.css with:
 *   npm run build:ui (from the repository root)
 *
 * Loaded explicitly by ui/tailwind-input.css through Tailwind v4's @config directive.
 * The generated CSS is committed so normal Visual Studio/MSBuild builds do not require Node.js.
 */
const defaultTheme = require("tailwindcss/defaultTheme");

module.exports = {
  content: ["./ui/**/*.html"],
  darkMode: "class",
  theme: {
    extend: {
      fontFamily: {
        mono: ["JetBrains Mono", ...defaultTheme.fontFamily.mono]
      },
      colors: {
        // Refined v2 Calm Slate-Navy Surfaces
        canvas: "#070b13",
        background: "#0d1320",
        surface: "#0d1320",
        "surface-container-lowest": "#090e19",
        "surface-container-low": "#121a2b",
        "surface-container": "#161f32",
        "surface-container-high": "#1e283e",
        "surface-container-highest": "#27324a",
        "surface-variant": "#27324a",
        "surface-bright": "#2d3a54",

        // Deep Glacier Teal & Soft Glacier Cyan (Interactive / Primary)
        primary: "#85d6e6",
        "primary-fixed": "#4cc3d9",
        "primary-container": "#28829c",
        "primary-deep": "#1e6a80",
        "on-primary": "#f8fafc",
        "on-primary-container": "#f8fafc",

        // Muted Sage Emerald (Healthy / Approve only)
        secondary: "#6ad0a2",
        "secondary-container": "#38b27c",
        "on-secondary": "#052318",

        // Muted Gold (Caution / Unsaved dirty indicators)
        warning: "#e6be73",
        "warning-container": "#d49e3d",

        // Muted Rose-Crimson (Errors / Destructive — calm, zero neon glare)
        error: "#df8a8c",
        "error-strong": "#b34d50",
        "error-container": "#8f3538",
        "on-error": "#fbf6f6",
        "on-error-container": "#f5d0d1",

        // Cool Slate Typography
        "on-surface": "#e4eaf4",
        "on-surface-variant": "#96a4b8",
        "on-surface-subtle": "#64748b"
      }
    }
  },
  plugins: [require("@tailwindcss/forms")]
};
