const reducedMotion = window.matchMedia('(prefers-reduced-motion: reduce)');
const activeAnimations = new Set();
const elementAnimations = new WeakMap();
let revealObserver;

export function motionAllowed() {
  return !reducedMotion.matches && typeof Element.prototype.animate === 'function';
}

function track(animation, cleanup = () => {}) {
  activeAnimations.add(animation);
  const finish = () => { activeAnimations.delete(animation); cleanup(); animation.cancel(); };
  void animation.finished.then(finish, finish);
  return animation;
}

export function reveal(element, { delay = 0, distance = 16, duration = 480 } = {}) {
  if (!element || !motionAllowed()) return;
  elementAnimations.get(element)?.cancel();
  // Animate independent translation so the paper's existing rotation stays intact.
  const from = { opacity: .3 };
  const to = { opacity: 1 };
  if (CSS.supports('translate', '0 1px')) { from.translate = `0 ${distance}px`; to.translate = '0 0'; }
  const animation = track(element.animate([from, to], {
    duration, delay, easing: 'cubic-bezier(.22,1,.36,1)', fill: 'backwards'
  }), () => { if (elementAnimations.get(element) === animation) elementAnimations.delete(element); });
  elementAnimations.set(element, animation);
  return animation;
}

export function fadePreviousPreview(image) {
  if (!motionAllowed() || !image.complete || image.naturalWidth === 0) return () => {};
  const previous = image.cloneNode(false);
  previous.removeAttribute('id'); previous.removeAttribute('fetchpriority');
  previous.alt = ''; previous.setAttribute('aria-hidden', 'true');
  previous.className = 'preview-outgoing';
  image.parentElement.append(previous);
  const animation = track(previous.animate([{ opacity: 1 }, { opacity: 0 }], {
    duration: 190, easing: 'cubic-bezier(.22,1,.36,1)', fill: 'both'
  }), () => previous.remove());
  return () => { previous.remove(); animation.cancel(); };
}

export function animateMenu(navigation, open) {
  if (open) reveal(navigation, { distance: -7, duration: 190 });
}

export function setupMotion() {
  // Content is fully readable without JavaScript or Web Animations. Nothing is
  // hidden in CSS while waiting for an observer, a download, or an animation.
  if (!motionAllowed()) return;
  const intro = document.querySelector('.hero-copy, .page-intro');
  if (intro) {
    const parts = [...intro.children].filter(element => element.getBoundingClientRect().height > 0);
    parts.forEach((element, index) => reveal(element, { delay: Math.min(index, 4) * 55, distance: 14, duration: 540 }));
  }
  reveal(document.querySelector('.hero-visual'), { delay: 110, distance: 20, duration: 640 });

  const targets = document.querySelectorAll('.everyday .section-heading, .habit-flow>a, .story-image, .story-copy, .ownership-copy, .markdown-paper, .download-invitation .container, .features-copy, .features-shot, .features-afterword, .guide-chapter>.eyebrow, .guide-chapter>.section-heading, .guide-package, .guide-closing, .release-card, .download-side, .release-changelog, .verification-note');
  if ('IntersectionObserver' in window) {
    revealObserver = new IntersectionObserver(entries => {
      for (const entry of entries) {
        if (!entry.isIntersecting) continue;
        revealObserver.unobserve(entry.target);
        reveal(entry.target, { delay: Number(entry.target.dataset.revealDelay || 0) });
      }
    }, { threshold: .12, rootMargin: '0px 0px -30px 0px' });
    for (const element of targets) {
      if (element.matches('.habit-flow>a')) element.dataset.revealDelay = String([...element.parentElement.children].indexOf(element) * 60);
      revealObserver.observe(element);
    }
  }
  for (const details of document.querySelectorAll('details')) {
    details.addEventListener('toggle', () => {
      if (details.open) reveal(details.querySelector('.guide-answer'), { distance: 5, duration: 220 });
    });
  }
}

function settleMotion() {
  if (!reducedMotion.matches) return;
  revealObserver?.disconnect();
  for (const animation of activeAnimations) animation.cancel();
  activeAnimations.clear();
}
reducedMotion.addEventListener('change', settleMotion);
window.addEventListener('pagehide', () => {
  revealObserver?.disconnect();
  for (const animation of activeAnimations) animation.cancel();
  activeAnimations.clear();
}, { once: true });
