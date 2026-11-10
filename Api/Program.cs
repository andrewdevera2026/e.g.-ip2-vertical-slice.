using Api.Common.Authorization;
using Api.Features.Admissions;
using Api.Features.Auth;
using Api.Features.Courses;
using Api.Features.Students;
using Application.Abstractions;
using Application.Abstractions.Data;
using Application.Abstractions.Messaging;
using Application.Features.Admissions.CreateAdmission;
using Application.Features.Admissions.GetAdmissionById;
using Application.Features.Auth.Login;
using Application.Features.Auth.Register;
using Application.Features.Courses.CreateCourse;
using Application.Features.Courses.DeleteCourse;
using Application.Features.Courses.GetAllCourses;
using Application.Features.Courses.GetCourseById;
using Application.Features.Students.CreateStudent;
using Application.Features.Students.GetAllStudents;
using Application.Features.Students.GetStudentById;
using Domain.Common;
using FluentValidation;
using Infrastructure.Authentication;
using Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;
using Scalar.AspNetCore;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;

var builder = WebApplication.CreateBuilder(args);

// Database
builder.Services.AddDbContext<IAppDbContext, AppDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("Database")));

// Validators
builder.Services.AddValidatorsFromAssemblyContaining<CreateStudentValidator>();

// Handlers
builder.Services.AddScoped<ICommandHandler<CreateStudentCommand, Result<CreateStudentResponse>>, CreateStudentCommandHandler>();
builder.Services.AddScoped<IQueryHandler<GetAllStudentsQuery, Result<GetAllStudentsResponse>>, GetAllStudentsQueryHandler>();
builder.Services.AddScoped<IQueryHandler<GetStudentByIdQuery, Result<GetStudentByIdResponse>>, GetStudentByIdQueryHandler>();
builder.Services.AddScoped<ICommandHandler<CreateCourseCommand, Result<CreateCourseResponse>>, CreateCourseCommandHandler>();
builder.Services.AddScoped<IQueryHandler<GetAllCoursesQuery, Result<GetAllCoursesResponse>>, GetAllCoursesQueryHandler>();
builder.Services.AddScoped<IQueryHandler<GetCourseByIdQuery, Result<GetCourseByIdResponse>>, GetCourseByIdQueryHandler>();
builder.Services.AddScoped<ICommandHandler<CreateAdmissionCommand, Result<CreateAdmissionResponse>>, CreateAdmissionCommandHandler>();
builder.Services.AddScoped<IQueryHandler<GetAdmissionByIdQuery, Result<GetAdmissionByIdResponse>>, GetAdmissionByIdQueryHandler>();
builder.Services.AddScoped<ICommandHandler<DeleteCourseCommand, Result>, DeleteCourseCommandHandler>();
builder.Services.AddScoped<ICommandHandler<RegisterCommand, Result<RegisterResponse>>, RegisterCommandHandler>();
builder.Services.AddScoped<ICommandHandler<LoginCommand, Result<LoginResponse>>, LoginCommandHandler>();

// Auth services
builder.Services.AddScoped<IPasswordHasher, Infrastructure.Authentication.PasswordHasher>();

builder.Services.AddOptions<JwtOptions>()
    .Bind(builder.Configuration.GetSection(JwtOptions.SectionName))
    .Validate(o => !string.IsNullOrWhiteSpace(o.SigningKey),
        "Jwt:SigningKey is required. Set it with: dotnet user-secrets set \"Jwt:SigningKey\" \"<value>\"")
    .Validate(o => Encoding.UTF8.GetByteCount(o.SigningKey ?? "") >= 32,
        "Jwt:SigningKey must be at least 256 bits (32 characters).")
    .ValidateOnStart();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<ITokenService, JwtTokenService>();

// JWT validation + revocation checks (Steps 6 and 9)
var jwtSection = builder.Configuration.GetSection(JwtOptions.SectionName);

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtSection["Issuer"],
            ValidAudience = jwtSection["Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(
                Encoding.UTF8.GetBytes(jwtSection["SigningKey"]!)),
            ClockSkew = TimeSpan.FromSeconds(30)
        };

        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = async context =>
            {
                var dbContext = context.HttpContext.RequestServices.GetRequiredService<IAppDbContext>();

                var jti = context.Principal?.FindFirstValue(JwtRegisteredClaimNames.Jti);
                var sub = context.Principal?.FindFirstValue(JwtRegisteredClaimNames.Sub)
                    ?? context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
                var iatRaw = context.Principal?.FindFirstValue(JwtRegisteredClaimNames.Iat);

                if (jti is not null && await dbContext.RevokedTokens.AnyAsync(r => r.Jti == jti))
                {
                    context.Fail("Token has been revoked.");
                    return;
                }

                if (Guid.TryParse(sub, out var userId) && long.TryParse(iatRaw, out var iatUnix))
                {
                    var issuedAtUtc = DateTimeOffset.FromUnixTimeSeconds(iatUnix).UtcDateTime;
                    var appUser = await dbContext.Users.FindAsync(userId);

                    if (appUser?.TokensValidFromUtc is { } validFrom && issuedAtUtc < validFrom)
                    {
                        context.Fail("Token was issued before the user's last sign-out-everywhere.");
                    }
                }
            }
        };
    });

// Policies + ownership handler (Steps 12 and 14)
builder.Services.AddAppAuthorization();
builder.Services.AddSingleton<IAuthorizationHandler, AdmissionOwnerHandler>();

// Background cleanup (Step 10)
builder.Services.AddHostedService<Infrastructure.BackgroundJobs.RevokedTokenCleanupService>();

// API + Scalar Bearer scheme (Step 7)
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddOpenApi(options =>
{
    options.AddDocumentTransformer((document, _, _) =>
    {
        document.Info.Title = "Vertical Slice API";
        document.Info.Description = "A simple API demonstrating Vertical Slice Architecture";

        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes["Bearer"] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
            Description = "Paste the access token from /api/auth/login"
        };
        document.Security ??= [];
        document.Security.Add(new OpenApiSecurityRequirement
        {
            [new OpenApiSecuritySchemeReference("Bearer", document)] = []
        });

        return Task.CompletedTask;
    });
});

var app = builder.Build();

await app.Services.MigrateAndSeedAsync();

// JSON bodies for 401/403 (Step 12)
app.UseStatusCodePages(async context =>
{
    var response = context.HttpContext.Response;
    if (response.StatusCode is StatusCodes.Status401Unauthorized or StatusCodes.Status403Forbidden)
    {
        response.ContentType = "application/problem+json";
        await response.WriteAsJsonAsync(new Microsoft.AspNetCore.Mvc.ProblemDetails
        {
            Status = response.StatusCode,
            Title = response.StatusCode == StatusCodes.Status401Unauthorized ? "Unauthorized" : "Forbidden",
            Detail = response.StatusCode == StatusCodes.Status401Unauthorized
                ? "Authentication is required to access this resource."
                : "You do not have permission to perform this action."
        });
    }
});

app.UseAuthentication();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.MapGet("/", () => "Welcome to the Vertical Slice API! Go to /scalar/v1 for API documentation.");

app.MapStudentEndpoints();
app.MapCourseEndpoints();
app.MapAdmissionEndpoints();
app.MapAuthEndpoints();

app.Run();