// OLED burn-in protection (Settings › Display): pixel shift and idle dim, against the web demo with a mocked clock.
const { test, expect } = require('@playwright/test');

async function show(page, display) {
  await page.clock.install();
  await page.goto('/panel/style.css');
  await page.evaluate((display) => {
    localStorage.clear();
    localStorage.setItem('pb-demo-config', JSON.stringify({
      night: { enabled: false }, rules: [], shortcuts: [], display: { idleReturnSec: 0, ...display },
      pages: [
        { id: 'one', title: 'One', widgets: [{ id: 'n1', type: 'note', x: 0, y: 0, w: 12, h: 6, options: { text: 'Page one' } }] },
        { id: 'two', title: 'Two', widgets: [{ id: 'n2', type: 'note', x: 0, y: 0, w: 12, h: 6, options: { text: 'Page two' } }] },
      ],
    }));
  }, display);
  await page.goto('/panel/');
  await expect(page.locator('.w-note').first()).toBeVisible();
  // Until the stored config arrives the panel runs on its defaults, whose night screen covers 00:30-07:30. Between
  // those hours (on the CI's UTC clock too) a tap would only wake that screen; the next tick clears it.
  await expect(page.locator('body')).not.toHaveClass(/night/);
}

test('burn-in fixture configuration survives delayed default initialization', async ({ page }) => {
  const { gatedLayout } = require('./layout-ordering');
  expect(await gatedLayout(page, p => show(p, {}))).toEqual(['one', 'two']);
});

const shift = (page) => page.evaluate(() => {
  const s = getComputedStyle(document.documentElement);
  return `${s.getPropertyValue('--shift-x').trim()} ${s.getPropertyValue('--shift-y').trim()}`;
});

test('pixel shift is off unless it is turned on', async ({ page }) => {
  await show(page, {});
  await page.clock.fastForward(130_000);
  await expect(page.locator('body')).not.toHaveClass(/shifted/);
  expect(await page.evaluate(() => getComputedStyle(document.body).transform)).toBe('none');
});

test('pixel shift moves the panel to its next position every 2 minutes', async ({ page }) => {
  await show(page, { pixelShift: true });
  await expect(page.locator('body')).toHaveClass(/shifted/);
  const first = await shift(page);
  await page.clock.fastForward(121_000);
  await expect.poll(() => shift(page)).not.toBe(first);
  // never more than 2 px from its place
  const [x, y] = (await shift(page)).split(' ').map((v) => Math.abs(parseFloat(v)));
  expect(Math.max(x, y)).toBeLessThanOrEqual(2);
});

test('idle dim covers the panel, and the touch that wakes it presses nothing', async ({ page }) => {
  await show(page, { idleDimMin: 1 });
  await expect(page.locator('body')).not.toHaveClass(/dimmed/);
  await page.clock.fastForward(61_000);
  await expect(page.locator('body')).toHaveClass(/dimmed/);

  // a tap on the dots bar's overview button only brightens the panel ...
  const all = await page.locator('#dots .page-all').boundingBox();
  const tap = () => page.mouse.click(all.x + all.width / 2, all.y + all.height / 2);
  await tap();
  await expect(page.locator('body')).not.toHaveClass(/dimmed/);
  await page.clock.fastForward(1_000);
  await expect(page.locator('#overview')).toBeHidden();

  // ... and the next one works as usual
  await tap();
  await expect(page.locator('#overview')).toBeVisible();
});

test('a notification brightens a dimmed panel', async ({ page }) => {
  await show(page, { idleDimMin: 1 });
  await page.clock.fastForward(61_000);
  await expect(page.locator('body')).toHaveClass(/dimmed/);
  await page.evaluate(() => fetch('/api/notify', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ title: 'Build', text: 'done' }) }));
  await expect(page.locator('body')).not.toHaveClass(/dimmed/);
});

test('a page switch waiting behind the open overview does not keep the panel from dimming', async ({ page }) => {
  await show(page, { idleDimMin: 1, switchCountdownSec: 0 });
  await page.locator('#dots .page-all').click();
  await expect(page.locator('#overview')).toBeVisible();
  await page.evaluate(() => fetch('/api/notify', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ title: 'Build', text: 'done', page: 'two' }) }));
  await expect(page.locator('#navChip')).toHaveClass(/on/); // waits until the overview is closed
  await page.clock.fastForward(61_000);
  await expect(page.locator('body')).toHaveClass(/dimmed/);
});

// PC idle: the dimmed clock, then pure black. The demo agent reports no idle time, so it is added to its state answers.
async function showIdle(page, display) {
  await show(page, { idleClockMin: 5, blackoutMin: 3, ...display });
  await page.evaluate(() => {
    const real = window.fetch;
    window.__idle = 0;
    window.fetch = async (url, init) => {
      if (window.__down) throw new TypeError('the agent is not running');
      const res = await real(url, init);
      if (!/\/api\/state/.test(String(url?.url ?? url))) return res;
      return new Response(JSON.stringify({ ...await res.json(), idleSec: window.__idle }), { headers: { 'Content-Type': 'application/json' } });
    };
  });
  await page.clock.runFor(32_000); // past the 30 s a fresh panel stays lit
}
const idle = async (page, sec) => { await page.evaluate((s) => { window.__idle = s; }, sec); await page.clock.runFor(2_500); };
const body = async (page) => page.evaluate(() => ['night', 'blackout'].filter((c) => document.body.classList.contains(c)).join('+'));

test('an idle PC brings the dimmed clock, then black, and its input brings the panel back from either', async ({ page }) => {
  await showIdle(page, {});
  await idle(page, 299);
  expect(await body(page)).toBe('');
  await idle(page, 300);
  expect(await body(page)).toBe('night');
  await idle(page, 1);
  expect(await body(page)).toBe('');
  await idle(page, 479);
  expect(await body(page)).toBe('night');
  await idle(page, 480);
  expect(await body(page)).toBe('night+blackout');
  await idle(page, 1);
  expect(await body(page)).toBe('');
});

test('either idle step can be left out', async ({ page }) => {
  await showIdle(page, { idleClockMin: 0 });
  await idle(page, 180);
  expect(await body(page)).toBe('blackout');
  await showIdle(page, { blackoutMin: 0 });
  await idle(page, 9999);
  expect(await body(page)).toBe('night');
});

test('a touch lifts the idle screens for 30 seconds', async ({ page }) => {
  await showIdle(page, {});
  await idle(page, 900);
  expect(await body(page)).toBe('night+blackout');
  await page.mouse.click(300, 150);
  expect(await body(page)).toBe('');
  await page.clock.runFor(25_000);
  expect(await body(page)).toBe('');
  await page.clock.runFor(8_000);
  expect(await body(page)).toBe('night+blackout');
});

test('a PC that goes to sleep behind the clock does not light the panel', async ({ page }) => {
  await showIdle(page, { nightWhenUnreachable: true, unreachableNightDelayMin: 1 });
  await idle(page, 310);
  expect(await body(page)).toBe('night');
  await page.evaluate(() => { window.__down = true; });
  await page.clock.runFor(30_000);
  expect(await body(page)).toBe('night');
  await page.clock.runFor(35_000);
  expect(await body(page)).toContain('blackout');
  await page.evaluate(() => { window.__down = false; window.__idle = 0; });
  await page.clock.runFor(4_000);
  expect(await body(page)).toBe('');
});

test('without the unreachable setting an old state keeps no idle screen on', async ({ page }) => {
  await showIdle(page, {});
  await idle(page, 900);
  await page.evaluate(() => { window.__down = true; });
  await page.clock.runFor(5_000);
  expect(await body(page)).toBe('');
});
