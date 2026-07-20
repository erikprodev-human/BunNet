// BunNet Demo — Frontend-Logik.
// Alle /api/*-Aufrufe gehen per POST an die in C# registrierten Endpoints.

const loginView = document.getElementById("login-view");
const profileView = document.getElementById("profile-view");
const loginForm = document.getElementById("login-form");
const loginButton = document.getElementById("login-button");
const loginError = document.getElementById("login-error");

// Kleiner Helfer: POST mit JSON-Body und optionalem Bearer-Token.
async function api(path, body) {
  const headers = { "Content-Type": "application/json" };
  const token = sessionStorage.getItem("token");
  if (token) headers["Authorization"] = `Bearer ${token}`;

  const response = await fetch(path, {
    method: "POST",
    headers,
    body: JSON.stringify(body ?? {}),
  });

  const data = response.status === 204 ? null : await response.json().catch(() => null);
  return { ok: response.ok, status: response.status, data };
}

function showError(message) {
  loginError.textContent = message;
  loginError.hidden = false;
}

function showLogin() {
  sessionStorage.removeItem("token");
  profileView.hidden = true;
  loginView.hidden = false;
}

async function showProfile() {
  const { ok, data } = await api("/api/profile");
  if (!ok) {
    showLogin();
    return;
  }

  document.getElementById("profile-user").textContent = data.user;

  const rows = {
    Serverzeit: new Date(data.serverTime).toLocaleString("de-DE"),
    "Sitzung gültig bis": new Date(data.sessionExpires).toLocaleString("de-DE"),
    Server: data.machine,
    Runtime: data.runtime,
  };
  document.getElementById("profile-data").innerHTML = Object.entries(rows)
    .map(([key, value]) => `<dt>${key}</dt><dd>${value}</dd>`)
    .join("");

  loginView.hidden = true;
  profileView.hidden = false;
}

loginForm.addEventListener("submit", async (event) => {
  event.preventDefault();
  loginError.hidden = true;
  loginButton.disabled = true;

  try {
    const { ok, data } = await api("/api/login", {
      username: document.getElementById("username").value.trim(),
      password: document.getElementById("password").value,
    });

    if (!ok) {
      showError(data?.error ?? "Anmeldung fehlgeschlagen.");
      return;
    }

    sessionStorage.setItem("token", data.token);
    loginForm.reset();
    await showProfile();
  } catch {
    showError("Server nicht erreichbar.");
  } finally {
    loginButton.disabled = false;
  }
});

document.getElementById("refresh-button").addEventListener("click", showProfile);

document.getElementById("logout-button").addEventListener("click", async () => {
  await api("/api/logout");
  showLogin();
});

// Bei vorhandenem Token direkt das Profil anzeigen.
if (sessionStorage.getItem("token")) showProfile();
