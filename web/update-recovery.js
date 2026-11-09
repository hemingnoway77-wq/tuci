const status = document.querySelector('#status');
const retry = document.querySelector('#retry');
function activated(worker) {
  return new Promise((resolve, reject) => {
    const finish = error => {
      clearTimeout(timer);
      worker.removeEventListener('statechange', check);
      error ? reject(error) : resolve();
    };
    const check = () => {
      if (worker.state === 'activated') finish();
      else if (worker.state === 'redundant') finish(new Error('Update did not install'));
    };
    const timer = setTimeout(() => finish(new Error('Update timed out')), 60000);
    worker.addEventListener('statechange', check);
    check();
  });
}
async function update() {
  retry.hidden = true;
  status.textContent = '正在获取最新版本…';
  try {
    if (!('serviceWorker' in navigator)) throw new Error('Offline support unavailable');
    const registration = await navigator.serviceWorker.register('./sw.js', { scope: './', updateViaCache: 'none' });
    let candidate = registration.installing || registration.waiting;
    const remember = () => { candidate = registration.installing || registration.waiting || candidate; };
    registration.addEventListener('updatefound', remember);
    try {
      await registration.update();
      remember();
      const worker = candidate || registration.active;
      if (!worker) throw new Error('No installed version');
      await activated(worker);
    } finally {
      registration.removeEventListener('updatefound', remember);
    }
    location.replace('./');
  } catch {
    status.textContent = '更新未完成，请保持联网后重试。';
    retry.hidden = false;
  }
}
retry.addEventListener('click', update);
update();
