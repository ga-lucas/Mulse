(() => {
    const storageKey = 'mulse.theme';
    const systemTheme = window.matchMedia('(prefers-color-scheme: dark)');
    let preference = null;

    try {
        preference = window.localStorage.getItem(storageKey);
    } catch (error) {
        console.warn('Mulse could not read the saved appearance preference.', error);
    }

    if (preference !== 'light' && preference !== 'dark') {
        preference = null;
    }

    function applyTheme() {
        const theme = preference ?? (systemTheme.matches ? 'dark' : 'light');
        document.documentElement.dataset.theme = theme;
        document.querySelectorAll('[data-theme-toggle]').forEach(button => {
            button.setAttribute('aria-pressed', String(theme === 'dark'));
        });
    }

    window.mulseTheme = { refresh: applyTheme };
    applyTheme();
    document.addEventListener('DOMContentLoaded', applyTheme);
    document.addEventListener('enhancedload', applyTheme);
    systemTheme.addEventListener('change', () => {
        if (preference === null) {
            applyTheme();
        }
    });

    document.addEventListener('click', event => {
        if (!(event.target instanceof Element) || !event.target.closest('[data-theme-toggle]')) {
            return;
        }

        preference = document.documentElement.dataset.theme === 'dark' ? 'light' : 'dark';
        applyTheme();
        try {
            window.localStorage.setItem(storageKey, preference);
        } catch (error) {
            console.warn('Mulse could not save the appearance preference. It will apply for this page only.', error);
        }
    });

    window.addEventListener('storage', event => {
        if (event.key === storageKey || event.key === null) {
            preference = event.newValue === 'light' || event.newValue === 'dark' ? event.newValue : null;
            applyTheme();
        }
    });
})();
