/**
 * Regenerate wwwroot/assets/css/app.css with:
 *   npx --yes --package tailwindcss@3.4.17 --package @tailwindcss/forms@0.5.10 tailwindcss -c tailwind.config.js -i ui/tailwind-input.css -o wwwroot/assets/css/app.css --minify
 *
 * The generated CSS is committed so normal Visual Studio/MSBuild builds do not require Node.js.
 */
const defaultTheme = require("tailwindcss/defaultTheme");

module.exports = {
  content: ["./wwwroot/**/*.html"],
  darkMode: "class",
  theme: {
    extend: {
      fontFamily: {
        mono: ["JetBrains Mono", ...defaultTheme.fontFamily.mono]
      },
      colors: {
        "surface-variant": "#2d3449",
        "on-surface": "#dae2fd",
        primary: "#c3f5ff",
        "primary-fixed": "#9cf0ff",
        background: "#0b1326",
        "surface-container-low": "#131b2e",
        "surface-container": "#171f33",
        "surface-container-high": "#222a3d",
        "primary-container": "#00e5ff",
        "on-surface-variant": "#bac9cc",
        "surface-container-lowest": "#060e20",
        "surface-container-highest": "#2d3449",
        surface: "#0b1326",
        "on-primary": "#00363d",
        secondary: "#4edea3",
        "on-secondary": "#003824",
        "surface-bright": "#31394d",
        error: "#ff5449",
        "on-error": "#690005",
        "error-container": "#93000a",
        "on-error-container": "#ffb4ab"
      }
    }
  },
  plugins: [require("@tailwindcss/forms")]
};
