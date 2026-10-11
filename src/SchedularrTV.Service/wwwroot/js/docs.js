// Documentation pages: after moving to another guide, show the heading the link pointed at, or the top.
export function scrollToFragment(id) {
    const target = id ? document.getElementById(id) : null;
    if (target) {
        target.scrollIntoView({ block: "start" });
    } else {
        window.scrollTo({ top: 0 });
    }
}
