// POST /api/logout — macht das Token ungültig.

using BunNet;

namespace BunNet.Sample;

static partial class Api
{
    public static BunResponse Logout(BunRequest request)
    {
        Auth.EndSession(request.BearerToken);
        return BunResponse.StatusCode(204);
    }
}
