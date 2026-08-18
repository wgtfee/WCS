namespace Wcs.Host.Controllers;

using Microsoft.AspNetCore.Mvc;
using Wcs.Core.AnomalyDetection.MachineLearning.Adapters;

[ApiController]
[Route("api/anomaly/ml/adapters")]
public sealed class PlcMlAdapterController : ControllerBase
{
    private readonly IPlcMlExternalRuntimeStatusProvider _statusProvider;

    public PlcMlAdapterController(IPlcMlExternalRuntimeStatusProvider statusProvider) =>
        _statusProvider = statusProvider;

    [HttpGet("status")]
    public ActionResult<IReadOnlyList<PlcMlExternalRuntimeStatus>> GetStatus() =>
        Ok(_statusProvider.GetExternalRuntimeStatus());
}
