(async function () {
  const rid = new URLSearchParams(location.search).get("rid");
  const errorEl = document.getElementById("error");
  const form = document.getElementById("form");
  const idpsEl = document.getElementById("idps");
  let cfg = null;

  function showError(msg) {
    errorEl.textContent = msg;
    errorEl.hidden = false;
  }

  async function submit(idp) {
    errorEl.hidden = true;
    const res = await fetch("/api/login", {
      method: "POST",
      credentials: "same-origin",
      headers: { "Content-Type": "application/json", [cfg.antiforgery.headerName]: cfg.antiforgery.requestToken },
      body: JSON.stringify({ rid: cfg.rid, email: document.getElementById("email").value, idp: idp || null })
    });
    const data = await res.json().catch(() => ({}));
    if (res.ok && data.redirectUrl) location.href = data.redirectUrl;
    else showError(data.error || "Sign-in failed.");
  }

  try {
    const res = await fetch("/api/login/config?rid=" + encodeURIComponent(rid || ""), { credentials: "same-origin" });
    cfg = await res.json();
    if (!res.ok) { showError(cfg.error || "Sign-in request expired or invalid."); form.hidden = true; return; }
  } catch {
    showError("Unable to load sign-in options."); form.hidden = true; return;
  }

  for (const idp of cfg.idps) {
    const b = document.createElement("button");
    b.type = "button";
    b.className = "login-button login-idp-button login-idp-" + idp.id;
    b.textContent = idp.displayName;
    b.addEventListener("click", () => submit(idp.id));
    idpsEl.appendChild(b);
  }

  form.addEventListener("submit", e => { e.preventDefault(); submit(null); });
})();
