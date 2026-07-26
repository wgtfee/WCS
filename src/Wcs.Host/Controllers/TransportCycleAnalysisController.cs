namespace Wcs.Host.Controllers;

using Microsoft.AspNetCore.Mvc;
using Wcs.Core.TransportScheduling;

[ApiController]
[Route("api/transport/cycle-analysis")]
public sealed class TransportCycleAnalysisController : ControllerBase
{
    private readonly ITransportCycleAnalysisService _analysis;

    public TransportCycleAnalysisController(ITransportCycleAnalysisService analysis)
    {
        _analysis = analysis;
    }

    [HttpGet("status")]
    public ActionResult<TransportCycleAnalysisStatus> GetStatus() =>
        Ok(_analysis.GetStatus());

    [HttpGet("cycles")]
    public ActionResult<IReadOnlyList<TransportCycleRecord>> GetCycles(
        [FromQuery] int maxCount = 200) =>
        Ok(_analysis.GetCycles(Math.Clamp(maxCount, 1, 5000)));

    [HttpGet("anomalies")]
    public ActionResult<IReadOnlyList<TransportCycleAnomalyRecord>> GetAnomalies(
        [FromQuery] int maxCount = 200) =>
        Ok(_analysis.GetAnomalies(Math.Clamp(maxCount, 1, 5000)));
}
