// Benutzer und Sitzungen der Beispielanwendung.
//
// Demo-Daten! In echten Anwendungen Passwörter niemals im Klartext speichern,
// sondern hashen (z. B. PBKDF2 via Rfc2898DeriveBytes).

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using BunNet;

namespace BunNet.Sample;

class Session
{
    public string User = "";
    public DateTimeOffset Expires;
}

static class Auth
{
    static Dictionary<string, string> Users = new Dictionary<string, string>
    {
        { "admin", "geheim123" },
        { "erik", "passwort42" },
    };

    static ConcurrentDictionary<string, Session> Sessions = new ConcurrentDictionary<string, Session>();
    static TimeSpan Lifetime = TimeSpan.FromMinutes(30);

    // Prüft Benutzername und Passwort (Vergleich in konstanter Zeit gegen Timing-Angriffe).
    public static bool CheckPassword(string user, string password)
    {
        string? expected;
        return Users.TryGetValue(user, out expected) &&
            CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(password), Encoding.UTF8.GetBytes(expected!));
    }

    // Legt eine neue Sitzung an und liefert ihr Token.
    public static string NewSession(string user)
    {
        string token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        Sessions[token] = new Session { User = user, Expires = DateTimeOffset.UtcNow + Lifetime };
        return token;
    }

    // Liefert die Sitzung zum Token oder null; abgelaufene Sitzungen werden verworfen.
    public static Session? FindSession(string token)
    {
        Session? session;
        if (token == "" || !Sessions.TryGetValue(token, out session)) return null;
        if (session.Expires < DateTimeOffset.UtcNow)
        {
            Sessions.TryRemove(token, out _);
            return null;
        }
        return session;
    }

    // Beendet die Sitzung zum Token (falls vorhanden).
    public static void EndSession(string token)
    {
        if (token != "") Sessions.TryRemove(token, out _);
    }
}