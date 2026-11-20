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
    public Task<CatalogPage<DepartmentView>> Departments([FromQuery] DepartmentListQuery query, CancellationToken cancellationToken) =>
        catalog.DepartmentsAsync(new(query.Page, query.PageSize), cancellationToken);

    /// <summary>Return subjects, optionally filtered by their parent department.</summary>
    [HttpGet("subjects")]
    public Task<CatalogPage<SubjectView>> Subjects([FromQuery] SubjectListQuery query, CancellationToken cancellationToken) =>
        catalog.SubjectsAsync(new(query.Page, query.PageSize, DepartmentId: query.DepartmentId), cancellationToken);

    /// <summary>Return chapters, optionally filtered by subject and grade.</summary>
    [HttpGet("topics")]
    public Task<CatalogPage<TopicView>> Topics([FromQuery] TopicListQuery query, CancellationToken cancellationToken) =>
        catalog.TopicsAsync(new(query.Page, query.PageSize, SubjectId: query.SubjectId, Grade: query.Grade), cancellationToken);
}
