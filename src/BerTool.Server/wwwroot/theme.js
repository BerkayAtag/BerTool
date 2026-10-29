/* BerTool tema yöneticisi: varsayılan kırmızı-beyaz, isteğe bağlı koyu tema. */
(function () {
    'use strict';

    const storageKey = 'bertool.theme';
    const root = document.documentElement;
    let currentTheme = 'light';

    try {
        const saved = window.localStorage.getItem(storageKey);
        if (saved === 'dark' || saved === 'light') {
            currentTheme = saved;
        }
    } catch {
        currentTheme = 'light';
    }

    function applyTheme(theme, persist) {
        currentTheme = theme === 'dark' ? 'dark' : 'light';
        root.setAttribute('data-theme', currentTheme);
        root.style.colorScheme = currentTheme;

        const colorSchemeMeta = document.querySelector('meta[name="color-scheme"]');
        if (colorSchemeMeta) {
            colorSchemeMeta.setAttribute('content', currentTheme);
        }

        const themeColorMeta = document.querySelector('meta[name="theme-color"]');
        if (themeColorMeta) {
            themeColorMeta.setAttribute('content', currentTheme === 'dark' ? '#7d050e' : '#e30613');
        }

        if (persist) {
            try {
                window.localStorage.setItem(storageKey, currentTheme);
            } catch {
                // Depolama kapalıysa tema yalnızca bu oturum için geçerli olur.
            }
        }

        updateButtons();
        window.dispatchEvent(new CustomEvent('bertool:themechange', { detail: currentTheme }));
    }

    function updateButtons() {
        const isDark = currentTheme === 'dark';
        document.querySelectorAll('[data-theme-toggle]').forEach(button => {
            button.setAttribute('aria-pressed', String(isDark));
            button.title = isDark ? 'Aydınlık temaya geç' : 'Karanlık temaya geç';

            const icon = button.querySelector('[data-theme-icon]');
            if (icon) {
                icon.textContent = isDark ? '☀' : '☾';
            }

            const label = button.querySelector('[data-theme-label]');
            if (label) {
                label.textContent = isDark ? 'Aydınlık tema' : 'Karanlık tema';
            }
        });
    }

    window.BerToolTheme = {
        get: function () {
            return currentTheme;
        },
        set: function (theme) {
            applyTheme(theme, true);
        },
        toggle: function () {
            applyTheme(currentTheme === 'dark' ? 'light' : 'dark', true);
        }
    };

    applyTheme(currentTheme, false);

    document.addEventListener('DOMContentLoaded', function () {
        document.querySelectorAll('[data-theme-toggle]').forEach(button => {
            button.addEventListener('click', function () {
                window.BerToolTheme.toggle();
            });
        });
        updateButtons();
    });
})();
