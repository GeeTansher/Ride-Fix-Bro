using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RideFixBro.API.Models.ManualPublishModels;
using RideFixBro.API.Services.BackgroundProcess.ManualPublish;
using System.Globalization;
using System.Security.Claims;

namespace RideFixBro.API.Controllers;

[ApiController, Authorize(Roles = "Admin"), Route("api/admin/manual-jobs")]
public sealed class ManualPublicationJobsController(ManualPublicationJobsService jobs) : ControllerBase
{
    private int UserId => int.Parse(User.FindFirstValue("app_user_id")!, CultureInfo.InvariantCulture);

    [HttpGet]
    public Task<List<ManualPublicationJobResponse>> List(CancellationToken token) => jobs.ListAsync(UserId, token);

    [HttpGet("{id:guid}")]
    public Task<ManualPublicationJobResponse> Get(Guid id, CancellationToken token) => jobs.GetAsync(UserId, id, token);
}
