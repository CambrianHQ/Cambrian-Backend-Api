using System.Text;
using Cambrian.Api.Common;
using Cambrian.Api.Middleware;
using Microsoft.AspNetCore.Mvc;
using ApiAdminService = Cambrian.Api.Services.AdminService;
using ApiApplicationDbContext = Cambrian.Api.Data.ApplicationDbContext;
using ApiAuthService = Cambrian.Api.Services.AuthService;
using ApiCatalogService = Cambrian.Api.Services.CatalogService;
using ApiIAdminService = Cambrian.Api.Services.Interfaces.IAdminService;
using ApiIAuthService = Cambrian.Api.Services.IAuthService;
using ApiICatalogService = Cambrian.Api.Services.Interfaces.ICatalogService;
using ApiIJwtService = Cambrian.Api.Security.IJwtService;
using ApiILibraryService = Cambrian.Api.Services.Interfaces.ILibraryService;
using ApiIObjectStorage = Cambrian.Api.Services.Interfaces.IObjectStorage;
using ApiIPayoutService = Cambrian.Api.Services.Interfaces.IPayoutService;
using ApiIStripeService = Cambrian.Api.Services.IStripeService;
using ApiObjectStorage = Cambrian.Api.Infrastructure.R2ObjectStorage;
using ApiJwtService = Cambrian.Api.Security.JwtService;
using ApiLibraryService = Cambrian.Api.Services.LibraryService;
using ApiPayoutService = Cambrian.Api.Services.PayoutService;
using ApiStripeService = Cambrian.Api.Services.StripeService;
using ApiIUserRepository = Cambrian.Api.Repositories.IUserRepository;
using ApiUserRepository = Cambrian.Api.Repositories.UserRepository;
using Cambrian.Application.Interfaces;
using Cambrian.Application.Services;
using Cambrian.Domain.Entities;
using Cambrian.Infrastructure.Storage;
using Cambrian.Infrastructure.Stripe;
using Cambrian.Persistence;
using Cambrian.Persistence.Repositories;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

// Database
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? "Host=localhost;Port=5432;Database=cambrian;Username=postgres;Password=postgres";
builder.Services.AddDbContext<CambrianDbContext>(options =>
    options.UseNpgsql(connectionString));
builder.Services.AddDbContext<ApiApplicationDbContext>(options =>
    options.UseNpgsql(connectionString));

// Identity
builder.Services.AddIdentityCore<ApplicationUser>(options =>
    {
        options.Password.RequireDigit = true;
        options.Password.RequireLowercase = true;
        options.Password.RequireUppercase = true;
        options.Password.RequireNonAlphanumeric = true;
        options.Password.RequiredLength = 8;
        options.User.RequireUniqueEmail = true;
    })
    .AddEntityFrameworkStores<CambrianDbContext>();

// JWT Authentication
var jwtKey = builder.Configuration["Jwt:Key"] ?? "cambrian-dev-secret-key-min-32-chars!!";
builder.Services.AddAuthentication("Bearer")
    .AddJwtBearer("Bearer", options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"] ?? "cambrian",
            ValidAudience = builder.Configuration["Jwt:Audience"] ?? "cambrian",
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey))
        };
    });

builder.Services.AddAuthorization();

builder.Services.AddControllers();
builder.Services.Configure<ApiBehaviorOptions>(options =>
{
    options.InvalidModelStateResponseFactory = context =>
    {
        var errors = context.ModelState
            .Where(e => e.Value?.Errors.Count > 0)
            .SelectMany(e => e.Value!.Errors.Select(x => x.ErrorMessage))
            .ToList();
        var response = ApiResponse.Fail(string.Join(" | ", errors));
        return new BadRequestObjectResult(response);
    };
});
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Services
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<ApiIAuthService, ApiAuthService>();
builder.Services.AddScoped<ApiICatalogService, ApiCatalogService>();
builder.Services.AddScoped<ApiIAdminService, ApiAdminService>();
builder.Services.AddScoped<ApiILibraryService, ApiLibraryService>();
builder.Services.AddScoped<ApiIPayoutService, ApiPayoutService>();
builder.Services.AddScoped<ICatalogService, CatalogService>();
builder.Services.AddScoped<ILibraryService, LibraryService>();
builder.Services.AddScoped<ICheckoutService, CheckoutService>();
builder.Services.AddScoped<IPayoutService, PayoutService>();
builder.Services.AddScoped<IPaymentService, PaymentService>();
builder.Services.AddScoped<IAdminService, AdminService>();
builder.Services.AddScoped<IWebhookService, StripeWebhookService>();
builder.Services.AddScoped<ApiIJwtService, ApiJwtService>();
builder.Services.AddScoped<ApiIStripeService, ApiStripeService>();
builder.Services.AddScoped<ApiIObjectStorage, ApiObjectStorage>();

// Repositories
builder.Services.AddScoped<ApiIUserRepository, ApiUserRepository>();
builder.Services.AddScoped<ITrackRepository, TrackRepository>();
builder.Services.AddScoped<IPurchaseRepository, PurchaseRepository>();
builder.Services.AddScoped<ILibraryRepository, LibraryRepository>();
builder.Services.AddScoped<IPayoutRepository, PayoutRepository>();

// Infrastructure
builder.Services.AddSingleton<StripeFacade>();
builder.Services.AddSingleton<IObjectStorage, R2ObjectStorage>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseMiddleware<ExceptionMiddleware>();
app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.Run();