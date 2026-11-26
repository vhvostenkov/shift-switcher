'use strict';
// Keep the public URL and filename stable, while fetching fresh bytes on each click.
for (const link of document.querySelectorAll('a[download]')) {
  link.addEventListener('click', () => {
    const url = new URL(link.href, location.href);
    if (url.origin !== location.origin || !url.pathname.startsWith('/shift-switcher/downloads/')) return;
    url.searchParams.set('download', Date.now().toString());
    link.href = url.href;
  });
}
