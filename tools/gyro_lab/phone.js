const el = id => document.getElementById(id);
const token = document.querySelector('meta[name="lab-token"]').content;
const page = crypto.randomUUID();
let source = 'DeviceMotion', running = false, sequence = 0, samples = [], error = '', gyro = null;
function record(timestamp, x, y, z) {
  if (!running || ![timestamp,x,y,z].every(Number.isFinite)) return;
  if(error){error='';el('error').textContent='';}
  samples.push([++sequence, timestamp, x, y, z]);
  if (samples.length > 240) samples.shift();
  for (const [id,value] of [['x',x],['y',y],['z',z]]) el(id).textContent = (value >= 0 ? '+' : '') + value.toFixed(3);
  el('count').textContent = sequence;
  const window = samples.slice(-60), seconds = (window.at(-1)[1] - window[0][1]) / 1000;
  el('frequency').textContent = seconds > 0 ? `${((window.length-1)/seconds).toFixed(1)} Hz` : '采样中';
  el('status').textContent = '正在读取';
  draw();
}
function motion(event) {
  const rate = event.rotationRate;
  if (!rate || [rate.beta,rate.gamma,rate.alpha].some(x => typeof x !== 'number')) return;
  const scale = Math.PI / 180;
  // Android Chromium forwards native gyro x/y/z as alpha/beta/gamma.
  record(performance.timeOrigin + event.timeStamp, rate.alpha * scale, rate.beta * scale, rate.gamma * scale);
}
function draw() {
  const canvas = el('plot'), width = canvas.getBoundingClientRect().width, height = 240, ratio = devicePixelRatio || 1;
  canvas.width = Math.round(width*ratio); canvas.height = height*ratio;
  const ctx = canvas.getContext('2d'); ctx.scale(ratio,ratio); ctx.font = '11px sans-serif';
  for (let i=-2;i<=2;i++) {
    const y=15+(2-i)/4*205;
    ctx.strokeStyle='#303638';ctx.beginPath();ctx.moveTo(32,y);ctx.lineTo(width-8,y);ctx.stroke();
    ctx.fillStyle='#969fa4';ctx.fillText(i.toFixed(0),8,y+4);
  }
  if (!samples.length) return;
  const end=samples.at(-1)[1], start=end-4000;
  for (const [index,color] of [[2,'#76d7c4'],[3,'#efb96b'],[4,'#b79ee9']]) {
    let previous=null;ctx.strokeStyle=color;ctx.beginPath();
    for (const sample of samples) {
      if(sample[1]<start)continue;
      const x=32+(sample[1]-start)/4000*(width-40), y=15+(2-sample[index])/4*205;
      if(previous && sample[1]-previous[1]<100)ctx.lineTo(x,y);else ctx.moveTo(x,y);
      previous=sample;
    }
    ctx.stroke();
  }
}
async function start() {
  try {
    if (!isSecureContext) throw new Error('需要从手机本机 localhost 安全上下文打开');
    if (typeof window.DeviceMotionEvent?.requestPermission === 'function' &&
        await DeviceMotionEvent.requestPermission() !== 'granted') throw new Error('未获得运动传感器权限');
    error='';running=true;
    window.addEventListener('devicemotion',motion);
    if (!('DeviceMotionEvent' in window) && 'Gyroscope' in window) {
      source='Gyroscope';gyro=new Gyroscope({frequency:60});
      gyro.addEventListener('reading',()=>record(performance.timeOrigin+gyro.timestamp,gyro.x,gyro.y,gyro.z));
      gyro.addEventListener('error',event=>{error=event.error.message;el('error').textContent=error;});
      gyro.start();
    }
    el('source').textContent=`${source} · 安全上下文`;
    el('start').disabled=true;el('stop').disabled=false;el('status').textContent='等待真实事件';el('error').textContent='';
    setTimeout(()=>{if(running && !samples.length){error='未收到陀螺仪事件，请检查浏览器运动传感器权限';el('error').textContent=error;}},4000);
  } catch(cause) {error=cause.message;el('error').textContent=error;}
}
function stop() {
  running=false;window.removeEventListener('devicemotion',motion);gyro?.stop();gyro=null;
  el('start').disabled=false;el('stop').disabled=true;el('status').textContent='读取已停止';
}
el('start').onclick=start;el('stop').onclick=stop;
document.addEventListener('visibilitychange',()=>{el('visibility').textContent=document.hidden?'后台':'前台';});
window.addEventListener('resize',draw);
async function upload() {
  try {
    const response=await fetch('/api/browser',{method:'POST',headers:{'Content-Type':'application/json','X-Lab-Token':token},
      body:JSON.stringify({page,source,layout:'android-xyz-v2',secure:isSecureContext,visible:!document.hidden,running,error,samples})});
    if(!response.ok)throw new Error(`HTTP ${response.status}`);
    const state=await response.json();el('phase').textContent=state.phase;el('upload').textContent='已回传';
  } catch(cause) {el('upload').textContent=cause.message;}
  setTimeout(upload,250);
}
draw();upload();
if (typeof DeviceMotionEvent !== 'undefined' && typeof DeviceMotionEvent.requestPermission !== 'function') start();
