// Records the demo story against a running app with the real model, as full-resolution frames
// (Chrome screencast) plus markers; scripts/record-demo.sh turns them into an MP4 and the README GIF.
// Run inside the toolbox: ./scripts/dev.sh scripts/record-demo.sh   (needs `make dev` and a Gemini key)
import fs from 'node:fs';
import path from 'node:path';

const pw = await import(process.env.PW_CORE);
const { chromium } = pw.default ?? pw;

const appUrl = (process.env.APP_URL ?? 'http://host.docker.internal:5080').replace(/\/$/, '');
const outDir = process.env.OUT_DIR ?? 'artifacts/video';
const staffEmail = process.env.RECORDING_STAFF_EMAIL;
const staffPassword = process.env.RECORDING_STAFF_PASSWORD;
if (!staffEmail || !staffPassword) throw new Error('Set RECORDING_STAFF_EMAIL / RECORDING_STAFF_PASSWORD (.env) for the staff part.');

const W = 1440, H = 900;
const framesDir = path.join(outDir, 'frames');
fs.rmSync(outDir, { recursive: true, force: true });
fs.mkdirSync(framesDir, { recursive: true });

const browser = await chromium.launch();
const context = await browser.newContext({ viewport: { width: W, height: H }, deviceScaleFactor: 1 });
const page = await context.newPage();
page.setDefaultTimeout(90_000);

// ---- frame capture -------------------------------------------------------------------------------
const frames = [];
const markers = {};
const waits = []; // [start, end] epoch seconds of model waits: compressed in the edit, flagged on screen
const cdp = await context.newCDPSession(page);
cdp.on('Page.screencastFrame', ({ data, metadata, sessionId }) => {
  const file = path.join(framesDir, `f${String(frames.length).padStart(6, '0')}.jpg`);
  fs.writeFileSync(file, Buffer.from(data, 'base64'));
  frames.push({ file: path.basename(file), t: metadata.timestamp });
  cdp.send('Page.screencastFrameAck', { sessionId }).catch(() => {});
});
await cdp.send('Page.startScreencast', { format: 'jpeg', quality: 92, maxWidth: W, maxHeight: H, everyNthFrame: 1 });
const mark = name => { markers[name] = Date.now() / 1000; console.log(`[mark] ${name}`); };
const pause = ms => page.waitForTimeout(ms);

// ---- overlays (CSSOM only: the app's CSP blocks inline style attributes) ---------------------------
async function caption(step, text) {
  await page.evaluate(([step, text]) => {
    let el = document.getElementById('__cap');
    if (!el) {
      el = document.createElement('div');
      el.id = '__cap';
      Object.assign(el.style, {
        position: 'fixed', left: '24px', bottom: '24px', zIndex: '2147483646', maxWidth: '600px',
        background: 'rgba(15,23,42,.93)', color: '#fff', fontWeight: '600', fontSize: '19px', lineHeight: '1.4',
        fontFamily: getComputedStyle(document.body).fontFamily, padding: '12px 18px', borderRadius: '12px',
        boxShadow: '0 10px 30px rgba(0,0,0,.25)', transition: 'opacity .25s',
      });
      document.body.appendChild(el);
    }
    el.replaceChildren();
    const badge = document.createElement('span');
    badge.textContent = step;
    Object.assign(badge.style, { display: 'inline-block', background: '#3b5bfd', borderRadius: '8px', padding: '1px 9px', marginRight: '10px', fontSize: '15px' });
    el.append(badge, document.createTextNode(text));
    el.style.opacity = '1';
  }, [step, text]);
}

async function fullCard(lines, ms) {
  await page.evaluate(lines => {
    document.getElementById('__card')?.remove();
    const card = document.createElement('div');
    card.id = '__card';
    Object.assign(card.style, {
      position: 'fixed', inset: '0', zIndex: '2147483647', display: 'flex', flexDirection: 'column', justifyContent: 'center',
      padding: '0 120px', background: 'linear-gradient(135deg,#0f172a 0%,#1e2a5a 100%)', color: '#fff',
      fontFamily: getComputedStyle(document.body).fontFamily,
    });
    for (const [kind, text] of lines) {
      const el = document.createElement('div');
      el.textContent = text;
      const styles = {
        logo: { fontWeight: '700', fontSize: '22px', lineHeight: '1', color: '#8ea2ff', letterSpacing: '.08em', textTransform: 'uppercase', marginBottom: '28px' },
        title: { fontWeight: '700', fontSize: '54px', lineHeight: '1.15', marginBottom: '22px', maxWidth: '1100px' },
        sub: { fontWeight: '400', fontSize: '26px', lineHeight: '1.45', color: '#cbd5e1', maxWidth: '1050px', marginBottom: '10px' },
        stat: { fontWeight: '600', fontSize: '26px', lineHeight: '1.6', color: '#e2e8f0' },
        foot: { fontWeight: '500', fontSize: '22px', lineHeight: '1', color: '#8ea2ff', marginTop: '36px' },
      }[kind];
      Object.assign(el.style, styles);
      card.appendChild(el);
    }
    document.body.appendChild(card);
  }, lines);
  await pause(ms);
  await page.evaluate(() => document.getElementById('__card')?.remove());
}

async function cursorTo(locator) {
  await locator.scrollIntoViewIfNeeded();
  const box = await locator.boundingBox();
  if (!box) return;
  await page.evaluate(([x, y]) => {
    let c = document.getElementById('__cursor');
    if (!c) {
      c = document.createElement('div');
      c.id = '__cursor';
      Object.assign(c.style, {
        position: 'fixed', left: `${innerWidth / 2}px`, top: `${innerHeight / 2}px`, width: '26px', height: '26px', margin: '-13px 0 0 -13px',
        borderRadius: '50%', background: 'rgba(59,91,253,.35)', border: '2px solid #3b5bfd', zIndex: '2147483647',
        pointerEvents: 'none', transition: 'left .55s ease, top .55s ease, transform .15s',
      });
      document.body.appendChild(c);
    }
    requestAnimationFrame(() => { c.style.left = `${x}px`; c.style.top = `${y}px`; });
  }, [box.x + box.width / 2, box.y + box.height / 2]);
  await pause(650);
}

async function click(locator) {
  await cursorTo(locator);
  await page.evaluate(() => { const c = document.getElementById('__cursor'); if (c) { c.style.transform = 'scale(.7)'; setTimeout(() => { c.style.transform = 'scale(1)'; }, 160); } });
  await locator.click();
}

// Model waits are sped up in the video; a badge says so while they're on screen.
async function waiting(work) {
  await page.evaluate(() => {
    const b = document.createElement('div');
    b.id = '__wait';
    b.textContent = '⏩ model working · sped up';
    Object.assign(b.style, {
      position: 'fixed', right: '24px', top: '100px', zIndex: '2147483646', background: 'rgba(15,23,42,.85)', color: '#fff',
      fontWeight: '500', fontSize: '14px', lineHeight: '1', fontFamily: getComputedStyle(document.body).fontFamily, padding: '8px 12px', borderRadius: '999px',
    });
    document.body.appendChild(b);
  });
  const start = Date.now() / 1000;
  try {
    await work();
  } finally {
    waits.push([start, Date.now() / 1000]);
    await page.evaluate(() => document.getElementById('__wait')?.remove()).catch(() => {});
  }
}

// ---- chat helpers --------------------------------------------------------------------------------
const counts = () => page.evaluate(() => ({
  messages: document.querySelectorAll('[data-testid=assistant-message]').length,
  cards: document.querySelectorAll('[data-testid=approval-card]').length,
  done: document.querySelectorAll('[data-testid=action-completed]').length,
}));

async function waitTurn(before) {
  await page.waitForFunction(before => {
    if (document.querySelector('[data-testid=chat-error]')) return true;
    if (document.querySelector('.bubble.typing')) return false;
    const input = document.querySelector('[data-testid=chat-input]');
    const msgs = document.querySelectorAll('[data-testid=assistant-message]').length;
    const cards = document.querySelectorAll('[data-testid=approval-card]').length;
    const pending = [...document.querySelectorAll('[data-testid=approval-confirm]')].some(b => !b.disabled);
    return (input && !input.disabled && msgs > before.messages) || (cards > before.cards && pending);
  }, before, { polling: 250 });
  if (await page.locator('[data-testid=chat-error]').count()) {
    throw new Error('Chat error: ' + await page.locator('[data-testid=chat-error]').last().innerText());
  }
  await pause(900); // let the last tokens and cards settle on screen
}

async function say(text) {
  const before = await counts();
  const input = page.getByTestId('chat-input');
  await click(input);
  await input.pressSequentially(text, { delay: 28 });
  await pause(300);
  await click(page.getByTestId('chat-send'));
  await waiting(() => waitTurn(before));
}

async function confirmPendingCard() {
  const confirm = page.getByTestId('approval-confirm').last();
  if (!(await confirm.isVisible()) || await confirm.isDisabled()) return false;
  const before = await counts();
  await pause(1800); // give viewers time to read the card
  await click(confirm);
  await waiting(async () => {
    await page.waitForFunction(n => document.querySelectorAll('[data-testid=action-completed]').length > n, before.done, { polling: 250 });
    await page.waitForFunction(() => { const i = document.querySelector('[data-testid=chat-input]'); return i && !i.disabled && !document.querySelector('.bubble.typing'); }, null, { polling: 250 });
  });
  await pause(1200);
  return true;
}

const lastAssistantText = async () => (await page.getByTestId('assistant-message').last().innerText()).toLowerCase();

// ---- the story -----------------------------------------------------------------------------------
const summary = JSON.parse(fs.readFileSync('eval/results.json', 'utf8')).summary;
const pct = v => `${Math.round(v * 100)}%`;

await page.goto(appUrl);
await page.getByTestId('open-chat').waitFor();
mark('start');
await fullCard([
  ['logo', 'Brightline · AI support agent for .NET'],
  ['title', 'Answers from your documents. Acts through your existing APIs.'],
  ['sub', 'A customer asks about returns, verifies an order and returns faulty headphones –'],
  ['sub', 'the agent cites its sources and asks before changing anything.'],
], 4500);

await caption('1', 'Help centre with an AI assistant');
await pause(1500);
for (let i = 0; i < 5 && !(await page.getByTestId('chat-input').isVisible()); i++) {
  await click(page.getByTestId('open-chat'));
  await pause(800);
}
mark('chat');

await caption('2', 'Answers only from the help-centre documents, with citations');
await say('How long do I have to return headphones?');
await pause(2500);

await caption('3', 'Verifies the customer through the existing Orders API');
await say('The left ear on my Aurora headphones keeps cutting out. Order BL-10482, email maya.t@example.com');
await page.getByTestId('verified-chip').waitFor();
await pause(2500);

await caption('4', 'Checks eligibility, then asks before changing anything');
if (!(await page.getByTestId('approval-confirm').last().isVisible().catch(() => false))) {
  await say('Yes, please start the return.');
}
if (!(await confirmPendingCard())) throw new Error('No return confirmation card appeared.');
await caption('5', 'Return created in the legacy system – idempotent, re-checked on the server');
await pause(2500);

if ((await lastAssistantText()).includes('ticket')) {
  await caption('6', 'Opens a support ticket for the defect – again only after Confirm');
  await say('Yes, please open a ticket for the defect too.');
  await confirmPendingCard();
  await pause(2000);
}
mark('chat-end');

await caption('7', 'Staff console: every conversation, tool call and approval');
await page.goto(`${appUrl}/admin/tickets`);
await page.getByLabel('Email').waitFor();
await click(page.getByLabel('Email'));
await page.getByLabel('Email').pressSequentially(staffEmail, { delay: 20 });
await click(page.getByLabel('Password'));
await page.getByLabel('Password').fill(staffPassword);
await click(page.getByRole('button', { name: 'Sign in' }));
await page.getByTestId('ticket-table').waitFor();
await caption('7', 'Staff console: tickets the agent created, with priority and order');
await pause(3000);

const view = page.getByTestId('ticket-table').getByRole('link', { name: 'View' }).first();
const href = await view.getAttribute('href');
await cursorTo(view);
await page.goto(`${appUrl}${href}`);
await page.locator('.tid-agent-trace').waitFor();
await caption('8', 'Full transcript and agent trace: tools, approvals, tokens');
await pause(2000);
for (let i = 0; i < 6; i++) { await page.mouse.wheel(0, 260); await pause(500); }
await pause(2000);
mark('staff-end');

await fullCard([
  ['logo', 'Measured with make eval (Microsoft.Extensions.AI.Evaluation)'],
  ['title', 'Safe by construction, measured on every change'],
  ['stat', `Groundedness ${summary.avgGroundedness.toFixed(2)} / 5 · citation validity ${pct(summary.citationValidity)} · "I don't know" ${pct(summary.iDontKnowAccuracy)}`],
  ['stat', `Agent scenarios ${summary.scenarios} · verification ${pct(summary.verificationEnforcement)} · approvals ${pct(summary.approvalBeforeWrites)}`],
  ['stat', `${summary.leaks} cross-customer leaks · ${summary.duplicates} duplicate writes · .NET 10 · Agent Framework · pgvector`],
  ['foot', 'github.com/hamza533/ai-support-agent'],
], 6000);
mark('end');

await cdp.send('Page.stopScreencast');
await browser.close();
fs.writeFileSync(path.join(outDir, 'frames.json'), JSON.stringify({ frames, markers, waits }, null, 1));

// Video time: model waits longer than MAX_WAIT play back compressed to MAX_WAIT (the on-screen badge says so).
const MAX_WAIT = 1.6;
const speed = t => { for (const [a, b] of waits) if (t >= a && t < b && b - a > MAX_WAIT) return MAX_WAIT / (b - a); return 1; };
const videoTime = t => { let v = 0, prev = frames[0].t; for (const f of frames) { if (f.t > t) break; v += (f.t - prev) * speed(prev); prev = f.t; } return v + (t - prev) * speed(prev); };

// ffmpeg concat list: each frame is shown until the next one arrived (the screencast only sends changes).
const lines = [];
frames.forEach((f, i) => {
  const next = frames[i + 1]?.t ?? f.t + 2;
  lines.push(`file 'frames/${f.file}'`, `duration ${Math.max(0.001, (next - f.t) * speed(f.t)).toFixed(4)}`);
});
lines.push(`file 'frames/${frames.at(-1).file}'`);
fs.writeFileSync(path.join(outDir, 'concat.txt'), lines.join('\n') + '\n');

// README GIF = the customer chat part (from opening the chat to the last confirmation).
fs.writeFileSync(path.join(outDir, 'gif.env'),
  `GIF_START=${videoTime(markers.chat).toFixed(2)}\nGIF_DURATION=${(videoTime(markers['chat-end']) - videoTime(markers.chat)).toFixed(2)}\n`);
console.log(`Captured ${frames.length} frames: ${(frames.at(-1).t - frames[0].t).toFixed(1)} s recorded, ${videoTime(frames.at(-1).t).toFixed(1)} s in the video.`);
