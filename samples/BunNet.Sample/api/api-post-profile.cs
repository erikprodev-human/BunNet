// POST /api/profile — geschützter Endpoint, liefert Profildaten nur mit gültigem Token.

using BunNet;

namespace BunNet.Sample;

static partial class Api
{
    public static BunResponse Profile(BunRequest request)
    {
        Session? session = Auth.FindSession(request.BearerToken);
        if (session == null)
            return BunResponse.Json(new JsonBuilder().Add("error", "Nicht angemeldet."), 401);

        return BunResponse.Json(new JsonBuilder()
            .Add("user", session.User)
            .Add("serverTime", DateTimeOffset.Now)
            .Add("sessionExpires", session.Expires)
            .Add("machine", Environment.MachineName)
            .Add("runtime", ".NET " + Environment.Version));
    }
}
