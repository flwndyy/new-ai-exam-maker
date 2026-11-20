using System.Security.Claims;
using System.IdentityModel.Tokens.Jwt;
using ExamMaker.Api.Authorization;
using ExamMaker.Application.Authentication;
using ExamMaker.Infrastructure.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace ExamMaker.Api.Authentication;

public static class AuthenticationServiceCollectionExtensions
{
    /// <summary>Register Cognito authentication, request-scoped users and shared authorization policies.</summary>
    public static IServiceCollection AddExamMakerAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<CognitoOptions>().Bind(configuration.GetSection(CognitoOptions.SectionName))
            .Validate(x => !x.HasAnyValue || x.HasValidValues(),
                "Provide valid Cognito:Region, Cognito:UserPoolId and Cognito:ClientId together.")
            .ValidateOnStart();
        services.AddMemoryCache();
        // Keep authenticated user state isolated to each request.
        services.AddScoped<IUserDirectory, DatabaseUserDirectory>();
        services.AddScoped<CurrentUser>();
        services.AddScoped<ICurrentUser>(provider => provider.GetRequiredService<CurrentUser>());
        services.AddScoped<CognitoBearerEvents>();

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<CognitoOptions>>((jwt, configured) => ConfigureBearer(jwt, configured.Value));

        var authenticated = new AuthorizationPolicyBuilder(JwtBearerDefaults.AuthenticationScheme)
            .RequireAuthenticatedUser().RequireClaim(CognitoBearerEvents.UserIdClaim).Build();
        services.AddAuthorization(options =>
        {
            // Protect endpoints without explicit authorization attributes; only AllowAnonymous is public.
            options.DefaultPolicy = authenticated;
            options.FallbackPolicy = authenticated;
            options.AddPolicy(AccessPolicies.Admin, policy => policy.Combine(authenticated).RequireRole(Roles.Admin));
            options.AddPolicy(AccessPolicies.ContentAuthor, policy => policy.Combine(authenticated).RequireRole(Roles.Teacher, Roles.Head));
            foreach (var permission in new[] { AccessPolicies.SameDepartment, AccessPolicies.Owner, AccessPolicies.Reviewer })
            {
                // Resource policies require identifiers loaded from the database as well as authentication.
                options.AddPolicy(permission, policy => policy.Combine(authenticated).AddRequirements(new ContentAccessRequirement(permission)));
            }
        });
        services.AddScoped<IAuthorizationHandler, ContentAccessHandler>();
        return services;
    }

    /// <summary>Validate the RS256 signature, issuer, lifetime and Cognito access-token client_id.</summary>
    private static void ConfigureBearer(JwtBearerOptions jwt, CognitoOptions cognito)
    {
        if (cognito.IsConfigured) jwt.Authority = cognito.Issuer;
        jwt.RequireHttpsMetadata = true;
        // Preserve Cognito claim names for exact sub and client_id validation.
        jwt.MapInboundClaims = false;
        // Do not retain tokens or expose token-validation details to clients.
        jwt.SaveToken = false;
        jwt.IncludeErrorDetails = false;
        jwt.EventsType = typeof(CognitoBearerEvents);
        jwt.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = cognito.IsConfigured ? cognito.Issuer : null,
            ValidateIssuerSigningKey = true,
            RequireSignedTokens = true,
            // Accept only the signing algorithm used by Cognito.
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            ValidateLifetime = true,
            RequireExpirationTime = true,
            // Allow a small clock difference between the API and Cognito.
            ClockSkew = TimeSpan.FromSeconds(30),
            // Cognito access tokens use client_id; ID tokens use aud and are rejected in TokenValidated.
            ValidateAudience = true,
            AudienceValidator = (_, token, _) => HasExpectedClient(token, cognito.ClientId)
        };
    }

    /// <summary>Support both .NET token handlers and reject duplicate client_id claims.</summary>
    private static bool HasExpectedClient(SecurityToken token, string clientId)
    {
        IEnumerable<Claim> claims = token switch
        {
            JsonWebToken json => json.Claims,
            JwtSecurityToken jwt => jwt.Claims,
            _ => []
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims));
        return !string.IsNullOrWhiteSpace(clientId)
            && CognitoBearerEvents.SingleClaim(principal, "client_id") == clientId;
    }
}
