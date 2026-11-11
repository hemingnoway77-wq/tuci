// Both edges move from their current positions. The leading edge starts first.
export function createTabIndicator(track, { vertical = () => false } = {}) {
  const line = track.ownerDocument.createElement('span');
  line.className = 'tab-motion';
  line.setAttribute('aria-hidden', 'true');
  line.hidden = true;
  track.append(line);
  const view = track.ownerDocument.defaultView;
  let active = null, previous = null;

  function select(tab, animate = true) {
    active = tab;
    const axis = vertical() ? 'vertical' : 'horizontal';
    if (track.getAttribute('role') === 'tablist') track.setAttribute('aria-orientation', axis);
    if (!tab) {
      line.hidden = true;
      previous = null;
      return;
    }
    const parent = track.getBoundingClientRect(), target = tab.getBoundingClientRect();
    const origin = axis === 'vertical' ? 'top' : 'left';
    const extent = axis === 'vertical' ? 'height' : 'width';
    const span = parent[extent];
    const center = target[origin] - parent[origin] + target[extent] / 2;
    if (!span || !target[extent] || !Number.isFinite(center)) return;
    const moving = animate && previous?.axis === axis && Math.abs(center - previous.center) > .5;
    line.dataset.axis = axis;
    line.dataset.motion = moving ? (center > previous.center ? 'forward' : 'backward') : 'still';
    line.style.setProperty('--tab-start', `${center - 11}px`);
    line.style.setProperty('--tab-end', `${span - center - 11}px`);
    line.hidden = false;
    previous = { axis, center };
  }

  const realign = () => select(active, false);
  view?.addEventListener('resize', realign, { passive: true });
  return {
    select,
    dispose() {
      view?.removeEventListener('resize', realign);
      line.remove();
    }
  };
}
