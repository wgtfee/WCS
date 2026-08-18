namespace Wcs.Host.Controllers;

using Microsoft.AspNetCore.Mvc;
using Wcs.Core.AnomalyDetection.MachineLearning;

[ApiController]
[Route("api/anomaly/ml/context-peer")]
public sealed class PlcMlContextPeerController : ControllerBase
{
    private readonly IPlcMlContextPeerRuntime _runtime;

    public PlcMlContextPeerController(IPlcMlContextPeerRuntime runtime)
    {
        _runtime = runtime;
    }

    [HttpGet("status")]
    public ActionResult<IReadOnlyList<PlcMlContextPeerProfileStatus>> GetStatus() =>
        Ok(_runtime.GetStatus());
}
