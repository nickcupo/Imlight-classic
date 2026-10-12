namespace Imlight.CoreLib.Classic.Friends;

// CLASSIC: extension of the launcher identity: its real spiral, purple frame,
// gold trim, warm parchment and Tahoma UI. This page contains no game art.
internal static class FriendPortalPage {

    public const string Html = """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<meta name="referrer" content="same-origin">
<title>Wizard101 Classic</title>
<link rel="icon" href="/friends/spiral.png" type="image/png">
<style>
:root { color-scheme:light; --purple:#241a4a; --purple-dark:#1c1538; --gold:#c9a94a; --gold-light:#ead69f; --paper:#f4f1ea; --paper-light:#fffdf8; --ink:#2b2418; --muted:#675843; --line:#e2dbcc; --error:#8b251c; --success:#28572f; }
* { box-sizing:border-box; }
[hidden] { display:none !important; }
body { margin:0; min-height:100vh; background:var(--purple-dark); color:var(--ink); font:16px/1.55 Tahoma,"Segoe UI",sans-serif; }
.shell { width:min(760px,calc(100% - 32px)); margin:40px auto; border:2px solid var(--gold); border-radius:12px; overflow:hidden; box-shadow:0 12px 40px #0005; }
.masthead { display:flex; gap:24px; align-items:center; padding:32px; background:var(--purple); color:var(--gold-light); border-bottom:2px solid var(--gold); }
.masthead img { width:80px; height:80px; object-fit:contain; flex:none; }
h1 { font-size:clamp(25px,5vw,36px); line-height:1.15; margin:0 0 8px; letter-spacing:.2px; }
h1,h2,h3 { font-family:Georgia,"Times New Roman",serif; }
.masthead p { margin:0; color:#ece6cb; font-size:15px; }
main { background:var(--paper); padding:32px; }
h2 { font-size:23px; line-height:1.3; margin:0 0 12px; }
p { margin:0 0 18px; text-wrap:pretty; }
.muted,.hint { color:var(--muted); }
.hint { font-size:14px; margin:8px 0 0; }
label { display:block; font-weight:700; margin:16px 0 8px; }
input,button { font:inherit; }
input { display:block; width:100%; min-height:48px; padding:12px; background:var(--paper-light); border:1px solid #9b8664; border-radius:5px; color:var(--ink); }
button,.download { display:inline-flex; align-items:center; justify-content:center; min-height:48px; padding:12px 16px; border:1px solid #927541; border-radius:5px; background:var(--gold-light); color:var(--purple-dark); font-weight:700; cursor:pointer; text-decoration:none; }
button:hover,.download:hover { background:#f9e9b9; }
button:active,.download:active { transform:translateY(1px); }
button:disabled { cursor:default; background:#e1d6c0; color:#6d6354; border-color:#ac9d81; }
button:disabled:active { transform:none; }
input:focus-visible,button:focus-visible,a:focus-visible { outline:3px solid var(--purple); outline-offset:3px; }
.actions { display:flex; flex-wrap:wrap; gap:16px; align-items:center; margin-top:24px; }
.quiet { background:transparent; border-color:var(--line); color:var(--purple); font-weight:400; }
.status { min-height:1.55em; margin:14px 0 0; font-size:15px; overflow-wrap:anywhere; }
.status.error { color:var(--error); }
.status.success { color:var(--success); }
.step { display:grid; grid-template-columns:32px 1fr; gap:16px; padding:24px 0; border-top:1px solid var(--line); }
.step:first-of-type { border-top:0; padding-top:8px; }
.number { display:flex; justify-content:center; align-items:center; width:32px; height:32px; border:1px solid #a98752; border-radius:50%; background:var(--gold-light); color:var(--purple-dark); font-weight:700; }
.step h2 { font-size:21px; margin-top:1px; }
.downloads { display:grid; grid-template-columns:1fr 1fr; gap:16px; }
.download-card { background:var(--paper-light); border:1px solid var(--line); border-radius:6px; padding:16px; }
.download-card h3 { font-size:18px; margin:0 0 8px; }
.download-card p { font-size:14px; margin:0 0 14px; }
.download-card .download,.download-card button { width:100%; }
.footer { padding:16px 32px; background:var(--purple); color:#ece6cb; font-size:13px; }
.footer p { margin:0; }
@media (max-width:560px) { .shell { width:calc(100% - 20px); margin:16px auto; } .masthead { padding:22px 20px; gap:14px; } .masthead img { width:56px; height:56px; } main { padding:24px 20px; } .step { grid-template-columns:30px 1fr; gap:12px; } .number { width:30px; height:30px; } .downloads { grid-template-columns:1fr; } .footer { padding:16px 20px; } .actions button { width:100%; } }
@media (prefers-reduced-motion:reduce) { button:active,.download:active { transform:none; } }
</style>
</head>
<body>
<div class="shell">
  <header class="masthead">
    <img src="/friends/spiral.png" alt="" width="76" height="76">
    <div><h1>Wizard101 Classic</h1><p>A place for friends to return to the Spiral.</p></div>
  </header>
  <main>
    <p id="pageStatus" class="status" role="status" aria-live="polite">Checking access…</p>
    <button id="retryButton" class="quiet" type="button" hidden>Try again</button>
    <section id="gate" hidden aria-labelledby="gateTitle">
      <h2 id="gateTitle">Welcome, friend</h2>
      <p>Enter the site password Nick shared with you to create your account and get the launcher.</p>
      <form id="unlockForm">
        <label for="sitePassword">Site password</label>
        <input id="sitePassword" name="sitePassword" type="password" autocomplete="current-password" required>
        <div class="actions"><button id="unlockButton" type="submit">Enter</button></div>
        <p id="gateStatus" class="status" role="status" aria-live="polite"></p>
      </form>
    </section>
    <div id="welcome" hidden>
      <section class="step" aria-labelledby="accountTitle">
        <span class="number" aria-hidden="true">1</span>
        <div>
          <h2 id="accountTitle">Create your game account</h2>
          <p>Choose your own game password. It is separate from the shared site password you just used.</p>
          <form id="registerForm">
            <label for="username">Username</label>
            <input id="username" name="username" type="text" autocomplete="username" autocapitalize="none" spellcheck="false" minlength="3" maxlength="24" pattern="[a-z0-9_\-]{3,24}" aria-describedby="usernameHint" required>
            <p id="usernameHint" class="hint">3–24 lowercase letters, numbers, underscores or hyphens.</p>
            <label for="gamePassword">Game password</label>
            <input id="gamePassword" name="password" type="password" autocomplete="new-password" minlength="6" maxlength="128" aria-describedby="passwordHint" required>
            <p id="passwordHint" class="hint">Use 6–128 characters.</p>
            <label for="passwordConfirm">Confirm game password</label>
            <input id="passwordConfirm" name="passwordConfirm" type="password" autocomplete="new-password" minlength="6" maxlength="128" required>
            <p class="hint">This creates a regular player account. Already have one? Use it in the launcher below.</p>
            <div class="actions"><button id="registerButton" type="submit">Create account</button></div>
          </form>
          <p id="accountStatus" class="status" role="status" aria-live="polite" tabindex="-1"></p>
        </div>
      </section>
      <section class="step" aria-labelledby="downloadTitle">
        <span class="number" aria-hidden="true">2</span>
        <div>
          <h2 id="downloadTitle">Get the launcher</h2>
          <p>Choose your computer, install the launcher, then sign in with your game account.</p>
          <div class="downloads">
            <div class="download-card">
              <h3>Mac</h3><p>macOS 12 or later. Apple Silicon Macs also need Rosetta.</p>
              <a id="macDownload" class="download" href="/friends/download/mac" hidden>Download for Mac</a>
              <button id="macUnavailable" type="button" disabled>Mac download unavailable</button>
            </div>
            <div class="download-card">
              <h3>Windows</h3><p>Install Wizard101 Classic with the Windows setup file.</p>
              <a id="windowsDownload" class="download" href="/friends/download/windows" hidden>Download for Windows</a>
              <button id="windowsUnavailable" type="button" disabled>Windows download unavailable</button>
            </div>
          </div>
          <p class="hint">Nick will share server availability and access details.</p>
        </div>
      </section>
      <div class="actions"><button id="logoutButton" class="quiet" type="button">Close site access</button></div>
      <p id="logoutStatus" class="status" role="status" aria-live="polite"></p>
    </div>
    <noscript><p>Turn on JavaScript to use the password form and create your account.</p></noscript>
  </main>
  <footer class="footer"><p>Wizard101 Classic · A private game shared by Nick and friends.</p></footer>
</div>
<script>
'use strict';
const $ = id => document.getElementById(id);
let unlocked = false;
function status(id, message, kind = '') {
  const target = $(id);
  target.textContent = message;
  target.className = 'status' + (kind ? ' ' + kind : '');
}
function clearGamePasswords() {
  $('gamePassword').value = '';
  $('passwordConfirm').value = '';
  $('passwordConfirm').setCustomValidity('');
}
function showState(state) {
  unlocked = state.unlocked === true;
  $('gate').hidden = unlocked;
  $('welcome').hidden = !unlocked;
  $('pageStatus').hidden = true;
  $('retryButton').hidden = true;
  $('sitePassword').value = '';
  for (const platform of ['mac', 'windows']) {
    const available = unlocked && state.downloads && state.downloads[platform] === true;
    $(platform + 'Download').hidden = !available;
    $(platform + 'Unavailable').hidden = available;
  }
  if (!unlocked) {
    clearGamePasswords();
    $('registerForm').reset();
    $('registerForm').hidden = false;
    status('accountStatus', '');
    status('logoutStatus', '');
  }
}
async function api(path, body) {
  const options = { credentials:'same-origin', cache:'no-store' };
  if (body !== undefined) {
    options.method = 'POST';
    options.headers = { 'Content-Type':'application/json', 'X-W101C':'1' };
    options.body = JSON.stringify(body);
  }
  let response;
  try { response = await fetch(path, options); }
  catch (error) { throw new Error('The site could not be reached. Please try again.'); }
  let data;
  try { data = await response.json(); }
  catch (error) { throw new Error('The site could not complete that request. Please try again.'); }
  if (!response.ok) {
    if (response.status === 401 && unlocked) {
      showState({ unlocked:false });
      status('gateStatus', 'Your site access expired. Enter the shared site password again.', 'error');
      $('sitePassword').focus();
    }
    throw new Error(typeof data.error === 'string' ? data.error : 'The site could not complete that request. Please try again.');
  }
  return data;
}
function busy(formId, buttonId, active, text) {
  const form = $(formId);
  form.setAttribute('aria-busy', String(active));
  for (const field of form.querySelectorAll('input, button')) field.disabled = active;
  $(buttonId).textContent = text;
  $('logoutButton').disabled = active;
}
async function loadState() {
  $('retryButton').hidden = true;
  $('pageStatus').hidden = false;
  status('pageStatus', 'Checking access…');
  try { showState(await api('/friends/api/state')); }
  catch (error) {
    status('pageStatus', error.message, 'error');
    $('retryButton').hidden = false;
  }
}
$('retryButton').addEventListener('click', loadState);
$('unlockForm').addEventListener('submit', async event => {
  event.preventDefault();
  if (!$('unlockForm').reportValidity()) return;
  const password = $('sitePassword').value;
  busy('unlockForm', 'unlockButton', true, 'Opening…');
  status('gateStatus', '');
  let accessOpened = false;
  try {
    await api('/friends/api/unlock', { password });
    accessOpened = true;
    showState(await api('/friends/api/state'));
    if (unlocked) $('username').focus();
  } catch (error) {
    if (accessOpened) {
      $('pageStatus').hidden = false;
      status('pageStatus', 'Access opened, but the page could not finish loading. Please try again.', 'error');
      $('retryButton').hidden = false;
    } else status('gateStatus', error.message, 'error');
  } finally {
    $('sitePassword').value = '';
    busy('unlockForm', 'unlockButton', false, 'Enter');
  }
});
function checkConfirmation() {
  $('passwordConfirm').setCustomValidity($('passwordConfirm').value && $('passwordConfirm').value !== $('gamePassword').value
    ? 'Your game passwords must match.' : '');
}
$('gamePassword').addEventListener('input', checkConfirmation);
$('passwordConfirm').addEventListener('input', checkConfirmation);
$('registerForm').addEventListener('submit', async event => {
  event.preventDefault();
  checkConfirmation();
  if (!$('registerForm').reportValidity() || !unlocked) return;
  const account = {
    username:$('username').value,
    password:$('gamePassword').value,
    passwordConfirm:$('passwordConfirm').value,
  };
  busy('registerForm', 'registerButton', true, 'Creating…');
  status('accountStatus', '');
  try {
    const result = await api('/friends/api/register', account);
    clearGamePasswords();
    $('registerForm').hidden = true;
    status('accountStatus', 'Account created for ' + result.username + '. Use this username and your game password in the launcher.', 'success');
    $('accountStatus').focus();
  } catch (error) {
    if (unlocked) status('accountStatus', error.message, 'error');
  } finally {
    busy('registerForm', 'registerButton', false, 'Create account');
  }
});
$('logoutButton').addEventListener('click', async () => {
  $('logoutButton').disabled = true;
  status('logoutStatus', '');
  try {
    await api('/friends/api/logout', {});
    showState({ unlocked:false });
    status('gateStatus', 'Site access closed.');
  } catch (error) {
    if (unlocked) status('logoutStatus', error.message, 'error');
  } finally { $('logoutButton').disabled = false; }
});
for (const platform of ['mac', 'windows']) {
  $(platform + 'Download').addEventListener('click', async event => {
    if (event.ctrlKey || event.metaKey || event.shiftKey || event.altKey) return;
    event.preventDefault();
    try {
      const state = await api('/friends/api/state');
      showState(state);
      if (!unlocked) {
        status('gateStatus', 'Your site access expired. Enter the shared site password again.', 'error');
        $('sitePassword').focus();
      } else if (!state.downloads || state.downloads[platform] !== true) {
        $('pageStatus').hidden = false;
        status('pageStatus', 'That download is unavailable right now. Please check with Nick.', 'error');
      } else window.location.assign('/friends/download/' + platform);
    } catch (error) {
      $('pageStatus').hidden = false;
      status('pageStatus', error.message, 'error');
    }
  });
}
loadState();
</script>
</body>
</html>
""";

}
