using FileTracert.Business.Setup;
using FileTracert.Contracts.Dtos;
using Microsoft.AspNetCore.Mvc;

namespace FileTracert.Host.Controllers;

/// <summary>Global default file-type filter. PUT reconciles the index for default-using roots.</summary>
[ApiController]
[Route("api/settings/filter")]
public sealed class SettingsController : ControllerBase
{
    private readonly FilterSettingsService _service;

    public SettingsController(FilterSettingsService service) => _service = service;

    [HttpGet]
    public async Task<ActionResult<FilterSettingsDto>> Get(CancellationToken ct) =>
        Ok(await _service.GetAsync(ct));

    [HttpPut]
    public async Task<ActionResult<ReconcileResultDto>> Put([FromBody] FilterSettingsDto request, CancellationToken ct) =>
        Ok(await _service.UpdateAsync(request, ct));
}
