using System.Security.Claims;
using System.Text;
using Cambrian.Application.Configuration;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace Cambrian.Api.Security;

public static class JwtAuthenticationConfiguration
{
    public static void Configure(
        JwtBearerOptions options,
        JwtSettings jwt,
        string transport,
        string? cookieName = null)
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwt.Issuer,
            ValidAudience = jwt.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Key)),
            ClockSkew = TimeSpan.FromSeconds(30)
        };

        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                if (!string.IsNullOrWhiteSpace(cookieName))
                    context.Token = context.Request.Cookies[cookieName];
                return Task.CompletedTask;
            },
            OnTokenValidated = context =>
            {
                if (context.Principal?.Identity is ClaimsIdentity identity)
                {
                    identity.AddClaim(new Claim(
                        AuthenticationConstants.AuthTransportClaim,
                        transport));
                }

                return Task.CompletedTask;
            }
        };
    }
}
