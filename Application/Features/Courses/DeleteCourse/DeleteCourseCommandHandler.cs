using System;
using System.Collections.Generic;
using System.Text;

namespace Application.Features.Courses.DeleteCourse;

using Application.Abstractions.Data;
using Application.Abstractions.Messaging;
using Domain.Common;
using Microsoft.EntityFrameworkCore;
public sealed class DeleteCourseCommandHandler(IAppDbContext dbContext)
: ICommandHandler<DeleteCourseCommand, Result>
{
    public async Task<Result> HandleAsync(
    DeleteCourseCommand command,
    CancellationToken cancellationToken = default)
    {
        // 1. Does it exist?
        var course = await dbContext.Courses
        .FirstOrDefaultAsync(c => c.Id == command.Id, cancellationToken);
        if (course is null)
        {
            return Result.Failure(
            Error.NotFound("Course.NotFound", "Course not found."));
        }
        // 2. Is any student admitted to it?
        var inUse = await dbContext.AdmissionCourses
        .AnyAsync(ac => ac.CourseId == command.Id, cancellationToken);
        if (inUse)
        {
            return Result.Failure(Error.Conflict(
            "Course.InUse",
            "Cannot delete a course that is part of an existing admission."));
        }
        // 3. Delete it
        dbContext.Courses.Remove(course);
        await dbContext.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}
