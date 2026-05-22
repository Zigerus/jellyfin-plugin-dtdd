using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Dtdd.Api;

[ApiController]
[Route("DTDD")]
public class DtddController : ControllerBase
{
    // Phase 2 endpoints:
    //   GET  /DTDD/safety/{jellyfinItemId}
    //   GET  /DTDD/topics
    //   GET  /DTDD/prefs
    //   PUT  /DTDD/prefs
}
