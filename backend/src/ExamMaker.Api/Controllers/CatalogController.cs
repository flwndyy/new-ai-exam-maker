using ExamMaker.Application.Catalog;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ExamMaker.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/v1")]
public sealed class CatalogController(ICatalogManagement catalog) : ControllerBase
{
    /// <summary>Return paginated departments for every authenticated application role.</summary>
    [HttpGet("departments")]
    public Task<CatalogPage<DepartmentView>> Departments([FromQuery] CatalogQuery query, CancellationToken cancellationToken) =>
        catalog.DepartmentsAsync(query, cancellationToken);

    /// <summary>Return subjects, optionally filtered by their parent department.</summary>
    [HttpGet("subjects")]
    public Task<CatalogPage<SubjectView>> Subjects([FromQuery] CatalogQuery query, CancellationToken cancellationToken) =>
        catalog.SubjectsAsync(query, cancellationToken);

    /// <summary>Return chapters, optionally filtered by subject and grade.</summary>
    [HttpGet("topics")]
    public Task<CatalogPage<TopicView>> Topics([FromQuery] CatalogQuery query, CancellationToken cancellationToken) =>
        catalog.TopicsAsync(query, cancellationToken);
}
