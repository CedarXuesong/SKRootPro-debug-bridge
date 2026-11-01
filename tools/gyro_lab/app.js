const token = document.querySelector('meta[name="lab-token"]').content;
const el = id => document.getElementById(id);
let latest = null;
let closed = false;
let starting = false, revision = 0;
async function api(path, body) {
  const response = await fetch(path, {method: body ? 'POST' : 'GET', headers: {'X-Lab-Token': token, 'Content-Type': 'application/json'}, ...(body ? {body: JSON.stringify(body)} : {})});
  const result = await response.json();
  if (!response.ok) throw new Error(result.error || `HTTP ${response.status}`);
  return result;
}
function render(data) {
  latest = data;
  el('connection').textContent = data.connected ? '已连接' : '设备离线 · 自动重连';
  el('connection').classList.toggle('fail', !data.connected);
  el('device').textContent = `${data.host} · ${data.version}`;
  el('uid').textContent = '整机 HAL';
  el('hits').textContent = data.result.hits ?? '—';
  el('updates').textContent = data.result.updates ?? '—';
  el('phase').textContent = data.connected ? data.result.phase : '无法连接手机，请确认模块桥接服务正在运行';
  el('backend').textContent = 'vendor HAL · DSP Direct Channel 尚未覆盖';
  const phone = data.browser;
  const phoneReady = phone?.secure && phone.visible && phone.running && Date.now()/1000-phone.received < 2 && !phone.error;
  el('phone-state').textContent = phoneReady ? `手机前台 · ${phone.source} · 实际事件回传中` : (phone?.error || '手机网页未连接或未持续读取传感器');
  el('count').textContent = data.result.samples.length;
  el('run').disabled = !data.connected || data.result.running ||
    (document.querySelector('input[name="mode"]:checked').value === 'browser' && !phoneReady);
  el('stop').disabled = !data.result.running;
  for (const input of document.querySelectorAll('input[name="mode"]')) input.disabled = data.result.running;
  el('log').textContent = [...data.result.events, ...(data.error ? [data.error] : [])].join('\n') || '等待开始';
  const rows = data.result.checks.map(check => {
    const row = document.createElement('tr');
    for (const text of [check.name, check.target, check.control, check.passed ? '通过' : '失败']) {
      const cell = document.createElement('td'); cell.textContent = text; row.append(cell);
    }
    row.lastChild.className = check.passed ? 'pass' : 'fail';
    return row;
  });
  if (rows.length) el('checks').replaceChildren(...rows);
  else {
    const row = document.createElement('tr'), cell = document.createElement('td');
    cell.colSpan = 4; cell.className = 'empty'; cell.textContent = '尚无测试结果'; row.append(cell);
    el('checks').replaceChildren(row);
  }
  draw();
}
function draw() {
  const canvas = el('plot'), box = canvas.getBoundingClientRect(), ratio = devicePixelRatio || 1;
  canvas.width = Math.round(box.width * ratio); canvas.height = Math.round(320 * ratio);
  const ctx = canvas.getContext('2d'); ctx.scale(ratio, ratio);
  const width = box.width, height = 320, left = 48, top = 18, bottom = 30;
  ctx.font = '11px Segoe UI'; ctx.lineWidth = 1;
  for (let i = -2; i <= 2; i++) {
    const y = top + (2 - i) / 4 * (height - top - bottom);
    ctx.strokeStyle = '#2b3032'; ctx.beginPath(); ctx.moveTo(left, y); ctx.lineTo(width - 16, y); ctx.stroke();
    ctx.fillStyle = '#a6adb1'; ctx.fillText(i.toFixed(1), 10, y + 4);
  }
  const samples = latest?.result.samples || [];
  if (!samples.length) {ctx.fillStyle = '#8c9498'; ctx.fillText('等待实际传感器样本', left + 15, height / 2); return;}
  const end = Math.max(samples.at(-1).time, 1);
  for (const [key, color] of [['requested', '#e9b563'], ['target', '#76d7c4'], ['control', '#ad97e6']]) {
    ctx.beginPath(); ctx.strokeStyle = color; let previous = null;
    for (const sample of samples) {
      if (!Number.isFinite(sample[key])) continue;
      const x = left + sample.time / end * (width - left - 16);
      const y = top + (2 - sample[key]) / 4 * (height - top - bottom);
      if (previous && sample.time - previous.time <= .1 && sample.axis === previous.axis) ctx.lineTo(x, y);
      else ctx.moveTo(x, y);
      previous = sample;
    }
    ctx.stroke();
  }
  ctx.fillStyle = '#a6adb1'; ctx.fillText('0 s', left, height - 8); ctx.fillText(`${end.toFixed(1)} s`, width - 55, height - 8);
}
el('run').onclick = async () => {
  starting = true; revision++; el('run').disabled = true; el('phase').textContent = '正在启动测试';
  try {
    await api('/api/run', {mode: document.querySelector('input[name="mode"]:checked').value});
    starting = false; revision++; render(await api('/api/status'));
  } catch (error) {starting = false; el('phase').textContent = error.message;}
};
el('stop').onclick = async () => {try {await api('/api/stop', {});} catch (error) {el('phase').textContent = error.message;}};
el('shutdown').onclick = async () => {
  try {
    await api('/api/shutdown', {});
    closed = true; el('connection').textContent = '正在关闭服务';
    el('phase').textContent = '已请求停止测试并恢复';
    for (const button of document.querySelectorAll('button')) button.disabled = true;
  } catch (error) {el('phase').textContent = error.message;}
};
window.addEventListener('resize', draw);
for (const input of document.querySelectorAll('input[name="mode"]')) input.onchange = () => {if(latest)render(latest);};
async function poll() {
  if (closed) return;
  const observed = revision;
  try {const data = await api('/api/status'); if (!starting && observed === revision) render(data);} catch (error) {el('connection').textContent = '连接失败'; el('phase').textContent = error.message;}
  if (!closed) setTimeout(poll, latest?.connected ? 750 : 5000);
}
poll();
