/** @type {import('tailwindcss').Config} */
module.exports = {
    content: ["./src/**/*.{js,jsx,ts,tsx,md,mdx}", "./docs/**/*.{md,mdx}", "./blog/**/*.{md,mdx}"],
    theme: { extend: {} },
    plugins: [],
    darkMode: ["class", '[data-theme="dark"]'], // Support dark mode
    // NOTE: Tailwind v4's JS-config compatibility layer (loaded via the
    // `@config` at-rule in src/css/custom.css) does not read `corePlugins`
    // at all — it was a v3-only mechanism and has no code path in v4
    // (verified empirically: zero references to "corePlugins" anywhere in
    // the installed tailwindcss/@tailwindcss packages). Do not reintroduce
    // a `corePlugins: { preflight: false, container: false }` block here —
    // it would be silently ignored.
    //   - Preflight is safely never applied because custom.css only
    //     imports 'tailwindcss/theme' and 'tailwindcss/utilities' — it
    //     never imports 'tailwindcss/preflight' in the first place.
    //   - The `.container` utility collides with Infima's own `.container`
    //     (see custom.css), so it's suppressed the v4-native way: via
    //     `blocklist`, which IS still honored by the config compat layer
    //     (it feeds Tailwind's `invalidCandidates` set) and prevents the
    //     utility from being generated even when "container" appears as a
    //     class name in scanned content.
    blocklist: ['container'],
}
