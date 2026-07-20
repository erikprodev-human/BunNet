// POST /api/upload — nimmt eine Datei als rohen Body entgegen (nur angemeldet).
//
// Als Upload-Endpoint registriert (MapUpload): Bun streamt den Body direkt auf
// die Platte, nichts landet im RAM — auch Dateien mit vielen GiB funktionieren.
// SaveBodyTo() verschiebt die fertige Datei nur noch (kein Kopieren).
//
// Größenlimit: options.MaxUploadSize (0 = unbegrenzt); zu große Requests
// beantwortet Bun selbst mit 413, dieser Handler läuft dann gar nicht.
//
// Aufruf:  curl -sk -X POST "https://127.0.0.1:8080/api/upload?name=film.mp4" \
//            --data-binary @film.mp4 -H "Authorization: Bearer $TOKEN"

using BunNet;

namespace BunNet.Sample;

static partial class Api
{
    public static BunResponse Upload(BunRequest request)
    {
        Session? session = Auth.FindSession(request.BearerToken);
        if (session == null)
            return BunResponse.Json(new JsonBuilder().Add("error", "Nicht angemeldet."), 401);

        if (request.BodyLength == 0)
            return BunResponse.Json(new JsonBuilder().Add("error", "Leerer Body — Datei fehlt."), 400);

        // Nur der reine Dateiname zählt — Pfadanteile fliegen raus (kein Traversal).
        string name = Path.GetFileName(request.GetQuery("name") ?? "");
        if (name == "" || name == "." || name == "..") name = "upload.bin";

        string directory = Path.Combine(AppContext.BaseDirectory, "Uploads");
        Directory.CreateDirectory(directory);
        request.SaveBodyTo(Path.Combine(directory, name));

        return BunResponse.Json(new JsonBuilder()
            .Add("gespeichert", name)
            .Add("bytes", request.BodyLength)
            .Add("von", session.User));
    }
}
