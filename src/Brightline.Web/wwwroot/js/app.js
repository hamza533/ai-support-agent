// Small helpers used via JS interop (no inline scripts: CSP script-src 'self').
window.brightline = {
  scrollToBottom: (el) => { if (el) { el.scrollTop = el.scrollHeight; } },
  focus: (el) => { if (el) { el.focus(); } },
};
