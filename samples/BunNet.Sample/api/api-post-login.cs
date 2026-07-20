// POST /api/login — Benutzername + Passwort → Sitzungstoken.

using BunNet;

namespace BunNet.Sample;

static partial class Api
{
    public static async Task<BunResponse> Login(BunRequest request)
    {
        string username = request["username"];
        string password = request["password"];

        if (username == "" || password == "")
            return BunResponse.Json(new JsonBuilder().Add("error", "Benutzername und Passwort erforderlich."), 400);

        if (!Auth.CheckPassword(username, password))
        {
            await Task.Delay(250); // einfacher Schutz gegen Brute-Force
            return BunResponse.Json(new JsonBuilder().Add("error", "Ungültige Anmeldedaten."), 401);
        }

        return BunResponse.Json(new JsonBuilder()
            .Add("token", Auth.NewSession(username))
            .Add("user", username));
    }
}
