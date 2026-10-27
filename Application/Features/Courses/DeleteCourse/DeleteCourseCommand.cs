using System;
using System.Collections.Generic;
using System.Text;

namespace Application.Features.Courses.DeleteCourse;

using Application.Abstractions.Messaging;
using Domain.Common;
public sealed record DeleteCourseCommand(Guid Id) : ICommand<Result>;