// Exercise the real page's touch handlers without a phone or browser runtime.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

async function main() {
  const handlers = new Map();
  let startClick, start = {addEventListener: (_, handler) => startClick = handler, remove: () => start = null};
  const context = vm.createContext({
    innerWidth: 1000, innerHeight: 600, devicePixelRatio: 1,
    performance: {now: () => 10}, navigator: {},
    screen: {orientation: {lock: async () => {}}},
    document: {visibilityState: 'visible', documentElement: {requestFullscreen: async () => {}},
      querySelector: selector => selector === 'canvas' ? {getContext: () => ({setTransform() {}})} :
        selector === '#start' ? start : {}},
    addEventListener: (name, handler) => handlers.set(name, handler),
    setInterval() {}, requestAnimationFrame() {},
    AbortSignal: {timeout() {}},
    fetch: async () => ({json: async () => ({generation: 0, run: 'test'})})
  });
  const page = fs.readFileSync(path.join(__dirname, 'index.html'), 'utf8');
  vm.runInContext(page.match(/<script>([\s\S]*?)<\/script>/)[1], context);
  await new Promise(resolve => setImmediate(resolve)); // Complete the initial reset.
  await startClick();
  const point = (identifier, clientX, clientY) => ({identifier, clientX, clientY});
  const fire = (type, touches, changedTouches = []) => handlers.get(type)({type, touches, changedTouches, preventDefault() {}});
  const state = () => JSON.parse(vm.runInContext('JSON.stringify({dxTotal,dyTotal,n:touches.size})', context));
  let a = point(1, 700, 300), b = point(2, 800, 350), move = point(3, 100, 500);
  fire('touchstart', [a], [a]);
  fire('touchstart', [a, b], [b]);
  fire('touchstart', [a, b, move], [move]);
  assert.deepEqual(state(), {dxTotal: 0, dyTotal: 0, n: 3});
  a = point(1, 706, 298); b = point(2, 804, 353); move = point(3, 200, 400);
  fire('touchmove', [a, b, move]);
  assert.deepEqual(state(), {dxTotal: 10, dyTotal: 1, n: 3}); // Both look fingers, no move finger.
  fire('touchmove', [a, b, move]);
  assert.equal(state().dxTotal, 10); // Restated held positions are idempotent.
  a = point(1, 707, 298); b = point(2, 806, 353);
  fire('touchend', [b, move], [a]);
  assert.deepEqual(state(), {dxTotal: 13, dyTotal: 1, n: 2}); // Flush both endpoints, no anchor jump.
  b = point(2, 801, 350);
  fire('touchmove', [b, move]);
  assert.deepEqual(state(), {dxTotal: 8, dyTotal: -2, n: 2});
  const c = point(4, 900, 250);
  fire('touchstart', [b, c, move], [c]);
  assert.equal(state().dxTotal, 8);
  b = point(2, 798, 352);
  const reversed = point(4, 896, 249);
  fire('touchmove', [b, reversed, move]);
  assert.deepEqual(state(), {dxTotal: 1, dyTotal: -1, n: 3});
  fire('touchcancel', [], [b, reversed, move]);
  assert.deepEqual(state(), {dxTotal: 1, dyTotal: -1, n: 0});
  console.log('PASS browser lab touch handlers: additive overlap, independent DOWN baselines, move-button exclusion, endpoint flush, reversal and cancel');
}
main().catch(error => { console.error(error); process.exitCode = 1; });
